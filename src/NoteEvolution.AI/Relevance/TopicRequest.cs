using NoteEvolution.Core.Books;

namespace NoteEvolution.AI.Relevance;

/// <summary>
/// What the "Relevant" tab is about: the current book section, the text block at the cursor, a range of text blocks,
/// or the own text of one element. <paramref name="OwnText"/> wins over <paramref name="RangeTextBlockKeys"/>, which
/// wins over the cursor and the section.
/// </summary>
/// <param name="SectionKey"><see cref="OutlineNode.Key"/> of the section; <see cref="Guid.Empty"/> is the book's root.</param>
/// <param name="CursorTextBlockKey">The text block at the cursor; only used when <paramref name="Manuscript"/> is true.</param>
/// <param name="Manuscript">True when the cursor is in a text block: its neighbours in the manuscript define the topic.</param>
/// <param name="RangeTextBlockKeys">The text blocks of the marked range: their mean plus the heading path define the topic. Empty or unknown keys: the path alone.</param>
/// <param name="OwnText">The text of one element (for example a sub-bullet): it plus the heading path define the topic.</param>
public record TopicRequest(
    Book Book,
    Guid SectionKey,
    Guid? CursorTextBlockKey,
    bool Manuscript,
    IReadOnlyList<Guid>? RangeTextBlockKeys = null,
    string? OwnText = null);

/// <summary>A book section that fits a note; <paramref name="SectionKey"/> is <see cref="Guid.Empty"/> for the root.</summary>
public record SectionHit(Guid SectionKey, double Score);

public record PlacementHit(Guid TextBlockKey, double Score);
