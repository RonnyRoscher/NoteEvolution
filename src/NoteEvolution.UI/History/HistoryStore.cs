using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NoteEvolution.Core.Books;

namespace NoteEvolution.UI.History;

/// <summary>One stored version of a text block.</summary>
public sealed record HistoryVersion(long Id, Guid BlockId, DateTimeOffset At, HistoryReason Reason, BlockStand Stand);

/// <summary>A text block that was deleted, with where it stood so it can be put back.</summary>
/// <param name="AfterBlockId">The <c>id::</c> of the text block before it, if it had one.</param>
/// <param name="AfterText">The start of the text of the block before it, which finds that block when it has no <c>id::</c>.</param>
public sealed record DeletedBlock(Guid BlockId, string Book, DateTimeOffset At, string SectionPath, Guid? AfterBlockId, BlockStand Stand, string? AfterText = null);

/// <summary>
/// Persistent store of the versions of text blocks in <c>&lt;vault&gt;/.noteevolution/history.db</c>. Unlike the
/// understanding store it holds the user's work, so a damaged file is renamed to <c>history.db.defekt-&lt;time&gt;</c>
/// and never deleted; a file that only cannot be reached now (locked, read-only, a full disk) stays where it is. Safe to
/// call from several threads.
/// </summary>
public sealed class HistoryStore : IDisposable
{
    private const string VersionColumns = "id, block_id, at, reason, stand";
    private const string DeletedColumns = "block_id, book, at, section_path, after_block_id, stand, after_text";

    /// <summary>SQLITE_CORRUPT: the file is damaged.</summary>
    private const int SqliteCorrupt = 11;

    /// <summary>SQLITE_NOTADB: the file is no database.</summary>
    private const int SqliteNotADatabase = 26;

    /// <summary>The form of a stored stand: a status by its name, so reordering the enum leaves old stands intact; what is empty stays out.</summary>
    private static readonly JsonSerializerOptions StandJson = new()
    {
        Converters = { new JsonStringEnumConverter<BlockStatus>() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SqliteConnection _db;
    private readonly object _gate = new();
    private bool _disposed;

    private HistoryStore(SqliteConnection db) => _db = db;

    /// <param name="vaultRoot">The vault; the database lies in its <c>.noteevolution</c> folder.</param>
    /// <param name="logger">Told when a damaged database is moved aside.</param>
    /// <exception cref="IOException">The file is damaged and could not be moved aside either.</exception>
    /// <exception cref="SqliteException">The file cannot be used now (locked, read-only, a full disk); it stays in place.</exception>
    public static HistoryStore Open(string vaultRoot, ILogger logger)
    {
        var folder = Path.Combine(vaultRoot, ".noteevolution");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "history.db");

        if (File.Exists(path))
        {
            try
            {
                var existing = TryOpen(path);
                if (existing is not null) return new HistoryStore(existing);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
            {
                // damaged: fall through to move it aside; any other error may pass, and the history is irreplaceable
            }

            var aside = $"{path}.defekt-{DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}";
            logger.LogWarning("The history {Path} could not be read and is moved to {Aside}", path, aside);
            MoveAside(path, aside);
        }

        var fresh = TryOpen(path) ?? throw new InvalidOperationException("Could not create the history store.");
        return new HistoryStore(fresh);
    }

    public void Add(Guid blockId, string book, DateTimeOffset at, HistoryReason reason, BlockStand stand)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var command = _db.CreateCommand();
            command.CommandText = "INSERT INTO versions(block_id, book, at, reason, stand) VALUES ($block, $book, $at, $reason, $stand)";
            command.Parameters.AddWithValue("$block", blockId.ToString("D"));
            command.Parameters.AddWithValue("$book", book);
            command.Parameters.AddWithValue("$at", at.UtcTicks);
            command.Parameters.AddWithValue("$reason", (int)reason);
            command.Parameters.AddWithValue("$stand", JsonSerializer.Serialize(stand, StandJson));
            command.ExecuteNonQuery();
        }
    }

