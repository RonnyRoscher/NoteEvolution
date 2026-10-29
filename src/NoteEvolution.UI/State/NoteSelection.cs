namespace NoteEvolution.UI.State;

/// <summary>
/// The active note block: the note <paramref name="NoteKey"/> the card shows, the clicked block
/// <paramref name="BlockKey"/> (the note itself or one of its sub-points) and the range
/// <paramref name="Level"/> around it (see <c>NoteRanges</c>).
/// </summary>
public sealed record NoteSelection(Guid NoteKey, Guid BlockKey, int Level);
