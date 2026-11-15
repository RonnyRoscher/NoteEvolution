using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NoteEvolution.AI.Embeddings;
using NoteEvolution.AI.Semantic;
using NoteEvolution.Core.Vaults;

namespace NoteEvolution.AI.Search;

/// <summary>
/// The full-text hits (notes that contain the words) followed by the notes that do not contain them but are clearly
/// similar in meaning to the search text (<see cref="SearchHit.Similar"/>): at most <see cref="MaxSimilar"/>, with a
/// cosine similarity of at least <see cref="SimilarThreshold"/>. Without a usable semantic index the plain full-text
/// hits are returned. <see cref="Search"/> reads the note repository, so call it on the thread that owns the vault,
/// like the full-text search.
/// </summary>
public sealed class HybridSearchService(
    ISearchService fullText, ISemanticIndex semantic, INoteRepository notes, ILogger? logger = null) : ISearchService
{
    /// <summary>
    /// The least cosine similarity between the search text and a note for the note to count as similar. A first guess
    /// for the e5 models (unrelated texts lie around 0.75 to 0.80); the log line of each search shows the best scores.
    /// </summary>
    public const float SimilarThreshold = 0.84f;

    public const int MaxSimilar = 20;

    private const int SemanticCandidates = 100;

    /// <summary>Enough to name every note that contains the words, so none of them is listed as merely similar.</summary>
    private const int AllLiteral = 1_000_000;

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public void Rebuild(INoteRepository repository) => fullText.Rebuild(repository);

    public void UpdatePage(INoteRepository repository, string pagePath) => fullText.UpdatePage(repository, pagePath);

    /// <summary>
    /// The notes with the words in the order the query asks for, then the similar ones (newest first if the query asks
    /// for that, most similar first otherwise). <see cref="SearchQuery.Limit"/> limits the notes with the words.
    /// </summary>
    public IReadOnlyList<SearchHit> Search(SearchQuery query)
    {
        if (query.Limit <= 0) return [];
        var literal = fullText.Search(query);
        if (FtsQuery.Build(query.Text) is null || !semantic.IsReady) return literal;

        var vector = semantic.EmbedQuery(EmbeddingText.ForSearch(query.Text));
        if (vector is null) return literal;

        var withWords = (literal.Count < query.Limit ? literal : fullText.Search(query with { Limit = AllLiteral }))
            .Select(hit => hit.NoteBlockKey).ToHashSet();
        var nearest = semantic.Nearest(
            vector, SemanticCandidates,
            key => !withWords.Contains(key) && notes.Get(key) is { } note && notes.Matches(note, query.Filter));
        var similar = nearest.Where(hit => hit.Score >= SimilarThreshold).Take(MaxSimilar)
            .Select(hit => new SearchHit(hit.Key, hit.Score, Similar: true));
        if (query.NewestFirst)
        {
            similar = similar
                .OrderBy(hit => notes.Get(hit.NoteBlockKey)?.Date is null)
                .ThenByDescending(hit => notes.Get(hit.NoteBlockKey)?.Date)
                .ThenByDescending(hit => hit.Score);
        }

        List<SearchHit> hits = [.. literal, .. similar];
        _logger.LogInformation(
            "Search: {Literal} with the words, {Similar} similar (threshold {Threshold:F2}); best similarities without the words: {Scores}",
            literal.Count, hits.Count - literal.Count, SimilarThreshold,
            string.Join(' ', nearest.Take(5).Select(hit => hit.Score.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))));
        return hits;
    }
}
