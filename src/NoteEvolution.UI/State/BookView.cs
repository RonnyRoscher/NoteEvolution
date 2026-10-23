using NoteEvolution.Core.Books;
using NoteEvolution.UI.Editor;

namespace NoteEvolution.UI.State;

/// <summary>
/// One position in a book: the section, the editor cursor, the marked range and the pending reveal of one half of the
/// (possibly split) book view, plus the callbacks of the editor showing it. <see cref="AppState"/> holds one view, or two
/// with a split, and forwards its own position members to the active one. A view knows no book: the book is passed in.
/// </summary>
public sealed class BookView
{
    private CursorInfo? _cursor;

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
    /// The level of the marked range around the cursor element: 0 = the element with everything below it, −1 = the
    /// element alone, k &gt; 0 = its k-th ancestor; see <see cref="BookRanges"/>.
    /// </summary>
    public int RangeLevel { get; set; }

    /// <summary>
    /// The element the editor is to scroll to and put the cursor at, until the editor has done so (after its next
    /// document load if the book is still being loaded); <c>null</c> if none.
    /// </summary>
    public Guid? PendingReveal { get; private set; }

    /// <summary>Where in the text of <see cref="PendingReveal"/> the cursor goes, in characters as the editor shows them.</summary>
    public int PendingRevealOffset { get; private set; }

    /// <summary>Saves the editor's pending text, <c>null</c> while no editor is shown for this view.</summary>
    public Func<Task>? FlushEditor { get; set; }

    /// <summary>
    /// Asked on the UI thread when the window is about to close, after <see cref="FlushEditor"/>: <c>true</c> if the
    /// editor still holds text it could not save, after telling the user so; <c>null</c> while no editor is shown.
    /// </summary>
    public Func<bool>? WarnUnsavedText { get; set; }

    /// <summary>
    /// Asked on the UI thread after <see cref="FlushEditor"/>: <c>true</c> if the editor still holds text it could not
    /// save (without telling the user); <c>null</c> while no editor is shown.
    /// </summary>
    public Func<bool>? HasUnsavedEditorText { get; set; }

    /// <summary>An element is to be revealed in this view's editor (see <see cref="RequestReveal"/>).</summary>
    public event Action<Guid>? RevealRequested;

    /// <summary>Sets the pending reveal and raises <see cref="RevealRequested"/>.</summary>
    internal void RequestReveal(Guid elementKey, int offset)
    {
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
    /// The <see cref="Cursor"/>'s element in <paramref name="book"/>. A key the book does not have (a paragraph the
    /// book stored under another key after a save) falls back to the cursor's text block, else to the heading of
    /// <see cref="CurrentSectionKey"/> (the nearest heading before it); <c>null</c> without a cursor or book, or when
    /// neither is found (the start of the book).
    /// </summary>
    public BookElement? ElementIn(Book? book)
    {
        if (Cursor is not { } cursor || book is null)
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

    /// <summary>The marked range of <see cref="RangeLevel"/> around <see cref="ElementIn"/>; <c>null</c> without an element.</summary>
    public RangeInfo? RangeIn(Book? book) =>
        book is not null && ElementIn(book) is { } element ? BookRanges.Of(book, element, RangeLevel) : null;

    /// <summary>Whether the range in <paramref name="book"/> can grow: there is one and it is not the whole book yet.</summary>
    public bool CanRangeUp(Book? book) => RangeBounds(book) is { } bounds && bounds.Level < bounds.Max;

    /// <summary>Whether the range in <paramref name="book"/> can shrink: there is one and it is not at level −1.</summary>
    public bool CanRangeDown(Book? book) => RangeBounds(book) is { Level: > -1 };

    /// <summary>
    /// Moves the range <paramref name="delta"/> levels up (positive) or down, clamped to −1 and the whole book;
    /// <c>false</c> (nothing changed) without an element.
    /// </summary>
    public bool ChangeRange(Book? book, int delta)
    {
        if (book is null || ElementIn(book) is not { } element)
        {
            return false;
        }

        RangeLevel = Math.Clamp(BookRanges.Of(book, element, RangeLevel).Level + delta, -1, BookRanges.MaxLevel(book, element));
        return true;
    }

    /// <summary>
    /// <see cref="RangeLevel"/> clamped as <see cref="BookRanges.Of"/> clamps it, and the level of the whole book, around
    /// <see cref="ElementIn"/> (without computing the range); <c>null</c> without an element.
    /// </summary>
    private (int Level, int Max)? RangeBounds(Book? book)
    {
        if (book is null || ElementIn(book) is not { } element)
        {
            return null;
        }

        var max = BookRanges.MaxLevel(book, element);
        return (Math.Clamp(RangeLevel, -1, max), max);
    }
}
