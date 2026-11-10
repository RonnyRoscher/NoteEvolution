namespace NoteEvolution.Core.Books;

/// <summary>One reference of a block of a text block (the block itself or one of its details) to a source note.</summary>
/// <param name="BlockKey">The key of the referencing block in the book page.</param>
/// <param name="BlockId">The <c>id::</c> of the referencing block; <c>null</c> if it has none.</param>
/// <param name="NoteId">The block id of the referenced note.</param>
public sealed record SourceLink(Guid BlockKey, Guid? BlockId, Guid NoteId);
