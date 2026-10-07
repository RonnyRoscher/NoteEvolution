using NoteEvolution.AI.Search;
using NoteEvolution.Core.Books;

namespace NoteEvolution.AI.Relevance;

/// <summary>
/// Meaning-based suggestions that connect notes and the book. Every method returns an empty list when the semantic
/// index is not ready, the embedder fails or the topic has no meaning (a zero vector). May embed book texts, which is
/// slow the first time: call from a background task.
/// <para>
/// Threading: a <see cref="Book"/> is an immutable snapshot and may be read off the UI thread. The vault model
/// (<c>INoteRepository</c>, <c>NoteBlock</c>, <c>Vault</c>) is not thread-safe, so the service never touches it: the
/// caller captures everything it needs from there on the UI thread first (note text, the set of allowed note keys).
/// </para>
/// </summary>
public interface IRelevanceService
{
    /// <summary>Mirrors <see cref="Semantic.ISemanticIndex.IsReady"/>.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// The notes most similar to the topic whose key <paramref name="include"/> accepts, best first; Score is the cosine
    /// similarity. <paramref name="include"/> runs on the calling thread and must not touch the vault model: build it
    /// from a UI-thread snapshot, e.g. a set of the allowed note keys.
    /// </summary>
    IReadOnlyList<SearchHit> Relevant(TopicRequest topic, Func<Guid, bool> include, int limit = 30);

    /// <summary>
    /// The sections of <paramref name="book"/> that fit the note best, best first. The root counts only if it has text blocks.
    /// </summary>
    /// <param name="noteKey">The note's key; its indexed vector is used if there is one.</param>
    /// <param name="noteText"><c>EmbeddingText.ForNote(note)</c>, computed on the UI thread; embedded if the note is not indexed.</param>
    IReadOnlyList<SectionHit> WhereTo(Guid noteKey, string noteText, Book book, int limit = 5);

    /// <summary>The text blocks of <paramref name="book"/> that fit the note best, best first. Parameters as for <see cref="WhereTo"/>.</summary>
    IReadOnlyList<PlacementHit> Placements(Guid noteKey, string noteText, Book book, int limit = 3);
}
