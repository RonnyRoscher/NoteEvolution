using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Vaults;

public sealed class Vault : IVault
{
    private readonly object _gate = new();
    private IReadOnlyList<Page> _pages;
    private IReadOnlyList<Book> _books;

    private Vault(string root, VaultSettings settings, IReadOnlyList<Page> pages)
    {
        Root = root;
        Settings = settings;
        _pages = pages;
        _books = BuildBooks(pages, []);
    }

    public string Root { get; }

    public VaultSettings Settings { get; }

    public IReadOnlyList<Page> Pages => _pages;

    public IReadOnlyList<Book> Books => _books;

    public event Action<Page>? PageReplaced;

    /// <summary>Reads every <c>*.md</c> file below the settings' note folders; unparsable files become read-only pages.</summary>
    public static Vault Open(string root)
    {
        root = Path.GetFullPath(root);
        var settings = VaultSettings.Load(root);
        var pages = new List<Page>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in settings.NoteFolders)
        {
            var dir = Path.GetFullPath(Path.Combine(root, folder));
            if (!Directory.Exists(dir)) continue;
            var files = Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories)
                .Where(f => IsNoteFile(root, f))
                .Order(StringComparer.Ordinal);
            foreach (var file in files)
            {
                if (seen.Add(file))
                {
                    pages.Add(LogseqParser.Parse(file, File.ReadAllBytes(file)));
                }
            }
        }
        return new Vault(root, settings, pages);
    }

    public Page? FindPageByPath(string path)
    {
        var full = Path.GetFullPath(path);
        return _pages.FirstOrDefault(p => SamePath(p.FilePath, full));
    }

    public Book? FindBook(string linkName) =>
        _books.FirstOrDefault(b => string.Equals(b.LinkName, linkName, StringComparison.OrdinalIgnoreCase));

    public (Page Page, Block Block)? FindBlockById(Guid id) => FindBlock(b => b.Id == id);

    public (Page Page, Block Block)? FindBlockByKey(Guid key) => FindBlock(b => b.Key == key);

    public void ReplacePage(Page page)
    {
        lock (_gate)
        {
            var pages = _pages.ToList();
            var index = pages.FindIndex(p => SamePath(p.FilePath, page.FilePath));
            if (index >= 0)
            {
                pages[index] = page;
            }
            else
            {
                pages.Add(page);
            }
            Publish(pages);
        }
        PageReplaced?.Invoke(page);
    }

    public void RemovePage(string path)
    {
        lock (_gate)
        {
            var pages = _pages.Where(p => !SamePath(p.FilePath, path)).ToList();
            if (pages.Count != _pages.Count)
            {
                Publish(pages);
            }
        }
    }

    private void Publish(List<Page> pages)
    {
        _books = BuildBooks(pages, _books);
        _pages = pages;
    }

    /// <summary>Reuses the views of unchanged book pages so consumers keep stable instances.</summary>
    private static IReadOnlyList<Book> BuildBooks(IEnumerable<Page> pages, IReadOnlyList<Book> existing) =>
        [.. pages.Where(Book.IsBook).Select(p => existing.FirstOrDefault(b => ReferenceEquals(b.Page, p)) ?? Book.Load(p))];

    private (Page Page, Block Block)? FindBlock(Func<Block, bool> predicate)
    {
        foreach (var page in _pages)
        {
            foreach (var block in page.AllBlocks())
            {
                if (predicate(block)) return (page, block);
            }
        }
        return null;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Skips hidden files (also our <c>.x.ne-tmp</c> temp files) and everything below <c>.noteevolution</c>.</summary>
    private static bool IsNoteFile(string root, string file)
    {
        if (Path.GetFileName(file).StartsWith('.')) return false;
        var segments = Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !segments.Any(s => string.Equals(s, VaultSettings.FolderName, StringComparison.OrdinalIgnoreCase));
    }
}