    /// <summary>The versions of the block, newest first.</summary>
    public IReadOnlyList<HistoryVersion> Versions(Guid blockId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var command = _db.CreateCommand();
            command.CommandText = $"SELECT {VersionColumns} FROM versions WHERE block_id = $block ORDER BY id DESC";
            command.Parameters.AddWithValue("$block", blockId.ToString("D"));
            return ReadVersions(command);
        }
    }

    /// <summary>Whether the block has at least one version; reads no stand.</summary>
    public bool HasVersions(Guid blockId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var command = _db.CreateCommand();
            command.CommandText = "SELECT 1 FROM versions WHERE block_id = $block LIMIT 1";
            command.Parameters.AddWithValue("$block", blockId.ToString("D"));
            return command.ExecuteScalar() is not null;
        }
    }

    /// <summary>The newest version of the block, or null.</summary>
    public HistoryVersion? Latest(Guid blockId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var command = _db.CreateCommand();
            command.CommandText = $"SELECT {VersionColumns} FROM versions WHERE block_id = $block ORDER BY id DESC LIMIT 1";
            command.Parameters.AddWithValue("$block", blockId.ToString("D"));
            return ReadVersions(command).FirstOrDefault();
        }
    }

    /// <summary>The blocks that have at least one version.</summary>
    public IReadOnlySet<Guid> BlocksWithHistory()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var command = _db.CreateCommand();
            command.CommandText = "SELECT DISTINCT block_id FROM versions";
            var result = new HashSet<Guid>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) result.Add(Guid.ParseExact(reader.GetString(0), "D"));
            return result;
        }
    }

    /// <summary>Notes that the block was deleted (replacing an earlier note about the same block).</summary>
    public void MarkDeleted(DeletedBlock deleted)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var command = _db.CreateCommand();
            command.CommandText =
                $"INSERT OR REPLACE INTO deleted({DeletedColumns}) VALUES ($block, $book, $at, $path, $after, $stand, $afterText)";
            command.Parameters.AddWithValue("$block", deleted.BlockId.ToString("D"));
            command.Parameters.AddWithValue("$book", deleted.Book);
            command.Parameters.AddWithValue("$at", deleted.At.UtcTicks);
            command.Parameters.AddWithValue("$path", deleted.SectionPath);
            command.Parameters.AddWithValue("$after", deleted.AfterBlockId is { } after ? after.ToString("D") : DBNull.Value);
            command.Parameters.AddWithValue("$stand", JsonSerializer.Serialize(deleted.Stand, StandJson));
            command.Parameters.AddWithValue("$afterText", (object?)deleted.AfterText ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Forgets that the block was deleted (it is back); its versions stay.</summary>
    public void ClearDeleted(Guid blockId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var command = _db.CreateCommand();
            command.CommandText = "DELETE FROM deleted WHERE block_id = $block";
            command.Parameters.AddWithValue("$block", blockId.ToString("D"));
            command.ExecuteNonQuery();
        }
    }

    /// <summary>The deleted blocks of the book, newest first.</summary>
    public IReadOnlyList<DeletedBlock> Deleted(string book)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var command = _db.CreateCommand();
            command.CommandText = $"SELECT {DeletedColumns} FROM deleted WHERE book = $book ORDER BY at DESC, rowid DESC";
            command.Parameters.AddWithValue("$book", book);
            var result = new List<DeletedBlock>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new DeletedBlock(
                    Guid.ParseExact(reader.GetString(0), "D"),
                    reader.GetString(1),
                    new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : Guid.ParseExact(reader.GetString(4), "D"),
                    ReadStand(reader.GetString(5)),
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
            }

            return result;
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

    private static List<HistoryVersion> ReadVersions(SqliteCommand command)
    {
        var result = new List<HistoryVersion>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new HistoryVersion(
                reader.GetInt64(0),
                Guid.ParseExact(reader.GetString(1), "D"),
                new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero),
                (HistoryReason)reader.GetInt32(3),
                ReadStand(reader.GetString(4))));
        }

        return result;
    }

    private static BlockStand ReadStand(string json) =>
        JsonSerializer.Deserialize<BlockStand>(json, StandJson) ?? throw new InvalidDataException("A stored stand is empty.");

    /// <summary>
    /// Opens the database and makes sure it is intact. Returns null when it failed the integrity check; throws
    /// <see cref="SqliteException"/> when it cannot be opened or read, or is not a database at all. The connection is
    /// closed in both cases.
    /// </summary>
    private static SqliteConnection? TryOpen(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false, // the file must be movable as soon as the connection is disposed
        }.ToString());

        try
        {
            db.Open();
            if (!Scalar(db, "PRAGMA quick_check").Equals("ok", StringComparison.OrdinalIgnoreCase))
            {
                db.Dispose();
                return null;
            }

            Run(db, "PRAGMA journal_mode=WAL");
            Run(db, "PRAGMA synchronous=NORMAL");
            Run(db, "CREATE TABLE IF NOT EXISTS versions(id INTEGER PRIMARY KEY, block_id TEXT NOT NULL, book TEXT NOT NULL, " +
                    "at INTEGER NOT NULL, reason INTEGER NOT NULL, stand TEXT NOT NULL)");
            Run(db, "CREATE INDEX IF NOT EXISTS versions_block ON versions(block_id, id)");
            Run(db, "CREATE TABLE IF NOT EXISTS deleted(block_id TEXT PRIMARY KEY, book TEXT NOT NULL, at INTEGER NOT NULL, " +
                    "section_path TEXT NOT NULL, after_block_id TEXT, stand TEXT NOT NULL, after_text TEXT)");
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    private static void MoveAside(string path, string aside)
    {
        File.Move(path, aside);
        // the log files belong to the database; a leftover one would be applied to the new file
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            if (File.Exists(path + suffix)) File.Move(path + suffix, aside + suffix);
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
}
