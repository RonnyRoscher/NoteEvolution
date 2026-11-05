using Microsoft.Extensions.Logging;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Storage;
using NoteEvolution.Core.Vaults;

namespace NoteEvolution.UI.History;

/// <summary>
/// Decides when a version of a text block arises: compares the text blocks of a book page with their last known stand
/// when the page is saved (<see cref="OnSaving"/>, which may give a block its <c>id::</c>) and when it is replaced in the
/// vault (<see cref="OnReplaced"/>, which changes only blocks holding local text not saved yet: those get their
/// <c>id::</c> there, since the save that follows finds nothing changed). Headings, the appendix, note pages and read-only
/// pages are left out. Nothing here ever stops a book from being saved: every error is caught and logged, and sets
/// <see cref="HasProblem"/>. Safe to call from several threads.
/// </summary>
public sealed class BlockHistoryTracker(IVault vault, Func<HistoryStore?> store, IClock clock, TimeProvider time, ILogger logger) : IDisposable
{
    /// <summary>How long a block must stay unchanged before its open stand becomes a version.</summary>
    public static readonly TimeSpan Pause = TimeSpan.FromMinutes(10);

    private const int ShortestDeletedStand = 40;
    private const int LargeDeletion = 200;
    private const int ShortestMergedText = 20;
    private const int LongestAfterText = 200;

    private readonly object _gate = new();

    /// <summary>The known blocks of each book page (full path), in reading order.</summary>
    private readonly Dictionary<string, List<Known>> _pages = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The id each block key had in this session: undoing a first change takes the <c>id::</c> away again, and the
    /// block gets it back by its key.
    /// </summary>
    private readonly Dictionary<Guid, Guid> _idOfKey = [];

    /// <summary>The ids noted as deleted in the store.</summary>
    private readonly HashSet<Guid> _deletedIds = [];

    /// <summary>The blocks deleted in this session; a new block with the same text is taken for one coming back.</summary>
    private readonly List<(Guid Id, BlockStand Stand)> _deletedNow = [];

    private ITimer? _timer;
    private bool _warnedDuplicate;
    private bool _problem;
    private bool _problemToRaise;
    private bool _disposed;

    /// <summary>A part of the history could not be stored in this session.</summary>
    public bool HasProblem
    {
        get
        {
            lock (_gate)
            {
                return _problem;
            }
        }
    }

    /// <summary><see cref="HasProblem"/> became true; raised once, on the thread that met the problem.</summary>
    public event Action? ProblemChanged;

    /// <summary>
    /// Takes the stands of all books as they are in the vault; a block whose text differs from its newest version
    /// (a crash, a change in Logseq while the app was closed) gets a version „Beim Öffnen“.
    /// </summary>
    public void Start() => Guarded("Reading the books for the history", () =>
    {
        if (store() is null)
        {
            Report(new InvalidOperationException("The history store could not be opened."), "Opening the history");
            return;
        }

        foreach (var book in vault.Books)
        {
            Observe(book.Page, mayWrite: false);
        }
    });

    /// <summary>The <paramref name="page"/> is about to be written; a block that changed gets its <c>id::</c> in it.</summary>
    public void OnSaving(Page page) => Guarded("Comparing a saved page with its history", () => Observe(page, mayWrite: true));

    /// <summary>The <paramref name="page"/> was saved or read again.</summary>
    public void OnReplaced(Page page) => Guarded("Comparing a replaced page with its history", () => Observe(page, mayWrite: false));

    /// <summary>Stores all open stands as versions „Beim Schließen“.</summary>
    public void Flush() => Guarded("Storing the open stands", () =>
    {
        foreach (var known in _pages.Values.SelectMany(blocks => blocks).Where(known => known.Open))
        {
            StoreOpen(known, HistoryReason.OnClose);
        }

        Reschedule();
    });

    /// <summary>Stores the block's current stand as a version „Vor dem Wiederherstellen“, unless the newest version has its text.</summary>
    public void SaveBeforeRestore(Guid blockId, string book) => Guarded("Storing the stand before a restore", () =>
    {
        var known = _pages.Values.SelectMany(blocks => blocks).FirstOrDefault(known => known.Id == blockId);
        var stand = vault.FindBook(book)?.FindTextBlockById(blockId) is { } text ? BlockStand.Of(text) : known?.Stand;
        if (stand is null)
        {
            return;
        }

        AddUnlessLatest(blockId, book, HistoryReason.BeforeRestore, stand);
        if (known is not null)
        {
            known.Stand = stand;
            known.Longest = stand;
            known.Open = false;
            Reschedule();
        }
    });

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>Runs <paramref name="action"/> under the lock; nothing it throws gets out.</summary>
    private void Guarded(string what, Action action)
    {
        bool raise;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                action();
            }
            catch (Exception ex)
            {
                Report(ex, what);
            }

