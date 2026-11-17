namespace NoteEvolution.UI.Editor;

/// <summary>The "≈ n" mark of a text block: how many repetitions it is in, and its localized tooltip.</summary>
public sealed record RepetitionMark(int Count, string Title);
