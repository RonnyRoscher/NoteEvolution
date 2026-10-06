using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using NoteEvolution.Core.Vaults;

namespace NoteEvolution.AI.Search;

/// <summary>
/// SQLite FTS5 index of all notes, held in memory (the vault files are the truth; the index is rebuilt on start).
/// Hits are ranked by bm25 with the note's own text weighted above its context. Safe to call from several threads.
/// </summary>
public sealed class FullTextSearchService : ISearchService, IDisposable
{
    private readonly SqliteConnection _db = new("Data Source=:memory:");
    private readonly object _gate = new();
    private bool _disposed;

    public FullTextSearchService()
    {
        _db.Open();
        Execute(
            "CREATE VIRTUAL TABLE notes USING fts5(content, context, key UNINDEXED, page UNINDEXED, tokenize='unicode61 remove_diacritics 2')");
        Execute("CREATE TABLE meta(key TEXT PRIMARY KEY, page TEXT, date TEXT, is_used INTEGER)");
    }

    public void Rebuild(INoteRepository notes)
    {
        var all = notes.All().ToList();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var tx = _db.BeginTransaction();
            Execute("DELETE FROM notes");
            Execute("DELETE FROM meta");
            Insert(all);
            tx.Commit();
        }
    }

    public void UpdatePage(INoteRepository notes, string pagePath)
    {
        var page = PageKey(pagePath);
        var current = notes.All().Where(n => PageKey(n.Page.FilePath) == page).ToList();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var tx = _db.BeginTransaction();
            using (var delete = _db.CreateCommand())
            {
                delete.CommandText = "DELETE FROM notes WHERE key IN (SELECT key FROM meta WHERE page = $page)";
                delete.Parameters.AddWithValue("$page", page);
                delete.ExecuteNonQuery();
                delete.CommandText = "DELETE FROM meta WHERE page = $page";
                delete.ExecuteNonQuery();
            }
            Insert(current);
            tx.Commit();
        }
    }

    /// <summary>Hits best first; <see cref="SearchHit.Score"/> is the negated bm25 rank, so higher is better.</summary>
    public IReadOnlyList<SearchHit> Search(SearchQuery query)
    {
        var match = FtsQuery.Build(query.Text);
        if (match is null || query.Limit <= 0) return [];

        var sql = new StringBuilder(
            "SELECT notes.key, bm25(notes, 1.0, 0.3) AS rank FROM notes JOIN meta ON meta.key = notes.key WHERE notes MATCH $match");
        if (query.Filter.HideUsed) sql.Append(" AND meta.is_used = 0");
        if (query.Filter.From is not null) sql.Append(" AND meta.date >= $from");
        if (query.Filter.To is not null) sql.Append(" AND meta.date <= $to");
        sql.Append(" ORDER BY rank LIMIT $limit");

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var command = _db.CreateCommand();
            command.CommandText = sql.ToString();
            command.Parameters.AddWithValue("$match", match);
            if (query.Filter.From is { } from) command.Parameters.AddWithValue("$from", DateText(from));
            if (query.Filter.To is { } to) command.Parameters.AddWithValue("$to", DateText(to));
            command.Parameters.AddWithValue("$limit", query.Limit);
            using var reader = command.ExecuteReader();
            var hits = new List<SearchHit>();
            while (reader.Read())
            {
                hits.Add(new SearchHit(Guid.Parse(reader.GetString(0)), -reader.GetDouble(1)));
            }
            return hits;
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

    /// <summary>Inserts the notes; a note whose key is already indexed (the same id on two pages) is skipped.</summary>
    private void Insert(List<NoteBlock> blocks)
    {
        using var meta = _db.CreateCommand();
        meta.CommandText = "INSERT OR IGNORE INTO meta(key, page, date, is_used) VALUES ($key, $page, $date, $used)";
        var metaKey = meta.Parameters.Add("$key", SqliteType.Text);
        var metaPage = meta.Parameters.Add("$page", SqliteType.Text);
        var metaDate = meta.Parameters.Add("$date", SqliteType.Text);
        var metaUsed = meta.Parameters.Add("$used", SqliteType.Integer);

        using var note = _db.CreateCommand();
        note.CommandText = "INSERT INTO notes(content, context, key, page) VALUES ($content, $context, $key, $page)";
        var noteContent = note.Parameters.Add("$content", SqliteType.Text);
        var noteContext = note.Parameters.Add("$context", SqliteType.Text);
        var noteKey = note.Parameters.Add("$key", SqliteType.Text);
        var notePage = note.Parameters.Add("$page", SqliteType.Text);

        foreach (var block in blocks)
        {
            var key = block.Key.ToString("D");
            var page = PageKey(block.Page.FilePath);
            metaKey.Value = key;
            metaPage.Value = page;
            metaDate.Value = block.Date is { } date ? DateText(date) : DBNull.Value;
            metaUsed.Value = block.IsUsed ? 1 : 0;
            if (meta.ExecuteNonQuery() == 0) continue;

            noteContent.Value = block.Block.Content;
            noteContext.Value = string.Join(" / ", block.ContextPath);
            noteKey.Value = key;
            notePage.Value = page;
            note.ExecuteNonQuery();
        }
    }

    private void Execute(string sql)
    {
        using var command = _db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string PageKey(string path) => Path.GetFullPath(path).ToLowerInvariant();

    private static string DateText(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
