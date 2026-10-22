using NoteEvolution.Core.Books;
using NoteEvolution.UI.Editor;

namespace NoteEvolution.UI.State;

/// <summary>The tabs of the notes pane that others can ask for (<see cref="AppState.RequestNotesTab"/>).</summary>
public enum NotesTab
{
    Relevant,
    Search,
    Journal,
    Coverage,
}

/// <summary>
/// What the user is looking at, shared by all components (a DI singleton). Whoever changes it calls
/// <see cref="Notify"/>; components subscribe to <see cref="Changed"/> (unsubscribing on dispose) and re-render via
/// <c>InvokeAsync</c>, since it may be raised from any thread.
/// </summary>
/// <param name="time">The clock of the cursor dwell (<see cref="NavigationHistory.DwellTime"/>); <see cref="TimeProvider.System"/> if <c>null</c>.</param>
public sealed class AppState(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The back/forward history; locked, since the dwell timer records from another thread.</summary>
    private readonly NavigationHistory _history = new();

    /// <summary>The timer recording the cursor's element after the dwell; <c>null</c> while none runs.</summary>
    private ITimer? _dwell;

    /// <summary>Counts the dwells stopped, so a timer that fires after it was replaced records nothing.</summary>
    private long _dwellVersion;

    private VaultSession? _session;
    private Book? _currentBook;
    private CursorInfo? _cursor;
    private Guid _currentSectionKey;
    private readonly HashSet<Guid> _whereToOpenNotes = [];

    /// <summary>
    /// The open vault; <c>null</c> until one is opened. Another session puts the range back to level 0 and clears the
    /// back/forward history. The session is told the current book and section (<see cref="CurrentBook"/>,
    /// <see cref="CurrentSectionKey"/>) whenever they change.
    /// </summary>
    public VaultSession? Session
    {
        get => _session;
        set
        {
            if (!ReferenceEquals(value, _session))
            {
                RangeLevel = 0;
                lock (_history)
                {
                    StopDwell();
                    _history.Clear();
                }
            }

            _session = value;
            _session?.SetFocus(_currentBook, _currentSectionKey);
        }
    }

    /// <summary>
    /// The book the editor shows. Another book (by link name; not the same book taken from the vault again, see
    /// <see cref="RefreshBook"/>) puts the range back to level 0.
    /// </summary>
    public Book? CurrentBook
    {
        get => _currentBook;
        set
        {
            if (!string.Equals(value?.LinkName, _currentBook?.LinkName, StringComparison.OrdinalIgnoreCase))
            {
                RangeLevel = 0;
            }

            _currentBook = value;
            _session?.SetFocus(_currentBook, _currentSectionKey);
        }
    }

    /// <summary>
    /// The <see cref="OutlineNode.Key"/> of the current section (<see cref="Guid.Empty"/> = book root, the prologue): the
    /// heading the cursor element belongs to. Without a cursor in the book the last one stays.
    /// </summary>
    public Guid CurrentSectionKey
    {
        get => _currentSectionKey;
        set
        {
            _currentSectionKey = value;
            _session?.SetFocus(_currentBook, _currentSectionKey);
        }
    }

    /// <summary>
    /// The element at the editor cursor as the editor reported it; <c>null</c> if the cursor is in none. Another element
    /// (another key, or none) puts the range back to level 0 and is recorded in the back/forward history once the cursor
    /// stayed there for <see cref="NavigationHistory.DwellTime"/>; a change of the offset alone keeps both.
    /// </summary>
    public CursorInfo? Cursor
    {
        get => _cursor;
        set
        {
            var moved = value?.Key != _cursor?.Key;
            if (moved)
            {
                RangeLevel = 0;
            }

            _cursor = value;
            if (moved)
            {
                StartDwell();
            }
        }
    }

    /// <summary>
    /// The level of the marked range around <see cref="CurrentElement"/> (package B, spec 2): 0 = the element with
    /// everything below it, −1 = the element alone, k &gt; 0 = its k-th ancestor; see <see cref="BookRanges"/>.
    /// </summary>
    public int RangeLevel { get; set; }

    /// <summary>The marked range of <see cref="RangeLevel"/> around <see cref="CurrentElement"/>; <c>null</c> without an element.</summary>
    public RangeInfo? CurrentRange =>
        CurrentBook is { } book && CurrentElement is { } element ? BookRanges.Of(book, element, RangeLevel) : null;

    /// <summary>Whether the range can grow: there is one and it is not the whole book yet.</summary>
    public bool CanRangeUp => RangeBounds() is { } bounds && bounds.Level < bounds.Max;

    /// <summary>Whether the range can shrink: there is one and it is not at level −1.</summary>
    public bool CanRangeDown => RangeBounds() is { Level: > -1 };

    /// <summary>
    /// <see cref="RangeLevel"/> clamped as <see cref="BookRanges.Of"/> clamps it, and the level of the whole book, around
    /// <see cref="CurrentElement"/> (without computing the range); <c>null</c> without an element.
    /// </summary>
    private (int Level, int Max)? RangeBounds()
    {
        if (CurrentBook is not { } book || CurrentElement is not { } element)
        {
            return null;
        }

        var max = BookRanges.MaxLevel(book, element);
        return (Math.Clamp(RangeLevel, -1, max), max);
    }

    /// <summary>The text block the editor cursor is in (also in one of its details); <c>null</c> on a heading or without a cursor.</summary>
    public Guid? CursorTextBlockKey => Cursor is { Kind: not ElementKind.Heading } c ? c.TextBlockKey ?? c.Key : null;

    /// <summary>
    /// The <see cref="Cursor"/>'s element in <see cref="CurrentBook"/>. A key the book does not have (a paragraph the
    /// book stored under another key after a save) falls back to the cursor's text block, else to the heading of
    /// <see cref="CurrentSectionKey"/> (the nearest heading before it); <c>null</c> without a cursor, or when neither
    /// is found (the start of the book).
    /// </summary>
    public BookElement? CurrentElement
    {
        get
        {
            if (Cursor is not { } cursor || CurrentBook is not { } book)
            {
                return null;
            }

            return BookElements.Find(book, cursor.Key)
                   ?? (cursor.TextBlockKey is { } textBlock && book.FindTextBlock(textBlock) is not null
                       ? new BookElement(ElementKind.TextBlock, textBlock)
                       : null)
                   ?? (book.FindNode(CurrentSectionKey) is { Block: not null } heading
                       ? new BookElement(ElementKind.Heading, heading.Key)
                       : null);
        }
    }

    /// <summary>
    /// The element the editor is to scroll to and put the cursor at, until the editor has done so (after its next
    /// document load if the book is still being loaded); <c>null</c> if none.
    /// </summary>
    public Guid? PendingReveal { get; private set; }

    /// <summary>Where in the text of <see cref="PendingReveal"/> the cursor goes, in characters as the editor shows them.</summary>
    public int PendingRevealOffset { get; private set; }

    /// <summary>The note the notes pane should show (e.g. after a click on a source chip).</summary>
    public Guid? FocusedNoteKey { get; set; }

    /// <summary>
    /// The note card being dragged from the notes pane; <c>null</c> when no drag is running. Blazor cannot fill a
    /// drag's <c>dataTransfer</c> from C#, and the editor (the only JavaScript component) must not be told by
    /// script, so the card records its note here on <c>dragstart</c> and clears it on <c>dragend</c>; the editor's
    /// drop handler reads it (the card also carries the key in its <c>data-note-key</c> attribute).
    /// </summary>
    public Guid? DraggedNoteKey { get; set; }

    /// <summary>
    /// The note keys of the cards with an open "Wohin damit?" list (package B, spec 3: the list stays open). The tabs
    /// of the notes pane keep such a card at its place while they compute their lists again, even when the new list
    /// (its top N, or "Verwendete ausblenden" after adopting the note) would leave it out; see
    /// <see cref="OpenWhereTo"/> and <see cref="CloseWhereTo"/>.
    /// </summary>
    public IReadOnlySet<Guid> WhereToOpenNotes => _whereToOpenNotes;

    /// <summary>The card of <paramref name="noteKey"/> opened its first "Wohin damit?" list.</summary>
    public void OpenWhereTo(Guid noteKey) => _whereToOpenNotes.Add(noteKey);

    /// <summary>
    /// The card of <paramref name="noteKey"/> closed its last "Wohin damit?" list (or is gone); <c>true</c> if it had
    /// one open. Whoever closes it calls <see cref="Notify"/>, so the tabs can let the card go.
    /// </summary>
    public bool CloseWhereTo(Guid noteKey) => _whereToOpenNotes.Remove(noteKey);

    /// <summary>
    /// Saves the editor's pending text, <c>null</c> while no editor is shown. The editor registers it; whoever is
    /// about to change the book page behind the editor's back (adopting a note) awaits it first, so no typed text
    /// is lost and the editor's page is current.
    /// </summary>
    public Func<Task>? FlushEditor { get; set; }

    /// <summary>
    /// Asked on the UI thread when the window is about to close, after <see cref="FlushEditor"/>: <c>true</c> if the
    /// editor still holds text it could not save, after telling the user so; <c>null</c> while no editor is shown.
    /// </summary>
    public Func<bool>? WarnUnsavedText { get; set; }

    /// <summary>
    /// Asked on the UI thread after <see cref="FlushEditor"/>: <c>true</c> if the editor still holds text it could not
    /// save (without telling the user, unlike <see cref="WarnUnsavedText"/>); <c>null</c> while no editor is shown.
    /// </summary>
    public Func<bool>? HasUnsavedEditorText { get; set; }

    public event Action? Changed;

    /// <summary>An element is to be revealed in the editor (see <see cref="RevealElement"/>).</summary>
    public event Action<Guid>? RevealRequested;

    /// <summary>A note is to be brought into view in the notes pane (see <see cref="RevealNote"/>).</summary>
    public event Action<Guid>? NoteRevealRequested;

    /// <summary>Someone asked for a tab of the notes pane to be shown (see <see cref="RequestNotesTab"/>).</summary>
    public event Action<NotesTab>? NotesTabRequested;

    /// <summary>Someone asked for the AI model dialog (e.g. the settings' download button); the shell shows it.</summary>
    public event Action? AiModelDialogRequested;

    /// <summary>
    /// The user chose another book (<see cref="SelectBook"/>, or back/forward into another book); the shell remembers it
    /// for the next start.
    /// </summary>
    public event Action<Book>? BookSelected;

    public void Notify() => Changed?.Invoke();

    /// <summary>
    /// Moves the range <paramref name="delta"/> levels up (positive) or down, clamped to −1 and the whole book, and
    /// raises <see cref="Notify"/>; without an element (<see cref="CurrentRange"/> is <c>null</c>) nothing happens.
    /// </summary>
    public void ChangeRange(int delta)
    {
        if (CurrentBook is not { } book || CurrentElement is not { } element)
        {
            return;
        }

        RangeLevel = Math.Clamp(BookRanges.Of(book, element, RangeLevel).Level + delta, -1, BookRanges.MaxLevel(book, element));
        Notify();
    }

    /// <summary>Asks the shell to show the AI model dialog, which offers the download.</summary>
    public void RequestAiModelDialog() => AiModelDialogRequested?.Invoke();

    /// <summary>
    /// Asks the notes pane to bring the note with the block id <paramref name="noteId"/> (the <c>id::</c> a
    /// <c>source::</c> refers to) into view, e.g. after a click on a source of the current section.
    /// </summary>
    public void RevealNote(Guid noteId) => NoteRevealRequested?.Invoke(noteId);

    /// <summary>Asks the notes pane to show <paramref name="tab"/>.</summary>
    public void RequestNotesTab(NotesTab tab) => NotesTabRequested?.Invoke(tab);

    /// <summary>
    /// Asks the editor to scroll to the element of <see cref="CurrentBook"/> with <paramref name="elementKey"/> (a
    /// heading, text block or detail; <see cref="Guid.Empty"/> = the start of the book) and to put the cursor at its
    /// start, or at <paramref name="offset"/> in its text as the editor shows it (the editor clamps it to the text). Its
    /// section becomes the current section and the cursor is taken to be there; an element the book does not have
    /// changes neither. The range goes back to level 0 unless <paramref name="keepRange"/>. The editor reveals it once it
    /// shows the book (<see cref="PendingReveal"/>). The caller calls <see cref="Notify"/>.
    /// </summary>
    public void RevealElement(Guid elementKey, int offset = 0, bool keepRange = false)
    {
        if (!keepRange)
        {
            RangeLevel = 0;
        }

        if (CurrentBook is { } book)
        {
            if (elementKey == Guid.Empty)
            {
                CurrentSectionKey = Guid.Empty;
                Cursor = null;
            }
            else if (BookElements.Find(book, elementKey) is { } element)
            {
                CurrentSectionKey = BookElements.SectionOf(book, element);
                Cursor = new CursorInfo(element.Kind, element.Key, BookElements.TextBlockOf(book, element)?.Key, offset);
            }
        }

        PendingReveal = elementKey;
        PendingRevealOffset = offset;
        RevealRequested?.Invoke(elementKey);
    }

    /// <summary>Returns <see cref="PendingReveal"/> and clears it; called by the editor when it reveals the element.</summary>
    public Guid? TakePendingReveal()
    {
        var pending = PendingReveal;
        PendingReveal = null;
        return pending;
    }

    /// <summary>
    /// The active block: <see cref="CurrentElement"/>, else the current section (the book root: the start of the book);
    /// <c>null</c> without a book.
    /// </summary>
    public NavEntry? Position =>
        CurrentBook is { } book ? new NavEntry(book.LinkName, CurrentElement?.Key ?? CurrentSectionKey) : null;

    /// <summary>
    /// Whether <see cref="GoBackAsync"/> can go somewhere: an earlier entry, or the active block is not the history's
    /// current entry (it was not stayed in long enough to be recorded), so back records it and returns to that entry.
    /// </summary>
    public bool CanGoBack
    {
        get
        {
            var position = Position;
            lock (_history)
            {
                return _history.CanGoBack || (_history.Current is { } current && position is not null && position != current);
            }
        }
    }

    /// <summary>Whether <see cref="GoForwardAsync"/> can go somewhere: there is a later entry.</summary>
    public bool CanGoForward
    {
        get
        {
            lock (_history)
            {
                return _history.CanGoForward;
            }
        }
    }

    /// <summary>Records <paramref name="entry"/> in the back/forward history at once (a jump), ending a running dwell.</summary>
    public void RecordJump(NavEntry entry)
    {
        lock (_history)
        {
            StopDwell();
            _history.Record(entry);
        }
    }

    /// <summary>
    /// A jump (spec 3: a click in the outline, in the recent changes, on a cross reference …): records the active block,
    /// makes <paramref name="book"/> the current book if it is another one, reveals the element with
    /// <paramref name="elementKey"/> (<see cref="RevealElement"/>) and records it. The caller calls <see cref="Notify"/>.
    /// </summary>
    public void JumpTo(Guid elementKey, Book? book = null)
    {
        RecordPosition();
        if (book is not null)
        {
            var other = !IsCurrentBook(book.LinkName);
            CurrentBook = book;
            if (other)
            {
                CurrentSectionKey = book.Root.Key;
                Cursor = null;
            }
        }

        RevealElement(elementKey);
        if (CurrentBook is { } current)
        {
            RecordJump(new NavEntry(current.LinkName, elementKey));
        }
    }

    /// <summary>
    /// The user chose <paramref name="book"/> (a jump): records the active block, shows the book from its start (root
    /// section, no cursor), records that and raises <see cref="BookSelected"/>. The caller calls <see cref="Notify"/>.
    /// </summary>
    public void SelectBook(Book book)
    {
        RecordPosition();
        CurrentBook = book;
        CurrentSectionKey = book.Root.Key;
        Cursor = null;
        RecordJump(new NavEntry(book.LinkName, book.Root.Key));
        BookSelected?.Invoke(book);
    }

    /// <summary>Goes back in the history (see <see cref="GoAsync"/>); the active block is recorded first, so forward returns to it.</summary>
    public Task GoBackAsync() => GoAsync(back: true);

    /// <summary>Goes forward in the history (see <see cref="GoAsync"/>).</summary>
    public Task GoForwardAsync() => GoAsync(back: false);

    /// <summary>
    /// Saves the editor's pending text (an exception from it propagates and nothing moves), then steps through the
    /// history to the nearest entry whose book and block still exist (the others are removed), selects its book if it
    /// is another one (<see cref="BookSelected"/>), reveals the block and raises <see cref="Notify"/>.
    /// </summary>
    private async Task GoAsync(bool back)
    {
        if (FlushEditor is { } flush)
        {
            await flush();
        }

        if (Session is not { } session)
        {
            return;
        }

        var here = Position;
        NavEntry? entry;
        lock (_history)
        {
            StopDwell();
            if (back && here is not null)
            {
                _history.Record(here);
            }

            entry = back ? _history.Back(Exists) : _history.Forward(Exists);
        }

        if (entry is null || session.Vault.FindBook(entry.BookLink) is not { } book)
        {
            return;
        }

        if (!IsCurrentBook(book.LinkName))
        {
            CurrentBook = book;
            CurrentSectionKey = book.Root.Key;
            Cursor = null;
            BookSelected?.Invoke(book);
        }

        RevealElement(entry.ElementKey);
        lock (_history)
        {
            // Revealing moved the cursor; the history stands at the entry already.
            StopDwell();
        }

        Notify();

        bool Exists(NavEntry e) =>
            session.Vault.FindBook(e.BookLink) is { } b
            && (e.ElementKey == b.Root.Key || BookElements.Find(b, e.ElementKey) is not null);
    }

    private bool IsCurrentBook(string linkName) =>
        string.Equals(CurrentBook?.LinkName, linkName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Records the active block (the origin of a jump), if there is one.</summary>
    private void RecordPosition()
    {
        if (Position is { } here)
        {
            RecordJump(here);
        }
    }

    /// <summary>Starts the dwell for the cursor's new element, stopping the previous one; none without a cursor.</summary>
    private void StartDwell()
    {
        var entry = Cursor is null ? null : Position;
        lock (_history)
        {
            StopDwell();
            if (entry is null)
            {
                return;
            }

            var version = _dwellVersion;
            _dwell = _time.CreateTimer(_ => OnDwelled(version, entry), null, NavigationHistory.DwellTime, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Raised by the dwell timer (on any thread): records the entry unless the dwell was stopped meanwhile.</summary>
    private void OnDwelled(long version, NavEntry entry)
    {
        lock (_history)
        {
            if (version != _dwellVersion)
            {
                return;
            }

            StopDwell();
            _history.Record(entry);
        }

        Notify();
    }

    /// <summary>Stops a running dwell; called under the history's lock.</summary>
    private void StopDwell()
    {
        _dwellVersion++;
        _dwell?.Dispose();
        _dwell = null;
    }

    /// <summary>
    /// Takes <see cref="CurrentBook"/> from the session's vault again, after its page was saved, reloaded or removed:
    /// the same book by link name, else the first book. Keeps the current section if the book still has it,
    /// otherwise selects the book root and clears the cursor.
    /// </summary>
    public void RefreshBook()
    {
        var books = Session?.Vault.Books ?? [];
        var book = CurrentBook is null ? null : Session?.Vault.FindBook(CurrentBook.LinkName);
        CurrentBook = book ?? books.FirstOrDefault();
        if (CurrentBook?.FindNode(CurrentSectionKey) is null)
        {
            CurrentSectionKey = CurrentBook?.Root.Key ?? Guid.Empty;
            Cursor = null;
        }
    }
}
