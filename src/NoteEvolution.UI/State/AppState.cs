using NoteEvolution.Core.Books;

namespace NoteEvolution.UI.State;

public enum ViewMode
{
    /// <summary>Only the current section, text blocks set apart.</summary>
    Section,

    /// <summary>The current part as continuous text with headings.</summary>
    Manuscript,
}

/// <summary>
/// What the user is looking at, shared by all components (a DI singleton). Whoever changes it calls
/// <see cref="Notify"/>; components subscribe to <see cref="Changed"/> (unsubscribing on dispose) and re-render via
/// <c>InvokeAsync</c>, since it may be raised from any thread.
/// </summary>
public sealed class AppState
{
    /// <summary>The open vault; <c>null</c> until one is opened.</summary>
    public VaultSession? Session { get; set; }

    public Book? CurrentBook { get; set; }

    /// <summary>The <see cref="OutlineNode.Key"/> of the selected outline node (<see cref="Guid.Empty"/> = book root).</summary>
    public Guid CurrentSectionKey { get; set; }

    public ViewMode Mode { get; set; }

    /// <summary>The text block the editor cursor is in; <c>null</c> if none.</summary>
    public Guid? CursorTextBlockKey { get; set; }

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

    public event Action? Changed;

    /// <summary>Someone asked for the AI model dialog (e.g. the settings' download button); the shell shows it.</summary>
    public event Action? AiModelDialogRequested;

    public void Notify() => Changed?.Invoke();

    /// <summary>Asks the shell to show the AI model dialog, which offers the download.</summary>
    public void RequestAiModelDialog() => AiModelDialogRequested?.Invoke();

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
            CursorTextBlockKey = null;
        }
    }
}
