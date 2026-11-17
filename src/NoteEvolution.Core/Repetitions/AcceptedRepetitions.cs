using System.Text.Json;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Vaults;

namespace NoteEvolution.Core.Repetitions;

/// <summary>
/// The accepted pairs in <c>.noteevolution/repetitions.json</c>: <c>{ "books": { "&lt;page name&gt;": [["&lt;id&gt;","&lt;id&gt;"], …] } }</c>.
/// A missing or damaged file counts as empty. The file is read once and then kept in memory; every change is written atomically.
/// A file that cannot be read right now (I/O or access error) is not cached as empty: reading gives empty and is tried
/// again on the next call, and <see cref="Set"/> throws rather than overwrite what may be there.
/// </summary>
/// <param name="onError">Told about a read failure of the file (an I/O or access error).</param>
public sealed class AcceptedRepetitions(string vaultRoot, Action<Exception>? onError = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path = Path.Combine(vaultRoot, VaultSettings.FolderName, "repetitions.json");
    private readonly object _gate = new();
    private Dictionary<string, HashSet<(Guid, Guid)>>? _books;

    /// <summary>The accepted pairs of the book, normalized: the smaller id first.</summary>
    public IReadOnlySet<(Guid, Guid)> Of(string bookName)
    {
        lock (_gate)
        {
            return TryBooks() is { } books && books.TryGetValue(bookName, out var pairs) ? new HashSet<(Guid, Guid)>(pairs) : [];
        }
    }

    public bool Contains(string bookName, Guid idA, Guid idB)
    {
        lock (_gate)
        {
            return TryBooks() is { } books && books.TryGetValue(bookName, out var pairs) && pairs.Contains(Normalize(idA, idB));
        }
    }

    /// <summary>
    /// Adds or removes the pair and writes the file; pairs of <paramref name="bookName"/> naming an id not in
    /// <paramref name="liveIds"/> are dropped. If writing fails the pairs are as they were before the call and the
    /// exception is passed on.
    /// </summary>
    public void Set(string bookName, Guid idA, Guid idB, bool accepted, IReadOnlySet<Guid> liveIds)
    {
        lock (_gate)
        {
            var books = Books; // throws if the file cannot be read: nothing is written then
            var before = books.TryGetValue(bookName, out var existing) ? new HashSet<(Guid, Guid)>(existing) : null;
            var pairs = existing ?? [];
            books[bookName] = pairs;

            pairs.RemoveWhere(p => !liveIds.Contains(p.Item1) || !liveIds.Contains(p.Item2));
            var pair = Normalize(idA, idB);
            if (accepted)
            {
                pairs.Add(pair);
            }
            else
            {
                pairs.Remove(pair);
            }

            try
            {
                Write();
            }
            catch
            {
                if (before is null)
                {
                    Books.Remove(bookName);
                }
                else
                {
                    Books[bookName] = before;
                }

                throw;
            }
        }
    }

    private static (Guid, Guid) Normalize(Guid a, Guid b) => a.CompareTo(b) <= 0 ? (a, b) : (b, a);

    /// <summary>The books as read; throws on an I/O or access error, which is not cached (the next call reads again).</summary>
    private Dictionary<string, HashSet<(Guid, Guid)>> Books => _books ??= Read();

    /// <summary>The books, or null when the file cannot be read right now (reported to the error callback).</summary>
    private Dictionary<string, HashSet<(Guid, Guid)>>? TryBooks()
    {
        try
        {
            return Books;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onError?.Invoke(ex);
            return null;
        }
    }

    private Dictionary<string, HashSet<(Guid, Guid)>> Read()
    {
        var books = new Dictionary<string, HashSet<(Guid, Guid)>>();
        try
        {
            if (!File.Exists(_path))
            {
                return books;
            }

            var file = JsonSerializer.Deserialize<FileShape>(File.ReadAllBytes(_path), Json);
            foreach (var (name, entries) in file?.Books ?? [])
            {
                var pairs = new HashSet<(Guid, Guid)>();
                foreach (var entry in entries ?? [])
                {
                    if (entry is { Length: 2 } && Guid.TryParse(entry[0], out var a) && Guid.TryParse(entry[1], out var b))
                    {
                        pairs.Add(Normalize(a, b));
                    }
                }

                books[name] = pairs;
            }

            return books;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void Write()
    {
        var shape = new FileShape
        {
            Books = _books!
                .Where(b => b.Value.Count > 0)
                .ToDictionary(
                    b => b.Key,
                    b => (List<string[]>?)[.. b.Value.Select(p => new[] { p.Item1.ToString("D"), p.Item2.ToString("D") })]),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        AtomicFile.Write(_path, JsonSerializer.SerializeToUtf8Bytes(shape, Json));
    }

    private sealed class FileShape
    {
        public Dictionary<string, List<string[]>?>? Books { get; set; }
    }
}
