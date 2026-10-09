using NoteEvolution.Core.Books;
using NoteEvolution.UI.Editor;

namespace NoteEvolution.UI.State;

/// <summary>
/// What the user is looking at, shared by all components (a DI singleton). Whoever changes it calls
/// <see cref="Notify"/>; components subscribe to <see cref="Changed"/> (unsubscribing on dispose) and re-render via
/// <c>InvokeAsync</c>, since it may be raised from any thread.
/// </summary>
public sealed class AppState
{
    private VaultSession? _session;
    private Book? _currentBook;
    private CursorInfo? _cursor;
    private readonly HashSet<Guid> _whereToOpenNotes = [];

    /// <summary>The open vault; <c>null</c> until one is opened. Another session puts the range back to level 0.</summary>
    public VaultSession? Session
    {
        get => _session;
        set
        {
            if (!ReferenceEquals(value, _session))
            {
                RangeLevel = 0;
            }

            _session = value;
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
        }
    }

    /// <summary>
    /// The <see cref="OutlineNode.Key"/> of the current section (<see cref="Guid.Empty"/> = book root, the prologue): the
    /// heading the cursor element belongs to. Without a cursor in the book the last one stays.
    /// </summary>
    public Guid CurrentSectionKey { get; set; }

    /// <summary>
    /// The element at the editor cursor as the editor reported it; <c>null</c> if the cursor is in none. Another element
    /// (another key, or none) puts the range back to level 0; a change of the offset alone keeps it.
    /// </summary>
    public CursorInfo? Cursor
    {
        get => _cursor;
        set
        {
            if (value?.Key != _cursor?.Key)
            {
                RangeLevel = 0;
            }

            _cursor = value;
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

    /// <summary>Someone asked for the AI model dialog (e.g. the settings' download button); the shell shows it.</summary>
    public event Action? AiModelDialogRequested;

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

    /// <summary>
    /// Asks the editor to scroll to the element of <see cref="CurrentBook"/> with <paramref name="elementKey"/> (a
    /// heading, text block or detail; <see cref="Guid.Empty"/> = the start of the book) and to put the cursor at its
    /// start. Its section becomes the current section and the cursor is taken to be there; an element the book does not
    /// have changes neither. The range goes back to level 0. The editor reveals it once it shows the book
    /// (<see cref="PendingReveal"/>). The caller calls <see cref="Notify"/>.
    /// </summary>
    public void RevealElement(Guid elementKey)
    {
        RangeLevel = 0;
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
                Cursor = new CursorInfo(element.Kind, element.Key, BookElements.TextBlockOf(book, element)?.Key, 0);
            }
        }

        PendingReveal = elementKey;
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
