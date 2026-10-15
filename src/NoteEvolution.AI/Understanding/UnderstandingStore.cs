using System.Buffers.Binary;
using Microsoft.Data.Sqlite;

namespace NoteEvolution.AI.Understanding;

/// <summary>What an understanding entry describes: a heading's topic summary or a note's core statement.</summary>
public enum UnderstandingKind { Heading, Note }

/// <summary>
/// One piece of the model's understanding: <paramref name="Text"/> produced by <paramref name="ChatModel"/> for the content
/// with the given <paramref name="Fingerprint"/>, optionally with its meaning <paramref name="Vector"/> from
/// <paramref name="EmbedModel"/>. <paramref name="Identity"/> names the thing across edits (it stays while the fingerprint changes).
/// </summary>
public sealed record UnderstandingEntry(UnderstandingKind Kind, string Fingerprint, string Identity, string Text,
    string ChatModel, string? EmbedModel, float[]? Vector);

/// <summary>
/// Persistent store of the model's understanding in <c>&lt;vault&gt;/.noteevolution/understanding.db</c>, keyed by content
/// fingerprint. It only saves recomputation, so it is disposable: an unusable file is deleted and recreated. "Newest" for
/// <see cref="LatestByIdentity"/> is a monotonically increasing sequence number stored in each row. Safe to call from
/// several threads.
/// </summary>
public sealed class UnderstandingStore : IDisposable
{
    private const string Columns = "kind, fingerprint, identity, text, chat_model, embed_model, vector";

    private readonly SqliteConnection _db;
    private readonly object _gate = new();
    private bool _disposed;

    private UnderstandingStore(SqliteConnection db) => _db = db;

    public static UnderstandingStore Open(string vaultRoot)
    {
        var folder = Path.Combine(vaultRoot, ".noteevolution");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "understanding.db");

        if (File.Exists(path))
        {
            try
            {
                var existing = TryOpen(path);
                if (existing is not null) return new UnderstandingStore(existing);
            }
            catch (SqliteException)
            {
                // not a usable database: fall through to recreate it
            }

            DeleteFiles(path);
        }

