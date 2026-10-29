using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Vaults;
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
public sealed class AppState
{
    private readonly TimeProvider _time;

    /// <summary>The back/forward history; locked, since the dwell timer records from another thread.</summary>
    private readonly NavigationHistory _history = new();

    /// <summary>The timer recording the cursor's element after the dwell; <c>null</c> while none runs.</summary>
    private ITimer? _dwell;

    /// <summary>Counts the dwells stopped, so a timer that fires after it was replaced records nothing.</summary>
    private long _dwellVersion;

    private readonly BookView _first = new();
    private BookView? _second;
    private BookView _active;
    private VaultSession? _session;

    private NoteSelection? _noteSelection;
    private Book? _currentBook;
    private readonly HashSet<Guid> _whereToOpenNotes = [];

    /// <summary>Another session was set: the next <see cref="CurrentBook"/> resets the inactive views, even for the same book.</summary>
    private bool _sessionChanged;

    /// <param name="time">The clock of the cursor dwell (<see cref="NavigationHistory.DwellTime"/>); <see cref="TimeProvider.System"/> if <c>null</c>.</param>
    public AppState(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _active = _first;
        Views = [_first];
    }

    /// <summary>The book views: one without a split, two with it (the upper and the lower half).</summary>
    public IReadOnlyList<BookView> Views { get; private set; }

    /// <summary>The view the position members (<see cref="Cursor"/>, <see cref="CurrentSectionKey"/> …) refer to.</summary>
    public BookView ActiveView => _active;

    /// <summary>Whether the book is shown in two halves.</summary>
    public bool IsSplit => _second is not null;

    /// <summary>
    /// Shows the book in two halves or in one. A new second view starts at the active view's section, without a cursor,
    /// and asks its editor to reveal that section. Unsplitting while the second view is active makes the first one
    /// active, with its own place (the cursor dwell restarts for it). Raises <see cref="Notify"/>. The caller saves the
    /// second editor's pending text before unsplitting.
    /// </summary>
    public void SetSplit(bool split)
    {
        if (split != IsSplit)
        {
            if (split)
            {
                _second = new BookView { CurrentSectionKey = _active.CurrentSectionKey };
                // Shown at that section, while the focus stays in the active half.
                _second.RequestReveal(_second.CurrentSectionKey, 0, focus: false);
                Views = [_first, _second];
            }
            else
            {
                var wasActive = ReferenceEquals(_active, _second);
                _second = null;
                Views = [_first];
                _active = _first;
                _session?.SetFocus(_currentBook, _active.CurrentSectionKey);
                if (wasActive)
                {
                    // A dwell started in the removed half must not record its position.
                    StartDwell();
                }
            }
        }

        Notify();
    }

    /// <summary>
    /// Makes <paramref name="view"/> the active one, after the previous active view's editor saved its pending text (an
    /// exception from that propagates and nothing changes); nothing happens if it is active already or was removed
    /// meanwhile. If that editor still holds text it could not save (<see cref="BookView.HasFailedEditorText"/>), the
    /// previous view stays active: typing in the other half would save a document without that text over it. The cursor
    /// dwell restarts for the new view's cursor. Raises <see cref="Notify"/> (also when the switch is refused, so the
    /// halves show why).
    /// </summary>
    public async Task ActivateAsync(BookView view)
    {
        if (ReferenceEquals(view, _active))
        {
            return;
        }

        if (!Views.Contains(view))
        {
            throw new ArgumentException("The view does not belong to this state.", nameof(view));
        }

        var previous = _active;
        if (previous.FlushEditor is { } flush)
        {
            await flush();
        }

        // The split may have been switched off while the editor saved.
        if (!Views.Contains(view))
        {
            return;
        }

        if (previous.HasFailedEditorText?.Invoke() == true)
        {
            Notify();
            return;
        }

        _active = view;
        _session?.SetFocus(_currentBook, _active.CurrentSectionKey);

        // A dwell started in the old half must not record a position of the new one.
        StartDwell();
        Notify();
    }

    /// <summary>
    /// <see cref="ActivateAsync"/> for the components of a half (its editor, its outline): <c>true</c> if
    /// <paramref name="view"/> is the active view afterwards (at once if it is already); <c>false</c> if it no longer
    /// belongs to <see cref="Views"/> (the split was switched off), was removed while the previous editor saved, or that
    /// save failed (an exception is logged to <paramref name="logger"/>; nothing changes, also while the previous editor
    /// still holds text it could not save).
    /// </summary>
    public async Task<bool> TryActivateAsync(BookView view, ILogger logger)
    {
        if (ReferenceEquals(view, _active))
        {
            return true;
        }

        if (!Views.Contains(view))
        {
            return false;
        }

        try
        {
            await ActivateAsync(view);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Saving the active editor before switching the book view failed");
            return false;
        }

        return ReferenceEquals(view, _active);
    }

