using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NoteEvolution.AI.Embeddings;
using NoteEvolution.AI.Language;
using NoteEvolution.AI.Relevance;
using NoteEvolution.AI.Search;
using NoteEvolution.AI.Semantic;
using NoteEvolution.AI.Understanding;
using NoteEvolution.Core.Assistants;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Links;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Storage;
using NoteEvolution.Core.Vaults;
using NoteEvolution.UI.Platform;

namespace NoteEvolution.UI.State;

/// <summary>
/// An open vault with all services working on it. Pages are edited, saved and reconciled with external changes on
/// one thread, the UI thread: watcher events reach <see cref="HandleExternalChange"/> through the dispatcher given to
/// <see cref="OpenAsync"/>, and <see cref="PagesChanged"/> and <see cref="ConflictDetected"/> are raised there.
/// <para>
/// With an <see cref="AiRuntime"/> whose model is installed, the notes are embedded in the background after opening
/// (<see cref="Semantic"/>, spec 5.1 step 4); a page replaced in the vault is embedded again <see cref="PageIndexDelay"/>
/// after its last change. Search and relevance fall back to full text and "unavailable" while there is no index.
/// </para>
/// <para>
/// With a <see cref="LanguageModelRuntime"/> that is <see cref="LanguageModelState.Ready"/> and a semantic index, the
/// language model's background work runs for this vault (spec 4): the <see cref="UnderstandingStore"/> in the vault's
/// <c>.noteevolution</c> folder, the <see cref="UnderstandingWorker"/> fed with snapshots taken on the UI thread (on
/// opening, <see cref="PageIndexDelay"/> after page changes, when the current book changes and <see cref="FocusFeedDelay"/>
/// after the current section last changed; the items are computed from the snapshots in the background), and
/// <see cref="Relevance"/> with the summaries and core statements. While the model is not ready, or the worker finds the
/// server gone, relevance works as without it. Nothing of it writes a note or book file. While the
/// <see cref="ISystemMonitor"/> tells that the computer runs on battery, the work pauses (read at most every
/// <see cref="BatteryReadInterval"/>).
/// </para>
/// </summary>
public sealed class VaultSession : IDisposable
{
    /// <summary>How long a replaced page is left alone before its notes are embedded again (debounced per path).</summary>
    public static readonly TimeSpan PageIndexDelay = TimeSpan.FromMilliseconds(2000);

    /// <summary>How long the current section must stay before the worker is fed with it (debounced).</summary>
    public static readonly TimeSpan FocusFeedDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>How long the battery state read from the <see cref="ISystemMonitor"/> is used before it is read again.</summary>
    public static readonly TimeSpan BatteryReadInterval = TimeSpan.FromSeconds(30);

    private readonly ILogger _logger;
    private readonly Func<Action, Task> _dispatch;
    private readonly BackupService _backups;
    private readonly FullTextSearchService _search;
    private readonly HashSet<string> _openConflicts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private List<string>? _changesDuringOpen = [];
    private bool _disposed;

    private readonly AiRuntime? _ai;
    private readonly TimeProvider _time;

    /// <summary>Cancelled when the session closes; never disposed, so late callers can still read its token.</summary>
    private readonly CancellationTokenSource _aiLifetime = new();

    /// <summary>The pending re-index of each replaced page (full path); guarded by <see cref="_gate"/>.</summary>
    private readonly Dictionary<string, ITimer> _pageIndexTimers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set once, under <see cref="_gate"/>, when the semantic index exists; never after <see cref="Dispose"/>.</summary>
    private volatile AiParts? _aiParts;

    /// <summary>Why the semantic index could not be created (e.g. its cache could not be opened).</summary>
    private volatile string? _aiError;

    private int _aiStarted;

    private readonly LanguageModelRuntime? _language;

    /// <summary>Tells the worker whether the computer runs on battery; without it the work never pauses for that.</summary>
    private readonly ISystemMonitor? _monitor;

    /// <summary>The battery state last read and when; guarded by <see cref="_gate"/>.</summary>
    private (DateTimeOffset At, bool OnBattery)? _battery;

    /// <summary>The language model's parts while it is ready; set and cleared under <see cref="_gate"/>.</summary>
    private volatile UnderstandingParts? _understanding;

    /// <summary>Feeds the worker again after page changes (debounced like the page index); guarded by <see cref="_gate"/>.</summary>
    private ITimer? _feedTimer;

    /// <summary>Feeds the worker after the current section changed (debounced); guarded by <see cref="_gate"/>.</summary>
    private ITimer? _focusTimer;

    // On the UI thread: the current book (link name) and section the worker puts first, and the notes' snapshot (newest
    // first; taken again after page changes).
    private string? _focusBook;
    private Guid _focusSection;
    private IReadOnlyList<NoteSnapshot>? _noteSnapshot;

    /// <summary>The number of the last feed started; only its result is applied (under <see cref="_feedGate"/>).</summary>
    private int _feedsStarted;
    private readonly object _feedGate = new();
    private int _feedsApplied;

