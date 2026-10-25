using NoteEvolution.AI.Embeddings;
using NoteEvolution.AI.Search;
using NoteEvolution.AI.Semantic;
using NoteEvolution.AI.Understanding;
using NoteEvolution.Core.Books;

namespace NoteEvolution.AI.Relevance;

/// <summary>
/// Embeds the texts of the book on demand and compares them with the note vectors of the <see cref="ISemanticIndex"/>.
/// The vectors of book texts are cached in memory, keyed by the hash of the embedded text (the e5 prefix is part of
/// it), so an unchanged text is never embedded twice; at most <see cref="DefaultCacheCapacity"/> are kept, the oldest
/// leave first. With an <see cref="IUnderstanding"/> the language model's topic summaries and core statements take part
/// (two-stage <see cref="Placements"/>, summaries in <see cref="Relevant"/>, notes through core statement and text);
/// without one every method works as before. Safe to call from several threads. It reads only <see cref="Book"/>
/// snapshots and what the caller passes in, never the vault model, which belongs to the UI thread.
/// </summary>
public sealed class RelevanceService : IRelevanceService
{
    public const int DefaultCacheCapacity = 5000;

    /// <summary>Stands in for the model id in <see cref="EmbeddingText.Hash"/>: one cache serves one embedder.</summary>
    private const string CacheKeyScope = "query";

    /// <summary>How many topics the two-stage <see cref="Placements"/> searches in (spec 5.1).</summary>
    private const int PlacementTopics = 3;

    /// <summary>A heading scores at least this share of its best sub-heading (spec 5.1).</summary>
    private const double ChildTopicWeight = 0.9;

    /// <summary>Weight of the parent section's summary in the "Relevant" topic (spec 6).</summary>
    private const float ParentSummaryWeight = 0.25f;

    /// <summary>"Relevant" with core statements scores this many times the requested notes from each source.</summary>
    private const int CandidateFactor = 3;

    private readonly ISemanticIndex _index;
    private readonly int _cacheCapacity;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, float[]> _cache = [];
    private readonly Queue<string> _cacheOrder = new();

    private readonly IUnderstanding? _understanding;

    public RelevanceService(ISemanticIndex index, IUnderstanding? understanding = null)
        : this(index, DefaultCacheCapacity, understanding)
    {
    }

    internal RelevanceService(ISemanticIndex index, int cacheCapacity, IUnderstanding? understanding = null)
    {
        _index = index;
        _cacheCapacity = cacheCapacity;
        _understanding = understanding;
    }

    public bool IsAvailable => _index.IsReady;

    public IReadOnlyList<SearchHit> Relevant(TopicRequest topic, Func<Guid, bool> include, int limit = 30)
    {
        if (!IsAvailable || limit <= 0) return [];
        if (TopicVector(topic) is not { } vector || IsZero(vector)) return [];

        if (_understanding is not null) return RelevantByUnderstanding(_understanding, WithSummaries(topic, vector), include, limit);

        // Nearest applies the filter itself and returns best first.
        var hits = _index.Nearest(vector, limit, include);
        return [.. hits.Select(hit => new SearchHit(hit.Key, hit.Score))];
    }

    public IReadOnlyList<SearchHit> Sources(Book book, Guid textBlockKey, Func<Guid, bool> include, int limit = 5)
    {
        if (!IsAvailable || limit <= 0 || book.FindTextBlock(textBlockKey) is not { } textBlock) return [];
        var text = EmbeddingText.ForTextBlock(textBlock);
        if (IsBlank(text) || Embed(text) is not { } vector || IsZero(vector)) return [];

        return [.. _index.Nearest(vector, limit, include).Select(hit => new SearchHit(hit.Key, hit.Score))];
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

        IEnumerable<(TextBlock Block, Guid Topic)> candidates;
        if (_understanding is null)
        {
            candidates = Sections(book).SelectMany(section => section.TextBlocks).Select(tb => (tb, Guid.Empty));
        }
        else
        {
            if (ScoreTopics(book, noteVector) is not { } topics) return [];
            candidates = WithinTopics([.. topics.OrderByDescending(t => t.Score).Take(PlacementTopics).Select(t => t.Node)]);
        }

        var hits = new List<PlacementHit>();
        foreach (var (textBlock, topic) in candidates)
        {
            var text = EmbeddingText.ForTextBlock(textBlock);
            if (IsBlank(text)) continue;
            if (Embed(text) is not { } vector) return [];
            if (IsZero(vector)) continue;
            hits.Add(new PlacementHit(textBlock.Key, VectorMath.Cosine(noteVector, vector), topic));
        }

        return [.. hits.OrderByDescending(hit => hit.Score).Take(limit)];
    }

