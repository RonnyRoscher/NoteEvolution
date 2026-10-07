namespace NoteEvolution.Core.Storage;

/// <summary>The page could not be parsed safely and is therefore never written.</summary>
public sealed class ReadOnlyPageException(string message) : InvalidOperationException(message);