            raise = _problemToRaise;
            _problemToRaise = false;
        }

        if (raise)
        {
            try
            {
                ProblemChanged?.Invoke();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Telling about a problem of the history failed");
            }
        }
    }

    private void Report(Exception ex, string what)
    {
        logger.LogError(ex, "{What} failed; the book is saved as usual", what);
        _problemToRaise |= !_problem;
        _problem = true;
    }

    private void Observe(Page page, bool mayWrite)
    {
        if (!Book.IsBook(page) || page.IsReadOnly || store() is null)
        {
            return;
        }

        var book = Book.Load(page);
        var firstLook = !_pages.TryGetValue(page.FilePath, out var before);
        if (firstLook)
        {
            NoteDeleted(book.LinkName);
        }

        var look = new Look(book.LinkName, firstLook, mayWrite, before ?? []);
        var texts = new Lazy<List<string>>(() => TextsOf(book));
        var now = new List<Known>();
        Known? previous = null;
        foreach (var text in TextBlocksOf(book.Root))
        {
            if (ObserveBlock(look, text) is not { } known)
            {
                continue;
            }

            known.Key = text.Key;
            known.Id = text.Block.Id ?? known.Id;
            known.Book = book.LinkName;
            known.SectionPath = SectionPathOf(text.Section);
            known.Previous = previous;
            if (known.Id is { } id)
            {
                _idOfKey[text.Key] = id;
            }

            // What is read has really been in the file; what a save has just added is only sure once the save is through.
            known.IdInFile |= text.Block.Id is not null && !mayWrite && !text.Block.IsDirty;
            now.Add(known);
            previous = known;
        }

        foreach (var gone in look.Before.Except(now))
        {
            ObserveGone(gone, book, texts, mayWrite);
        }

        _pages[page.FilePath] = now;
        Reschedule();
    }

    /// <summary>What one block looks like now against what was known; null for a block that does not count.</summary>
    private Known? ObserveBlock(Look look, TextBlock text)
    {
        var fileId = text.Block.Id;
        if (fileId is { } own && !look.Seen.Add(own))
        {
            WarnDuplicate(own);
            return null;
        }

        var stand = BlockStand.Of(text);
        Guid? lostId = fileId is null && _idOfKey.TryGetValue(text.Key, out var byKey) && look.Seen.Add(byKey) ? byKey : null;
        var known = look.Find(fileId ?? lostId, text.Key);

        if (look.FirstLook || (known is null && !look.MayWrite))
        {
            return FirstSight(look.Book, text, stand);
        }

        if (lostId is { } lost && look.MayWrite && SetId(text.Block, lost))
        {
            return Returned(look.Book, text, stand, known, lost);
        }

        if (fileId is { } back && _deletedIds.Contains(back))
        {
            ClearDeleted(back);
        }

        return known is null ? NewBlock(look.Book, text, stand) : Compare(known, text, stand, look.MayWrite);
    }

    /// <summary>A block seen for the first time, or without a recognizable past after reading a page: only a starting point.</summary>
    private Known FirstSight(string book, TextBlock text, BlockStand stand)
    {
        if (text.Block.Id is { } id)
        {
            if (_deletedIds.Contains(id))
            {
                ClearDeleted(id);
            }

            if (HasVersion(id))
            {
                AddUnlessLatest(id, book, HistoryReason.OnOpen, stand);
            }
        }

        return new Known(text.Key, stand) { Id = text.Block.Id };
    }

    /// <summary>
    /// A block that lost its <c>id::</c> (an undo) and has just got it back: its history goes on without a version, and
    /// it does not count as deleted.
    /// </summary>
    private Known Returned(string book, TextBlock text, BlockStand stand, Known? known, Guid id)
    {
        ClearDeleted(id);
        if (known is null)
        {
            return new Known(text.Key, stand) { Id = id, Book = book };
        }

        if (!stand.SameText(known.Stand))
        {
            known.Longest = stand;
            MakeOpen(known, stand);
        }

        known.Stand = stand;
        return known;
    }

    /// <summary>A block not known before, written in this session.</summary>
    private Known NewBlock(string book, TextBlock text, BlockStand stand)
    {
        var known = new Known(text.Key, stand) { Id = text.Block.Id, Book = book, Started = true };
        if (text.Block.Id is null && _deletedNow.FindIndex(deleted => deleted.Stand.SameText(stand)) is >= 0 and var index
            && !IsBlank(stand) && SetId(text.Block, _deletedNow[index].Id))
        {
            known.Id = _deletedNow[index].Id;
            ClearDeleted(_deletedNow[index].Id);
            return known;
        }

        if (!IsBlank(stand))
        {
            known.Id = TryEnsureId(text.Block);
            if (known.Id is not null)
            {
                MakeOpen(known, stand);
            }
        }

        return known;
    }

    /// <summary>A known block: its text is compared with the last stand; status and sources do not count.</summary>
    private Known? Compare(Known known, TextBlock text, BlockStand stand, bool mayWrite)
    {
        if (stand.SameText(known.Stand))
        {
            known.Stand = stand;
            return known;
        }

        // A dirty block in a page just replaced holds local text merged into an external change (a block changed from
        // outside comes clean). The save that follows sees no change any more, so it gets its id here.
        var id = text.Block.Id ?? (mayWrite || text.Block.IsDirty ? TryEnsureId(text.Block) : null);
        if (id is not { } blockId)
        {
            // Read again without an id: the new text is only the starting point. What waited for the pause must not go with it.
            if (known.Open)
            {
                StoreOpen(known, HistoryReason.BeforeDelete);
            }

            known.Stand = stand;
            known.Longest = stand;
            known.Open = false;
            return known;
        }

        known.Id = blockId;
        if (IsBlank(known.Stand))
        {
            // An empty block typed into starts here; what is typed next is no first version of it.
            known.Started = true;
        }
        else if (!known.Started && !HasVersion(blockId))
        {
            Add(blockId, known.Book, HistoryReason.First, known.Stand);
        }

        if (IsLargeDeletion(known.Longest, stand))
        {
            AddUnlessLatest(blockId, known.Book, HistoryReason.BeforeDelete, known.Longest);
            known.Longest = stand;
        }
        else if (stand.Length >= known.Longest.Length)
        {
            known.Longest = stand;
        }

        MakeOpen(known, stand);
        return known;
    }

    /// <summary>A known block is no longer among the page's text blocks.</summary>
    private void ObserveGone(Known gone, Book book, Lazy<List<string>> texts, bool mayWrite)
    {
        if (IsBlank(gone.Stand))
        {
            // Emptied first and removed then: what can come back is the text it had; a block that never had one is not missed.
            gone.Open = false;
            if (LastTextOf(gone) is not { } last)
            {
                return;
            }

            gone.Stand = last;
        }

        // After reading a page a missing block without an id in the file may just have been changed in Logseq; unless the
        // file still has it and only the page, merged with local changes, does not.
        var unrecognizable = !mayWrite && (gone.Id is null || !gone.IdInFile) && !IsRemovedLocally(gone, book.Page);
        if (unrecognizable || IsStillOnPage(gone, book.Page) || IsMerged(gone, texts) || IsUndoneTitle(gone, book))
        {
            // Not deleted, but it is forgotten here: what waited for the pause must not go with it.
            if (gone.Open)
            {
                StoreOpen(gone, HistoryReason.BeforeDelete);
            }

            return;
        }

        var id = gone.Id ?? Guid.CreateVersion7();
        _idOfKey[gone.Key] = id;
        AddUnlessLatest(id, gone.Book, HistoryReason.BeforeDelete, gone.Stand);
        try
        {
            store()?.MarkDeleted(new DeletedBlock(id, gone.Book, clock.Now, gone.SectionPath, gone.Previous?.Id, gone.Stand, AfterTextOf(gone.Previous)));
            _deletedIds.Add(id);
            _deletedNow.Add((id, gone.Stand));
        }
        catch (Exception ex)
        {
            Report(ex, "Noting a deleted block");
        }
    }

    /// <summary>The newest stand of the block with some text, among its versions.</summary>
    private BlockStand? LastTextOf(Known gone)
    {
        if (gone.Id is not { } id)
        {
            return null;
        }

        try
        {
            return store()?.Versions(id).FirstOrDefault(version => !IsBlank(version.Stand))?.Stand;
        }
        catch (Exception ex)
        {
            Report(ex, "Reading the versions of a removed block");
            return null;
        }
    }

    /// <summary>The start of the predecessor's own text; a pair of surrogates is not cut in two (the store could not keep half of it).</summary>
    private static string? AfterTextOf(Known? previous)
    {
        if (previous is null || string.IsNullOrWhiteSpace(previous.Stand.Text))
        {
            return null;
        }

        var text = previous.Stand.Text;
        if (text.Length <= LongestAfterText)
        {
            return text;
        }

        return text[..(char.IsHighSurrogate(text[LongestAfterText - 1]) ? LongestAfterText - 1 : LongestAfterText)];
    }

    /// <summary>A block with this text was in the file the page was last read from and is not on the page.</summary>
    private static bool IsRemovedLocally(Known gone, Page page) =>
        page.RemovedSinceSaved().Any(block => block.Content == gone.Stand.Text);

    /// <summary>The block is still on the page as something else: a heading now, or a sub-point.</summary>
    private static bool IsStillOnPage(Known gone, Page page) =>
        page.AllBlocks().Any(block => block.Key == gone.Key || (gone.Id is not null && block.Id == gone.Id));

    /// <summary>
    /// The block went into others: its text, long enough not to stand there by chance, is in another text block or
    /// sub-point, and its sub-points are on the page too.
    /// </summary>
    private static bool IsMerged(Known gone, Lazy<List<string>> texts) =>
        gone.Stand.Text.Length >= ShortestMergedText
        && Holds(texts.Value, gone.Stand.Text)
        && gone.Stand.Paragraphs.All(paragraph => string.IsNullOrWhiteSpace(paragraph.Text) || Holds(texts.Value, paragraph.Text));

    /// <summary>The texts of all text blocks and sub-points of the book's text.</summary>
    private static List<string> TextsOf(Book book) =>
        [.. TextBlocksOf(book.Root).SelectMany(block => block.Paragraphs.Select(paragraph => paragraph.Text).Prepend(block.Text))];

    private static bool Holds(List<string> texts, string text) => texts.Any(other => other.Contains(text, StringComparison.Ordinal));

    /// <summary>
    /// The text block made from a heading's title in this session, gone again because the removal of the heading was
    /// undone: the heading's title is back, exactly its text.
    /// </summary>
    private static bool IsUndoneTitle(Known gone, Book book) =>
        gone.Started && gone.Stand.Paragraphs.Count == 0 && HeadingsOf(book.Root).Any(heading => heading.Title == gone.Stand.Text);

    private static IEnumerable<OutlineNode> HeadingsOf(OutlineNode section) =>
        section.Children.SelectMany(child => HeadingsOf(child).Prepend(child));

    private static bool IsLargeDeletion(BlockStand longest, BlockStand stand)
    {
        var shorter = longest.Length - stand.Length;
        return longest.Length >= ShortestDeletedStand && (shorter >= LargeDeletion || shorter * 4 >= longest.Length);
    }

    private static bool IsBlank(BlockStand stand) =>
        string.IsNullOrWhiteSpace(stand.Text) && stand.Paragraphs.All(paragraph => string.IsNullOrWhiteSpace(paragraph.Text));

    /// <summary>The text blocks of the book's text in reading order, without the appendix sections.</summary>
    private static IEnumerable<TextBlock> TextBlocksOf(OutlineNode section) => section.Items.SelectMany(item => item switch
    {
        OutlineNode { AppendixKind: not AppendixKind.None } => [],
        OutlineNode node => TextBlocksOf(node),
        TextBlock text => [text],
        _ => [],
    });

    private static string SectionPathOf(OutlineNode section)
    {
        var titles = new List<string>();
        for (var node = section; node.Parent is not null; node = node.Parent)
        {
            titles.Insert(0, node.Title);
        }

        return string.Join(ManuscriptEditor.SectionPathSeparator, titles);
    }

    private void MakeOpen(Known known, BlockStand stand)
    {
        known.Stand = stand;
        known.Open = true;
        known.Due = time.GetUtcNow() + Pause;
    }

    private void StoreOpen(Known known, HistoryReason reason)
    {
        known.Open = false;
        known.Longest = known.Stand;
        if (known.Id is { } id)
        {
            AddUnlessLatest(id, known.Book, reason, known.Stand);
        }
    }

    /// <summary>Sets the timer to the earliest open stand; there is one timer for all blocks.</summary>
    private void Reschedule()
    {
        var open = _pages.Values.SelectMany(blocks => blocks).Where(known => known.Open).ToList();
        if (open.Count == 0)
        {
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        var delay = open.Min(known => known.Due) - time.GetUtcNow();
        delay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        if (_timer is null)
        {
            _timer = time.CreateTimer(_ => OnTimer(), null, delay, Timeout.InfiniteTimeSpan);
        }
        else
        {
            _timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnTimer() => Guarded("Storing the stands after a pause", () =>
    {
        var now = time.GetUtcNow();
        foreach (var known in _pages.Values.SelectMany(blocks => blocks).Where(known => known.Open && known.Due <= now))
        {
            StoreOpen(known, HistoryReason.Pause);
        }

        Reschedule();
    });

    private void WarnDuplicate(Guid id)
    {
        if (!_warnedDuplicate)
        {
            _warnedDuplicate = true;
            logger.LogWarning("The id {Id} stands on several text blocks; the history stays with the first of them", id);
        }
    }

    /// <summary>Gives the block its id; a block that has no place for properties stays without one.</summary>
    private bool SetId(Block block, Guid id)
    {
        try
        {
            block.SetProperty("id", id.ToString("D"));
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            logger.LogWarning(ex, "A text block could not get its id back; it has no history for now");
            return false;
        }
    }

    private Guid? TryEnsureId(Block block)
    {
        try
        {
            return block.EnsureId();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            logger.LogWarning(ex, "A text block could not get an id; it has no history for now");
            return null;
        }
    }

    private void NoteDeleted(string book)
    {
        try
        {
            if (store() is { } history)
            {
                _deletedIds.UnionWith(history.Deleted(book).Select(deleted => deleted.BlockId));
            }
        }
        catch (Exception ex)
        {
            Report(ex, "Reading the deleted blocks");
        }
    }

    private void ClearDeleted(Guid id)
    {
        _deletedNow.RemoveAll(deleted => deleted.Id == id);
        if (_deletedIds.Remove(id))
        {
            try
            {
                store()?.ClearDeleted(id);
            }
            catch (Exception ex)
            {
                Report(ex, "Taking a block off the deleted ones");
            }
        }
    }

    private bool HasVersion(Guid id)
    {
        try
        {
            return store()?.Latest(id) is not null;
        }
        catch (Exception ex)
        {
            Report(ex, "Reading the newest version of a block");
            return true; // nothing is added on a guess
        }
    }

    private void Add(Guid id, string book, HistoryReason reason, BlockStand stand)
    {
        try
        {
            store()?.Add(id, book, clock.Now, reason, stand);
        }
        catch (Exception ex)
        {
            Report(ex, "Storing a version");
        }
    }

    private void AddUnlessLatest(Guid id, string book, HistoryReason reason, BlockStand stand)
    {
        try
        {
            if (store() is { } history && history.Latest(id)?.Stand.SameText(stand) != true)
            {
                history.Add(id, book, clock.Now, reason, stand);
            }
        }
        catch (Exception ex)
        {
            Report(ex, "Storing a version");
        }
    }

    /// <summary>One comparison of a page with what was known of it.</summary>
    private sealed class Look(string book, bool firstLook, bool mayWrite, List<Known> before)
    {
        private readonly Dictionary<Guid, Known> _byId = before.Where(known => known.Id is not null).DistinctBy(known => known.Id).ToDictionary(known => known.Id!.Value);
        private readonly Dictionary<Guid, Known> _byKey = before.DistinctBy(known => known.Key).ToDictionary(known => known.Key);

        public string Book => book;

        public bool FirstLook => firstLook;

        public bool MayWrite => mayWrite;

        public List<Known> Before => before;

        /// <summary>The ids met so far in reading order.</summary>
        public HashSet<Guid> Seen { get; } = [];

        /// <summary>The known block with this id, else the one with this key that has no other id.</summary>
        public Known? Find(Guid? id, Guid key)
        {
            if (id is { } own && _byId.TryGetValue(own, out var known))
            {
                return known;
            }

            return _byKey.TryGetValue(key, out var byKey) && (byKey.Id is null || byKey.Id == id) ? byKey : null;
        }
    }

    /// <summary>What is known of one text block.</summary>
    private sealed class Known(Guid key, BlockStand stand)
    {
        public Guid Key { get; set; } = key;

        public Guid? Id { get; set; }

        /// <summary>The <c>id::</c> was seen in the page as read or saved, not only as about to be saved.</summary>
        public bool IdInFile { get; set; }

        /// <summary>The block began in this session, so there is no earlier text a first version would keep.</summary>
        public bool Started { get; set; }

        /// <summary>The last stand.</summary>
        public BlockStand Stand { get; set; } = stand;

        /// <summary>The longest stand since the last version.</summary>
        public BlockStand Longest { get; set; } = stand;

        /// <summary><see cref="Stand"/> waits for the pause to become a version.</summary>
        public bool Open { get; set; }

        public DateTimeOffset Due { get; set; }

        public string Book { get; set; } = "";

        public string SectionPath { get; set; } = "";

        /// <summary>The block before it in reading order, at the last look.</summary>
        public Known? Previous { get; set; }
    }
}
