using System.Buffers.Binary;
using Microsoft.Data.Sqlite;

namespace NoteEvolution.AI.Semantic;

/// <summary>
/// Persistent cache of embeddings in <c>&lt;vault&gt;/.noteevolution/vectors.db</c>, keyed by content hash (which already
/// includes the model id). It only saves recomputation, so it is disposable: a file written for another model or one
/// that is corrupt is deleted and recreated. Safe to call from several threads.
/// </summary>
public sealed class VectorCache : IDisposable
{
    /// <summary>Largest number of parameters in one <c>IN (...)</c> list (SQLite's default limit is far higher).</summary>
    private const int MaxParametersPerStatement = 500;

    private readonly SqliteConnection _db;
    private readonly object _gate = new();
    private bool _disposed;

    private VectorCache(SqliteConnection db) => _db = db;

    public static VectorCache Open(string vaultRoot, string modelId)
    {
        var folder = Path.Combine(vaultRoot, ".noteevolution");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "vectors.db");

        if (File.Exists(path))
        {
            try
            {
                var existing = TryOpen(path, modelId);
                if (existing is not null) return new VectorCache(existing);
            }
            catch (SqliteException)
            {
                // not a usable database: fall through to recreate it
            }

            DeleteFiles(path);
        }

        var fresh = TryOpen(path, modelId) ?? throw new InvalidOperationException("Could not create the vector cache.");
        return new VectorCache(fresh);
    }

    /// <summary>The embeddings stored for the given hashes; hashes that are not cached are missing from the result.</summary>
    public IReadOnlyDictionary<string, float[]> Get(IReadOnlyCollection<string> hashes)
    {
        var wanted = hashes.Distinct().ToList();
        var found = new Dictionary<string, float[]>();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var chunk in wanted.Chunk(MaxParametersPerStatement))
            {
                using var command = _db.CreateCommand();
                var names = new string[chunk.Length];
                for (var i = 0; i < chunk.Length; i++)
                {
                    names[i] = "$h" + i;
                    command.Parameters.AddWithValue(names[i], chunk[i]);
                }

                command.CommandText = $"SELECT hash, vector FROM vectors WHERE hash IN ({string.Join(',', names)})";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var blob = (byte[])reader.GetValue(1);
                    if (blob.Length % sizeof(float) != 0) continue; // damaged row: treat as not cached
                    found[reader.GetString(0)] = FromBlob(blob);
                }
            }
        }

        return found;
    }

    /// <summary>Stores (or replaces) the embeddings in one transaction.</summary>
    public void Put(IReadOnlyList<(string Hash, string File, float[] Vector)> items)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var tx = _db.BeginTransaction();
            using var command = _db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "INSERT OR REPLACE INTO vectors(hash, file, vector) VALUES ($hash, $file, $vector)";
            var hash = command.Parameters.Add("$hash", SqliteType.Text);
            var file = command.Parameters.Add("$file", SqliteType.Text);
            var vector = command.Parameters.Add("$vector", SqliteType.Blob);
            foreach (var item in items)
            {
                hash.Value = item.Hash;
                file.Value = item.File;
                vector.Value = ToBlob(item.Vector);
                command.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    /// <summary>Deletes every cached embedding whose hash is not in <paramref name="liveHashes"/>.</summary>
    public void Prune(IReadOnlySet<string> liveHashes)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var stale = new List<string>();
            using (var select = _db.CreateCommand())
            {
                select.CommandText = "SELECT hash FROM vectors";
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    var hash = reader.GetString(0);
                    if (!liveHashes.Contains(hash)) stale.Add(hash);
                }
            }

            if (stale.Count == 0) return;
            using var tx = _db.BeginTransaction();
            using var delete = _db.CreateCommand();
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM vectors WHERE hash = $hash";
            var parameter = delete.Parameters.Add("$hash", SqliteType.Text);
            foreach (var hash in stale)
            {
                parameter.Value = hash;
                delete.ExecuteNonQuery();
            }

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
    /// Opens the database and makes sure it is intact and belongs to <paramref name="modelId"/>. Returns null when it is
    /// usable only after being discarded (another model, failed integrity check); throws <see cref="SqliteException"/>
    /// when the file is not a database at all. The connection is closed in both cases.
    /// </summary>
    private static SqliteConnection? TryOpen(string path, string modelId)
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

            Run(db, "CREATE TABLE IF NOT EXISTS vectors(hash TEXT PRIMARY KEY, file TEXT NOT NULL, vector BLOB NOT NULL)");
            Run(db, "CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT)");

            using var read = db.CreateCommand();
            read.CommandText = "SELECT value FROM meta WHERE key = 'model'";
            var stored = read.ExecuteScalar() as string;
            if (stored is null)
            {
                using var write = db.CreateCommand();
                write.CommandText = "INSERT OR REPLACE INTO meta(key, value) VALUES ('model', $model)";
                write.Parameters.AddWithValue("$model", modelId);
                write.ExecuteNonQuery();
            }
            else if (stored != modelId)
            {
                db.Dispose();
                DeleteFiles(path);
                return TryOpen(path, modelId);
            }

            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
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
