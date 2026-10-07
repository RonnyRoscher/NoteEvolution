using Microsoft.AspNetCore.Components;

namespace NoteEvolution.UI.Editor;

/// <summary>What the editor reports; implemented by the component that shows it (<c>EditorPane</c>).</summary>
public interface IEditorCallbacks
{
    /// <summary>The document changed; <paramref name="docJson"/> is the whole document (see <see cref="EditorDocMapper"/>).</summary>
    Task OnDocumentChanged(string docJson);

    /// <summary>The cursor moved into another text block; <c>null</c> outside text blocks.</summary>
    Task OnCursorBlockChanged(Guid? textBlockKey);

    /// <summary>A source chip was clicked; <paramref name="noteId"/> is the note's <c>id::</c>.</summary>
    Task OnChipClicked(Guid noteId);

    /// <summary>The × of a source chip was clicked.</summary>
    Task OnChipRemoved(Guid textBlockKey, Guid noteId);

    /// <summary>
    /// A note card was dropped into the editor. <paramref name="afterTextBlockKey"/> is the key of the text block the
    /// drop point follows, or of the heading it directly follows (manuscript view: the start of that section), or
    /// <c>null</c> for the start of the shown section.
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
    /// undo history starts anew). <paramref name="manuscript"/> selects the manuscript view's look,
    /// <paramref name="showChips"/> shows the source chips there too (the section view always shows them).
    /// </summary>
    Task SetDocumentAsync(string docJson, bool manuscript, bool showChips);
}
