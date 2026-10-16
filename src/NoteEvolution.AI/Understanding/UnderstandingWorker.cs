using Microsoft.Extensions.Logging;
using NoteEvolution.AI.Embeddings;
using NoteEvolution.AI.Language;

namespace NoteEvolution.AI.Understanding;

/// <summary>
/// State of the background computation: <paramref name="Done"/> of <paramref name="Total"/> entries of the current book,
/// the notes and the placement checks (an entry skipped after too many failures counts as done), the estimated
/// <paramref name="Remaining"/> time (null until three answers were timed) and why the worker is waiting, if it is. Not
/// <paramref name="Planned"/> until the worker looked at the first work it was given: then nothing is known yet (the counts
/// are 0). <paramref name="Kinds"/> breaks the work down by kind (headings of all books); <paramref name="Current"/> is the
/// request with the server, if any.
/// </summary>
public sealed record UnderstandingProgress(int Done, int Total, TimeSpan? Remaining, UnderstandingPause Pause,
    bool Planned = true, IReadOnlyDictionary<UnderstandingKind, KindProgress>? Kinds = null,
    UnderstandingCurrent? Current = null);

/// <summary>
/// The work of one kind: <paramref name="Done"/> of <paramref name="Total"/> (skipped ones count as done and as
/// <paramref name="Failed"/>) and the estimated <paramref name="Remaining"/> time from this kind's mean answer time.
/// </summary>
public sealed record KindProgress(int Done, int Total, int Failed, TimeSpan? Remaining);

/// <summary>The request with the server: what kind of entry it computes and for which heading or note (session key).</summary>
public sealed record UnderstandingCurrent(UnderstandingKind Kind, Guid Key);

/// <summary>Why the worker waits although there is work: the user's switch, typing, battery, or the language-model server.</summary>
public enum UnderstandingPause { None, User, Typing, Battery, Unreachable, ModelMissing }

