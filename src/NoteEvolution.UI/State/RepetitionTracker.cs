using Microsoft.Extensions.Logging;
using NoteEvolution.AI.Embeddings;
using NoteEvolution.AI.Semantic;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Repetitions;

namespace NoteEvolution.UI.State;

/// <summary>
/// The repetitions of the shown books (spec 4), computed in the background from immutable <see cref="Book"/> snapshots:
/// the literal pairs at once, the semantic ones as the vectors arrive: first the blocks' (they give the candidate pairs),
/// then the sentences' of the candidate blocks (they confirm a pair). Vectors are cached by the hash of the embedded text
/// (never by block key), so an unchanged block or sentence is not embedded again, not even after a restart.
/// The results are immutable <see cref="RepetitionSet"/>s, one per page, swapped in as they grow.
/// </summary>
internal sealed class RepetitionTracker : IDisposable
{
    /// <summary>After how many newly embedded texts (blocks or sentences) the set is published again.</summary>
    internal const int PublishEvery = 20;

    /// <summary>
    /// How many candidate pairs a session logs at most (each with its best sentence score): enough to judge the
    /// thresholds, and a book of similar blocks does not write hundreds of thousands of lines.
    /// </summary>
    internal const int MaxNearLines = 200;

    private readonly ILogger _logger;
    private readonly Func<Action, Task> _dispatch;
    private readonly AcceptedRepetitions _accepted;
    private readonly Func<string, float[]?>? _embed;
    private readonly Func<VectorCache?> _cache;
    private readonly string _modelId;
    private readonly Func<bool> _mayEmbed;
    private readonly Func<bool> _cacheMayCome;

    // Everything below under _gate.
    private readonly object _gate = new();

    /// <summary>The running analysis of each page (full path); cancelled by a newer one and on dispose.</summary>
    private readonly Dictionary<string, CancellationTokenSource> _runs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What was last computed for each page (full path), with its set.</summary>
    private readonly Dictionary<string, Computed> _computed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The analyses started and not yet seen completed; for <see cref="WhenIdle"/>.</summary>
    private readonly List<Task> _tasks = [];

    /// <summary>Text hashes whose embedding failed: left out until their text changes (for this session).</summary>
    private readonly HashSet<string> _failed = [];

    /// <summary>Pairs of text hashes already reported as a near pair to the log.</summary>
    private readonly HashSet<(string, string)> _logged = [];

    /// <summary>Raised by <see cref="Refresh"/>: a set built from accepted pairs read before must be built again.</summary>
    private int _acceptedVersion;

    /// <summary>The cache failed once in this session: logged, later failures are not.</summary>
    private bool _cacheFailureLogged;

    private bool _disposed;

    /// <summary>The cache was pruned (or is being) in this session.</summary>
    private bool _pruneStarted;

    /// <param name="dispatch">Runs an action on the UI thread.</param>
    /// <param name="embed">Embeds one text that carries its role prefix, null on failure; null without AI.</param>
    /// <param name="cache">The book blocks' vector cache; null while the semantic part cannot run (it is literal only then).</param>
    /// <param name="modelId">The embedding model, part of the text hash.</param>
    /// <param name="mayEmbed">
    /// Whether blocks may be embedded now (the notes' index is not busy); asked before each block. While it says no,
    /// an analysis publishes what the cache already holds and stops; the owner schedules again once it may. Always yes
    /// when null.
    /// </param>
    /// <param name="cacheMayCome">
    /// Whether a cache that is not available yet (null) may still become available (the AI parts are on their way). If not,
    /// the set is literal only and complete; if so, it is pending until the owner schedules again. Always yes when null.
    /// </param>
    public RepetitionTracker(ILogger logger, Func<Action, Task> dispatch, AcceptedRepetitions accepted,
        Func<string, float[]?>? embed, Func<VectorCache?> cache, string modelId, Func<bool>? mayEmbed = null,
        Func<bool>? cacheMayCome = null)
    {
        _cacheMayCome = cacheMayCome ?? (() => true);
        _logger = logger;
        _dispatch = dispatch;
        _accepted = accepted;
        _embed = embed;
        _cache = cache;
        _modelId = modelId;
        _mayEmbed = mayEmbed ?? (() => true);
    }