    private VaultSession(
        string root, IClock clock, ILogger logger, TimeProvider timeProvider, Func<Action, Task> dispatch, AiRuntime? ai,
        LanguageModelRuntime? language, ISystemMonitor? monitor)
    {
        _logger = logger;
        _monitor = monitor;
        _dispatch = dispatch;
        _ai = ai;
        _language = language;
        _time = timeProvider;
        var vault = Core.Vaults.Vault.Open(root);
        var registry = new SelfWriteRegistry();
        _backups = new BackupService(vault.Root, clock);
        _search = new FullTextSearchService();
        Vault = vault;
        Notes = new NoteRepository(vault);
        Writer = new PageWriter(vault, _backups, registry);
        Undo = new UndoManager();
        Links = new LinkService(vault, Writer, Undo, new PendingStore(vault.Root));
        Checker = new LinkChecker(vault, Links);
        Changes = new ExternalChangeHandler(vault);
        Handled = new HandledConverter(Notes, Writer, Links);
        Drafts = new DraftConverter(_backups, Writer);
        Watcher = new VaultWatcher(vault, registry, timeProvider);
        Watcher.ExternalChange += OnWatcherChange;
        if (_ai is not null)
        {
            _ai.ModelChanged += OnModelChanged;
            vault.PageReplaced += OnPageReplaced;
        }

        if (_language is not null)
        {
            _language.Changed += OnLanguageChanged;
        }
    }

    public IVault Vault { get; }

    public INoteRepository Notes { get; }

    public IPageWriter Writer { get; }

    public ILinkService Links { get; }

    public UndoManager Undo { get; }

    /// <summary>Hybrid (full text and meaning) once the semantic index exists, otherwise full text only.</summary>
    public ISearchService Search => (ISearchService?)_aiParts?.Search ?? _search;

    /// <summary>The embeddings of the notes; <c>null</c> without AI, while the model is missing or could not be loaded.</summary>
    public ISemanticIndex? Semantic => _aiParts?.Index;

    /// <summary>
    /// Meaning-based suggestions; without a semantic index a service that is never available. With the language model's
    /// understanding while the model is ready and its server reachable, otherwise without it (as before).
    /// </summary>
    public IRelevanceService Relevance =>
        _understanding is { ServerAway: false } understanding
            ? understanding.Relevance
            : (IRelevanceService?)_aiParts?.Relevance ?? UnavailableRelevance.Instance;

    /// <summary>The language model's background progress; <c>null</c> while it does not run for this vault.</summary>
    public UnderstandingProgress? Understanding => _understanding?.Worker.Progress;

    /// <summary>
    /// The background work found the language model's server unreachable or the model missing and got no answer since;
    /// <see cref="AiStatusChanged"/> is raised when this changes.
    /// </summary>
    public bool LanguageModelAway => _understanding is { ServerAway: true };

    /// <summary>How many feeds reached the worker (stale ones are dropped); for tests.</summary>
    internal int FeedsApplied => Volatile.Read(ref _feedsApplied);

    /// <summary><see cref="Understanding"/> may have changed (also from or to <c>null</c>); raised on the UI thread.</summary>
    public event Action? UnderstandingChanged;

    /// <summary>The AI's state for this vault: the runtime's (model missing, downloading, load failed) or the index's.</summary>
    public AiStatus AiStatus
    {
        get
        {
            if (_ai is null)
            {
                return AiStatus.Off;
            }

            if (_aiParts is { } parts)
            {
                var status = parts.Index.Status;
                return status.State switch
                {
                    SemanticState.Ready => new AiStatus(AiState.Ready, status.Done, status.Total, null),
                    SemanticState.Failed => new AiStatus(AiState.Failed, status.Done, status.Total, status.Error),
                    _ => new AiStatus(AiState.Indexing, status.Done, status.Total, null),
                };
            }

            if (_aiError is { } error)
            {
                return AiStatus.Failed(error);
            }

            // A usable model without an index yet: the index is about to be created.
            var runtime = _ai.Status;
            return runtime.State == AiState.Ready ? AiStatus.Of(AiState.Indexing) : runtime;
        }
    }

    /// <summary>
    /// <see cref="AiStatus"/> may have changed; raised on the UI thread (through the dispatcher). Also raised when a
    /// page's notes were embedded again after a change (the status stays <see cref="AiState.Ready"/> then, but the
    /// notes' keys are new), so that views of the semantic index read it again.
    /// </summary>
    public event Action? AiStatusChanged;

    public LinkChecker Checker { get; }

    public ExternalChangeHandler Changes { get; }

    /// <summary>The one-time assistant for <c>[handled]</c> notes (spec 5.5).</summary>
    public HandledConverter Handled { get; }

    /// <summary>The assistant that turns a plain outline into a book (spec 5.6).</summary>
    public DraftConverter Drafts { get; }

    public VaultWatcher Watcher { get; }

    /// <summary>The link check run on opening; a report with entries is shown to the user.</summary>
    public LinkCheckReport? StartupReport { get; private set; }

    /// <summary>
    /// A page (full path) was reloaded, merged or removed after an external change, or resolved after a conflict;
    /// the search index is already updated. Views of that page are rebuilt from the vault.
    /// </summary>
    public event Action<string>? PagesChanged;

    /// <summary>Unsaved local changes collide with an external change; until it is resolved the page is not saved.</summary>
    public event Action<ExternalChangeOutcome.Conflict>? ConflictDetected;

    /// <summary>A conflict was opened or closed (<see cref="HasAnyOpenConflict"/> may have changed); raised on the UI thread.</summary>
    public event Action? ConflictsChanged;

    /// <summary>
    /// Raised on the UI thread with the full path just before an external change of that file is brought in. A view
    /// holding unsaved edits of the page (the editor's pending text) puts them into the page now, without saving: the
    /// change handling then merges them with the external version instead of the edits later overwriting it.
    /// </summary>
    public event Action<string>? ExternalChangeStarting;

    /// <summary>
    /// Some conflict waits for the user's decision. Operations that may write any page (undo, and every UI-triggered
    /// write) are refused meanwhile, so no deleted file is recreated and no unresolved conflict is overwritten.
    /// </summary>
    public bool HasAnyOpenConflict
    {
        get
        {
            lock (_gate)
            {
                return _openConflicts.Count > 0;
            }
        }
    }

