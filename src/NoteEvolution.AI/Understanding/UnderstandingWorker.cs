using Microsoft.Extensions.Logging;
using NoteEvolution.AI.Embeddings;
using NoteEvolution.AI.Language;

namespace NoteEvolution.AI.Understanding;

/// <summary>
/// State of the background computation: <paramref name="Done"/> of <paramref name="Total"/> entries of the current book and
/// the notes (an entry skipped after too many failures counts as done), the estimated <paramref name="Remaining"/> time
/// (null until three answers were timed) and why the worker is waiting, if it is.
/// </summary>
public sealed record UnderstandingProgress(int Done, int Total, TimeSpan? Remaining, UnderstandingPause Pause);

/// <summary>Why the worker waits although there is work: the user's switch, typing, battery, or the language-model server.</summary>
public enum UnderstandingPause { None, User, Typing, Battery, Unreachable, ModelMissing }

/// <summary>
/// The background queue of the language model: computes heading summaries (bottom-up) and note core statements one request
/// at a time, stores them with their meaning vectors in the <see cref="UnderstandingStore"/>, pauses for the user's switch,
/// typing, battery and an unreachable server, retries failures and reports progress with a remaining-time estimate.
/// It only works on the <see cref="UnderstandingItem"/> snapshots it is given and never touches the vault model.
/// All waiting uses the injected <see cref="TimeProvider"/>. <see cref="RunAsync"/> runs the loop (once per worker);
/// the other members may be called from any thread.
/// </summary>
public sealed class UnderstandingWorker : IDisposable
{
    /// <summary>How long nothing must be typed before the computation continues.</summary>
    public static readonly TimeSpan TypingQuiet = TimeSpan.FromSeconds(10);

    /// <summary>A background request that takes longer is cancelled and counts as a failed attempt.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    /// <summary>How often an unreachable server (or a missing model) is checked again.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(60);

    /// <summary>How often the battery state is checked again while running on battery.</summary>
    public static readonly TimeSpan BatteryCheck = TimeSpan.FromSeconds(30);

    /// <summary>Failed attempts per item and fingerprint after which it is skipped until its content changes.</summary>
    public const int MaxAttempts = 3;

    private const int DurationWindow = 10;
    private const int MinDurations = 3;

    private readonly IChatModel _chat;
    private readonly string _chatModel;
    private readonly UnderstandingStore _store;
    private readonly Func<string, float[]?> _embed;
    private readonly string _embedModel;
    private readonly TimeProvider _time;
    private readonly Func<bool> _onBattery;
    private readonly ILogger _logger;

    // Shared with the callers' threads, guarded by _gate.
    private readonly Lock _gate = new();
    private readonly Queue<PendingCall> _calls = new();
    private TaskCompletionSource _wake = NewWake();
    private Work _work = new([], null, [], []);
    private bool _workChanged = true;
    private DateTimeOffset _lastTyping = DateTimeOffset.MinValue;
    private bool _paused;
    private bool _disposed;

    // Store writes against Dispose: once Dispose returned, nothing is written any more.
    private readonly Lock _storeGate = new();
    private readonly CancellationTokenSource _dispose = new();

    // Only touched by the loop.
    private Work _planWork = new([], null, [], []);
    private Plan? _plan;
    private bool _planStale = true;
    private readonly HashSet<string> _retryLater = [];
    private readonly HashSet<string> _unembeddable = [];
    private readonly Queue<TimeSpan> _durations = new();
    private bool _connected;
    private UnderstandingPause _connectionPause;
    private DateTimeOffset _nextConnectionCheck = DateTimeOffset.MinValue;

    private volatile UnderstandingProgress _progress = new(0, 0, null, UnderstandingPause.None);

    /// <param name="chat">The language-model server.</param>
    /// <param name="chatModel">The model to ask; it is part of every item's fingerprint.</param>
    /// <param name="store">Where results go; owned by the caller and disposed after this worker.</param>
    /// <param name="embed">Meaning vector of a role-marked text (<see cref="EmbeddingText"/> prefix), or null when no embedder is ready.</param>
    /// <param name="embedModel">Id of the model behind <paramref name="embed"/>.</param>
    /// <param name="time">The clock for all waiting and timing.</param>
    /// <param name="onBattery">Whether the computer runs on battery; false when that cannot be told.</param>
    /// <param name="logger">Failures and connection changes.</param>
    public UnderstandingWorker(IChatModel chat, string chatModel, UnderstandingStore store, Func<string, float[]?> embed,
        string embedModel, TimeProvider time, Func<bool> onBattery, ILogger logger)
    {
        _chat = chat;
        _chatModel = chatModel;
        _store = store;
        _embed = embed;
        _embedModel = embedModel;
        _time = time;
        _onBattery = onBattery;
        _logger = logger;
    }