    /// <summary>A set changed; raised on the UI thread (through the dispatcher).</summary>
    public event Action? Changed;

    /// <summary>
    /// The set of <paramref name="book"/>: the one computed for this very snapshot, else <see cref="RepetitionSet.Empty"/>
    /// until it is computed. Call it on the UI thread.
    /// </summary>
    public RepetitionSet Of(Book? book)
    {
        if (book is null)
        {
            return RepetitionSet.Empty;
        }

        var path = book.Page.FilePath;
        lock (_gate)
        {
            return _computed.TryGetValue(path, out var computed) && computed.Set.IsFor(book) ? computed.Set : RepetitionSet.Empty;
        }
    }

    /// <summary>For tests: runs on the analysis's thread before each set is built and published (it may block).</summary>
    internal Action? BeforePublish { get; set; }

    /// <summary>
    /// What to show for <paramref name="book"/>: the set computed for this very snapshot (pending while it is not
    /// <see cref="RepetitionSet.Complete"/>), else the last published set of the same page, pending until the snapshot is
    /// computed (block keys survive a save from the editor, so it still counts the right blocks). Nothing known: the
    /// empty set, not pending. Call it on the UI thread.
    /// </summary>
    public (RepetitionSet Set, bool Pending) Shown(Book? book)
    {
        if (book is null)
        {
            return (RepetitionSet.Empty, false);
        }

        lock (_gate)
        {
            if (!_computed.TryGetValue(book.Page.FilePath, out var computed))
            {
                return (RepetitionSet.Empty, false);
            }

            return computed.Set.IsFor(book) ? (computed.Set, !computed.Set.Complete) : (computed.Set, true);
        }
    }

    /// <summary>Analyses <paramref name="book"/> in the background; replaces a running analysis of the same page. Call it on the UI thread.</summary>
    public void Schedule(Book book)
    {
        var path = book.Page.FilePath;
        var name = book.LinkName;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_runs.Remove(path, out var previous))
            {
                previous.Cancel();
            }

