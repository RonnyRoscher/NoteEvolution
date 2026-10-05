namespace NoteEvolution.Core.Storage;

/// <summary>
/// The file was changed by another program since the page was loaded or last saved; it is not overwritten.
/// The change is brought in with <see cref="ExternalChangeHandler.Handle"/>, then the page can be saved.
/// </summary>
public sealed class FileChangedExternallyException(string message) : IOException(message);
