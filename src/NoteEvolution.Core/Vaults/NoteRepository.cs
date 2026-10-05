using System.Globalization;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Vaults;

/// <summary>
/// Creates <see cref="NoteBlock"/> views over a vault on demand and caches them per page until the page is replaced.
/// Every structural change to a page (blocks inserted, removed or moved) must be followed by
/// <see cref="IVault.ReplacePage"/>; property changes are visible live.
/// </summary>
public sealed class NoteRepository : INoteRepository
{
    private readonly IVault _vault;
    private readonly Dictionary<Page, PageNotes> _cache = [];
    private readonly object _gate = new();

    public NoteRepository(IVault vault)
    {
        _vault = vault;
        _vault.PageReplaced += Invalidate;
    }

    public IEnumerable<NoteBlock> All()
    {
        var pages = _vault.Pages;
        DropStale(pages);
        return pages.Where(IsNotePage).SelectMany(p => NotesOf(p).All);
    }

    public NoteBlock? Get(Guid key) =>
        _vault.FindBlockByKey(key) is { } found && IsNotePage(found.Page)
            ? NotesOf(found.Page).ByKey.GetValueOrDefault(key)
            : null;

    public IReadOnlyList<NoteBlock> Journal(NoteFilter filter) =>
    [
        .. _vault.Pages
            .Where(IsNotePage)
            .Select(p => (Page: p, Date: DateOf(p)))
            .Where(x => x.Date is not null)
            .OrderByDescending(x => x.Date)
            .SelectMany(x => NotesOf(x.Page).Roots)
            .Where(n => Matches(n, filter))
    ];

    public bool Matches(NoteBlock note, NoteFilter filter)
    {
        if (filter.HideUsed && note.IsUsed) return false;
        if (filter.From is null && filter.To is null) return true;
        return note.Date is { } date
               && (filter.From is not { } from || date >= from)
               && (filter.To is not { } to || date <= to);
    }

    private static bool IsNotePage(Page page) => !Book.IsBook(page);

    private PageNotes NotesOf(Page page)
    {
        lock (_gate)
        {
            if (!_cache.TryGetValue(page, out var notes))
            {
                _cache[page] = notes = new PageNotes(page, DateOf(page));
            }
            return notes;
        }
    }

    private void Invalidate(Page page)
    {
        lock (_gate)
        {
            _cache.Remove(page);
        }
    }

    /// <summary>Drops views of pages that left the vault (<see cref="IVault.RemovePage"/> raises no event).</summary>
    private void DropStale(IReadOnlyList<Page> pages)
    {
        lock (_gate)
        {
            if (_cache.Count == 0) return;
            var live = pages.ToHashSet();
            foreach (var stale in _cache.Keys.Where(p => !live.Contains(p)).ToList())
            {
                _cache.Remove(stale);
            }
        }
    }

    /// <summary>The day from a <c>yyyy_MM_dd.md</c> file name directly inside a folder named <c>journals</c>.</summary>
    private static DateOnly? DateOf(Page page)
    {
        var folder = Path.GetFileName(Path.GetDirectoryName(page.FilePath));
        if (!string.Equals(folder, "journals", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(page.FilePath), ".md", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return DateOnly.TryParseExact(
            Path.GetFileNameWithoutExtension(page.FilePath), "yyyy_MM_dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    private sealed class PageNotes
    {
        public PageNotes(Page page, DateOnly? date)
        {
            All = [.. page.AllBlocks().Select(b => new NoteBlock(page, b, date))];
            Roots = [.. All.Where(n => n.Block.Parent is null)];
            ByKey = All.ToDictionary(n => n.Key);
        }

        public IReadOnlyList<NoteBlock> All { get; }

        public IReadOnlyList<NoteBlock> Roots { get; }

        public Dictionary<Guid, NoteBlock> ByKey { get; }
    }
}
