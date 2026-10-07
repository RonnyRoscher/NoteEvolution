using NoteEvolution.Core.Books;

namespace NoteEvolution.AI.Relevance;

/// <summary>What the "Relevant" tab is about: a book section, or in the manuscript view the text block at the cursor.</summary>
/// <param name="SectionKey"><see cref="OutlineNode.Key"/> of the section; <see cref="Guid.Empty"/> is the book's root.</param>
/// <param name="CursorTextBlockKey">The text block at the cursor; only used when <paramref name="Manuscript"/> is true.</param>
/// <param name="Manuscript">True in the manuscript view, where the cursor's neighbours define the topic.</param>
public record TopicRequest(Book Book, Guid SectionKey, Guid? CursorTextBlockKey, bool Manuscript);

/// <summary>A book section that fits a note; <paramref name="SectionKey"/> is <see cref="Guid.Empty"/> for the root.</summary>
public record SectionHit(Guid SectionKey, double Score);

public record PlacementHit(Guid TextBlockKey, double Score);
