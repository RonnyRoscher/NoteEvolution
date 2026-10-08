namespace NoteEvolution.UI.State;

/// <summary>
/// The editor's text could not be saved, so the vault is not closed (switching the AI model): nothing was changed.
/// </summary>
public sealed class UnsavedEditorTextException(Exception? inner = null)
    : InvalidOperationException("The editor's text could not be saved.", inner);