    /// <summary>
    /// Opens the vault on a background thread (spec 5.1): parse all files, start the watcher, retry pending note
    /// updates, run the link check (<see cref="StartupReport"/>), build the search index, clean up old backups; then
    /// the semantic indexing starts in the background (with <paramref name="ai"/>), without holding up the opening.
    /// A failing step after parsing is logged and does not stop the opening.
    /// </summary>
    /// <param name="logger">Receives errors that are handled here; none if <c>null</c>.</param>
    /// <param name="timeProvider">
    /// The clock for the watcher's debounce and <see cref="PageIndexDelay"/>; <see cref="TimeProvider.System"/> if <c>null</c>.
    /// </param>
    /// <param name="dispatch">
    /// Runs an action on the UI thread (e.g. a component's <c>InvokeAsync</c>); watcher events, the start of the
    /// semantic indexing and <see cref="AiStatusChanged"/> go through it. If <c>null</c>, they run on the calling thread.
    /// </param>
    /// <param name="ai">The app's AI runtime; without it the AI is off (<see cref="AiState.Off"/>).</param>
    /// <param name="language">The app's language model; without it there is no background understanding.</param>
    /// <param name="monitor">Tells whether the computer runs on battery (the background understanding pauses then).</param>
    /// <exception cref="DirectoryNotFoundException"><paramref name="root"/> is not a folder.</exception>
    public static Task<VaultSession> OpenAsync(
        string root, IClock clock, ILogger? logger = null, TimeProvider? timeProvider = null, Func<Action, Task>? dispatch = null,
        AiRuntime? ai = null, LanguageModelRuntime? language = null, ISystemMonitor? monitor = null)
    {
        if (!Directory.Exists(root))
        {
            return Task.FromException<VaultSession>(new DirectoryNotFoundException($"The folder '{root}' does not exist."));
        }

        return Task.Run(() =>
        {
            var session = new VaultSession(
                root, clock, logger ?? NullLogger.Instance, timeProvider ?? TimeProvider.System, dispatch ?? RunInline, ai,
                language, monitor);
            try
            {
                session.Start();
                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        });
    }

    /// <summary>
    /// Brings in the external change of <paramref name="path"/> (<see cref="ExternalChangeHandler.Handle"/>). Call it on
    /// the UI thread. A merged page is saved right away; a conflict is reported through <see cref="ConflictDetected"/>.
    /// Never throws: failures are logged (the watcher reports the file again when it changes). Does nothing once the
    /// session is disposed (a call queued on the UI thread may arrive after a vault switch).
    /// </summary>
    public void HandleExternalChange(string path)
    {
        if (IsDisposed)
        {
            return;
        }

        var fullPath = Path.GetFullPath(path);
        try
        {
            ExternalChangeStarting?.Invoke(fullPath);
            var outcome = Changes.Handle(fullPath);
            if (outcome is ExternalChangeOutcome.Conflict conflict)
            {
                SetConflictOpen(fullPath, true);
                _logger.LogWarning("Conflict between local and external changes in {Path}", fullPath);
                ConflictDetected?.Invoke(conflict);
                return;
            }

            SetConflictOpen(fullPath, false);
            if (outcome is ExternalChangeOutcome.Reloaded { Page: { IsDirty: true, IsReadOnly: false } merged })
            {
                SaveMerged(merged);
            }

            PageChanged(fullPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Handling the external change of {Path} failed", fullPath);
        }
    }

    /// <summary>A conflict for <paramref name="path"/> waits for the user's decision; the page is not saved meanwhile.</summary>
    public bool HasOpenConflict(string path)
    {
        lock (_gate)
        {
            return _openConflicts.Contains(Path.GetFullPath(path));
        }
    }

    /// <summary>
    /// Applies the user's choices (<see cref="ExternalChangeHandler.Resolve"/>), closes the conflict and raises
    /// <see cref="PagesChanged"/>. Saving the resulting page is left to the caller (<see cref="TrySave"/>).
    /// </summary>
    /// <exception cref="FileChangedExternallyException">
    /// The file changed again since the conflict was reported; the choices were not applied. The change is handled
    /// again right away (<see cref="HandleExternalChange"/>): that either reports a new conflict or merges the page and
    /// closes the conflict, so check <see cref="HasOpenConflict"/> afterwards.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.</exception>
    public void ResolveConflict(ExternalChangeOutcome.Conflict conflict, IReadOnlyDictionary<Guid, ConflictChoice> choices)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var fullPath = Path.GetFullPath(conflict.Local.FilePath);
        try
        {
            Changes.Resolve(conflict, choices);
        }
        catch (FileChangedExternallyException)
        {
            HandleExternalChange(fullPath);
            throw;
        }

        SetConflictOpen(fullPath, false);
        PageChanged(fullPath);
    }

    /// <summary>
    /// Reverses the latest operation (<see cref="UndoManager.Undo"/>), unless a conflict is open: undoing writes pages
    /// directly and could recreate a deleted file or overwrite an unresolved conflict. Failures are logged.
    /// </summary>
    /// <returns><c>true</c> if the undo ran without error (or there was nothing to undo); otherwise <paramref name="error"/> says why.</returns>
    /// <exception cref="ObjectDisposedException">The session is disposed.</exception>
    public bool TryUndo(out Exception? error)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (HasAnyOpenConflict)
        {
            error = new InvalidOperationException("A conflict is open; nothing is undone until it is resolved.");
            return false;
        }

        try
        {
            Undo.Undo();
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            _logger.LogError(ex, "Undo failed");
            error = ex;
            return false;
        }
    }

