using Microsoft.Extensions.Logging;
using NoteEvolution.AI.Embeddings;
using NoteEvolution.AI.Semantic;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Repetitions;

namespace NoteEvolution.UI.State;

/// <summary>
/// The repetitions of the shown books (spec 4), computed in the background from immutable <see cref="Book"/> snapshots:
/// the literal pairs at once, the semantic ones as the blocks' vectors arrive. Vectors are cached by the hash of the
/// block's comparison text (never by block key), so an unchanged block is not embedded again, not even after a restart.
/// The results are immutable <see cref="RepetitionSet"/>s, one per page, swapped in as they grow.
/// </summary>
internal sealed class RepetitionTracker : IDisposable
{
    /// <summary>After how many newly embedded blocks the set is published again.</summary>
    internal const int PublishEvery = 20;

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
    /// Once per session, in the background: removes the vectors of texts no block of <paramref name="books"/> has any more
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
    /// The literal pairs with the vectors already cached, published once; then, while <c>mayEmbed</c> allows it,
    /// the missing blocks embedded one by one (each stored at once), published again every <see cref="PublishEvery"/>
    /// blocks and at the end. A failing cache leaves the run literal only (reading) or the block out (storing). Never throws.
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
            await PublishAsync(path, Compute(book, name, blocks, literal, hashes, known), token);

            var sincePublished = 0;
            for (var i = 0; i < blocks.Count; i++)
            {
                if (known.ContainsKey(hashes[i]) || IsFailed(hashes[i]))
                {
                    continue; // also a second block with the same text
                }

                token.ThrowIfCancellationRequested();
                if (!_mayEmbed())
                {
                    break; // the notes' index is busy: scheduled again once it is settled
                }

                var vector = embed(texts[i]);
                token.ThrowIfCancellationRequested();
                if (vector is { Length: > 0 })
                {
                    try
                    {
                        cache.Put([(hashes[i], path, vector)]);
                        known[hashes[i]] = vector;
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested)
                    {
                        LogCacheFailure(ex);
                        MarkFailed(hashes[i]);
                    }
                }
                else if (MarkFailed(hashes[i]))
                {
                    _logger.LogWarning("Embedding a block for the repetitions failed; it is left out: \"{Block}\"",
                        Excerpt.FirstLine(blocks[i].Paragraphs[0].Plain, 40));
                }

                if (++sincePublished == PublishEvery)
                {
                    sincePublished = 0;
                    await PublishAsync(path, Compute(book, name, blocks, literal, hashes, known), token);
                }
            }

            if (sincePublished > 0)
            {
                await PublishAsync(path, Compute(book, name, blocks, literal, hashes, known), token);
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

    /// <summary>The semantic pairs from the vectors known so far; blocks whose embedding failed do not count in the total.</summary>
    private Computed Compute(Book book, string name, IReadOnlyList<ComparedBlock> blocks, IReadOnlyList<LiteralPair> literal,
        string[] hashes, IReadOnlyDictionary<string, float[]> known)
    {
        HashSet<string> failed;
        lock (_gate)
        {
            failed = [.. _failed];
        }

        var vectors = new Dictionary<Guid, float[]>();
        var index = new Dictionary<Guid, int>();
        var left = 0;
        for (var i = 0; i < blocks.Count; i++)
        {
            index[blocks[i].Key] = i;
            if (known.TryGetValue(hashes[i], out var vector))
            {
                vectors[blocks[i].Key] = vector;
            }
            else if (failed.Contains(hashes[i]))
            {
                left++;
            }
        }

        var semantic = SemanticRepetitions.Find(
            [.. blocks.Select(block => block.Key)],
            vectors.GetValueOrDefault,
            literal.Select(pair => (pair.A, pair.B)).ToHashSet(),
            (a, b, score, listed) => LogNear(blocks[index[a]], hashes[index[a]], blocks[index[b]], hashes[index[b]], score, listed));
        return new Computed(book, name, blocks, literal, semantic, vectors.Count, blocks.Count - left, RepetitionSet.Empty);
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
        lock (_gate)
        {
            if (!_logged.Add((hashA, hashB)))
            {
                return;
            }
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
}