    public IReadOnlyList<TopicHit> Topics(Guid noteKey, string noteText, Book book, int limit = 3)
    {
        if (!IsAvailable || limit <= 0) return [];
        if (NoteVector(noteKey, noteText) is not { } noteVector || IsZero(noteVector)) return [];
        if (ScoreTopics(book, noteVector) is not { } topics) return [];

        return [.. topics.OrderByDescending(t => t.Score).Take(limit).Select(t => new TopicHit(t.Node.Key, t.Score, t.Summary))];
    }

    /// <summary>
    /// Every topic candidate with its score, children before their parents: the headings, compared through their summary
    /// vector if there is one, else through <see cref="SectionVector"/>; a heading scores at least
    /// <see cref="ChildTopicWeight"/> × its best sub-heading. The root counts with its own score only, and only if it has
    /// text blocks. Headings without meaning (zero vector) and no scored sub-heading are left out. Null if the embedder failed.
    /// </summary>
    private List<ScoredTopic>? ScoreTopics(Book book, float[] noteVector)
    {
        var topics = new List<ScoredTopic>();
        foreach (var child in book.ContentChildren)
        {
            if (!AddTopics(book, child, noteVector, topics, out _)) return null;
        }

        if (book.Root.TextBlocks.Any())
        {
            if (SectionVector(book, book.Root) is not { } rootVector) return null;
            if (!IsZero(rootVector)) topics.Add(new ScoredTopic(book.Root, VectorMath.Cosine(noteVector, rootVector), null));
        }

        return topics;
    }

    /// <summary>Scores the heading and its sub-headings (see <see cref="ScoreTopics"/>); false if the embedder failed.</summary>
    private bool AddTopics(Book book, OutlineNode heading, float[] noteVector, List<ScoredTopic> topics, out double? score)
    {
        score = null;
        double? bestChild = null;
        foreach (var child in heading.Children)
        {
            if (!AddTopics(book, child, noteVector, topics, out var childScore)) return false;
            if (childScore > (bestChild ?? double.NegativeInfinity)) bestChild = childScore;
        }

        string? summaryText = null;
        float[]? vector;
        if (_understanding?.Heading(book, heading) is { } summary && summary.Vector.Length == noteVector.Length)
        {
            summaryText = summary.Text;
            vector = summary.Vector;
        }
        else if ((vector = SectionVector(book, heading)) is null)
        {
            return false;
        }

        double? own = IsZero(vector) ? null : VectorMath.Cosine(noteVector, vector);
        score = (own, bestChild * ChildTopicWeight) switch
        {
            ({ } o, { } c) => Math.Max(o, c),
            (var o, var c) => o ?? c,
        };
        if (score is { } value) topics.Add(new ScoredTopic(heading, value, summaryText));
        return true;
    }

    /// <summary>
    /// The text blocks within the chosen topics (a heading's sub-sections included, the root's own blocks only), each once
    /// with the innermost chosen topic that holds it.
    /// </summary>
    private static IEnumerable<(TextBlock Block, Guid Topic)> WithinTopics(IReadOnlyList<OutlineNode> topics)
    {
        var order = new List<TextBlock>();
        var owner = new Dictionary<Guid, (Guid Topic, int Depth)>();
        foreach (var topic in topics)
        {
            var depth = Depth(topic);
            var blocks = topic.Level == 0 ? topic.TextBlocks : Walk(topic).SelectMany(section => section.TextBlocks);
            foreach (var block in blocks)
            {
                if (!owner.TryGetValue(block.Key, out var current)) order.Add(block);
                else if (current.Depth >= depth) continue;
                owner[block.Key] = (topic.Key, depth);
            }
        }

        return order.Select(block => (block, owner[block.Key].Topic));
    }

    private static int Depth(OutlineNode node)
    {
        var depth = 0;
        for (var parent = node.Parent; parent is not null; parent = parent.Parent) depth++;
        return depth;
    }

    /// <summary>
    /// The topic as "Relevant" sees it with the model's understanding: the normalized mean of the section's summary
    /// vector (if any) and today's topic vector, plus <see cref="ParentSummaryWeight"/> × the parent section's summary
    /// vector (if any), normalized.
    /// </summary>
    private float[] WithSummaries(TopicRequest topic, float[] vector)
    {
        if (topic.Book.FindNode(topic.SectionKey) is not { } section || section.Level == 0) return vector;

        var result = vector;
        if (SummaryVector(topic.Book, section, vector.Length) is { } own) result = VectorMath.Normalize(VectorMath.Mean([own, vector]));

        if (section.Parent is { Level: > 0 } parent && SummaryVector(topic.Book, parent, vector.Length) is { } up)
        {
            var sum = (float[])result.Clone();
            for (var d = 0; d < sum.Length; d++) sum[d] += ParentSummaryWeight * up[d];
            result = VectorMath.Normalize(sum);
        }

        return result;
    }

    private float[]? SummaryVector(Book book, OutlineNode heading, int dimensions) =>
        _understanding?.Heading(book, heading) is { } summary && summary.Vector.Length == dimensions ? summary.Vector : null;