            var run = new CancellationTokenSource();
            _runs[path] = run;
            _tasks.RemoveAll(task => task.IsCompleted);
            _tasks.Add(Task.Run(() => AnalyseAsync(book, path, name, run.Token)));
        }
    }

    /// <summary>
    /// Cancels the running analyses of every page that is not one of <paramref name="shown"/>; what they computed and
    /// cached stays. Call it on the UI thread.
    /// </summary>
    public void CancelExcept(IReadOnlyCollection<Book> shown)
    {
        var keep = shown.Select(book => book.Page.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            foreach (var path in _computed.Keys.Where(path => !keep.Contains(path)).ToList())
            {
                _computed.Remove(path);
            }

            foreach (var path in _runs.Keys.Where(path => !keep.Contains(path)).ToList())
            {
                _runs.Remove(path, out var run);
                run!.Cancel();
            }
        }
    }

    /// <summary>
    /// Once per session, in the background: removes the vectors of texts no block (or sentence of a block) of
    /// <paramref name="books"/> has any more
    /// (every saved edit leaves the old text's vector behind). Call it on the UI thread, with the vault's books, once the
    /// cache is available; without cache (yet) or after a failure it does nothing and a later call tries again. A vector
    /// stored meanwhile for a text outside the snapshot is lost at worst and embedded again later; the cache's lock keeps
    /// prune and put apart.
    /// </summary>
    internal void PruneCache(IReadOnlyList<Book> books)
    {
        if (_embed is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || _pruneStarted)
            {
                return;
            }

            _pruneStarted = true;
            _tasks.RemoveAll(task => task.IsCompleted);
            _tasks.Add(Task.Run(() => Prune(books)));
        }
    }

    private void Prune(IReadOnlyList<Book> books)
    {
        try
        {
            var cache = _cache();
            if (cache is null)
            {
                lock (_gate)
                {
                    _pruneStarted = false; // not available yet: the next call tries again
                }

                return;
            }

            var live = new HashSet<string>();
            foreach (var book in books)
            {
                foreach (var block in ComparisonText.Of(book))
                {
                    live.Add(HashOf(block));
                    foreach (var sentence in Sentences.Of(block))
                    {
                        live.Add(EmbeddingText.Hash(_modelId, EmbeddingText.ForSentence(block, sentence)));
                    }
                }
            }

            cache.Prune(live);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pruning the book blocks' vector cache failed");
        }
    }

    private string HashOf(ComparedBlock block) => EmbeddingText.Hash(_modelId, EmbeddingText.ForRepetition(block));

    /// <summary>The accepted pairs changed: the sets are built again from what is computed, without embedding.</summary>
    public void Refresh()
    {
        List<KeyValuePair<string, Computed>> entries;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _acceptedVersion++;
            entries = [.. _computed];
        }

        var changed = false;
        foreach (var (path, entry) in entries)
        {
            var set = Build(entry);
            lock (_gate)
            {
                // A newer result was published meanwhile: it read the accepted pairs after the version changed.
                if (!_disposed && _computed.TryGetValue(path, out var current) && ReferenceEquals(current, entry))
                {
                    _computed[path] = entry with { Set = set };
                    changed = true;
                }
            }
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var run in _runs.Values)
            {
                run.Cancel();
            }

            _runs.Clear();
        }
    }

    /// <summary>For tests: completes when every analysis started so far has ended.</summary>
    internal Task WhenIdle()
    {
        lock (_gate)
        {
            return Task.WhenAll([.. _tasks]);
        }
    }

    /// <summary>
    /// The literal pairs with the vectors already cached, published once; then, while <c>mayEmbed</c> allows it, the
    /// missing blocks embedded one by one and, after them, the missing sentences of the candidate blocks (from all block
    /// vectors known by then), each stored at once, published again every <see cref="PublishEvery"/> texts and at the end.
    /// A failing cache leaves the run literal only (reading) or the text out (storing). Never throws.
    /// </summary>
    private async Task AnalyseAsync(Book book, string path, string name, CancellationToken token)
    {
        try
        {
            var blocks = ComparisonText.Of(book);
            var literal = LiteralRepetitions.Find(blocks);
            var literalOnly = new Computed(book, name, blocks, literal, [], 0, 0, RepetitionSet.Empty);
            var embed = _embed;
            if (embed is null)
            {
                await PublishAsync(path, literalOnly, token);
                return;
            }

            var texts = blocks.Select(EmbeddingText.ForRepetition).ToArray();
            var hashes = blocks.Select(HashOf).ToArray();
            var sentences = new SentenceTexts(blocks, _modelId);
            var known = new Dictionary<string, float[]>();
            VectorCache? cache;
            var cacheFailed = false;
            try
            {
                cache = _cache();
                foreach (var (hash, vector) in cache?.Get(hashes) ?? new Dictionary<string, float[]>())
                {
                    known[hash] = vector;
                }

                // The sentences of the blocks that are candidates with the cached block vectors (the usual case once a
                // book was analysed); those of later candidates are read when they are known.
                if (cache is not null)
                {
                    ReadSentences(cache, CandidatesOf(blocks, hashes, known, literal), sentences, known, token);
                }
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                LogCacheFailure(ex);
                cache = null;
                cacheFailed = true;
            }

            // Not available and never to come (model missing, load failed, AI off): literal only, as without AI.
            cacheFailed |= cache is null && !_cacheMayCome();

            if (cache is null)
            {
                // Failed: literal only, complete. Not available yet (the AI parts still start): the semantic pairs
                // follow, so the blocks count as pending until the owner schedules again.
                await PublishAsync(path, cacheFailed ? literalOnly : literalOnly with { Total = blocks.Count }, token);
                return;
            }

            // One first set: the literal pairs with what the cache already holds.
            await PublishAsync(path, Compute(book, name, blocks, literal, hashes, known, sentences), token);

            var sincePublished = 0;

            // Embeds the missing texts one by one; false if mayEmbed stopped it (scheduled again once it may).
            async Task<bool> EmbedMissingAsync(IEnumerable<(string Hash, string Text, string What, string Excerpt)> missing)
            {
                foreach (var (hash, text, what, excerpt) in missing)
                {
                    if (known.ContainsKey(hash) || IsFailed(hash))
                    {
                        continue; // also a second block or sentence with the same text
                    }

                    token.ThrowIfCancellationRequested();
                    if (!_mayEmbed())
                    {
                        return false; // the notes' index is busy: scheduled again once it is settled
                    }

                    EmbedAndStore(embed, cache, path, hash, text, known, what, excerpt, token);
                    if (++sincePublished == PublishEvery)
                    {
                        sincePublished = 0;
                        await PublishAsync(path, Compute(book, name, blocks, literal, hashes, known, sentences), token);
                    }
                }

                return true;
            }

            var blocksDone = await EmbedMissingAsync(
                blocks.Select((block, i) => (hashes[i], texts[i], "block", block.Paragraphs[0].Plain)));
            var unpublished = false;
            if (blocksDone)
            {
                // The candidates from every block vector known now: their sentences, cached or embedded.
                var candidates = CandidatesOf(blocks, hashes, known, literal);
                unpublished = ReadSentences(cache, candidates, sentences, known, token);
                await EmbedMissingAsync(sentences.OfBlocksIn(candidates)
                    .Select(sentence => (sentence.Hash, sentence.Text, "sentence", sentence.Excerpt)));
            }

            if (sincePublished > 0 || unpublished)
            {
                await PublishAsync(path, Compute(book, name, blocks, literal, hashes, known, sentences), token);
            }
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // Replaced by a newer analysis or disposed (the cache may be closed by then): nothing to report.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Analysing the repetitions of {Book} failed", name);
        }
    }

    /// <summary>
    /// Adds the cached vectors of the candidate blocks' sentences that are not known yet; true if it added any. A failing
    /// cache is logged and adds nothing (the sentences are then embedded, and left out if storing fails too).
    /// </summary>
    private bool ReadSentences(VectorCache cache, IReadOnlyList<(Guid A, Guid B, float Score)> candidates,
        SentenceTexts sentences, Dictionary<string, float[]> known, CancellationToken token)
    {
        var missing = sentences.OfBlocksIn(candidates).Select(s => s.Hash).Where(hash => !known.ContainsKey(hash)).Distinct().ToList();
        if (missing.Count == 0)
        {
            return false;
        }

        try
        {
            var found = cache.Get(missing);
            foreach (var (hash, vector) in found)
            {
                known[hash] = vector;
            }

            return found.Count > 0;
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            LogCacheFailure(ex);
            return false;
        }
    }

    /// <summary>
    /// Embeds one text and stores its vector at once; a text whose embedding or storing fails is left out for this session
    /// (a failed embedding is logged once per text).
    /// </summary>
    private void EmbedAndStore(Func<string, float[]?> embed, VectorCache cache, string path, string hash, string text,
        Dictionary<string, float[]> known, string what, string excerpt, CancellationToken token)
    {
        var vector = embed(text);
        token.ThrowIfCancellationRequested();
        if (vector is { Length: > 0 })
        {
            try
            {
                cache.Put([(hash, path, vector)]);
                known[hash] = vector;
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                LogCacheFailure(ex);
                MarkFailed(hash);
            }
        }
        else if (MarkFailed(hash))
        {
            _logger.LogWarning("Embedding a {What} for the repetitions failed; it is left out: \"{Text}\"",
                what, Excerpt.FirstLine(excerpt, 40));
        }
    }

    /// <summary>The blocks' vectors known so far, by block key.</summary>
    private static Dictionary<Guid, float[]> BlockVectors(
        IReadOnlyList<ComparedBlock> blocks, string[] hashes, IReadOnlyDictionary<string, float[]> known)
    {
        var vectors = new Dictionary<Guid, float[]>();
        for (var i = 0; i < blocks.Count; i++)
        {
            if (known.TryGetValue(hashes[i], out var vector))
            {
                vectors[blocks[i].Key] = vector;
            }
        }

        return vectors;
    }

    /// <summary>The candidate pairs from the block vectors known so far.</summary>
    private static IReadOnlyList<(Guid A, Guid B, float Score)> CandidatesOf(IReadOnlyList<ComparedBlock> blocks,
        string[] hashes, IReadOnlyDictionary<string, float[]> known, IReadOnlyList<LiteralPair> literal) =>
        CandidatesOf(blocks, BlockVectors(blocks, hashes, known), literal);

    private static IReadOnlyList<(Guid A, Guid B, float Score)> CandidatesOf(IReadOnlyList<ComparedBlock> blocks,
        Dictionary<Guid, float[]> vectors, IReadOnlyList<LiteralPair> literal) =>
        SemanticRepetitions.Candidates(
            [.. blocks.Select(block => block.Key)],
            vectors.GetValueOrDefault,
            literal.Select(pair => (pair.A, pair.B)).ToHashSet());

    /// <summary>
    /// The semantic pairs from the vectors known so far: candidates by block vectors, confirmed by sentence vectors. The
    /// counts take the blocks and the sentences of the candidate blocks (each sentence text once); texts whose embedding
    /// failed do not count in the total.
    /// </summary>
    private Computed Compute(Book book, string name, IReadOnlyList<ComparedBlock> blocks, IReadOnlyList<LiteralPair> literal,
        string[] hashes, IReadOnlyDictionary<string, float[]> known, SentenceTexts sentences)
    {
        HashSet<string> failed;
        lock (_gate)
        {
            failed = [.. _failed];
        }

        var vectors = BlockVectors(blocks, hashes, known);
        var index = new Dictionary<Guid, int>();
        var left = 0;
        for (var i = 0; i < blocks.Count; i++)
        {
            index[blocks[i].Key] = i;
            if (!known.ContainsKey(hashes[i]) && failed.Contains(hashes[i]))
            {
                left++;
            }
        }

        var candidates = CandidatesOf(blocks, vectors, literal);
        var sentenceHashes = sentences.OfBlocksIn(candidates).Select(s => s.Hash).ToHashSet();
        var sentencesEmbedded = sentenceHashes.Count(known.ContainsKey);
        var sentencesLeft = sentenceHashes.Count(hash => !known.ContainsKey(hash) && failed.Contains(hash));

        var semantic = SemanticRepetitions.Confirm(
            candidates,
            sentences.SentencesOf,
            (key, sentence) => known.GetValueOrDefault(sentences.HashOf(key, sentence)),
            (a, b, score, listed) => LogNear(blocks[index[a]], hashes[index[a]], blocks[index[b]], hashes[index[b]], score, listed));
        return new Computed(book, name, blocks, literal, semantic, vectors.Count + sentencesEmbedded,
            blocks.Count - left + sentenceHashes.Count - sentencesLeft, RepetitionSet.Empty);
    }

    /// <summary>
    /// Builds the set with the accepted pairs as they are now and swaps it in, unless the analysis was cancelled (checked
    /// under the lock that cancels it, so a replaced analysis never publishes); then raises <see cref="Changed"/>.
    /// </summary>
    private async Task PublishAsync(string path, Computed computed, CancellationToken token)
    {
        while (true)
        {
            int version;
            lock (_gate)
            {
                version = _acceptedVersion;
            }

            BeforePublish?.Invoke();
            var set = Build(computed);
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                if (version != _acceptedVersion)
                {
                    continue; // accepted pairs changed while it was built
                }

                _computed[path] = computed with { Set = set };
                break;
            }
        }

        await _dispatch(RaiseNow);
    }

    private RepetitionSet Build(Computed computed) =>
        RepetitionSet.Create(computed.Book, computed.Blocks, computed.Literal, computed.Semantic,
            _accepted.Of(computed.Name), computed.Embedded, computed.Total);

    private bool IsFailed(string hash)
    {
        lock (_gate)
        {
            return _failed.Contains(hash);
        }
    }

    /// <summary>Leaves the text out for this session; true if it was not left out before.</summary>
    private bool MarkFailed(string hash)
    {
        lock (_gate)
        {
            return _failed.Add(hash);
        }
    }

    private void LogCacheFailure(Exception ex)
    {
        lock (_gate)
        {
            if (_cacheFailureLogged)
            {
                return;
            }

            _cacheFailureLogged = true;
        }

        _logger.LogError(ex, "The book blocks' vector cache failed; the repetitions go on without it");
    }

    private void LogNear(ComparedBlock a, string hashA, ComparedBlock b, string hashB, float score, bool listed)
    {
        var limitReached = false;
        lock (_gate)
        {
            if (_logged.Count > MaxNearLines || !_logged.Add((hashA, hashB)))
            {
                return;
            }

            if (_logged.Count > MaxNearLines)
            {
                limitReached = true; // the one entry past the limit only stands for "more"
            }
        }

        if (limitReached)
        {
            _logger.LogInformation("Repetition candidates: more than {Max} this session, the rest is not logged", MaxNearLines);
            return;
        }

        _logger.LogInformation("Repetition candidate {Score:F3} listed={Listed}: \"{A}\" ~ \"{B}\"",
            score, listed, Excerpt.FirstLine(a.Paragraphs[0].Plain, 40), Excerpt.FirstLine(b.Paragraphs[0].Plain, 40));
    }

    private void RaiseNow()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            // A failing view must not end the analysis that raised it.
            _logger.LogError(ex, "Handling the repetitions' change failed");
        }
    }

    /// <summary>Raises <see cref="Changed"/> through the dispatcher without waiting; never throws, a failure is logged.</summary>
    private void RaiseChanged()
    {
        try
        {
            _dispatch(RaiseNow).ContinueWith(
                t => _logger.LogError(t.Exception, "Raising the repetitions' change failed"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Raising the repetitions' change failed");
        }
    }

    /// <summary>One analysis' result for a book snapshot: its blocks and pairs, the counts, and the set built from them.</summary>
    private sealed record Computed(
        Book Book, string Name, IReadOnlyList<ComparedBlock> Blocks, IReadOnlyList<LiteralPair> Literal,
        IReadOnlyList<SemanticPair> Semantic, int Embedded, int Total, RepetitionSet Set);

    /// <summary>A sentence of a block: its embedding text (with role prefix), that text's hash, and the sentence as it stands.</summary>
    private sealed record SentenceText(ComparedSentence Sentence, string Text, string Hash, string Excerpt);

    /// <summary>The sentences of one analysis' blocks, split and hashed once per block when first asked for.</summary>
    private sealed class SentenceTexts
    {
        private readonly Dictionary<Guid, ComparedBlock> _blocks = [];
        private readonly Dictionary<Guid, IReadOnlyList<SentenceText>> _of = [];
        private readonly string _modelId;

        public SentenceTexts(IReadOnlyList<ComparedBlock> blocks, string modelId)
        {
            _modelId = modelId;
            foreach (var block in blocks)
            {
                _blocks.TryAdd(block.Key, block);
            }
        }

        public IReadOnlyList<SentenceText> Of(Guid key)
        {
            if (!_of.TryGetValue(key, out var sentences))
            {
                var block = _blocks[key];
                _of[key] = sentences = [.. Sentences.Of(block).Select(sentence =>
                {
                    var text = EmbeddingText.ForSentence(block, sentence);
                    return new SentenceText(sentence, text, EmbeddingText.Hash(_modelId, text), Sentences.Text(block, sentence));
                })];
            }

            return sentences;
        }

        public IReadOnlyList<ComparedSentence> SentencesOf(Guid key) => [.. Of(key).Select(s => s.Sentence)];

        public string HashOf(Guid key, ComparedSentence sentence) => Of(key).First(s => s.Sentence == sentence).Hash;

        /// <summary>The sentences of every block in <paramref name="candidates"/>, block by block in order of appearance.</summary>
        public IEnumerable<SentenceText> OfBlocksIn(IReadOnlyList<(Guid A, Guid B, float Score)> candidates) =>
            candidates.SelectMany(c => new[] { c.A, c.B }).Distinct().SelectMany(Of);
    }
}
