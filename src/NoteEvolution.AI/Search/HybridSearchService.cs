using NoteEvolution.AI.Embeddings;
using NoteEvolution.AI.Semantic;
using NoteEvolution.Core.Vaults;

namespace NoteEvolution.AI.Search;

/// <summary>
/// Combines the full-text ranking and the semantic ranking of a search text with Reciprocal Rank Fusion
/// (score = sum of <c>1 / (60 + rank)</c> over both lists, rank starting at 1). Without a usable semantic index the
/// plain full-text hits are returned. <see cref="Search"/> reads the note repository (through the semantic filter),
/// so call it on the thread that owns the vault, like the full-text search.
/// </summary>
public sealed class HybridSearchService(ISearchService fullText, ISemanticIndex semantic, INoteRepository notes) : ISearchService
{
    private const int RrfK = 60;
    private const int CandidatesPerList = 100;

    public void Rebuild(INoteRepository repository) => fullText.Rebuild(repository);

    public void UpdatePage(INoteRepository repository, string pagePath) => fullText.UpdatePage(repository, pagePath);

    /// <summary>Best first; <see cref="SearchHit.Score"/> is the RRF sum. Ties are ordered by key.</summary>
    public IReadOnlyList<SearchHit> Search(SearchQuery query)
    {
        if (query.Limit <= 0) return [];
        if (FtsQuery.Build(query.Text) is null || !semantic.IsReady) return fullText.Search(query);

        var vector = semantic.EmbedQuery(EmbeddingText.ForSearch(query.Text));
        if (vector is null) return fullText.Search(query);

        var textHits = fullText.Search(query with { Limit = CandidatesPerList });
        var semanticHits = semantic.Nearest(
            vector, CandidatesPerList, key => notes.Get(key) is { } note && notes.Matches(note, query.Filter));

        var scores = new Dictionary<Guid, double>();
        AddRanks(scores, textHits.Select(h => h.NoteBlockKey));
        AddRanks(scores, semanticHits.Select(h => h.Key));

        return [.. scores
            .OrderByDescending(s => s.Value)
            .ThenBy(s => s.Key)
            .Take(query.Limit)
            .Select(s => new SearchHit(s.Key, s.Value))];
    }

    private static void AddRanks(Dictionary<Guid, double> scores, IEnumerable<Guid> keysBestFirst)
    {
        var rank = 1;
        foreach (var key in keysBestFirst)
        {
            scores[key] = scores.GetValueOrDefault(key) + 1.0 / (RrfK + rank);
            rank++;
        }
    }
}
