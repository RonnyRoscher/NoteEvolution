using NoteEvolution.Core.Books;
using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Links;

/// <summary>
/// A detail of a linked book block without <c>source::</c> of its own (<paramref name="DetailKey"/>) and the sub-point of
/// a source note it was copied from (<paramref name="NoteBlockKey"/>); both are <see cref="Block.Key"/> values.
/// </summary>
public sealed record SubPointMatch(Book Book, Guid DetailKey, Guid NoteBlockKey);