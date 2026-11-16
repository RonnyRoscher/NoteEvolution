using System.Text.Json;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Vaults;

namespace NoteEvolution.Core.Repetitions;

/// <summary>
/// The accepted pairs in <c>.noteevolution/repetitions.json</c>: <c>{ "books": { "&lt;page name&gt;": [["&lt;id&gt;","&lt;id&gt;"], …] } }</c>.
/// A missing or damaged file counts as empty. The file is read once and then kept in memory; every change is written atomically.
/// </summary>
public sealed class AcceptedRepetitions(string vaultRoot)
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
            return Books.TryGetValue(bookName, out var pairs) ? new HashSet<(Guid, Guid)>(pairs) : [];
        }
    }

    public bool Contains(string bookName, Guid idA, Guid idB)
    {
        lock (_gate)
        {
            return Books.TryGetValue(bookName, out var pairs) && pairs.Contains(Normalize(idA, idB));
        }
    }

    /// <summary>Adds or removes the pair and writes the file; pairs of <paramref name="bookName"/> naming an id not in <paramref name="liveIds"/> are dropped.</summary>
    public void Set(string bookName, Guid idA, Guid idB, bool accepted, IReadOnlySet<Guid> liveIds)
    {
        lock (_gate)
        {
            if (!Books.TryGetValue(bookName, out var pairs))
            {
                pairs = [];
                Books[bookName] = pairs;
            }

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

            Write();
        }
    }

    private static (Guid, Guid) Normalize(Guid a, Guid b) => a.CompareTo(b) <= 0 ? (a, b) : (b, a);

    private Dictionary<string, HashSet<(Guid, Guid)>> Books => _books ??= Read();

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
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
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