    /// <summary>
    /// The open vault; <c>null</c> until one is opened. Another session puts the range back to level 0, clears the
    /// back/forward history and makes the next <see cref="CurrentBook"/> put every inactive view back to the start of the
    /// book (the same book of the new session holds other blocks). The session is told the current book and section
    /// (<see cref="CurrentBook"/>, <see cref="CurrentSectionKey"/>) whenever they change.
    /// </summary>
    public VaultSession? Session
    {
        get => _session;
        set
        {
            if (!ReferenceEquals(value, _session))
            {
                foreach (var view in Views)
                {
                    view.RangeLevel = 0;
                }

                _noteSelection = null;

                lock (_history)
                {
                    StopDwell();
                    _history.Clear();
                }

                _sessionChanged = true;
            }

            _session = value;
            _session?.SetFocus(_currentBook, _active.CurrentSectionKey);
        }
    }

    /// <summary>
    /// The book the editor shows. Another book (by link name; not the same book taken from the vault again, see
    /// <see cref="RefreshBook"/>) puts the range back to level 0 and every inactive view back to the start of the book;
    /// so does the first book set after another <see cref="Session"/>, even the same one.
    /// </summary>
    public Book? CurrentBook
    {
        get => _currentBook;
        set
        {
            var other = !string.Equals(value?.LinkName, _currentBook?.LinkName, StringComparison.OrdinalIgnoreCase);
            if (other)
            {
                RangeLevel = 0;
            }

            if (other || _sessionChanged)
            {
                foreach (var view in Views.Where(v => !ReferenceEquals(v, _active)))
                {
                    view.CurrentSectionKey = value?.Root.Key ?? Guid.Empty;
                    view.Cursor = null;
                    view.RangeLevel = 0;
                }
            }

            _sessionChanged = false;
            _currentBook = value;
            _session?.SetFocus(_currentBook, _active.CurrentSectionKey);
        }
    }

    /// <summary>
    /// The <see cref="OutlineNode.Key"/> of the current section (<see cref="Guid.Empty"/> = book root, the prologue): the
    /// heading the cursor element belongs to. Without a cursor in the book the last one stays.
    /// </summary>
    public Guid CurrentSectionKey
    {
        get => _active.CurrentSectionKey;
        set
        {
            _active.CurrentSectionKey = value;
            _session?.SetFocus(_currentBook, value);
        }
    }

