using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NoteEvolution.AI.Embeddings;
using NoteEvolution.Core.Vaults;

namespace NoteEvolution.AI.Semantic;

/// <summary>
/// Keeps an embedding of every note block in memory (<see cref="VectorIndex"/>, keyed by <see cref="NoteBlock.Key"/>)
/// and reuses the vectors of unchanged texts from the <see cref="VectorCache"/> (keyed by content hash).
/// <para>
/// Threads: <see cref="RebuildAsync"/> and <see cref="UpdatePageAsync"/> read the notes on the calling thread (the vault
/// model is not thread-safe) and then embed in the background, one run at a time in call order; an update that arrives
/// during a rebuild waits for it. Queries (<see cref="Nearest"/>, <see cref="VectorOf"/>, <see cref="EmbedQuery"/>) never
/// wait for indexing.
/// </para>
/// <para>
/// Ownership: the index owns <c>cache</c> and disposes it; it does not own <c>embedder</c>, which is shared app-wide.
/// <see cref="Dispose"/> cancels a running or waiting run, which then ends quietly at its next step: no cache write,
/// no status change, no exception. Later calls do nothing.
/// </para>
/// </summary>
public sealed class SemanticIndex : ISemanticIndex, IDisposable
{
    public const int BatchSize = 16;

    private readonly IEmbedder _embedder;
    private readonly VectorCache _cache;
    private readonly string _modelId;
    private readonly ILogger _logger;

    /// <summary>Lets one rebuild or page update run at a time.</summary>
    private readonly SemaphoreSlim _run = new(1, 1);

    /// <summary>Cancelled by <see cref="Dispose"/>. Never disposed, so late callers can still link to its token.</summary>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>Makes cache writes and status changes atomic with respect to <see cref="Dispose"/>.</summary>
    private readonly Lock _disposeGate = new();

    private volatile VectorIndex _index = new();
    private volatile SemanticStatus _status = new(SemanticState.Off, 0, 0, null);
    private volatile bool _disposed;

    public SemanticIndex(IEmbedder embedder, VectorCache cache, string modelId, ILogger? logger = null)
    {
        _embedder = embedder;
        _cache = cache;
        _modelId = modelId;
        _logger = logger ?? NullLogger.Instance;
    }

    public SemanticStatus Status => _status;

    public event Action? StatusChanged;

    public bool IsReady
    {
        get
        {
            var state = _status.State;
            return state == SemanticState.Ready || (state == SemanticState.Indexing && _index.Count > 0);
        }
    }

    /// <summary>
    /// Replaces the index with the embeddings of all current notes. An embedder (or cache) failure sets
    /// <see cref="SemanticState.Failed"/> and the task completes normally. When <paramref name="ct"/> is cancelled the
    /// run stops between batches, the status stays <see cref="SemanticState.Indexing"/> and the task throws
    /// <see cref="OperationCanceledException"/>; a cancellation by <see cref="Dispose"/> alone completes it normally.
    /// </summary>
    public Task RebuildAsync(INoteRepository notes, CancellationToken ct)
    {
        if (_disposed) return Task.CompletedTask;
        var items = Read(notes.All());
        return RunAsync(token => Rebuild(items, token), ct);
    }

    /// <summary>
    /// Drops the page's embeddings and indexes its current notes like <see cref="RebuildAsync"/>, without pruning the
    /// cache and without progress: the status does not change. A failure (embedder, cache) is logged and leaves the
    /// notes that could not be embedded out of the index; the page's next change retries them. Completes with whether
    /// the page's entries differ from before. Paths are compared ignoring case.
    /// </summary>
    public async Task<bool> UpdatePageAsync(INoteRepository notes, string pagePath, CancellationToken ct)
    {
        if (_disposed) return false;
        var page = Path.GetFullPath(pagePath);
        var items = Read(notes.All().Where(n => string.Equals(PageOf(n), page, StringComparison.OrdinalIgnoreCase)));
        var changed = false;
        await RunAsync(token => { changed = UpdatePage(page, items, token); }, ct).ConfigureAwait(false);
        return changed;
    }

    public IReadOnlyList<(Guid Key, float Score)> Nearest(float[] query, int k, Func<Guid, bool> include) =>
        _index.Nearest(query, k, include);

    public float[]? VectorOf(Guid noteKey) => _index.Get(noteKey);

