using NoteEvolution.AI.Search;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Vaults;

namespace NoteEvolution.AI.Relevance;

/// <summary>
/// Meaning-based suggestions that connect notes and the book. Every method returns an empty list when the semantic
/// index is not ready, the embedder fails or the topic has no meaning (a zero vector). May embed book texts, which is
/// slow the first time: call from a background task.
/// </summary>
public interface IRelevanceService
{
    /// <summary>Mirrors <see cref="Semantic.ISemanticIndex.IsReady"/>.</summary>
    bool IsAvailable { get; }

    /// <summary>The notes most similar to the topic that pass <paramref name="filter"/>, best first; Score is the cosine similarity.</summary>
    IReadOnlyList<SearchHit> Relevant(TopicRequest topic, NoteFilter filter, int limit = 30);

    /// <summary>The sections of <paramref name="book"/> that fit the note best, best first. The root counts only if it has text blocks.</summary>
    IReadOnlyList<SectionHit> WhereTo(NoteBlock note, Book book, int limit = 5);

    /// <summary>The text blocks of <paramref name="book"/> that fit the note best, best first.</summary>
    IReadOnlyList<PlacementHit> Placements(NoteBlock note, Book book, int limit = 3);
}