    /// <summary>
    /// The element at the editor cursor as the editor reported it; <c>null</c> if the cursor is in none. Another element
    /// (another key, or none) puts the range back to level 0 and is recorded in the back/forward history once the cursor
    /// stayed there for <see cref="NavigationHistory.DwellTime"/>; a change of the offset alone keeps both.
    /// </summary>
    public CursorInfo? Cursor
    {
        get => _active.Cursor;
        set
        {
            var moved = value?.Key != _active.Cursor?.Key;
            _active.Cursor = value;
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
    public int RangeLevel
    {
        get => _active.RangeLevel;
        set => _active.RangeLevel = value;
    }

    /// <summary>The marked range of <see cref="RangeLevel"/> around <see cref="CurrentElement"/>; <c>null</c> without an element.</summary>
    public RangeInfo? CurrentRange => _active.RangeIn(CurrentBook);

    /// <summary>Whether the range can grow: there is one and it is not the whole book yet.</summary>
    public bool CanRangeUp => _active.CanRangeUp(CurrentBook);

    /// <summary>Whether the range can shrink: there is one and it is not at level −1.</summary>
    public bool CanRangeDown => _active.CanRangeDown(CurrentBook);

    /// <summary>The text block the editor cursor is in (also in one of its details); <c>null</c> on a heading or without a cursor.</summary>
    public Guid? CursorTextBlockKey => Cursor is { Kind: not ElementKind.Heading } c ? c.TextBlockKey ?? c.Key : null;

    /// <summary>
    /// The <see cref="Cursor"/>'s element in <see cref="CurrentBook"/>. A key the book does not have (a paragraph the
    /// book stored under another key after a save) falls back to the cursor's text block, else to the heading of
    /// <see cref="CurrentSectionKey"/> (the nearest heading before it); <c>null</c> without a cursor, or when neither
    /// is found (the start of the book).
    /// </summary>
    public BookElement? CurrentElement => _active.ElementIn(CurrentBook);

    /// <summary>
    /// The element the active editor is to scroll to and put the cursor at, until it has done so; see <see cref="BookView.PendingReveal"/>.
    /// </summary>
    public Guid? PendingReveal => _active.PendingReveal;

    /// <summary>Where in the text of <see cref="PendingReveal"/> the cursor goes, in characters as the editor shows them.</summary>
    public int PendingRevealOffset => _active.PendingRevealOffset;

    /// <summary>The note the notes pane should show (e.g. after a click on a source chip).</summary>
    public Guid? FocusedNoteKey { get; set; }

    /// <summary>
    /// The note block being dragged from the notes pane (the head of the active block's range, by the handle in its
    /// bar); <c>null</c> when no drag is running. Blazor cannot fill a drag's <c>dataTransfer</c> from C#, and the
    /// editor (the only JavaScript component) must not be told by script, so the handle records the block here on
    /// <c>dragstart</c> and clears it on <c>dragend</c>; the editor's drop handler reads it (the handle also carries
    /// the key in its <c>data-note-key</c> attribute).
    /// </summary>
    public Guid? DraggedNoteKey { get; set; }

    /// <summary>
    /// Raised when the usages of notes changed outside the session's page events (an adoption, a note page read again
    /// after a failed write): the notes pane reads its notes again, so a used note leaves a list that hides used ones.
    /// </summary>
    public event Action? NotesChanged;

    /// <summary>Raises <see cref="NotesChanged"/>.</summary>
    public void NotifyNotesChanged() => NotesChanged?.Invoke();

    /// <summary>How many note bars show each selection (see <see cref="ShowNoteBar"/>).</summary>
    private readonly Dictionary<NoteSelection, int> _noteBars = [];

    /// <summary>A note bar shows <paramref name="selection"/> (it was rendered for it).</summary>
    public void ShowNoteBar(NoteSelection selection) =>
        _noteBars[selection] = _noteBars.GetValueOrDefault(selection) + 1;

    /// <summary>
    /// A note bar no longer shows <paramref name="selection"/>; <c>true</c> if no bar shows it any more. The bar then
    /// lets the selection go after the rendering (<see cref="ClearNoteSelectionIfNotShown"/>): its block is not shown.
    /// </summary>
    public bool HideNoteBar(NoteSelection selection)
    {
        if (!_noteBars.TryGetValue(selection, out var count))
        {
            return true;
        }

        if (count > 1)
        {
            _noteBars[selection] = count - 1;
            return false;
        }

        _noteBars.Remove(selection);
        return true;
    }

    /// <summary>
    /// Clears the selection if it is still <paramref name="selection"/> and no note bar shows it (the block left the
    /// list, its card was closed); another card that shows it, rendered meanwhile, keeps it.
    /// </summary>
    public void ClearNoteSelectionIfNotShown(NoteSelection selection)
    {
        if (ActiveNote == selection && !_noteBars.ContainsKey(selection))
        {
            ClearNoteSelection();
        }
    }

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
    /// Saves the pending text of every editor in turn, <c>null</c> while no editor is shown: each one is tried even if one
    /// before failed, then the failure is rethrown (an <see cref="AggregateException"/> if several failed). An editor
    /// registers it with its <see cref="BookView"/>; setting it here sets the first view's. Whoever is about to change the
    /// book page behind the editors' back (adopting a note) awaits it first, so no typed text is lost and the editors'
    /// page is current.
    /// </summary>
    public Func<Task>? FlushEditor
    {
        get
        {
            // A snapshot: the callbacks registered now. A single one is returned as it is, so its owner can recognise it.
            var flushes = Views.Select(v => v.FlushEditor).OfType<Func<Task>>().ToList();
            return flushes.Count switch
            {
                0 => null,
                1 => flushes[0],
                _ => async () =>
                {
                    var failures = new List<Exception>();
                    foreach (var flush in flushes)
                    {
                        try
                        {
                            await flush();
                        }
                        catch (Exception ex)
                        {
                            failures.Add(ex);
                        }
                    }

                    if (failures.Count == 1)
                    {
                        ExceptionDispatchInfo.Throw(failures[0]);
                    }

                    if (failures.Count > 1)
                    {
                        throw new AggregateException(failures);
                    }
                },
            };
        }
        set => _first.FlushEditor = value;
    }

    /// <summary>
    /// Asked on the UI thread when the window is about to close, after <see cref="FlushEditor"/>: <c>true</c> if an
    /// editor still holds text it could not save, after telling the user so (every editor is asked); <c>null</c> while
    /// no editor is shown. Setting it sets the first view's.
    /// </summary>
    public Func<bool>? WarnUnsavedText
    {
        get => AskAll(v => v.WarnUnsavedText);
        set => _first.WarnUnsavedText = value;
    }

    /// <summary>
    /// Asked on the UI thread after <see cref="FlushEditor"/>: <c>true</c> if an editor still holds text it could not
    /// save (without telling the user, unlike <see cref="WarnUnsavedText"/>); <c>null</c> while no editor is shown.
    /// Setting it sets the first view's.
    /// </summary>
    public Func<bool>? HasUnsavedEditorText
    {
        get => AskAll(v => v.HasUnsavedEditorText);
        set => _first.HasUnsavedEditorText = value;
    }

    /// <summary>
    /// A callback asking every view's callback registered now (none skipped), <c>true</c> if any said so; <c>null</c> if
    /// no view has one; a single one is returned as it is.
    /// </summary>
    private Func<bool>? AskAll(Func<BookView, Func<bool>?> select)
    {
        var asks = Views.Select(select).OfType<Func<bool>>().ToList();
        return asks.Count switch
        {
            0 => null,
            1 => asks[0],
            _ => () => asks.Aggregate(false, (any, ask) => ask() | any),
        };
    }

    public event Action? Changed;

    /// <summary>An element is to be revealed in the active view's editor (see <see cref="RevealElement"/>); raised besides the view's own event.</summary>
    public event Action<Guid>? RevealRequested;

    /// <summary>A note is to be brought into view in the notes pane (see <see cref="RevealNote"/>).</summary>
    public event Action<Guid>? NoteRevealRequested;

    /// <summary>Someone asked for a tab of the notes pane to be shown (see <see cref="RequestNotesTab"/>).</summary>
    public event Action<NotesTab>? NotesTabRequested;

    /// <summary>Someone asked for the AI model dialog (e.g. the settings' download button); the shell shows it.</summary>
    public event Action? AiModelDialogRequested;

    /// <summary>
    /// Someone asked for the appendix dialog of a kind (see <see cref="RequestAppendixDialog"/>), optionally with one entry
    /// selected; the shell shows it.
    /// </summary>
    public event Action<AppendixKind, Guid?>? AppendixDialogRequested;

    /// <summary>
    /// Someone asked for the appendix dialog of a kind with a new entry (see <see cref="RequestNewAppendixEntry"/>); the
    /// shell shows it and calls the action with the new entry's block key once it is saved.
    /// </summary>
    public event Action<AppendixKind, Action<Guid>>? NewAppendixEntryRequested;

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
        if (_active.ChangeRange(CurrentBook, delta))
        {
            Notify();
        }
    }

    /// <summary>
    /// The active note block, kept apart from the book views (it is not per split half). Checked against the vault
    /// whenever it is read: when the note or the block is gone (after a reload, or an adoption that hid the note), the
    /// selection is cleared and <c>null</c> is returned. Another <see cref="Session"/> clears it too.
    /// </summary>
    public NoteSelection? ActiveNote
    {
        get
        {
            if (_noteSelection is { } selection && FindNote(selection) is null)
            {
                _noteSelection = null;
            }

            return _noteSelection;
        }
    }

    /// <summary>The marked range of the active note block (see <see cref="NoteRanges"/>); <c>null</c> without one.</summary>
    public NoteRange? ActiveNoteRange =>
        ActiveNote is { } selection && FindNote(selection) is { } note
            ? NoteRanges.Of(note, selection.BlockKey, selection.Level)
            : null;

    /// <summary>
    /// Makes <paramref name="blockKey"/> (the note <paramref name="noteKey"/> shows, or one of its sub-points) the active
    /// note block at level 0 and raises <see cref="Notify"/>; a block that is not in the note clears the selection.
    /// <paramref name="inFocusedCard"/> says the click was in the card on top of the notes pane, so only that card is
    /// framed when a list shows the same note too.
    /// </summary>
    public void SelectNoteBlock(Guid noteKey, Guid blockKey, bool inFocusedCard = false)
    {
        var selection = new NoteSelection(noteKey, blockKey, 0, inFocusedCard);
        if (FindNote(selection) is null)
        {
            ClearNoteSelection();
            return;
        }

        if (_noteSelection != selection)
        {
            _noteSelection = selection;
            Notify();
        }
    }

    /// <summary>Moves the active note block's range <paramref name="delta"/> levels up (positive) or down, within <see cref="NoteRanges.LevelBounds"/>.</summary>
    public void ChangeNoteRange(int delta)
    {
        if (ActiveNote is not { } selection || FindNote(selection) is not { } note)
        {
            return;
        }

        var (min, max) = NoteRanges.LevelBounds(note, selection.BlockKey);
        var level = Math.Clamp(selection.Level + delta, min, max);
        if (level != selection.Level)
        {
            _noteSelection = selection with { Level = level };
            Notify();
        }
    }

    /// <summary>Clears the active note block and raises <see cref="Notify"/> if there was one.</summary>
    public void ClearNoteSelection()
    {
        if (_noteSelection is not null)
        {
            _noteSelection = null;
            Notify();
        }
    }

    /// <summary>The note the selection points at, if it and its block are still in the vault.</summary>
    private NoteBlock? FindNote(NoteSelection selection) =>
        _session?.Notes.Get(selection.NoteKey) is { } note && NoteRanges.Contains(note, selection.BlockKey) ? note : null;

    /// <summary>Asks the shell to show the AI model dialog, which offers the download.</summary>
    public void RequestAiModelDialog() => AiModelDialogRequested?.Invoke();

    /// <summary>
    /// Asks the shell to show the appendix dialog for <paramref name="kind"/> (glossary, people or sources), with the entry
    /// <paramref name="entryKey"/> selected if given.
    /// </summary>
    public void RequestAppendixDialog(AppendixKind kind, Guid? entryKey = null) => AppendixDialogRequested?.Invoke(kind, entryKey);

    /// <summary>
    /// Asks the shell to show the appendix dialog for <paramref name="kind"/> with a new, empty entry (the „Neu anlegen …“
    /// of an insert picker); <paramref name="onCreated"/> gets the entry's block key after its first save.
    /// </summary>
    public void RequestNewAppendixEntry(AppendixKind kind, Action<Guid> onCreated) => NewAppendixEntryRequested?.Invoke(kind, onCreated);

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

        _active.RequestReveal(elementKey, offset);
        RevealRequested?.Invoke(elementKey);
    }