    /// <summary>Raised on the worker's thread whenever <see cref="Progress"/> changes.</summary>
    public event Action? ProgressChanged;

    public UnderstandingProgress Progress => _progress;

    /// <summary>The user's pause switch: when set, no further background request starts (a running one finishes).</summary>
    public bool Paused
    {
        get
        {
            lock (_gate) return _paused;
        }
        set
        {
            lock (_gate) _paused = value;
            Signal();
        }
    }

    /// <summary>
    /// Replaces the queue (spec 4 order): headings of the current book (each list children before parents, as
    /// <see cref="UnderstandingItems.ForBook"/> builds it) with the current section, its sub-sections and its ancestors
    /// first, then the notes in the given order (the caller sorts them newest first), then the headings of other books.
    /// Within each group, items whose content changed (an older entry of the same identity exists) come first.
    /// </summary>
    public void SetWork(IReadOnlyList<UnderstandingItem> currentBook, Guid? currentSectionKey,
        IReadOnlyList<UnderstandingItem> notes, IReadOnlyList<UnderstandingItem> otherBooks)
    {
        lock (_gate)
        {
            _work = new Work(currentBook, currentSectionKey, notes, otherBooks);
            _workChanged = true;
        }

        Signal();
    }

    /// <summary>Called after every editor input: no new request starts until <see cref="TypingQuiet"/> passed without input.</summary>
    public void NotifyTyping()
    {
        lock (_gate) _lastTyping = _time.GetUtcNow();
        Signal();
    }

    /// <summary>
    /// Runs one request ("Genauer prüfen") as the next one: after the running background request, before the queue, also
    /// while the background computation is paused. Throws what the server throws; cancelling <paramref name="ct"/> withdraws
    /// or cancels it.
    /// </summary>
    public async Task<string> RunNextAsync(string system, string user, ChatOptions options, CancellationToken ct)
    {
        var call = new PendingCall(system, user, options, ct);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _calls.Enqueue(call);
        }