/// <summary>
/// The background queue of the language model: computes heading summaries (bottom-up), note core statements and, last,
/// placement checks one request at a time, stores them in the <see cref="UnderstandingStore"/> (summaries and core
/// statements with their meaning vectors), pauses for the user's switch, typing, battery and an unreachable server, retries
/// failures and reports progress per kind with a remaining-time estimate and the running request.
/// Once a plan has nothing left to do, the store is pruned to the items of that work (and the newest entry of each of
/// their identities), so it does not grow forever.
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

    /// <summary>A server that does not answer the version or model check within this time counts as unreachable.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How often the battery state is checked again while running on battery.</summary>
    public static readonly TimeSpan BatteryCheck = TimeSpan.FromSeconds(30);

    /// <summary>Failed attempts per item and fingerprint after which it is skipped until its content changes.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Different items failing in a row with a server error that make it a server problem rather than theirs.</summary>
    public const int ServerErrorItems = 3;

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
    private Work _work = new([], null, [], [], []);
    private bool _workChanged = true;
    private bool _workSet;
    private DateTimeOffset _lastTyping = DateTimeOffset.MinValue;
    private bool _paused;
    private bool _disposed;

    // Store writes against Dispose: once Dispose returned, nothing is written any more.
    private readonly Lock _storeGate = new();
    private readonly CancellationTokenSource _dispose = new();

    // Only touched by the loop.
    private Work _planWork = new([], null, [], [], []);
    private Plan? _plan;
    private bool _planStale = true;
    private bool _planned;
    private Plan? _prunedPlan;
    private readonly HashSet<string> _retryLater = [];
    private readonly HashSet<string> _unembeddable = [];
    private readonly Queue<TimeSpan> _durations = new();
    private readonly Dictionary<UnderstandingKind, Queue<TimeSpan>> _kindDurations = [];
    private UnderstandingCurrent? _current;
    private readonly List<string> _serverErrorStreak = [];
    private bool _serverTrouble;
    private bool _connected;
    private UnderstandingPause _connectionPause;
    private DateTimeOffset _nextConnectionCheck = DateTimeOffset.MinValue;

    private volatile UnderstandingProgress _progress = new(0, 0, null, UnderstandingPause.None, Planned: false);

    /// <summary>A request (background or <see cref="RunNextAsync"/>) is with the server.</summary>
    private volatile bool _busy;

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

    /// <summary>Whether a request (a background one or a <see cref="RunNextAsync"/> call) is with the server right now.</summary>
    public bool Busy => _busy;

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
    /// first, then the notes in the given order (the caller sorts them newest first), then the headings of other books, then
    /// the placement checks. Within each group, items whose content changed (an older entry of the same identity exists)
    /// come first.
    /// </summary>
    public void SetWork(IReadOnlyList<UnderstandingItem> currentBook, Guid? currentSectionKey,
        IReadOnlyList<UnderstandingItem> notes, IReadOnlyList<UnderstandingItem> otherBooks,
        IReadOnlyList<UnderstandingItem>? placements = null)
    {
        lock (_gate)
        {
            _work = new Work(currentBook, currentSectionKey, notes, otherBooks, placements ?? []);
            _workChanged = true;
            _workSet = true;
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
    /// <param name="waiting">Called at once (on the caller's thread) when another request is with the server, so this one waits.</param>
    /// <param name="started">Called on the worker's thread when this request goes to the server.</param>
    /// <param name="current">Reported as <see cref="UnderstandingProgress.Current"/> while this request is with the server.</param>
    public async Task<string> RunNextAsync(string system, string user, ChatOptions options, CancellationToken ct,
        Action? waiting = null, Action? started = null, UnderstandingCurrent? current = null)
    {
        var call = new PendingCall(system, user, options, ct, started, current);
        if (_busy)
        {
            waiting?.Invoke(); // before the call is queued, so it always comes before started
        }

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
                Prune(plan, ct);
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
        var (system, user, options) = item.Kind switch
        {
            UnderstandingKind.Placement => (item.Check!.System, item.Check.User, PlacementCheck.Options(item.Check)),
            UnderstandingKind.Heading => Request(
                UnderstandingPrompts.Summary(item, [.. item.ChildFingerprints.Select(c => ChildSummary(plan, c))]),
                UnderstandingPrompts.SummaryTokens),
            _ => Request(UnderstandingPrompts.Core(item), UnderstandingPrompts.CoreTokens),
        };

        var started = _time.GetTimestamp();
        string answer;
        try
        {
            using var timeout = new CancellationTokenSource(RequestTimeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            _busy = true;
            SetCurrent(new UnderstandingCurrent(item.Kind, item.Key));
            try
            {
                answer = await _chat.ChatAsync(_chatModel, system, user, options, linked.Token);
            }
            finally
            {
                _busy = false;
                SetCurrent(null);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Language model request for {Identity} timed out", item.Identity);
            _serverErrorStreak.Clear();
            Fail(item, ct);
            return;
        }
        catch (ChatModelUnavailableException ex)
        {
            _logger.LogWarning(ex, "Language model request for {Identity} failed", item.Identity);
            // Unreachable or model gone: pause without blaming the item; the server answered with an error: see ServerError.
            _connected = false;
            if (await ConnectAsync(ct)) ServerError(item, ct);
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Language model request for {Identity} failed", item.Identity);
            _serverErrorStreak.Clear();
            Fail(item, ct);
            return;
        }

        // The server answered: whatever failed before was not a server-wide problem.
        _serverErrorStreak.Clear();
        _serverTrouble = false;
        RecordDuration(item.Kind, _time.GetElapsedTime(started));
        if (item.Kind == UnderstandingKind.Placement)
        {
            StorePlacement(item, answer, ct);
            return;
        }

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

    private static (string System, string User, ChatOptions Options) Request((string System, string User) prompt, int tokens) =>
        (prompt.System, prompt.User, new ChatOptions(tokens));

    /// <summary>A usable verdict is stored as JSON without a vector; an unusable answer is a failed attempt.</summary>
    private void StorePlacement(UnderstandingItem item, string answer, CancellationToken ct)
    {
        if (PlacementCheck.Parse(answer, item.Check!) is not { } verdict)
        {
            _logger.LogWarning("Language model gave an unusable placement answer for {Identity}", item.Identity);
            Fail(item, ct);
            return;
        }

        // Like the other kinds: stored under the fingerprint it was requested for.
        Write(ct, () => _store.Put(new UnderstandingEntry(item.Kind, item.Fingerprint, item.Identity,
            PlacementCheck.Serialize(verdict), _chatModel, null, null)));
        _retryLater.Remove(item.Fingerprint);
        _planStale = true;
    }

    private async Task ServeAsync(PendingCall call, CancellationToken ct)
    {
        try
        {
            if (call.Result.Task.IsCompleted) return; // withdrawn while waiting
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, call.Ct);
            _busy = true;
            SetCurrent(call.Current);
            call.Started?.Invoke();
            var answer = await _chat.ChatAsync(_chatModel, call.System, call.User, call.Options, linked.Token);
            _busy = false; // before the caller continues
            SetCurrent(null);
            call.Result.TrySetResult(answer);
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
            _busy = false;
            if (_current is not null) SetCurrent(null);
            call.Result.TrySetCanceled(); // the worker stopped
        }
    }

    /// <summary>
    /// The reachable server answered a request with an error. One item failing among successes is that item's failed
    /// attempt. <see cref="ServerErrorItems"/> different items failing in a row is a server problem (out of memory, crashed
    /// runner): their attempts are taken back and the worker pauses as for an unreachable server; until a request succeeds
    /// again, every further error pauses again without charging the item.
    /// </summary>
    private void ServerError(UnderstandingItem item, CancellationToken ct)
    {
        if (!_serverTrouble)
        {
            Fail(item, ct);
            _serverErrorStreak.Add(item.Fingerprint);
            if (_serverErrorStreak.Distinct(StringComparer.Ordinal).Count() < ServerErrorItems) return;

            _logger.LogWarning("Language model server fails every request; pausing");
            foreach (var fingerprint in _serverErrorStreak) Write(ct, () => _store.ForgetFailure(fingerprint));
            _serverErrorStreak.Clear();
            _serverTrouble = true;
            _planStale = true;
        }

        PauseConnection(UnderstandingPause.Unreachable);
    }

    /// <summary>Checks server and model; on a problem sets the pause and the next check. Returns whether requests can go out.</summary>
    private async Task<bool> ConnectAsync(CancellationToken ct)
    {
        var pause = await ConnectionPauseAsync(ct);
        if (pause != UnderstandingPause.None)
        {
            PauseConnection(pause);
            return false;
        }

        _connected = true;
        _connectionPause = UnderstandingPause.None;
        return true;
    }

    /// <summary>No request goes out (except <see cref="RunNextAsync"/>) before the next check in <see cref="RetryInterval"/>.</summary>
    private void PauseConnection(UnderstandingPause pause)
    {
        if (pause != _connectionPause) _logger.LogInformation("Language model paused: {Pause}", pause);
        _connected = false;
        _connectionPause = pause;
        _nextConnectionCheck = _time.GetUtcNow() + RetryInterval;
        Report(pause);
    }

    private async Task<UnderstandingPause> ConnectionPauseAsync(CancellationToken ct)
    {
        // The HTTP client has no timeout of its own: a server that accepts the connection but never answers must not hang the loop.
        using var timeout = new CancellationTokenSource(CheckTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            if (await _chat.VersionAsync(linked.Token) is null) return UnderstandingPause.Unreachable;
            var models = await _chat.ModelsAsync(linked.Token);
            return models.Any(m => IsModel(m.Name)) ? UnderstandingPause.None : UnderstandingPause.ModelMissing;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("Language model server check timed out");
            return UnderstandingPause.Unreachable;
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

    /// <summary>
    /// Once per completed plan of given work: deletes what no item of it needs any more (an empty work before the first
    /// <see cref="SetWork"/> is not "everything is gone"). A failure is logged; the store is only a cache.
    /// </summary>
    private void Prune(Plan plan, CancellationToken ct)
    {
        if (!_planned || ReferenceEquals(plan, _prunedPlan)) return;
        _prunedPlan = plan;
        var fingerprints = new HashSet<string>(plan.Items.Keys, StringComparer.Ordinal);
        var identities = plan.Items.Values.Select(i => i.Identity).ToHashSet(StringComparer.Ordinal);
        try
        {
            Write(ct, () => _store.Prune(fingerprints, identities));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
        {
            _logger.LogWarning(ex, "Pruning the language model's store failed");
        }
    }

    private (string Title, string Summary) ChildSummary(Plan plan, string fingerprint) =>
        (plan.Items.TryGetValue(fingerprint, out var child) ? child.Title : "", _store.Get(fingerprint)?.Text ?? "");

    /// <summary>Keeps the last <see cref="DurationWindow"/> answer times overall and of the item's kind.</summary>
    private void RecordDuration(UnderstandingKind kind, TimeSpan duration)
    {
        if (!_kindDurations.TryGetValue(kind, out var ofKind)) _kindDurations[kind] = ofKind = new Queue<TimeSpan>();
        foreach (var window in new[] { _durations, ofKind })
        {
            window.Enqueue(duration);
            while (window.Count > DurationWindow) window.Dequeue();
        }
    }

    /// <summary>The mean answer time of a kind, else of all kinds; null until <see cref="MinDurations"/> answers were timed.</summary>
    private TimeSpan? MeanDuration(UnderstandingKind kind)
    {
        var window = _kindDurations.GetValueOrDefault(kind) is { Count: >= MinDurations } ofKind ? ofKind
            : _durations.Count >= MinDurations ? _durations
            : null;
        return window is null ? null : TimeSpan.FromTicks((long)window.Average(d => d.Ticks));
    }

    /// <summary>Sets and reports the request with the server, keeping the reported pause.</summary>
    private void SetCurrent(UnderstandingCurrent? current)
    {
        _current = current;
        Report(_progress.Pause);
    }

    private void Report(UnderstandingPause pause)
    {
        var open = _plan?.Open ?? 0;
        var total = _plan?.Total ?? 0;

        // Overall: the counted open items of each kind times that kind's mean; null if one of them has no estimate.
        TimeSpan? remaining = _durations.Count >= MinDurations ? TimeSpan.Zero : null;
        foreach (var (kind, countedOpen) in _plan?.CountedOpen ?? [])
        {
            remaining = remaining is { } sum && MeanDuration(kind) is { } mean ? sum + mean * countedOpen : null;
        }

        var kinds = _plan?.Kinds.ToDictionary(k => k.Key, k => new KindProgress(k.Value.Done, k.Value.Total, k.Value.Failed,
            MeanDuration(k.Key) * (k.Value.Total - k.Value.Done)));
        var progress = new UnderstandingProgress(total - open, total, remaining, pause, _planned, kinds, _current);
        if (progress with { Kinds = _progress.Kinds } == _progress && SameKinds(kinds, _progress.Kinds)) return;

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

    private static bool SameKinds(IReadOnlyDictionary<UnderstandingKind, KindProgress>? a,
        IReadOnlyDictionary<UnderstandingKind, KindProgress>? b) =>
        a is null || b is null
            ? a == b
            : a.Count == b.Count && a.All(k => b.TryGetValue(k.Key, out var other) && other == k.Value);

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
                _planned = _workSet;
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
        foreach (var item in work.Book.Concat(work.Notes).Concat(work.OtherBooks).Concat(work.Placements))
            items.TryAdd(item.Fingerprint, item);

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

        // An ancestor waits for all its children, so its whole chapter comes first; the section's own path before its siblings.
        var (path, chapter) = SectionAndChapter(work.Book, work.SectionKey);
        var groups = new[]
        {
            work.Book.Where(i => path.Contains(i.Fingerprint)),
            work.Book.Where(i => chapter.Contains(i.Fingerprint) && !path.Contains(i.Fingerprint)),
            work.Book.Where(i => !chapter.Contains(i.Fingerprint)),
            work.Notes,
            work.OtherBooks,
            work.Placements,
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

        var counted = work.Book.Concat(work.Notes).Concat(work.Placements).Select(i => i.Fingerprint)
            .ToHashSet(StringComparer.Ordinal);
        var countedOpen = counted.Where(fp => states[fp] == ItemState.Pending)
            .GroupBy(fp => items[fp].Kind)
            .ToDictionary(g => g.Key, g => g.Count());
        var kinds = Enum.GetValues<UnderstandingKind>().ToDictionary(kind => kind, kind =>
        {
            var ofKind = items.Values.Where(i => i.Kind == kind).Select(i => states[i.Fingerprint]).ToList();
            return new KindCount(ofKind.Count(s => s != ItemState.Pending), ofKind.Count, ofKind.Count(s => s == ItemState.Skipped));
        });
        var missing = _store.WithoutVector(_embedModel).Where(fp => !_unembeddable.Contains(fp)).ToList();
        return new Plan(order, states, items, counted.Count, countedOpen.Values.Sum(), countedOpen, kinds, missing);
    }

    /// <summary>
    /// <c>Path</c>: fingerprints of the current section, everything below it and its ancestors. <c>Chapter</c>: everything
    /// below the outermost ancestor (the current top-level chapter), including it.
    /// </summary>
    private static (HashSet<string> Path, HashSet<string> Chapter) SectionAndChapter(IReadOnlyList<UnderstandingItem> book,
        Guid? sectionKey)
    {
        var path = new HashSet<string>(StringComparer.Ordinal);
        if (sectionKey is null || book.FirstOrDefault(i => i.Key == sectionKey) is not { } section) return (path, path);

        var byFingerprint = new Dictionary<string, UnderstandingItem>(StringComparer.Ordinal);
        var parentOf = new Dictionary<string, UnderstandingItem>(StringComparer.Ordinal);
        foreach (var item in book)
        {
            byFingerprint.TryAdd(item.Fingerprint, item);
            foreach (var child in item.ChildFingerprints) parentOf.TryAdd(child, item);
        }

        AddSubtree(section, byFingerprint, path);
        var top = section;
        while (parentOf.TryGetValue(top.Fingerprint, out var parent) && path.Add(parent.Fingerprint)) top = parent;

        var chapter = new HashSet<string>(StringComparer.Ordinal);
        AddSubtree(top, byFingerprint, chapter);
        return (path, chapter);
    }

    private static void AddSubtree(UnderstandingItem root, Dictionary<string, UnderstandingItem> byFingerprint,
        HashSet<string> result)
    {
        var below = new Stack<UnderstandingItem>([root]);
        while (below.TryPop(out var item))
        {
            if (!result.Add(item.Fingerprint)) continue;
            foreach (var child in item.ChildFingerprints)
                if (byFingerprint.TryGetValue(child, out var childItem)) below.Push(childItem);
        }
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
        IReadOnlyList<UnderstandingItem> OtherBooks, IReadOnlyList<UnderstandingItem> Placements);

    private sealed record PendingCall(string System, string User, ChatOptions Options, CancellationToken Ct, Action? Started,
        UnderstandingCurrent? Current)
    {
        public TaskCompletionSource<string> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>The items of one kind in a plan: done (or skipped), all, skipped.</summary>
    private sealed record KindCount(int Done, int Total, int Failed);

    /// <summary>
    /// The queue as computed from the work and the store: pending items in order, every item's state, the counted items
    /// (<c>Total</c>, <c>Open</c>, open ones per kind) and per kind over all items.
    /// </summary>
    private sealed record Plan(List<UnderstandingItem> Order, Dictionary<string, ItemState> States,
        Dictionary<string, UnderstandingItem> Items, int Total, int Open, Dictionary<UnderstandingKind, int> CountedOpen,
        Dictionary<UnderstandingKind, KindCount> Kinds, List<string> MissingVectors)
    {
        /// <summary>The first pending item whose sub-headings all have a current entry.</summary>
        public UnderstandingItem? NextReady() =>
            Order.FirstOrDefault(i => States[i.Fingerprint] == ItemState.Pending &&
                                      i.ChildFingerprints.All(c => States.GetValueOrDefault(c) == ItemState.Done));
    }
}