    /// <summary>
    /// Saves <paramref name="page"/> unless a conflict for its file is open. If the file was changed by another program
    /// in the meantime (<see cref="FileChangedExternallyException"/>), the change is handled right away
    /// (<see cref="HandleExternalChange"/>: merged and saved, or reported as a conflict); <paramref name="page"/> itself
    /// is then not saved, and views take the page from the vault again (<see cref="PagesChanged"/>).
    /// </summary>
    /// <returns><c>true</c> if <paramref name="page"/> was written; otherwise <paramref name="error"/> says why.</returns>
    /// <exception cref="ObjectDisposedException">The session is disposed.</exception>
    public bool TrySave(Page page, out Exception? error)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (HasOpenConflict(page.FilePath))
        {
            error = new InvalidOperationException($"A conflict for '{page.FilePath}' is open; the page is not saved.");
            return false;
        }

        try
        {
            Writer.Save(page);
            error = null;
            return true;
        }
        catch (FileChangedExternallyException ex)
        {
            error = ex;
            HandleExternalChange(page.FilePath);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogError(ex, "Saving {Path} failed", page.FilePath);
            error = ex;
            return false;
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
            foreach (var timer in _pageIndexTimers.Values)
            {
                timer.Dispose();
            }

            _pageIndexTimers.Clear();
            _feedTimer?.Dispose();
            _focusTimer?.Dispose();
        }

        if (_ai is not null)
        {
            _ai.ModelChanged -= OnModelChanged;
            Vault.PageReplaced -= OnPageReplaced;
        }

        if (_language is not null)
        {
            _language.Changed -= OnLanguageChanged;
        }

