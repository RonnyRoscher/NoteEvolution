using Microsoft.AspNetCore.Components;
using NoteEvolution.Core.Books;

namespace NoteEvolution.UI.Editor;

/// <summary>The element at the editor cursor (spec 2, "Aktueller Abschnitt und Markierung").</summary>
/// <param name="Kind">Whether the cursor is in a heading's title, a text block's own text or a detail.</param>
/// <param name="Key">The key of the heading, the text block or the detail.</param>
/// <param name="TextBlockKey">The text block of a text block or detail; <c>null</c> for a heading.</param>
/// <param name="Offset">The cursor's character offset in the title or paragraph.</param>
public sealed record CursorInfo(ElementKind Kind, Guid Key, Guid? TextBlockKey, int Offset);

/// <summary>
/// The structure commands of the current element (spec 3) and the commands of the range (package B, spec 2); the
/// editor sends all but <see cref="Delete"/>, <see cref="Merge"/> and <see cref="Wrap"/> as shortcuts.
/// </summary>
public enum SectionCommand
{
    /// <summary>Alt+Enter: a new empty element after the current one.</summary>
    InsertAfter,

    /// <summary>Alt+Shift+Enter: a new empty first child of the current element.</summary>
    InsertChild,

    /// <summary>Tab in a heading: one level deeper.</summary>
    Indent,

    /// <summary>Shift+Tab in a heading: one level higher.</summary>
    Outdent,

    /// <summary>Backspace at the start of a heading's title: the heading goes, its content stays.</summary>
    RemoveHeading,

    /// <summary>
    /// Button only: the element with everything below it (sub-sections, details). At range level −1, which marks the
    /// element alone, it is only possible on an element without sub-elements.
    /// </summary>
    Delete,

    /// <summary>Alt+Up: the range one level higher.</summary>
    RangeUp,

    /// <summary>Alt+Down: the range one level lower.</summary>
    RangeDown,

    /// <summary>Button only: merges the text blocks of the range per heading.</summary>
    Merge,

    /// <summary>Button only: puts the range under a new, empty heading.</summary>
    Wrap,
}

/// <summary>What the editor reports; implemented by the component that shows it (<c>EditorPane</c>).</summary>
public interface IEditorCallbacks
{
    /// <summary>The document changed; <paramref name="docJson"/> is the whole document (see <see cref="EditorDocMapper"/>).</summary>
    Task OnDocumentChanged(string docJson);

    /// <summary>The cursor moved to another element or offset; <c>null</c> when it is in no element.</summary>
    Task OnCursorChanged(CursorInfo? cursor);

    /// <summary>A structure command's shortcut was pressed for the element at the cursor.</summary>
    Task OnSectionCommand(SectionCommand command);

    /// <summary>
    /// The marking box moved (selection change, scrolling, resizing). <paramref name="barTop"/> is the box's bottom in
    /// px relative to <c>.ne-editor-pane</c>, held above the pane's visible bottom by the bar's height;
    /// <paramref name="visible"/> says whether the box is in the visible part of the pane.
    /// </summary>
    Task OnSectionBoxMoved(double barTop, bool visible);

    /// <summary>A source chip was clicked; <paramref name="noteId"/> is the note's <c>id::</c>.</summary>
    Task OnChipClicked(Guid noteId);

    /// <summary>The × of a source chip was clicked.</summary>
    Task OnChipRemoved(Guid textBlockKey, Guid noteId);

    /// <summary>
    /// A note card was dropped into the editor. <paramref name="afterTextBlockKey"/> is the key of the text block the
    /// drop point follows, or of the heading it directly follows (the start of that section), or <c>null</c> for the
    /// start of the document (before its first element).
    /// </summary>
    Task OnNoteDropped(Guid noteBlockKey, Guid? afterTextBlockKey);
}

/// <summary>The TipTap editor behind a narrow interface, so components can be tested without JavaScript.</summary>
public interface IEditorInterop : IAsyncDisposable
{
    /// <summary>Creates the editor inside <paramref name="host"/>; events go to <paramref name="callbacks"/>.</summary>
    Task InitAsync(ElementReference host, IEditorCallbacks callbacks);

    /// <summary>
    /// Replaces the shown document without raising <see cref="IEditorCallbacks.OnDocumentChanged"/> (the editor's
    /// undo history starts anew). <paramref name="showChips"/> shows the source chips below the text blocks. With
    /// <paramref name="keepCursor"/> (the same book again) the cursor stays near where it was and is reported if one
    /// had been reported for the previous document; otherwise (another book or session, the first document) the
    /// selection goes to the document's start without a report, so no cursor is invented before the user places one.
    /// </summary>
    Task SetDocumentAsync(string docJson, bool showChips, bool keepCursor);

    /// <summary>
    /// Puts the cursor at the start of the element's text, scrolls it into view and gives the editor the focus (unless
    /// a text field has it); <see cref="Guid.Empty"/> is the start of the book. An element the editor does not show is
    /// ignored.
    /// </summary>
    Task RevealAsync(Guid elementKey);

    /// <summary>
    /// Marks the nodes of the range (<see cref="RangeInfo.Nodes"/>); the editor draws a box per contiguous piece and
    /// keeps the list for later documents, ignoring keys it does not show. An empty list removes the marking.
    /// </summary>
    Task SetMarkedAsync(IReadOnlyList<MarkedNode> nodes);
}