    /// <summary>
    /// "Relevant" through core statements: the candidates are the <see cref="CandidateFactor"/> × <paramref name="limit"/>
    /// notes nearest by text and as many whose core statement fits best; each is scored through the mean of its core
    /// statement and its text (<see cref="WithCore"/>), the text alone without one.
    /// </summary>
    private List<SearchHit> RelevantByUnderstanding(IUnderstanding understanding, float[] topic, Func<Guid, bool> include, int limit)
    {
        var pool = CandidateFactor * limit;
        var cores = new Dictionary<Guid, float[]>();
        foreach (var (key, core) in understanding.NoteCores())
            if (core.Length == topic.Length) cores.TryAdd(key, core);

        var candidates = _index.Nearest(topic, pool, include).Select(hit => hit.Key).ToHashSet();
        candidates.UnionWith(cores.Select(core => (core.Key, Score: VectorMath.Cosine(topic, core.Value)))
            .OrderByDescending(core => core.Score).Select(core => core.Key).Where(include).Take(pool));

        var hits = new List<SearchHit>();
        foreach (var key in candidates)
        {
            // a note can leave the index between the calls
            if (_index.VectorOf(key) is not { } text || text.Length != topic.Length) continue;
            var vector = cores.TryGetValue(key, out var core) ? WithCore(text, core) : text;
            hits.Add(new SearchHit(key, VectorMath.Cosine(topic, vector)));
        }

        return [.. hits.OrderByDescending(hit => hit.Score).Take(limit)];
    }

    /// <summary>The note's vector with its core statement: the normalized mean of both (spec 5.3).</summary>
    private static float[] WithCore(float[] text, float[] core) => VectorMath.Normalize(VectorMath.Mean([core, text]));

    private sealed record ScoredTopic(OutlineNode Node, double Score, string? Summary);

    /// <summary>The topic's vector; null if it cannot be determined (unknown section, embedder failure).</summary>
    private float[]? TopicVector(TopicRequest topic)
    {
        if (topic.OwnText is { } ownText)
        {
            return topic.Book.FindNode(topic.SectionKey) is { } ownSection
                ? PathPlus(topic.Book, ownSection, [EmbeddingText.ForOwnText(ownText)])
                : null;
        }

        if (topic.RangeTextBlockKeys is { } keys)
        {
            return topic.Book.FindNode(topic.SectionKey) is { } rangeSection
                ? PathPlus(topic.Book, rangeSection, [.. keys.Select(topic.Book.FindTextBlock).OfType<TextBlock>().Select(EmbeddingText.ForTextBlock)])
                : null;
        }

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
    private float[]? SectionVector(Book book, OutlineNode section) =>
        PathPlus(book, section, [.. section.TextBlocks.Select(EmbeddingText.ForTextBlock)]);

    /// <summary>
    /// Heading path plus the mean of the given (prefixed) texts, normalized; blank texts are skipped, with none left the
    /// path alone. Null if the embedder failed.
    /// </summary>
    private float[]? PathPlus(Book book, OutlineNode section, IReadOnlyList<string> texts)
    {
        if (Embed(EmbeddingText.ForHeadingPath(book, section)) is not { } path) return null;

        var blocks = new List<float[]>();
        foreach (var text in texts)
        {
            if (IsBlank(text)) continue;
            if (Embed(text) is not { } vector) return null;
            blocks.Add(vector);
        }

        if (blocks.Count == 0) return VectorMath.Normalize(path);

        var sum = VectorMath.Mean(blocks);
        for (var d = 0; d < sum.Length; d++) sum[d] += path[d];
        return VectorMath.Normalize(sum);
    }

    /// <summary>
    /// The note's indexed vector, else its freshly embedded text; with a core statement the mean of both
    /// (<see cref="WithCore"/>). Null if the embedder failed.
    /// </summary>
    private float[]? NoteVector(Guid noteKey, string noteText)
    {
        var text = _index.VectorOf(noteKey) ?? _index.EmbedQuery(noteText);
        return text is not null && _understanding?.NoteCore(noteKey) is { } core && core.Length == text.Length
            ? WithCore(text, core)
            : text;
    }

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

    /// <summary>Every section of the book's text in outline order, the root first; the appendix sections are not included.</summary>
    private static IEnumerable<OutlineNode> Sections(Book book) => book.ContentChildren.SelectMany(Walk).Prepend(book.Root);

    private static IEnumerable<OutlineNode> Walk(OutlineNode node) => node.Children.SelectMany(Walk).Prepend(node);

    /// <summary>True for a text without content besides its prefix.</summary>
    private static bool IsBlank(string prefixedText) =>
        prefixedText.AsSpan(EmbeddingText.QueryPrefix.Length).IsWhiteSpace();

    private static bool IsZero(float[] vector) => !vector.Any(x => x != 0);
}