        // The worker stops its request (nothing of it is stored) before its store closes; then the index stops its run
        // and closes its cache. The shared embedder stays with the runtime.
        _aiLifetime.Cancel();
        StopUnderstanding();
        _aiParts?.Index.Dispose();
        Watcher.ExternalChange -= OnWatcherChange;
        Watcher.Dispose();
        _search.Dispose();
    }

    private bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _disposed;
            }
        }
    }

    private void SetConflictOpen(string fullPath, bool open)
    {
        bool changed;
        lock (_gate)
        {
            changed = open ? _openConflicts.Add(fullPath) : _openConflicts.Remove(fullPath);
        }

        if (changed)
        {
            ConflictsChanged?.Invoke();
        }
    }

    private static Task RunInline(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    private void Start()
    {
        Watcher.Start();
        Guarded("Retrying pending note updates", () => Links.RetryPending());
        Guarded("The link check", () => StartupReport = Checker.Analyze());
        Guarded("Building the search index", () => Search.Rebuild(Notes));
        Guarded("Cleaning up backups", _backups.Cleanup);

        // Changes reported while the vault was being opened are handled now, in order.
        List<string> early;
        lock (_gate)
        {
            early = _changesDuringOpen!;
            _changesDuringOpen = null;
        }

        foreach (var path in early.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Dispatch(path);
        }

        StartAi();
    }

    private void Guarded(string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Step} failed while opening the vault", step);
        }
    }

    /// <summary>Raised on the watcher's thread: waits for the end of the opening, then goes to the UI thread.</summary>
    private void OnWatcherChange(string path)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_changesDuringOpen is not null)
            {
                _changesDuringOpen.Add(path);
                return;
            }
        }

        Dispatch(path);
    }

    private void Dispatch(string path) => Dispatch(() => HandleExternalChange(path), "the external change of " + path);

    /// <summary>Runs <paramref name="action"/> through the dispatcher; never throws, a failure is logged.</summary>
    private void Dispatch(Action action, string what)
    {
        try
        {
            _dispatch(action).ContinueWith(
                t => _logger.LogError(t.Exception, "Dispatching {What} failed", what),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dispatching {What} failed", what);
        }
    }

    /// <summary>The local changes merged into the external version are written at once, so they cannot get lost.</summary>
    private void SaveMerged(Page merged)
    {
        try
        {
            Writer.Save(merged);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // If the file changed yet again, the watcher reports it and the next merge saves.
            _logger.LogWarning(ex, "Saving the merged page {Path} failed", merged.FilePath);
        }
    }

    private void PageChanged(string fullPath)
    {
        Search.UpdatePage(Notes, fullPath);

        // A removed page raises no PageReplaced; this way its notes leave the semantic index too.
        SchedulePageIndex(fullPath);
        PagesChanged?.Invoke(fullPath);
    }

    // ---- AI ----

    /// <summary>
    /// Creates the semantic index once the model is installed (when opening, or when a download completed): the
    /// embedder is loaded on a background thread, the indexing is started on the UI thread (it reads the notes there).
    /// </summary>
    private void StartAi()
    {
        if (_ai is null || IsDisposed || !_ai.ModelInstalled || Interlocked.Exchange(ref _aiStarted, 1) == 1)
        {
            return;
        }

        _ = Task.Run(CreateIndex);
    }

    private void CreateIndex()
    {
        try
        {
            // A failed load shows in the runtime's status (LoadError).
            if (_ai!.Embedder is not { } embedder)
            {
                return;
            }

            var index = new SemanticIndex(embedder, VectorCache.Open(Vault.Root, _ai.Model.Id), _ai.Model.Id, _logger);
            lock (_gate)
            {
                if (_disposed)
                {
                    index.Dispose();
                    return;
                }

                _aiParts = new AiParts(
                    index, new HybridSearchService(_search, index, Notes), new RelevanceService(index), embedder, _ai.Model.Id);
            }

            index.StatusChanged += RaiseAiStatusChanged;
            Dispatch(StartIndexing, "the start of the semantic indexing");
            Dispatch(UpdateUnderstanding, "the start of the language model");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Creating the semantic index failed");
            _aiError = ex.Message;
        }
        finally
        {
            RaiseAiStatusChanged();
        }
    }

    /// <summary>On the UI thread: embeds every note in the background.</summary>
    private void StartIndexing()
    {
        if (IsDisposed || _aiParts is not { } parts)
        {
            return;
        }

        Observe(() => parts.Index.RebuildAsync(Notes, _aiLifetime.Token), "Semantic indexing");
    }

    /// <summary>A download started or ended (on the download's thread).</summary>
    private void OnModelChanged()
    {
        StartAi();
        RaiseAiStatusChanged();
    }

    /// <summary>Raised on the thread that replaced the page (the UI thread, or the opening's thread).</summary>
    private void OnPageReplaced(Page page) => SchedulePageIndex(page.FilePath);

    /// <summary>(Re)starts the page's debounce timer; when it fires, the page is embedded again on the UI thread.</summary>
    private void SchedulePageIndex(string path)
    {
        if (_ai is null)
        {
            return;
        }

        var fullPath = Path.GetFullPath(path);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            ScheduleFeed();
            if (_pageIndexTimers.TryGetValue(fullPath, out var timer))
            {
                timer.Change(PageIndexDelay, Timeout.InfiniteTimeSpan);
                return;
            }

            _pageIndexTimers[fullPath] = _time.CreateTimer(
                _ => OnPageIndexDue(fullPath), null, PageIndexDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Raised on a timer thread.</summary>
    private void OnPageIndexDue(string fullPath)
    {
        lock (_gate)
        {
            if (_disposed || !_pageIndexTimers.Remove(fullPath, out var timer))
            {
                return;
            }

            timer.Dispose();
        }

        Dispatch(() => IndexPage(fullPath), "the semantic update of " + fullPath);
    }

    /// <summary>
    /// On the UI thread. Without an index there is nothing to do: the indexing that comes with it reads every page.
    /// The update is cancelled only when the session closes (a cancelled update leaves the page out of the index).
    /// </summary>
    private void IndexPage(string fullPath)
    {
        if (IsDisposed || _aiParts is not { } parts)
        {
            return;
        }

        Observe(
            async () =>
            {
                if (await parts.Index.UpdatePageAsync(Notes, fullPath, _aiLifetime.Token))
                {
                    RaiseAiStatusChanged();
                }
            },
            "Updating the semantic index");
    }

    /// <summary>Starts the work and logs its failure; a cancellation (the session closed) ends quietly.</summary>
    private void Observe(Func<Task> start, string what)
    {
        try
        {
            start().ContinueWith(
                t => _logger.LogError(t.Exception, "{What} failed", what),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{What} failed", what);
        }
    }

    /// <summary>Called on any thread (the index reports from its background thread); never throws.</summary>
    private void RaiseAiStatusChanged()
    {
        if (IsDisposed)
        {
            return;
        }

        Dispatch(
            () =>
            {
                if (!IsDisposed)
                {
                    AiStatusChanged?.Invoke();
                }
            },
            "the AI status");
    }

    // ---- Language model ----

    /// <summary>Called after every input in the editor: the background work waits until nothing was typed for a while (spec 4).</summary>
    public void NotifyTyping() => _understanding?.Worker.NotifyTyping();

    /// <summary>
    /// "Genauer prüfen" (spec 5.2): asks the language model which of the note's best topics and places fits, as the
    /// worker's next request (after a running background request), within <see cref="UnderstandingWorker.RequestTimeout"/>.
    /// Call it on the UI thread; topics and places are computed in the background from the given snapshots.
    /// </summary>
    /// <param name="noteText"><c>EmbeddingText.ForNote(note)</c>, as for <see cref="IRelevanceService.Placements"/>.</param>
    /// <param name="waiting">Called when the request has to wait behind a running background request (spec 5.2).</param>
    /// <param name="started">Called (on the worker's thread) when the request goes to the server.</param>
    /// <returns>
    /// The verdict, or why there is none: <see cref="PlacementCheckFailure.Unavailable"/> without a ready model or when the
    /// server fails, <see cref="PlacementCheckFailure.NoAnswer"/> when the time ran out or the search for topics and places failed, <see cref="PlacementCheckFailure.Unusable"/>
    /// for an answer that names no offered topic and place (or without topics to offer), <see cref="PlacementCheckFailure.Cancelled"/>
    /// when <paramref name="ct"/> was cancelled.
    /// </returns>
    public async Task<(PlacementVerdict? Verdict, PlacementCheckFailure? Failure)> CheckPlacementAsync(
        Guid noteKey, string noteText, Book book, CancellationToken ct, Action? waiting = null, Action? started = null)
    {
        if (IsDisposed || _understanding is not { } understanding)
        {
            return (null, PlacementCheckFailure.Unavailable);
        }

        var relevance = Relevance;
        var text = noteText.StartsWith(EmbeddingText.PassagePrefix, StringComparison.Ordinal)
            ? noteText[EmbeddingText.PassagePrefix.Length..]
            : noteText;
        PlacementCheckPrompt prompt;
        try
        {
            // Only snapshots (the book, the note's text and key) go to the background.
            prompt = await Task.Run(
                () =>
                {
                    var topics = relevance.Topics(noteKey, noteText, book, PlacementCheck.TopicLimit);
                    var places = relevance.Placements(noteKey, noteText, book, int.MaxValue)
                        .GroupBy(hit => hit.SectionKey)
                        .ToDictionary(group => group.Key, group => (IReadOnlyList<PlacementHit>)[.. group]);
                    return PlacementCheck.Prompt(book, text, topics, places,
                        section => understanding.Understanding.Heading(book, section)?.Text);
                },
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (null, PlacementCheckFailure.Cancelled);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Searching topics and places for the placement check failed");
            return (null, PlacementCheckFailure.NoAnswer);
        }

        if (prompt.Topics.Count == 0)
        {
            return (null, PlacementCheckFailure.Unusable);
        }

        using var timeout = new CancellationTokenSource(UnderstandingWorker.RequestTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            var answer = await understanding.Worker.RunNextAsync(
                prompt.System, prompt.User, PlacementCheck.Options(prompt), linked.Token,
                waiting, started);
            return PlacementCheck.Parse(answer, prompt) is { } verdict
                ? (verdict, null)
                : (null, PlacementCheckFailure.Unusable);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (null, PlacementCheckFailure.Cancelled);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            _logger.LogWarning("The placement check timed out");
            return (null, PlacementCheckFailure.NoAnswer);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or ChatModelUnavailableException)
        {
            // The server failed, or the worker stopped (the model was switched off, the vault closed).
            _logger.LogWarning(ex, "The placement check failed");
            return (null, PlacementCheckFailure.Unavailable);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The placement check failed");
            return (null, PlacementCheckFailure.NoAnswer);
        }
    }

    /// <summary>
    /// The topic summary of the heading <paramref name="sectionKey"/> of <paramref name="book"/> (spec 5.1 tooltip), while
    /// the relevance search uses the language model's understanding; null without one. Reads only the snapshot and the
    /// store, so it may be called from any thread.
    /// </summary>
    public string? TopicSummary(Book book, Guid sectionKey)
    {
        if (_understanding is not { ServerAway: false } understanding || book.FindNode(sectionKey) is not { } heading)
        {
            return null;
        }

        return understanding.Understanding.Heading(book, heading)?.Text;
    }

    /// <summary>
    /// The current book and section changed (<see cref="AppState"/> reports it on the UI thread): the worker puts their
    /// headings first. Another book is fed at once; another section once it stayed for <see cref="FocusFeedDelay"/>
    /// (moving the cursor through the book feeds only once).
    /// </summary>
    internal void SetFocus(Book? book, Guid sectionKey)
    {
        var name = book?.LinkName;
        if (string.Equals(name, _focusBook, StringComparison.OrdinalIgnoreCase) && sectionKey == _focusSection)
        {
            return;
        }

        var bookChanged = !string.Equals(name, _focusBook, StringComparison.OrdinalIgnoreCase);
        _focusBook = name;
        _focusSection = sectionKey;
        lock (_gate)
        {
            // Under the lock: starting the language model's parts (which sets them under it) feeds the focus as it is then.
            if (_disposed || _understanding is null)
            {
                return;
            }

            if (!bookChanged)
            {
                if (_focusTimer is { } timer)
                {
                    timer.Change(FocusFeedDelay, Timeout.InfiniteTimeSpan);
                }
                else
                {
                    _focusTimer = _time.CreateTimer(
                        _ => Dispatch(() => Feed(notesChanged: false), "the language model's focus"), null, FocusFeedDelay,
                        Timeout.InfiniteTimeSpan);
                }

                return;
            }

            // Another book (rare; its headings make the progress's count): fed at once, a pending section change with it.
            _focusTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        Feed(notesChanged: false);
    }

    /// <summary>Raised on the thread of the runtime's refresh or pull.</summary>
    private void OnLanguageChanged() => Dispatch(UpdateUnderstanding, "the language model's state");

    /// <summary>
    /// On the UI thread: starts the language model's parts when the model is ready and the semantic index exists, stops
    /// them when it no longer is (or another server or model is configured), and forwards the pause switch.
    /// </summary>
    private void UpdateUnderstanding()
    {
        if (IsDisposed || _language is null)
        {
            return;
        }

        var chat = _aiParts is null ? null : _language.Chat;
        var model = _language.Settings.LanguageModelName;
        if (_understanding is { } current)
        {
            if (chat is not null && ReferenceEquals(current.Chat, chat) && current.ChatModel == model)
            {
                current.Worker.Paused = _language.Settings.LanguageModelPaused;
                return;
            }

            StopUnderstanding();
        }

        if (chat is not null)
        {
            StartUnderstanding(chat, model);
        }
    }

    /// <summary>
    /// Whether the computer runs on battery, for the worker (on its thread): read from the monitor at most every
    /// <see cref="BatteryReadInterval"/>; not recognizable (no monitor, unknown) means no.
    /// </summary>
    private bool OnBattery()
    {
        if (_monitor is null)
        {
            return false;
        }

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_battery is { } known && now - known.At < BatteryReadInterval)
            {
                return known.OnBattery;
            }
        }

        var onBattery = _monitor.Read(null).OnBattery == true;
        lock (_gate)
        {
            _battery = (now, onBattery);
        }

        return onBattery;
    }

    private void StartUnderstanding(IChatModel chat, string model)
    {
        var ai = _aiParts!;
        UnderstandingStore store;
        try
        {
            store = UnderstandingStore.Open(Vault.Root, _logger);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening the language model's store failed");
            return;
        }

        var understanding = new StoreUnderstanding(store, model, ai.EmbedModel);
        var worker = new UnderstandingWorker(
            chat, model, store, text => ai.Embedder.Embed([text])[0], ai.EmbedModel, _time, OnBattery, _logger)
        {
            Paused = _language!.Settings.LanguageModelPaused,
        };
        var parts = new UnderstandingParts(chat, model, store, understanding, new RelevanceService(ai.Index, understanding), worker);
        lock (_gate)
        {
            if (_disposed)
            {
                parts.Dispose();
                return;
            }

            _understanding = parts;
        }

        worker.ProgressChanged += () => OnUnderstandingProgress(parts);
        Feed(notesChanged: true);
        Observe(() => Task.Run(() => worker.RunAsync(_aiLifetime.Token)), "The language model's background work");
        RaiseUnderstandingChanged();
        RaiseAiStatusChanged();
    }

    /// <summary>Stops the worker (a running request is cancelled, nothing of it stored), then closes the store.</summary>
    private void StopUnderstanding()
    {
        UnderstandingParts? parts;
        lock (_gate)
        {
            parts = _understanding;
            _understanding = null;
        }

        if (parts is null)
        {
            return;
        }

        parts.Dispose();
        RaiseUnderstandingChanged();
        RaiseAiStatusChanged();
    }

    /// <summary>
    /// On the UI thread: takes snapshots of the books and the notes (newest first; their texts read again only with
    /// <paramref name="notesChanged"/>) and gives the worker its work from them (the current book's headings, the notes,
    /// the other books' headings) and the understanding the notes' fingerprints. The items are computed in the background
    /// (headings once per book instance, notes once per snapshot); when all of them are known already (a focus change),
    /// they are handed over at once. A result is dropped when a later feed started meanwhile (its snapshots are newer).
    /// </summary>
    private void Feed(bool notesChanged)
    {
        if (IsDisposed || _understanding is not { } parts)
        {
            return;
        }

        try
        {
            var book = _focusBook is { } name ? Vault.FindBook(name) : null;
            var section = _focusSection == Guid.Empty ? (Guid?)null : _focusSection;
            var books = Vault.Books;
            if (notesChanged || _noteSnapshot is null)
            {
                // Only the texts are read here (the vault model belongs to the UI thread); the items are computed later.
                _noteSnapshot =
                [
                    .. Notes.All()
                        .Where(note => note.Block.Parent is null)
                        .OrderByDescending(note => note.Date ?? DateOnly.MinValue)
                        .Select(note => new NoteSnapshot(
                            note.Key, UnderstandingItems.NoteText(note), note.Block.Content, note.Page.FilePath)),
                ];
            }

            var notes = _noteSnapshot;
            var feed = Interlocked.Increment(ref _feedsStarted);
            if (Work(parts, book, books, notes, cachedOnly: true) is { } known)
            {
                Apply(parts, feed, section, notes, known);
                return;
            }

            Observe(
                () => Task.Run(() => Apply(parts, feed, section, notes, Work(parts, book, books, notes, cachedOnly: false)!.Value)),
                "Feeding the language model's background work");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Feeding the language model's background work failed");
        }
    }

    /// <summary>
    /// The worker's work from the snapshots; with <paramref name="cachedOnly"/> only from items computed before (null if
    /// any is missing), otherwise computing what is missing (in the background).
    /// </summary>
    private static FeedWork? Work(UnderstandingParts parts, Book? book, IReadOnlyList<Book> books,
        IReadOnlyList<NoteSnapshot> notes, bool cachedOnly)
    {
        var understanding = parts.Understanding;
        IReadOnlyList<UnderstandingItem>? Items(Book b) => cachedOnly ? understanding.CachedItems(b) : understanding.Items(b);

        if ((cachedOnly ? parts.CachedNoteItems(notes) : parts.NoteItems(notes)) is not { } noteItems)
        {
            return null;
        }

        IReadOnlyList<UnderstandingItem> current = [];
        if (book is not null)
        {
            if (Items(book) is not { } bookItems)
            {
                return null;
            }

            current = bookItems;
        }

        var others = new List<UnderstandingItem>();
        foreach (var other in books)
        {
            if (string.Equals(other.LinkName, book?.LinkName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Items(other) is not { } items)
            {
                return null;
            }

            others.AddRange(items);
        }

        return new FeedWork(current, noteItems, others);
    }

    /// <summary>Hands the work to the worker and the notes' fingerprints to the understanding, unless a later feed started.</summary>
    private void Apply(UnderstandingParts parts, int feed, Guid? section, IReadOnlyList<NoteSnapshot> notes, FeedWork work)
    {
        lock (_feedGate)
        {
            if (feed != Volatile.Read(ref _feedsStarted) || !ReferenceEquals(_understanding, parts))
            {
                return; // a later feed started, or the model's parts were replaced
            }

            if (parts.TakeNotesChanged(notes))
            {
                // Only notes long enough for a core statement are looked up.
                var map = new Dictionary<Guid, string>();
                foreach (var item in work.Notes)
                {
                    map[item.Key] = item.Fingerprint;
                }

                parts.Understanding.UpdateNotes(map);
            }

            parts.Worker.SetWork(work.Current, section, work.Notes, work.Others);
            Interlocked.Increment(ref _feedsApplied);
        }
    }

    /// <summary>(Re)starts the debounce of feeding the worker after a page change; call it under <see cref="_gate"/>.</summary>
    private void ScheduleFeed()
    {
        if (_language is null)
        {
            return;
        }

        if (_feedTimer is { } timer)
        {
            timer.Change(PageIndexDelay, Timeout.InfiniteTimeSpan);
            return;
        }

        _feedTimer = _time.CreateTimer(
            _ => Dispatch(() => Feed(notesChanged: true), "the language model's work"), null, PageIndexDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Raised on the worker's thread. A pause for an unreachable server or a missing model makes relevance work without the
    /// understanding until the worker gets answers again (spec 8); the worker itself retries every 60 s.
    /// </summary>
    private void OnUnderstandingProgress(UnderstandingParts parts)
    {
        var pause = parts.Worker.Progress.Pause;
        var away = pause switch
        {
            UnderstandingPause.Unreachable or UnderstandingPause.ModelMissing => true,
            UnderstandingPause.None => false,
            _ => parts.ServerAway,
        };
        var changed = away != parts.ServerAway;
        parts.ServerAway = away;
        RaiseUnderstandingChanged();
        if (changed)
        {
            RaiseAiStatusChanged();
        }
    }

    /// <summary>Called on any thread; never throws.</summary>
    private void RaiseUnderstandingChanged()
    {
        if (IsDisposed)
        {
            return;
        }

        Dispatch(
            () =>
            {
                if (!IsDisposed)
                {
                    UnderstandingChanged?.Invoke();
                }
            },
            "the language model's progress");
    }

    /// <summary>The language model's parts for this vault; the relevance service uses the understanding over the store.</summary>
    private sealed class UnderstandingParts(
        IChatModel chat, string chatModel, UnderstandingStore store, StoreUnderstanding understanding, RelevanceService relevance,
        UnderstandingWorker worker) : IDisposable
    {
        public IChatModel Chat { get; } = chat;

        public string ChatModel { get; } = chatModel;

        public StoreUnderstanding Understanding { get; } = understanding;

        public RelevanceService Relevance { get; } = relevance;

        public UnderstandingWorker Worker { get; } = worker;

        // The notes' items of the last snapshot computed, and the snapshot last given to the understanding; under _notesGate.
        private readonly Lock _notesGate = new();
        private (IReadOnlyList<NoteSnapshot> Snapshot, IReadOnlyList<UnderstandingItem> Items)? _noteItems;
        private IReadOnlyList<NoteSnapshot>? _notesGiven;

        /// <summary>The notes' items (notes long enough for a core statement, in the snapshot's order); computed once per snapshot.</summary>
        public IReadOnlyList<UnderstandingItem> NoteItems(IReadOnlyList<NoteSnapshot> snapshot)
        {
            if (CachedNoteItems(snapshot) is { } known)
            {
                return known;
            }

            IReadOnlyList<UnderstandingItem> items =
            [
                .. snapshot
                    .Select(note => UnderstandingItems.ForNote(note.Key, note.Text, note.Content, note.FilePath, ChatModel))
                    .OfType<UnderstandingItem>(),
            ];
            lock (_notesGate)
            {
                _noteItems = (snapshot, items);
            }

            return items;
        }

        /// <summary>The notes' items if they were computed for <paramref name="snapshot"/> already, else null.</summary>
        public IReadOnlyList<UnderstandingItem>? CachedNoteItems(IReadOnlyList<NoteSnapshot> snapshot)
        {
            lock (_notesGate)
            {
                return _noteItems is { } known && ReferenceEquals(known.Snapshot, snapshot) ? known.Items : null;
            }
        }

        /// <summary>Whether <paramref name="snapshot"/> was not the last one given to the understanding (and marks it given).</summary>
        public bool TakeNotesChanged(IReadOnlyList<NoteSnapshot> snapshot)
        {
            lock (_notesGate)
            {
                if (ReferenceEquals(_notesGiven, snapshot))
                {
                    return false;
                }

                _notesGiven = snapshot;
                return true;
            }
        }

        /// <summary>The worker paused because the server is unreachable or the model missing, and got no answer since.</summary>
        public bool ServerAway
        {
            get => Volatile.Read(ref _serverAway);
            set => Volatile.Write(ref _serverAway, value);
        }

        private bool _serverAway;

        /// <summary>The worker first (so nothing is stored any more), then the store; searches still running find no understanding.</summary>
        public void Dispose()
        {
            Worker.Dispose();
            store.Dispose();
        }
    }

    /// <summary>What a note's understanding item is computed from, read on the UI thread.</summary>
    private sealed record NoteSnapshot(Guid Key, string Text, string Content, string FilePath);

    /// <summary>The worker's work of one feed: the current book's headings, the notes, the other books' headings.</summary>
    private readonly record struct FeedWork(
        IReadOnlyList<UnderstandingItem> Current, IReadOnlyList<UnderstandingItem> Notes, IReadOnlyList<UnderstandingItem> Others);

    /// <summary>The services that exist once the semantic index does, with the embedder and model id behind it.</summary>
    private sealed record AiParts(
        SemanticIndex Index, HybridSearchService Search, RelevanceService Relevance, IEmbedder Embedder, string EmbedModel);

    /// <summary>Relevance without a semantic index: never available, never a suggestion.</summary>
    private sealed class UnavailableRelevance : IRelevanceService
    {
        public static readonly UnavailableRelevance Instance = new();

        public bool IsAvailable => false;

        public IReadOnlyList<SearchHit> Relevant(TopicRequest topic, Func<Guid, bool> include, int limit = 30) => [];

        public IReadOnlyList<SectionHit> WhereTo(Guid noteKey, string noteText, Book book, int limit = 5) => [];

        public IReadOnlyList<PlacementHit> Placements(Guid noteKey, string noteText, Book book, int limit = 3) => [];

        public IReadOnlyList<TopicHit> Topics(Guid noteKey, string noteText, Book book, int limit = 3) => [];
    }
}