    public float[]? EmbedQuery(string text)
    {
        if (_disposed) return null;
        try
        {
            return _embedder.Embed([text], _lifetime.Token)[0];
        }
        catch (Exception) when (_disposed)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Embedding a query failed");
            return null;
        }
    }

    public void Dispose()
    {
        lock (_disposeGate)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
        }

        _cache.Dispose();
    }

    /// <summary>One note as it is embedded: its key, page (full path), text and the text's hash.</summary>
    private sealed record Item(Guid Key, string Page, string Text, string Hash);

    private List<Item> Read(IEnumerable<NoteBlock> notes) =>
    [
        .. notes.Select(n =>
        {
            var text = EmbeddingText.ForNote(n);
            return new Item(n.Key, PageOf(n), text, EmbeddingText.Hash(_modelId, text));
        }),
    ];

    private static string PageOf(NoteBlock note) => Path.GetFullPath(note.Page.FilePath);

    /// <summary>
    /// Runs <paramref name="work"/> in the background once the previous run has finished. Failures end in
    /// <see cref="SemanticState.Failed"/>; cancellation reaches the caller only if <paramref name="ct"/> was cancelled.
    /// </summary>
    private async Task RunAsync(Action<CancellationToken> work, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var token = linked.Token;
        try
        {
            await _run.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await Task.Run(() => Execute(work, token), token).ConfigureAwait(false);
            }
            finally
            {
                _run.Release();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // cancelled by Dispose: a normal end
        }
    }

    private void Execute(Action<CancellationToken> work, CancellationToken token)
    {
        try
        {
            work(token);
        }
        catch (Exception ex) when (token.IsCancellationRequested
                                   && ex is OperationCanceledException or ObjectDisposedException)
        {
            // the embedder or the cache noticed the cancellation (or the cache was disposed) before we did
            throw new OperationCanceledException(token);
        }
        catch (Exception) when (_disposed)
        {
            // any other failure after Dispose (e.g. of the closed cache) is part of shutting down, not a failed index
            throw new OperationCanceledException(token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Semantic indexing failed");
            var status = _status;
            SetStatus(new SemanticStatus(SemanticState.Failed, status.Done, status.Total, ex.Message));
        }
    }

    private void Rebuild(List<Item> items, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var index = new VectorIndex();
        var missing = AddCached(index, items);
        _index = index;
        var done = items.Count - missing.Count;
        SetStatus(new SemanticStatus(SemanticState.Indexing, done, items.Count, null));

        Embed(index, missing, token, embedded =>
        {
            done += embedded;
            SetStatus(new SemanticStatus(SemanticState.Indexing, done, items.Count, null));
        });

        lock (_disposeGate)
        {
            token.ThrowIfCancellationRequested();
            _cache.Prune(items.Select(i => i.Hash).ToHashSet());
        }

        _logger.LogInformation("Semantic index ready: {Total} notes, {Embedded} embedded", items.Count, missing.Count);
        SetStatus(new SemanticStatus(SemanticState.Ready, items.Count, items.Count, null));
    }

    private bool UpdatePage(string page, List<Item> items, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var index = _index;
        var before = index.VectorsOfPage(page);
        index.RemovePage(page);
        try
        {
            Embed(index, AddCached(index, items), token, _ => { });
        }
        catch (Exception ex) when (!token.IsCancellationRequested && !_disposed)
        {
            // a transient failure of one page must not disable the AI for the session: the notes not embedded yet
            // stay out of the index and the next change of the page retries them
            _logger.LogError(ex, "Updating the semantic index for {Page} failed", page);
        }

        var after = index.VectorsOfPage(page);
        return before.Count != after.Count
               || before.Any(b => !after.TryGetValue(b.Key, out var vector) || !b.Value.AsSpan().SequenceEqual(vector));
    }

    /// <summary>Puts the items whose hash is cached into <paramref name="index"/>; returns the others.</summary>
    private List<Item> AddCached(VectorIndex index, List<Item> items)
    {
        var cached = _cache.Get([.. items.Select(i => i.Hash)]);
        var missing = new List<Item>();
        foreach (var item in items)
        {
            if (cached.TryGetValue(item.Hash, out var vector)) index.Set(item.Key, item.Page, vector);
            else missing.Add(item);
        }

        return missing;
    }

    /// <summary>
    /// Embeds the items in batches of <see cref="BatchSize"/> distinct texts, shortest first (less padding), and after
    /// each batch stores the vectors in the cache and the index and reports how many items it covered.
    /// </summary>
    private void Embed(VectorIndex index, List<Item> items, CancellationToken token, Action<int> batchDone)
    {
        var texts = items.GroupBy(i => i.Hash).OrderBy(g => g.First().Text.Length).ToList();
        foreach (var batch in texts.Chunk(BatchSize))
        {
            token.ThrowIfCancellationRequested();
            var vectors = _embedder.Embed([.. batch.Select(g => g.First().Text)], token);

            lock (_disposeGate)
            {
                token.ThrowIfCancellationRequested(); // also after Dispose: no write to a closed cache
                _cache.Put([.. batch.Select((g, i) => (Hash: g.Key, File: g.First().Page, Vector: vectors[i]))]);
            }

            for (var i = 0; i < batch.Length; i++)
            {
                foreach (var item in batch[i]) index.Set(item.Key, item.Page, vectors[i]);
            }

            batchDone(batch.Sum(g => g.Count()));
        }
    }

    private void SetStatus(SemanticStatus status)
    {
        lock (_disposeGate)
        {
            if (_disposed) return;
            _status = status;
        }

        StatusChanged?.Invoke();
    }
}
