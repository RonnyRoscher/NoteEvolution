using NoteEvolution.AI.Embeddings;
using NoteEvolution.AI.Search;
using NoteEvolution.AI.Semantic;
using NoteEvolution.Core.Books;

namespace NoteEvolution.AI.Relevance;

/// <summary>
/// Embeds the texts of the book on demand and compares them with the note vectors of the <see cref="ISemanticIndex"/>.
/// The vectors of book texts are cached in memory, keyed by the hash of the embedded text (the e5 prefix is part of
/// it), so an unchanged text is never embedded twice; at most <see cref="DefaultCacheCapacity"/> are kept, the oldest
/// leave first. Safe to call from several threads. It reads only <see cref="Book"/> snapshots and what the caller passes
/// in, never the vault model, which belongs to the UI thread.
/// </summary>
public sealed class RelevanceService : IRelevanceService
{
    public const int DefaultCacheCapacity = 5000;

    /// <summary>Stands in for the model id in <see cref="EmbeddingText.Hash"/>: one cache serves one embedder.</summary>
    private const string CacheKeyScope = "query";

    private readonly ISemanticIndex _index;
    private readonly int _cacheCapacity;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, float[]> _cache = [];
    private readonly Queue<string> _cacheOrder = new();

    public RelevanceService(ISemanticIndex index) : this(index, DefaultCacheCapacity)
    {
    }

    internal RelevanceService(ISemanticIndex index, int cacheCapacity)
    {
        _index = index;
        _cacheCapacity = cacheCapacity;
    }

    public bool IsAvailable => _index.IsReady;

    public IReadOnlyList<SearchHit> Relevant(TopicRequest topic, Func<Guid, bool> include, int limit = 30)
    {
        if (!IsAvailable || limit <= 0) return [];
        if (TopicVector(topic) is not { } vector || IsZero(vector)) return [];

        // Nearest applies the filter itself and returns best first.
        var hits = _index.Nearest(vector, limit, include);
        return [.. hits.Select(hit => new SearchHit(hit.Key, hit.Score))];
    }

    public IReadOnlyList<SectionHit> WhereTo(Guid noteKey, string noteText, Book book, int limit = 5)
    {
        if (!IsAvailable || limit <= 0) return [];
        if (NoteVector(noteKey, noteText) is not { } noteVector || IsZero(noteVector)) return [];

        var hits = new List<SectionHit>();
        foreach (var section in Sections(book))
        {
            if (section.Level == 0 && !section.TextBlocks.Any()) continue;
            if (SectionVector(book, section) is not { } vector) return [];
            if (IsZero(vector)) continue;
            hits.Add(new SectionHit(section.Key, VectorMath.Cosine(noteVector, vector)));
        }

        return [.. hits.OrderByDescending(hit => hit.Score).Take(limit)];
    }

    public IReadOnlyList<PlacementHit> Placements(Guid noteKey, string noteText, Book book, int limit = 3)
    {
        if (!IsAvailable || limit <= 0) return [];
        if (NoteVector(noteKey, noteText) is not { } noteVector || IsZero(noteVector)) return [];

        var hits = new List<PlacementHit>();
        foreach (var textBlock in Sections(book).SelectMany(section => section.TextBlocks))
        {
            var text = EmbeddingText.ForTextBlock(textBlock);
            if (IsBlank(text)) continue;
            if (Embed(text) is not { } vector) return [];
            if (IsZero(vector)) continue;
            hits.Add(new PlacementHit(textBlock.Key, VectorMath.Cosine(noteVector, vector)));
        }

        return [.. hits.OrderByDescending(hit => hit.Score).Take(limit)];
    }

    /// <summary>The topic's vector; null if it cannot be determined (unknown section, embedder failure).</summary>
    private float[]? TopicVector(TopicRequest topic)
    {
        if (topic.Manuscript && topic.CursorTextBlockKey is { } key && topic.Book.FindTextBlock(key) is { } cursor)
        {
            return Embed(EmbeddingText.ForCursor(topic.Book, cursor));
        }

        return topic.Book.FindNode(topic.SectionKey) is { } section ? SectionVector(topic.Book, section) : null;
    }

    /// <summary>
    /// Heading path plus the mean of the text blocks directly in the section (without sub-sections), normalized;
    /// null if the embedder failed.
    /// </summary>
    private float[]? SectionVector(Book book, OutlineNode section)
    {
        if (Embed(EmbeddingText.ForHeadingPath(book, section)) is not { } path) return null;

        var blocks = new List<float[]>();
        foreach (var textBlock in section.TextBlocks)
        {
            var text = EmbeddingText.ForTextBlock(textBlock);
            if (IsBlank(text)) continue;
            if (Embed(text) is not { } vector) return null;
            blocks.Add(vector);
        }

        if (blocks.Count == 0) return VectorMath.Normalize(path);

        var sum = VectorMath.Mean(blocks);
        for (var d = 0; d < sum.Length; d++) sum[d] += path[d];
        return VectorMath.Normalize(sum);
    }

    /// <summary>The note's indexed vector, else its freshly embedded text; null if the embedder failed.</summary>
    private float[]? NoteVector(Guid noteKey, string noteText) =>
        _index.VectorOf(noteKey) ?? _index.EmbedQuery(noteText);

    /// <summary>The embedding of a book text (prefix included), from the cache if possible; null if the embedder failed.</summary>
    private float[]? Embed(string text)
    {
        var key = EmbeddingText.Hash(CacheKeyScope, text);
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;
        }

        var vector = _index.EmbedQuery(text);
        if (vector is null) return null;

        lock (_gate)
        {
            if (_cache.TryAdd(key, vector))
            {
                _cacheOrder.Enqueue(key);
                while (_cacheOrder.Count > _cacheCapacity) _cache.Remove(_cacheOrder.Dequeue());
            }
        }

        return vector;
    }

    /// <summary>Every section of the book in outline order, the root first.</summary>
    private static IEnumerable<OutlineNode> Sections(Book book) => Walk(book.Root);

    private static IEnumerable<OutlineNode> Walk(OutlineNode node) => node.Children.SelectMany(Walk).Prepend(node);

    /// <summary>True for a text without content besides its prefix.</summary>
    private static bool IsBlank(string prefixedText) =>
        prefixedText.AsSpan(EmbeddingText.QueryPrefix.Length).IsWhiteSpace();

    private static bool IsZero(float[] vector) => !vector.Any(x => x != 0);
}