        var fresh = TryOpen(path) ?? throw new InvalidOperationException("Could not create the understanding store.");
        return new UnderstandingStore(fresh);
    }

    /// <summary>The entry stored for the fingerprint, or null.</summary>
    public UnderstandingEntry? Get(string fingerprint)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var command = _db.CreateCommand();
            command.CommandText = $"SELECT {Columns} FROM entries WHERE fingerprint = $fp";
            command.Parameters.AddWithValue("$fp", fingerprint);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadEntry(reader) : null;
        }
    }

    /// <summary>The most recently stored entry of that kind and identity (the stale fallback), or null.</summary>
    public UnderstandingEntry? LatestByIdentity(UnderstandingKind kind, string identity)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var command = _db.CreateCommand();
            command.CommandText = $"SELECT {Columns} FROM entries WHERE kind = $kind AND identity = $identity ORDER BY updated DESC LIMIT 1";
            command.Parameters.AddWithValue("$kind", (int)kind);
            command.Parameters.AddWithValue("$identity", identity);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadEntry(reader) : null;
        }
    }

    /// <summary>Stores the entry (replacing one with the same fingerprint), makes it the newest of its identity and clears its failed attempts.</summary>
    public void Put(UnderstandingEntry entry)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var tx = _db.BeginTransaction();
            using (var command = _db.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText =
                    $"INSERT OR REPLACE INTO entries({Columns}, updated) VALUES ($kind, $fp, $identity, $text, $chat, $embed, $vector, " +
                    "(SELECT COALESCE(MAX(updated), 0) + 1 FROM entries))";
                command.Parameters.AddWithValue("$kind", (int)entry.Kind);
                command.Parameters.AddWithValue("$fp", entry.Fingerprint);
                command.Parameters.AddWithValue("$identity", entry.Identity);
                command.Parameters.AddWithValue("$text", entry.Text);
                command.Parameters.AddWithValue("$chat", entry.ChatModel);
                command.Parameters.AddWithValue("$embed", (object?)entry.EmbedModel ?? DBNull.Value);
                command.Parameters.Add("$vector", SqliteType.Blob).Value = entry.Vector is null ? DBNull.Value : ToBlob(entry.Vector);
                command.ExecuteNonQuery();
            }

            using (var clear = _db.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM attempts WHERE fingerprint = $fp";
                clear.Parameters.AddWithValue("$fp", entry.Fingerprint);
                clear.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    /// <summary>Sets the meaning vector (and the model that made it) of an existing entry; does nothing for an unknown fingerprint.</summary>
    public void SetVector(string fingerprint, string embedModel, float[] vector)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var command = _db.CreateCommand();
            command.CommandText = "UPDATE entries SET embed_model = $embed, vector = $vector WHERE fingerprint = $fp";
            command.Parameters.AddWithValue("$embed", embedModel);
            command.Parameters.Add("$vector", SqliteType.Blob).Value = ToBlob(vector);
            command.Parameters.AddWithValue("$fp", fingerprint);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Fingerprints of entries that have no vector from <paramref name="embedModel"/> (none yet, or one from another model).</summary>
    public IReadOnlyList<string> WithoutVector(string embedModel)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var command = _db.CreateCommand();
            command.CommandText = "SELECT fingerprint FROM entries WHERE vector IS NULL OR embed_model IS NULL OR embed_model <> $embed ORDER BY updated";
            command.Parameters.AddWithValue("$embed", embedModel);
            var result = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) result.Add(reader.GetString(0));
            return result;
        }
    }

    /// <summary>How many times producing the understanding for the fingerprint failed since it was last stored.</summary>
    public int Attempts(string fingerprint)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var command = _db.CreateCommand();
            command.CommandText = "SELECT count FROM attempts WHERE fingerprint = $fp";
            command.Parameters.AddWithValue("$fp", fingerprint);
            return command.ExecuteScalar() is long count ? (int)count : 0;
        }
    }

    public void RecordFailure(string fingerprint)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var command = _db.CreateCommand();
            command.CommandText = "INSERT INTO attempts(fingerprint, count) VALUES ($fp, 1) ON CONFLICT(fingerprint) DO UPDATE SET count = count + 1";
            command.Parameters.AddWithValue("$fp", fingerprint);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Takes back one failed attempt (one that turned out to be the server's fault, not the content's); never below zero.</summary>
    public void ForgetFailure(string fingerprint)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var tx = _db.BeginTransaction();
            using (var command = _db.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = "UPDATE attempts SET count = count - 1 WHERE fingerprint = $fp";
                command.Parameters.AddWithValue("$fp", fingerprint);
                command.ExecuteNonQuery();
            }

            using (var clear = _db.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM attempts WHERE fingerprint = $fp AND count <= 0";
                clear.Parameters.AddWithValue("$fp", fingerprint);
                clear.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    /// <summary>
    /// Deletes every entry whose fingerprint is not live, except the newest entry of each kind for a live identity (the
    /// stale fallback), and the failed attempts of deleted fingerprints.
    /// </summary>
    public void Prune(IReadOnlySet<string> liveFingerprints, IReadOnlySet<string> liveIdentities)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var stale = new List<string>();
            using (var select = _db.CreateCommand())
            {
                // newest first, so the first row seen per (kind, identity) is the fallback
                select.CommandText = "SELECT fingerprint, kind, identity FROM entries ORDER BY updated DESC";
                using var reader = select.ExecuteReader();
                var seen = new HashSet<(long, string)>();
                while (reader.Read())
                {
                    var fingerprint = reader.GetString(0);
                    var identity = reader.GetString(2);
                    var newestOfLiveIdentity = liveIdentities.Contains(identity) && seen.Add((reader.GetInt64(1), identity));
                    if (!newestOfLiveIdentity && !liveFingerprints.Contains(fingerprint)) stale.Add(fingerprint);
                }
            }

            using var tx = _db.BeginTransaction();
            using var delete = _db.CreateCommand();
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM entries WHERE fingerprint = $fp";
            var parameter = delete.Parameters.Add("$fp", SqliteType.Text);
            foreach (var fingerprint in stale)
            {
                parameter.Value = fingerprint;
                delete.ExecuteNonQuery();
            }

            using var orphans = _db.CreateCommand();
            orphans.Transaction = tx;
            orphans.CommandText = "DELETE FROM attempts WHERE fingerprint NOT IN (SELECT fingerprint FROM entries)";
            orphans.ExecuteNonQuery();
            tx.Commit();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _db.Dispose();
        }
    }

    /// <summary>
    /// Opens the database and makes sure it is intact. Returns null when it failed the integrity check; throws
    /// <see cref="SqliteException"/> when the file is not a database at all. The connection is closed in both cases.
    /// </summary>
    private static SqliteConnection? TryOpen(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false, // the file must be deletable as soon as the connection is disposed
        }.ToString());

        try
        {
            db.Open();
            if (!Scalar(db, "PRAGMA quick_check").Equals("ok", StringComparison.OrdinalIgnoreCase))
            {
                db.Dispose();
                return null;
            }

            Run(db, "CREATE TABLE IF NOT EXISTS entries(fingerprint TEXT PRIMARY KEY, kind INTEGER NOT NULL, identity TEXT NOT NULL, " +
                    "text TEXT NOT NULL, chat_model TEXT NOT NULL, embed_model TEXT, vector BLOB, updated INTEGER NOT NULL)");
            Run(db, "CREATE INDEX IF NOT EXISTS entries_identity ON entries(kind, identity, updated)");
            Run(db, "CREATE TABLE IF NOT EXISTS attempts(fingerprint TEXT PRIMARY KEY, count INTEGER NOT NULL)");
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    private static UnderstandingEntry ReadEntry(SqliteDataReader reader)
    {
        // a damaged vector blob is treated as no vector, so it gets recomputed
        var blob = reader.IsDBNull(6) ? null : (byte[])reader.GetValue(6);
        var vector = blob is not null && blob.Length % sizeof(float) == 0 ? FromBlob(blob) : null;
        return new UnderstandingEntry((UnderstandingKind)reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), vector is null || reader.IsDBNull(5) ? null : reader.GetString(5), vector);
    }

    private static void DeleteFiles(string path)
    {
        foreach (var file in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
            if (File.Exists(file)) File.Delete(file);
    }

    private static void Run(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Scalar(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? "";
    }

    private static byte[] ToBlob(float[] vector)
    {
        var blob = new byte[vector.Length * sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(blob.AsSpan(i * sizeof(float)), vector[i]);
        return blob;
    }

    private static float[] FromBlob(byte[] blob)
    {
        var vector = new float[blob.Length / sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(i * sizeof(float)));
        return vector;
    }
}
