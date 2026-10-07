namespace NoteEvolution.Core.Books;

/// <summary>A structural oddity found while loading a book; the file is never changed because of it.</summary>
public sealed record OutlineWarning(Guid BlockKey, string Message);
