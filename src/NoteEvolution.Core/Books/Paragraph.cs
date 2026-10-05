using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Books;

/// <summary>A descendant block of a <see cref="TextBlock"/>; <paramref name="Depth"/> 1 is a direct child.</summary>
/// <param name="Block">The underlying block.</param>
/// <param name="Text">The block's content without the <c>#notiz</c> tag.</param>
/// <param name="Depth">Nesting below the text block; 1 is a direct child.</param>
/// <param name="IsNote">The block carries the <c>#notiz</c> tag.</param>
public sealed record Paragraph(Block Block, string Text, int Depth, bool IsNote);