    /// <summary>Returns <see cref="PendingReveal"/> and clears it; called by the editor when it reveals the element.</summary>
    public Guid? TakePendingReveal() => _active.TakePendingReveal();

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
    /// makes <paramref name="book"/> the current book if it is another one (raising <see cref="BookSelected"/>, as
    /// <see cref="SelectBook"/> does), reveals the element with <paramref name="elementKey"/> (<see cref="RevealElement"/>)
    /// and records it. The caller calls <see cref="Notify"/>.
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
                BookSelected?.Invoke(book);
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
        var recorded = back && here is not null;
        NavEntry? entry;
        lock (_history)
        {
            StopDwell();
            if (recorded)
            {
                _history.Record(here!);
            }

            entry = back ? _history.Back(Exists) : _history.Forward(Exists);
        }

        if (entry is null || session.Vault.FindBook(entry.BookLink) is not { } book)
        {
            if (recorded)
            {
                // Nowhere to go, but the history changed (the active block was recorded): the header shows it.
                Notify();
            }

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

    /// <summary>
    /// Raised by the dwell timer (on any thread): records the entry unless the dwell was stopped meanwhile, and raises
    /// <see cref="Notify"/> only if that changed <see cref="CanGoBack"/> or <see cref="CanGoForward"/> (the header's
    /// buttons; a render for nothing would disturb typing).
    /// </summary>
    private void OnDwelled(long version, NavEntry entry)
    {
        bool changed;
        lock (_history)
        {
            if (version != _dwellVersion)
            {
                return;
            }

            var before = (CanGoBack, CanGoForward);
            StopDwell();
            _history.Record(entry);
            changed = before != (CanGoBack, CanGoForward);
        }

        if (changed)
        {
            Notify();
        }
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
        foreach (var view in Views)
        {
            if (CurrentBook?.FindNode(view.CurrentSectionKey) is not null)
            {
                continue;
            }

            var root = CurrentBook?.Root.Key ?? Guid.Empty;
            if (ReferenceEquals(view, _active))
            {
                CurrentSectionKey = root;
                Cursor = null;
            }
            else
            {
                view.CurrentSectionKey = root;
                view.Cursor = null;
            }
        }
    }
}