        using var registration = ct.Register(() => call.Result.TrySetCanceled(ct));
        Signal();
        return await call.Result.Task;
    }

    /// <summary>The loop; returns when <paramref name="ct"/> is cancelled or the worker is disposed (a running request is cancelled and nothing of it stored).</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _dispose.Token);
        var token = linked.Token;
        try
        {
            await LoopAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // stopped
        }
        catch (ObjectDisposedException) when (token.IsCancellationRequested)
        {
            // the store was closed while stopping
        }
    }

    /// <summary>Cancels the running request and pending <see cref="RunNextAsync"/> calls; nothing is stored afterwards. The store is not disposed.</summary>
    public void Dispose()
    {
        // The token source is not disposed: RunAsync may still link to it.
        _dispose.Cancel();
        lock (_storeGate)
        {
            // waits for a store write in progress; later writes see the cancellation
        }

        PendingCall[] pending;
        lock (_gate)
        {
            _disposed = true;
            pending = [.. _calls];
            _calls.Clear();
        }

        foreach (var call in pending) call.Result.TrySetCanceled();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var wake = WakeTask(); // taken before looking at the state, so no signal is lost

            if (TakeCall() is { } call)
            {
                await ServeAsync(call, ct);
                continue;
            }

            var plan = CurrentPlan();
            var item = plan.NextReady();
            if (item is null && plan.MissingVectors.Count == 0)
            {
                Report(UnderstandingPause.None);
                await WaitAsync(wake, null, ct);
                continue;
            }

            var (pause, delay) = CurrentPause();
            if (pause != UnderstandingPause.None)
            {
                Report(pause);
                await WaitAsync(wake, delay, ct);
                continue;
            }

            if (plan.MissingVectors.Count > 0)
            {
                EmbedMissing(plan, ct); // needs no language model
                continue;
            }

            if (!_connected)
            {
                var now = _time.GetUtcNow();
                if (now < _nextConnectionCheck)
                {
                    Report(_connectionPause);
                    await WaitAsync(wake, _nextConnectionCheck - now, ct);
                    continue;
                }

                if (!await ConnectAsync(ct)) continue;
            }

            Report(UnderstandingPause.None);
            await ProcessAsync(plan, item!, ct);
        }
    }

    private async Task ProcessAsync(Plan plan, UnderstandingItem item, CancellationToken ct)
    {
        var heading = item.Kind == UnderstandingKind.Heading;
        var (system, user) = heading
            ? UnderstandingPrompts.Summary(item, [.. item.ChildFingerprints.Select(c => ChildSummary(plan, c))])
            : UnderstandingPrompts.Core(item);
        var options = new ChatOptions(heading ? UnderstandingPrompts.SummaryTokens : UnderstandingPrompts.CoreTokens);

        var started = _time.GetTimestamp();
        string answer;
        try
        {
            using var timeout = new CancellationTokenSource(RequestTimeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            answer = await _chat.ChatAsync(_chatModel, system, user, options, linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Language model request for {Identity} timed out", item.Identity);
            Fail(item, ct);
            return;
        }
        catch (ChatModelUnavailableException ex)
        {
            _logger.LogWarning(ex, "Language model request for {Identity} failed", item.Identity);
            // Unreachable or model gone: pause without blaming the item; the server answered: a failed attempt.
            _connected = false;
            if (await ConnectAsync(ct)) Fail(item, ct);
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Language model request for {Identity} failed", item.Identity);
            Fail(item, ct);
            return;
        }

        RecordDuration(_time.GetElapsedTime(started));
        var text = UnderstandingPrompts.Clean(answer, heading ? UnderstandingPrompts.SummaryLimit : UnderstandingPrompts.CoreLimit);
        if (text.Length == 0)
        {
            _logger.LogWarning("Language model gave an empty answer for {Identity}", item.Identity);
            Fail(item, ct);
            return;
        }

        // Stored under the fingerprint it was requested for, even if the content changed meanwhile (the new one is queued).
        Write(ct, () => _store.Put(new UnderstandingEntry(item.Kind, item.Fingerprint, item.Identity, text, _chatModel, null, null)));
        _retryLater.Remove(item.Fingerprint);
        _planStale = true;
        if (Embed(item.Kind, text) is { } vector)
        {
            Write(ct, () => _store.SetVector(item.Fingerprint, _embedModel, vector));
        }
    }

    private async Task ServeAsync(PendingCall call, CancellationToken ct)
    {
        try
        {
            if (call.Result.Task.IsCompleted) return; // withdrawn while waiting
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, call.Ct);
            call.Result.TrySetResult(await _chat.ChatAsync(_chatModel, call.System, call.User, call.Options, linked.Token));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            call.Result.TrySetCanceled(call.Ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is ChatModelUnavailableException) _connected = false; // checked again before the next background request
            call.Result.TrySetException(ex);
        }
        finally
        {
            call.Result.TrySetCanceled(); // the worker stopped
        }
    }

    /// <summary>Checks server and model; on a problem sets the pause and the next check. Returns whether requests can go out.</summary>
    private async Task<bool> ConnectAsync(CancellationToken ct)
    {
        var pause = await ConnectionPauseAsync(ct);
        _connected = pause == UnderstandingPause.None;
        if (!_connected)
        {
            if (pause != _connectionPause) _logger.LogInformation("Language model paused: {Pause}", pause);
            _connectionPause = pause;
            _nextConnectionCheck = _time.GetUtcNow() + RetryInterval;
            Report(pause);
        }
        else
        {
            _connectionPause = UnderstandingPause.None;
        }

        return _connected;
    }

    private async Task<UnderstandingPause> ConnectionPauseAsync(CancellationToken ct)
    {
        try
        {
            if (await _chat.VersionAsync(ct) is null) return UnderstandingPause.Unreachable;
            var models = await _chat.ModelsAsync(ct);
            return models.Any(m => IsModel(m.Name)) ? UnderstandingPause.None : UnderstandingPause.ModelMissing;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Language model server check failed");
            return UnderstandingPause.Unreachable;
        }
    }

    private bool IsModel(string name) =>
        string.Equals(name, _chatModel, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, _chatModel + ":latest", StringComparison.OrdinalIgnoreCase);

    private (UnderstandingPause Pause, TimeSpan? Delay) CurrentPause()
    {
        bool paused;
        DateTimeOffset lastTyping;
        lock (_gate)
        {
            paused = _paused;
            lastTyping = _lastTyping;
        }

        if (paused) return (UnderstandingPause.User, null);
        if (OnBattery()) return (UnderstandingPause.Battery, BatteryCheck);
        var quiet = lastTyping + TypingQuiet - _time.GetUtcNow();
        return quiet > TimeSpan.Zero ? (UnderstandingPause.Typing, quiet) : (UnderstandingPause.None, null);
    }

    private bool OnBattery()
    {
        try
        {
            return _onBattery();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Battery state unknown");
            return false; // not recognizable means: run
        }
    }

    private void EmbedMissing(Plan plan, CancellationToken ct)
    {
        foreach (var fingerprint in plan.MissingVectors)
        {
            ct.ThrowIfCancellationRequested();
            if (_store.Get(fingerprint) is not { } entry) continue;
            if (Embed(entry.Kind, entry.Text) is { } vector)
            {
                Write(ct, () => _store.SetVector(fingerprint, _embedModel, vector));
            }
            else
            {
                _unembeddable.Add(fingerprint); // tried again when the work changes
            }
        }

        plan.MissingVectors.Clear();
    }

    /// <summary>Ruling B: summaries stand for book sections (query role), core statements for notes (passage role).</summary>
    private float[]? Embed(UnderstandingKind kind, string text)
    {
        try
        {
            return _embed((kind == UnderstandingKind.Heading ? EmbeddingText.QueryPrefix : EmbeddingText.PassagePrefix) + text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Embedding an understanding text failed");
            return null;
        }
    }

    private void Fail(UnderstandingItem item, CancellationToken ct)
    {
        Write(ct, () => _store.RecordFailure(item.Fingerprint));
        _retryLater.Add(item.Fingerprint);
        _planStale = true;
    }

    private void Write(CancellationToken ct, Action write)
    {
        lock (_storeGate)
        {
            ct.ThrowIfCancellationRequested();
            write();
        }
    }

    private (string Title, string Summary) ChildSummary(Plan plan, string fingerprint) =>
        (plan.Items.TryGetValue(fingerprint, out var child) ? child.Title : "", _store.Get(fingerprint)?.Text ?? "");

    private void RecordDuration(TimeSpan duration)
    {
        _durations.Enqueue(duration);
        while (_durations.Count > DurationWindow) _durations.Dequeue();
    }

    private void Report(UnderstandingPause pause)
    {
        var open = _plan?.Open ?? 0;
        var total = _plan?.Total ?? 0;
        TimeSpan? remaining = _durations.Count >= MinDurations
            ? TimeSpan.FromTicks((long)_durations.Average(d => d.Ticks) * open)
            : null;
        var progress = new UnderstandingProgress(total - open, total, remaining, pause);
        if (progress == _progress) return;

        _progress = progress;
        try
        {
            ProgressChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Understanding progress handler failed");
        }
    }

    private Plan CurrentPlan()
    {
        lock (_gate)
        {
            if (_workChanged)
            {
                _planWork = _work;
                _workChanged = false;
                _planStale = true;
                _unembeddable.Clear();
            }
        }

        if (_planStale || _plan is null)
        {
            _plan = BuildPlan(_planWork);
            _planStale = false;
        }

        return _plan;
    }

    private Plan BuildPlan(Work work)
    {
        var items = new Dictionary<string, UnderstandingItem>(StringComparer.Ordinal);
        foreach (var item in work.Book.Concat(work.Notes).Concat(work.OtherBooks)) items.TryAdd(item.Fingerprint, item);

        // Done: has a current entry; Skipped: failed too often, or a sub-heading can never get an entry.
        var states = new Dictionary<string, ItemState>(StringComparer.Ordinal);
        foreach (var item in items.Values)
        {
            states[item.Fingerprint] = _store.Get(item.Fingerprint) is not null ? ItemState.Done
                : _store.Attempts(item.Fingerprint) >= MaxAttempts ? ItemState.Skipped
                : ItemState.Pending;
        }

        foreach (var item in work.Book.Concat(work.OtherBooks)) // children before parents
        {
            foreach (var child in item.ChildFingerprints)
            {
                if (!states.ContainsKey(child))
                    states[child] = _store.Get(child) is not null ? ItemState.Done : ItemState.Skipped;
            }

            if (states[item.Fingerprint] == ItemState.Pending && item.ChildFingerprints.Any(c => states[c] == ItemState.Skipped))
                states[item.Fingerprint] = ItemState.Skipped;
        }

        var priority = SectionAndAncestors(work.Book, work.SectionKey);
        var groups = new[]
        {
            work.Book.Where(i => priority.Contains(i.Fingerprint)),
            work.Book.Where(i => !priority.Contains(i.Fingerprint)),
            work.Notes,
            work.OtherBooks,
        };

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<UnderstandingItem>();
        foreach (var group in groups)
        {
            var members = group.Where(i => seen.Add(i.Fingerprint) && states[i.Fingerprint] == ItemState.Pending).ToList();
            var changed = members.Where(i => _store.LatestByIdentity(i.Kind, i.Identity) is not null).ToList();
            order.AddRange(changed);
            order.AddRange(members.Except(changed));
        }

        // A failed item is tried again after the rest.
        order = [.. order.Where(i => !_retryLater.Contains(i.Fingerprint)), .. order.Where(i => _retryLater.Contains(i.Fingerprint))];

        var counted = work.Book.Concat(work.Notes).Select(i => i.Fingerprint).ToHashSet(StringComparer.Ordinal);
        var open = counted.Count(fp => states[fp] == ItemState.Pending);
        var missing = _store.WithoutVector(_embedModel).Where(fp => !_unembeddable.Contains(fp)).ToList();
        return new Plan(order, states, items, counted.Count, open, missing);
    }

    /// <summary>Fingerprints of the current section, everything below it and its ancestors.</summary>
    private static HashSet<string> SectionAndAncestors(IReadOnlyList<UnderstandingItem> book, Guid? sectionKey)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (sectionKey is null || book.FirstOrDefault(i => i.Key == sectionKey) is not { } section) return result;

        var byFingerprint = new Dictionary<string, UnderstandingItem>(StringComparer.Ordinal);
        var parentOf = new Dictionary<string, UnderstandingItem>(StringComparer.Ordinal);
        foreach (var item in book)
        {
            byFingerprint.TryAdd(item.Fingerprint, item);
            foreach (var child in item.ChildFingerprints) parentOf.TryAdd(child, item);
        }

        var below = new Stack<UnderstandingItem>([section]);
        while (below.TryPop(out var item))
        {
            if (!result.Add(item.Fingerprint)) continue;
            foreach (var child in item.ChildFingerprints)
                if (byFingerprint.TryGetValue(child, out var childItem)) below.Push(childItem);
        }

        for (var fp = section.Fingerprint; parentOf.TryGetValue(fp, out var parent); fp = parent.Fingerprint)
        {
            if (!result.Add(parent.Fingerprint)) break;
        }

        return result;
    }

    private PendingCall? TakeCall()
    {
        lock (_gate)
        {
            while (_calls.TryDequeue(out var call))
            {
                if (!call.Result.Task.IsCompleted) return call;
            }

            return null;
        }
    }

    private Task WakeTask()
    {
        lock (_gate) return _wake.Task;
    }

    private void Signal()
    {
        TaskCompletionSource woken;
        lock (_gate)
        {
            woken = _wake;
            _wake = NewWake();
        }

        woken.TrySetResult();
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Waits for a signal, the delay (none: only a signal) or cancellation.</summary>
    private async Task WaitAsync(Task wake, TimeSpan? delay, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timer = delay is { } d ? Task.Delay(d, _time, stop.Token) : Task.Delay(Timeout.Infinite, stop.Token);
        await Task.WhenAny(wake, timer);
        stop.Cancel();
        ct.ThrowIfCancellationRequested();
    }

    private enum ItemState { Pending, Done, Skipped }

    private sealed record Work(IReadOnlyList<UnderstandingItem> Book, Guid? SectionKey, IReadOnlyList<UnderstandingItem> Notes,
        IReadOnlyList<UnderstandingItem> OtherBooks);

    private sealed record PendingCall(string System, string User, ChatOptions Options, CancellationToken Ct)
    {
        public TaskCompletionSource<string> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>The queue as computed from the work and the store: pending items in order, every item's state, the counts.</summary>
    private sealed record Plan(List<UnderstandingItem> Order, Dictionary<string, ItemState> States,
        Dictionary<string, UnderstandingItem> Items, int Total, int Open, List<string> MissingVectors)
    {
        /// <summary>The first pending item whose sub-headings all have a current entry.</summary>
        public UnderstandingItem? NextReady() =>
            Order.FirstOrDefault(i => States[i.Fingerprint] == ItemState.Pending &&
                                      i.ChildFingerprints.All(c => States.GetValueOrDefault(c) == ItemState.Done));
    }
}
