using System.Collections.Concurrent;
using System.Data.Common;
using System.Runtime.CompilerServices;
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

    /// <summary>How long after a change the notes' status (the note cards' dots, spec 7.2) is put together again.</summary>
    public static readonly TimeSpan NoteStatusDelay = TimeSpan.FromSeconds(1);

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

    /// <summary>The snapshots of the last work applied, which the notes' status is put together from.</summary>
    private volatile StatusSource? _statusSource;

    /// <summary>The notes' status inputs (without the running request) and the parts they were read with; null until put together.</summary>
    private volatile NoteStatuses? _noteStatuses;

    // Putting the notes' status together: a timer waits, or a build runs (and was asked for again meanwhile); under _gate.
    private StatusBuild _statusBuild;
    private ITimer? _statusTimer;
    private StatusChanges _statusChanged = new();

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

    /// <summary>How many notes the status builds read from the store and the understanding; for tests.</summary>
    internal int NoteStatusReads => Volatile.Read(ref _noteStatusReads);

    private int _noteStatusReads;

    /// <summary>How many status builds ended; for tests.</summary>
    internal int NoteStatusBuilds => Volatile.Read(ref _noteStatusBuilds);

    private int _noteStatusBuilds;

    /// <summary>Called in the background when a status build is done, just before <see cref="NoteStatusChanged"/> is dispatched; for tests.</summary>
    internal Action? NoteStatusRaising { get; set; }

    /// <summary>The notes' status is being put together now; for tests.</summary>
    internal bool NoteStatusBuilding
    {
        get
        {
            lock (_gate)
            {
                return _statusBuild is StatusBuild.Building or StatusBuild.BuildingAgain;
            }
        }
    }

    /// <summary><see cref="Understanding"/> may have changed (also from or to <c>null</c>); raised on the UI thread.</summary>
    public event Action? UnderstandingChanged;

    /// <summary>A placement was stored or the current book's notes got new placement checks (<see cref="Placement"/>); raised on the UI thread.</summary>
    public event Action? PlacementsChanged;

    /// <summary>Called in the background before a note's placement check is built (the book and the note's key); for tests.</summary>
    internal Action<Book, Guid>? PlacementPromptBuilding { get; set; }

    /// <summary>Called in the background when a feed found the book's summaries not settled yet (before it is applied); for tests.</summary>
    internal Action<Book>? PlacementReadinessChecked { get; set; }

    /// <summary>
    /// The note's stored placement check for the current book (current or stale, see <see cref="StoreUnderstanding.Placement"/>);
    /// null without the language model, without a current book or when there is none. Call it on the UI thread.
    /// </summary>
    public NotePlacement? Placement(Guid noteKey) =>
        _understanding is { } parts && _focusBook is { } name && Vault.FindBook(name) is { } book
            ? parts.Understanding.Placement(book, noteKey)
            : null;

    /// <summary>
    /// What the note's status dot is decided from (<see cref="NoteProgress"/>), with the request running for it now; null
    /// without the language model or until the status was put together (see <see cref="NoteStatusChanged"/>). The
    /// placement is the one for the current book, as <see cref="Placement"/> gives it. Call it on the UI thread.
    /// </summary>
    public NoteStatusInput? NoteStatus(Guid noteKey)
    {
        if (_understanding is not { } parts || _noteStatuses is not { } statuses || !ReferenceEquals(statuses.Parts, parts)
            || !statuses.Inputs.TryGetValue(noteKey, out var input))
        {
            return null;
        }

        var running = parts.Worker.Progress.Current is { } current && current.Key == noteKey ? current.Kind : (UnderstandingKind?)null;
        return input with { LanguageModel = !parts.ServerAway, Running = running };
    }

    /// <summary>
    /// <see cref="NoteStatus"/> may have changed (work was handed to the worker, something was stored, the worker's
    /// progress changed or notes were embedded again); raised on the UI thread, at most once per <see cref="NoteStatusDelay"/>.
    /// </summary>
    public event Action? NoteStatusChanged;

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
            _statusTimer?.Dispose();
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

            index.StatusChanged += OnIndexStatusChanged;
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
        ScheduleNoteStatus(all: true);
        RaiseAiStatusChanged();
    }

    /// <summary>The index embedded notes (once per batch while indexing; on its thread): only the notes' embeddings changed.</summary>
    private void OnIndexStatusChanged()
    {
        ScheduleNoteStatus(embeddings: true);
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
                    ScheduleNoteStatus(embeddings: true);
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
    /// Call it on the UI thread; topics and places are computed in the background from the given snapshots. The prompt is
    /// the one the background builds for the note (spec 3.4), and a verdict is stored under its fingerprint, so the
    /// background does not ask again.
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

        var text = noteText.StartsWith(EmbeddingText.PassagePrefix, StringComparison.Ordinal)
            ? noteText[EmbeddingText.PassagePrefix.Length..]
            : noteText;

        // Read here, on the UI thread: the identity the verdict is stored under (none for a note not in the vault).
        var identity = Notes.Get(noteKey) is { } note
            ? UnderstandingItems.NoteIdentity(note.Block.Content, note.Page.FilePath)
            : null;
        PlacementCheckPrompt prompt;
        try
        {
            // Only snapshots (the book, the note's text and key) go to the background. The prompt is the background's
            // (same builder and cache), so the verdict is stored under the fingerprint the background asks for; one built
            // before the book's summaries are all there is not remembered for the background.
            prompt = await Task.Run(
                () => understanding.PlacementPrompt(book, noteKey, text,
                    remember: PlacementsReady(understanding, understanding.Understanding.Items(book))),
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

        var item = identity is null
            ? null
            : UnderstandingItems.ForPlacement(noteKey, identity, book.LinkName, prompt, understanding.ChatModel);

        using var timeout = new CancellationTokenSource(UnderstandingWorker.RequestTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            // Read and stored on the worker's thread before it plans on, so the background does not ask the same question
            // again; the worker calls it before the answer is returned.
            PlacementVerdict? verdict = null;
            await understanding.Worker.RunNextAsync(
                prompt.System, prompt.User, PlacementCheck.Options(prompt), linked.Token,
                waiting, started, new UnderstandingCurrent(UnderstandingKind.Placement, noteKey),
                answered: answer =>
                {
                    verdict = PlacementCheck.Parse(answer, prompt);
                    if (item is not null && verdict is not null)
                    {
                        StorePlacement(understanding, item, verdict);
                    }
                });
            return verdict is not null ? (verdict, null) : (null, PlacementCheckFailure.Unusable);
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

    /// <summary>Stores a verdict of "Genauer prüfen" as the worker stores its own; a closed or failing store keeps nothing.</summary>
    private void StorePlacement(UnderstandingParts parts, UnderstandingItem item, PlacementVerdict verdict)
    {
        try
        {
            parts.Store.Put(new UnderstandingEntry(UnderstandingKind.Placement, item.Fingerprint, item.Identity,
                PlacementCheck.Serialize(verdict), parts.ChatModel, null, null));
        }
        catch (Exception ex) when (ex is ObjectDisposedException or DbException)
        {
            _logger.LogWarning(ex, "Storing the placement check failed");
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
        var parts = new UnderstandingParts(
            chat, model, ai.EmbedModel, store, understanding, new RelevanceService(ai.Index, understanding), worker);
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
        store.EntryChanged += fingerprint => OnEntryChanged(parts, fingerprint);
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
                            note.Key, UnderstandingItems.NoteText(note), note.Block.Content, note.Page.FilePath,
                            note.IsUsed, note.Usages)),
                ];
            }

            var notes = _noteSnapshot;
            var feed = Interlocked.Increment(ref _feedsStarted);
            if (Work(parts, feed, book, books, notes, cachedOnly: true) is { } known)
            {
                Apply(parts, feed, section, notes, book, known);
                return;
            }

            Observe(
                () => Task.Run(() =>
                    Apply(parts, feed, section, notes, book, Work(parts, feed, book, books, notes, cachedOnly: false)!.Value)),
                "Feeding the language model's background work");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Feeding the language model's background work failed");
        }
    }

    /// <summary>
    /// The worker's work from the snapshots; with <paramref name="cachedOnly"/> only from items computed before (null if
    /// any is missing), otherwise computing what is missing (in the background). The placement checks of the current
    /// book's unused notes come once its summaries are settled (<see cref="PlacementsReady"/>, spec 3.1); the cached path
    /// cannot read the store, so it takes them only when they were built for these snapshots already.
    /// </summary>
    private FeedWork? Work(UnderstandingParts parts, int feed, Book? book, IReadOnlyList<Book> books,
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

        IReadOnlyList<UnderstandingItem> placements = [];
        var ready = false;

        // Taken before the store is read: a summary settled after it re-feeds once the work is applied (see Apply).
        var settled = parts.Settled;
        if (book is not null)
        {
            if (parts.CachedPlacements(book, notes) is { } built)
            {
                (placements, ready) = (built, true);
            }
            else if (cachedOnly)
            {
                return null;
            }
            else if (PlacementsReady(parts, current))
            {
                if (BuildPlacements(parts, feed, book, notes) is { } fresh)
                {
                    (placements, ready) = (fresh, true);
                }
            }
            else
            {
                PlacementReadinessChecked?.Invoke(book);
            }
        }

        return new FeedWork(current, noteItems, others, book?.LinkName, placements, ready, settled);
    }

    /// <summary>
    /// Whether every heading of the book (<paramref name="bookItems"/>, children first) has a summary the prompt can use
    /// (with a vector from the current embedding model, as <see cref="StoreUnderstanding.Heading"/> requires) or will not
    /// get one: it failed <see cref="UnderstandingWorker.MaxAttempts"/> times, or one of its sub-headings will not (the
    /// worker skips it then). Reads the store; a closed or failing store means no.
    /// </summary>
    private static bool PlacementsReady(UnderstandingParts parts, IReadOnlyList<UnderstandingItem> bookItems)
    {
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var item in bookItems)
            {
                if (parts.Store.Get(item.Fingerprint) is { Kind: UnderstandingKind.Heading, Vector: not null } entry
                    && entry.EmbedModel == parts.EmbedModel)
                {
                    continue;
                }

                if (parts.Store.Attempts(item.Fingerprint) < UnderstandingWorker.MaxAttempts
                    && !item.ChildFingerprints.Any(skipped.Contains))
                {
                    return false;
                }

                skipped.Add(item.Fingerprint);
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or DbException)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// The placement checks of the unused notes in snapshot order (in the background). A note whose check cannot be built
    /// is logged and left out (spec 8), as is one without topics to offer; null when a later feed started meanwhile. The
    /// checks are remembered for the snapshots unless a note had no topics (the embedder may have failed): the next feed
    /// looks for them again.
    /// </summary>
    private IReadOnlyList<UnderstandingItem>? BuildPlacements(UnderstandingParts parts, int feed, Book book,
        IReadOnlyList<NoteSnapshot> notes)
    {
        var items = new List<UnderstandingItem>();
        var withoutTopics = false;
        foreach (var note in notes)
        {
            if (note.Used)
            {
                continue;
            }

            if (Volatile.Read(ref _feedsStarted) != feed)
            {
                return null; // its result would be dropped anyway
            }

            try
            {
                PlacementPromptBuilding?.Invoke(book, note.Key);
                var prompt = parts.PlacementPrompt(book, note.Key, note.Text, remember: true);
                if (prompt.Topics.Count == 0)
                {
                    withoutTopics = true;
                    continue; // nothing to choose from, as for "Genauer prüfen"
                }

                items.Add(UnderstandingItems.ForPlacement(note.Key, UnderstandingItems.NoteIdentity(note.Content, note.FilePath),
                    book.LinkName, prompt, parts.ChatModel));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Building the placement check of a note failed");
            }
        }

        if (!withoutTopics)
        {
            parts.RememberPlacements(book, notes, items);
        }

        return items;
    }

    /// <summary>Hands the work to the worker and the notes' fingerprints to the understanding, unless a later feed started.</summary>
    private void Apply(UnderstandingParts parts, int feed, Guid? section, IReadOnlyList<NoteSnapshot> notes, Book? book,
        FeedWork work)
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

            // Until the book's summaries are settled its last placement map stays (another book's is replaced by none).
            var placementsChanged = false;
            // The same checks again (a focus change) change nothing.
            var otherBook = !string.Equals(parts.PlacementBook, work.Book, StringComparison.Ordinal);
            if (work.Book is { } bookName
                && (otherBook || (work.PlacementsReady && !ReferenceEquals(parts.PlacementItems, work.Placements))))
            {
                var placements = new Dictionary<Guid, (string Fingerprint, string Identity)>();
                foreach (var item in work.Placements)
                {
                    placements[item.Key] = (item.Fingerprint, item.Identity);
                }

                parts.Understanding.UpdatePlacements(bookName, placements);
                parts.PlacementBook = bookName;
                parts.PlacementItems = work.Placements;
                placementsChanged = true;
            }

            // Waiting is marked first, then the summaries settled since the store was read are looked at (the handlers
            // count first, then look at waiting): one of the two sees the other, so no wake-up is lost.
            var waiting = work.Book is not null && !work.PlacementsReady;
            parts.PlacementsWaiting = waiting;
            if (waiting && parts.Settled != work.Settled)
            {
                ScheduleFeedFor(parts);
            }

            parts.Worker.SetWork(work.Current, section, work.Notes, work.Others, work.Placements);
            Interlocked.Increment(ref _feedsApplied);
            // The same snapshots again (a focus change) keep the source, so only what changed is read again.
            var placementItems = parts.PlacementItems ?? [];
            if (_statusSource is not { } source || !source.Of(parts, notes, book, work.Notes, placementItems))
            {
                _statusSource = new StatusSource(parts, notes, book, work.Notes, placementItems);
            }

            ScheduleNoteStatus();
            if (placementsChanged)
            {
                RaisePlacementsChanged();
            }
        }
    }

    /// <summary>
    /// The store stored something (on the writer's thread; never throws): a placement is shown, and a summary of the
    /// current book may settle its summaries, so the placement checks are built (debounced like a page change).
    /// </summary>
    private void OnEntryChanged(UnderstandingParts parts, string fingerprint)
    {
        if (IsDisposed || !ReferenceEquals(_understanding, parts))
        {
            return;
        }

        UnderstandingKind? kind;
        try
        {
            kind = parts.Store.Get(fingerprint)?.Kind;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or DbException)
        {
            return;
        }

        ScheduleNoteStatus(fingerprint);
        if (kind == UnderstandingKind.Placement)
        {
            RaisePlacementsChanged();
        }
        else if (kind == UnderstandingKind.Heading && parts.Settle())
        {
            ScheduleFeedFor(parts);
        }
    }

    /// <summary>Schedules a feed (as after a page change) while <paramref name="parts"/> are the current ones; any thread.</summary>
    private void ScheduleFeedFor(UnderstandingParts parts)
    {
        lock (_gate)
        {
            if (!_disposed && ReferenceEquals(_understanding, parts))
            {
                ScheduleFeed();
            }
        }
    }

    /// <summary>Called on any thread; never throws.</summary>
    private void RaisePlacementsChanged()
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
                    PlacementsChanged?.Invoke();
                }
            },
            "the language model's placements");
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

        // A heading given up settles the book's summaries as a stored one does, but the store reports no entry for it.
        var failed = parts.Worker.Progress.Kinds?.GetValueOrDefault(UnderstandingKind.Heading)?.Failed ?? 0;
        if (parts.SwapHeadingFailures(failed) != failed && parts.Settle())
        {
            ScheduleFeedFor(parts);
        }

        // The note whose request ended (its attempt is recorded after the request ends, so it is looked at again with the
        // next report as well) and the one whose request started.
        var current = parts.Worker.Progress.Current?.Key;
        var previous = parts.SwapLastRequest(current);
        ScheduleNoteStatus(keys: [previous, current], all: changed);
        RaiseUnderstandingChanged();
        if (changed)
        {
            RaiseAiStatusChanged();
        }
    }

    /// <summary>
    /// Puts the notes' status together <see cref="NoteStatusDelay"/> from now unless that is pending already; while a
    /// build runs, once more a delay after it ended, so <see cref="NoteStatusChanged"/> comes at most once per delay.
    /// Only the notes of the stored <paramref name="fingerprint"/> and the <paramref name="keys"/> are read again, all of
    /// them with <paramref name="all"/> or new snapshots (see <see cref="BuildNoteStatus"/>); with
    /// <paramref name="embeddings"/> the embeddings of all of them. Any thread; never throws.
    /// </summary>
    private void ScheduleNoteStatus(string? fingerprint = null, IEnumerable<Guid?>? keys = null, bool all = false,
        bool embeddings = false)
    {
        lock (_gate)
        {
            if (_disposed || _language is null)
            {
                return;
            }

            if (fingerprint is not null)
            {
                _statusChanged.Fingerprints.Add(fingerprint);
            }

            foreach (var key in keys ?? [])
            {
                if (key is { } k)
                {
                    _statusChanged.Keys.Add(k);
                }
            }

            _statusChanged.All |= all;
            _statusChanged.Embeddings |= embeddings;
            switch (_statusBuild)
            {
                case StatusBuild.Idle:
                    _statusBuild = StatusBuild.Waiting;
                    StartStatusTimer();
                    break;
                case StatusBuild.Building:
                    _statusBuild = StatusBuild.BuildingAgain;
                    break;
            }
        }
    }

    /// <summary>Starts the status timer; call it under <see cref="_gate"/>.</summary>
    private void StartStatusTimer()
    {
        if (_statusTimer is { } timer)
        {
            timer.Change(NoteStatusDelay, Timeout.InfiniteTimeSpan);
            return;
        }

        _statusTimer = _time.CreateTimer(_ => OnStatusTimer(), null, NoteStatusDelay, Timeout.InfiniteTimeSpan);
    }

    private void OnStatusTimer()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _statusBuild = StatusBuild.Building;
        }

        Observe(() => Task.Run(BuildNoteStatus), "Putting the notes' status together");
    }

    /// <summary>
    /// In the background, from the last snapshots: the notes' status, then the event, then the next build if one was asked
    /// for. New snapshots (another feed) or <see cref="StatusChanges.All"/> read every note; otherwise only the notes whose
    /// entries were stored or whose request started or ended are read again (the store forgets its cached placements at
    /// every change, so reading all notes each time would cost a query or three per note).
    /// </summary>
    private void BuildNoteStatus()
    {
        try
        {
            StatusChanges changes;
            lock (_gate)
            {
                changes = _statusChanged;
                _statusChanged = new StatusChanges();
            }

            if (_statusSource is { } source && ReferenceEquals(_understanding, source.Parts))
            {
                var known = _noteStatuses;
                NoteStatuses statuses;
                if (changes.All || known is null || !ReferenceEquals(known.Source, source))
                {
                    statuses = new NoteStatuses(source.Parts, source, NoteStatusInputs(source, source.Notes));
                }
                else
                {
                    var notes = changes.Keys
                        .Concat(changes.Fingerprints.SelectMany(fingerprint => source.KeysOf.GetValueOrDefault(fingerprint) ?? []))
                        .Distinct()
                        .Select(key => source.NoteOf.GetValueOrDefault(key))
                        .OfType<NoteSnapshot>()
                        .ToList();
                    var inputs = new Dictionary<Guid, NoteStatusInput>(known.Inputs);
                    var changed = changes.Embeddings && UpdateEmbeddings(inputs);
                    if (notes.Count == 0 && !changed)
                    {
                        return; // nothing to tell; the finally block still ends the build
                    }

                    foreach (var (key, input) in NoteStatusInputs(source, notes))
                    {
                        inputs[key] = input;
                    }

                    statuses = new NoteStatuses(source.Parts, source, inputs);
                }

                _noteStatuses = statuses;
                NoteStatusRaising?.Invoke();
                Dispatch(
                    () =>
                    {
                        if (!IsDisposed)
                        {
                            NoteStatusChanged?.Invoke();
                        }
                    },
                    "the notes' status");
            }
        }
        catch (Exception ex)
        {
            // The store was closed or failed: the last status stays, and the next change reads every note again.
            if (ex is not (ObjectDisposedException or DbException))
            {
                _logger.LogError(ex, "Putting the notes' status together failed");
            }

            lock (_gate)
            {
                _statusChanged.All = true;
            }
        }
        finally
        {
            Interlocked.Increment(ref _noteStatusBuilds);
            lock (_gate)
            {
                var again = _statusBuild == StatusBuild.BuildingAgain && !_disposed;
                _statusBuild = again ? StatusBuild.Waiting : StatusBuild.Idle;
                if (again)
                {
                    StartStatusTimer();
                }
            }
        }
    }

    /// <summary>Reads the embeddings of the known inputs again (in memory, no store reads); whether one changed.</summary>
    private bool UpdateEmbeddings(Dictionary<Guid, NoteStatusInput> inputs)
    {
        if (_aiParts?.Index is not { } index)
        {
            return false;
        }

        var changed = false;
        foreach (var (key, input) in inputs.ToList())
        {
            var embedded = index.VectorOf(key) is not null;
            if (input.Embedded != embedded)
            {
                inputs[key] = input with { Embedded = embedded };
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// The status inputs of <paramref name="notes"/>: the core statement from the store by the note's item (only notes
    /// long enough have one), the embedding from the index, the placement in the current book. A used note gets none (no
    /// dot, also if it is unused by now, until the next feed brings its snapshot) and nothing is read for it.
    /// </summary>
    private Dictionary<Guid, NoteStatusInput> NoteStatusInputs(StatusSource source, IEnumerable<NoteSnapshot> notes)
    {
        var parts = source.Parts;
        var index = _aiParts?.Index;
        var inputs = new Dictionary<Guid, NoteStatusInput>();
        foreach (var note in notes)
        {
            if (note.Used)
            {
                continue;
            }

            Interlocked.Increment(ref _noteStatusReads);

            var needsCore = source.CoreOf.TryGetValue(note.Key, out var fingerprint);
            var coreDone = needsCore && parts.Store.Get(fingerprint!) is { Kind: UnderstandingKind.Note };
            var coreFailed = needsCore && !coreDone && parts.Store.Attempts(fingerprint!) >= UnderstandingWorker.MaxAttempts;

            // Current and stale from the placement still usable in the book; given up only for the current fingerprint.
            var placement = source.Book is { } book ? parts.Understanding.Placement(book, note.Key) : null;
            var state = source.Book is null ? PlacementState.Missing
                : placement is not { Stale: false } && parts.Understanding.PlacementStateOf(note.Key) == PlacementState.Failed
                    ? PlacementState.Failed
                : placement is null ? PlacementState.Missing
                : placement.Stale ? PlacementState.Stale
                : PlacementState.Current;
            var fit = state == PlacementState.Current ? placement!.Verdict.Fit : (PlacementFit?)null;
            bool? embedded = index is null ? null : index.VectorOf(note.Key) is not null;
            inputs[note.Key] = new NoteStatusInput(false, true, embedded, needsCore, coreDone, coreFailed, state, null, fit);
        }

        return inputs;
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
        IChatModel chat, string chatModel, string embedModel, UnderstandingStore store, StoreUnderstanding understanding,
        RelevanceService relevance, UnderstandingWorker worker) : IDisposable
    {
        public IChatModel Chat { get; } = chat;

        public string ChatModel { get; } = chatModel;

        /// <summary>The embedding model whose vectors the understanding uses.</summary>
        public string EmbedModel { get; } = embedModel;

        public StoreUnderstanding Understanding { get; } = understanding;

        public RelevanceService Relevance { get; } = relevance;

        public UnderstandingWorker Worker { get; } = worker;

        public UnderstandingStore Store { get; } = store;

        /// <summary>The book (link name) the understanding's placement map was last given for, and from which checks; under the session's feed gate.</summary>
        public string? PlacementBook { get; set; }

        public IReadOnlyList<UnderstandingItem>? PlacementItems { get; set; }

        /// <summary>The last work applied waits for the current book's summaries before its placement checks are built.</summary>
        public bool PlacementsWaiting
        {
            get => Volatile.Read(ref _placementsWaiting) != 0;
            set => Interlocked.Exchange(ref _placementsWaiting, value ? 1 : 0); // a full fence before Settled is read
        }

        /// <summary>How many times a heading was settled (stored or given up) so far.</summary>
        public int Settled => Interlocked.CompareExchange(ref _settled, 0, 0);

        /// <summary>Counts a heading settled, then tells whether the last work applied waits for the summaries.</summary>
        public bool Settle()
        {
            Interlocked.Increment(ref _settled);
            return PlacementsWaiting;
        }

        private int _placementsWaiting;
        private int _settled;
        private int _headingFailures;

        /// <summary>
        /// Remembers the key of the request with the server unless there is none, and returns the key remembered before:
        /// the request that ended (also after the next one started).
        /// </summary>
        public Guid? SwapLastRequest(Guid? current)
        {
            lock (_notesGate)
            {
                var last = _lastRequest;
                _lastRequest = current ?? last;
                return last;
            }
        }

        private Guid? _lastRequest;

        /// <summary>Remembers how many headings the worker gave up and returns the number remembered before.</summary>
        public int SwapHeadingFailures(int failed) => Interlocked.Exchange(ref _headingFailures, failed);

        // The placement prompts per book instance and note text, and the placement checks last built (for which book and
        // snapshot; under _notesGate).
        private readonly ConditionalWeakTable<Book, ConcurrentDictionary<string, PlacementCheckPrompt>> _prompts = new();
        private (Book Book, IReadOnlyList<NoteSnapshot> Notes, IReadOnlyList<UnderstandingItem> Items)? _placements;

        /// <summary>
        /// The placement check's prompt for the note (its <see cref="UnderstandingItems.NoteText"/>) in the book snapshot, as
        /// "Genauer prüfen" asks it: built once per book instance and text if <paramref name="remember"/>ed (a prompt
        /// without topics is not, the embedder may have failed). Reads the semantic index and the store; any thread.
        /// </summary>
        public PlacementCheckPrompt PlacementPrompt(Book book, Guid noteKey, string noteText, bool remember)
        {
            var prompts = _prompts.GetValue(book, _ => new ConcurrentDictionary<string, PlacementCheckPrompt>(StringComparer.Ordinal));
            if (prompts.TryGetValue(noteText, out var known))
            {
                return known;
            }

            var passage = EmbeddingText.PassagePrefix + noteText;
            var topics = Relevance.Topics(noteKey, passage, book, PlacementCheck.TopicLimit);
            var places = Relevance.Placements(noteKey, passage, book, int.MaxValue)
                .GroupBy(hit => hit.SectionKey)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<PlacementHit>)[.. group]);
            var prompt = PlacementCheck.Prompt(book, noteText, topics, places, section => Understanding.Heading(book, section)?.Text);
            return remember && prompt.Topics.Count > 0 ? prompts.GetOrAdd(noteText, prompt) : prompt;
        }

        /// <summary>The placement checks built for this book instance and snapshot, else null.</summary>
        public IReadOnlyList<UnderstandingItem>? CachedPlacements(Book book, IReadOnlyList<NoteSnapshot> snapshot)
        {
            lock (_notesGate)
            {
                return _placements is { } known && ReferenceEquals(known.Book, book) && ReferenceEquals(known.Notes, snapshot)
                    ? known.Items
                    : null;
            }
        }

        public void RememberPlacements(Book book, IReadOnlyList<NoteSnapshot> snapshot, IReadOnlyList<UnderstandingItem> items)
        {
            lock (_notesGate)
            {
                _placements = (book, snapshot, items);
            }
        }

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
            Store.Dispose();
        }
    }

    /// <summary>What a note's understanding items are computed from (and whether it is used, and where), read on the UI thread.</summary>
    private sealed record NoteSnapshot(Guid Key, string Text, string Content, string FilePath, bool Used,
        IReadOnlyList<UsedInEntry> Usages);

    /// <summary>
    /// The worker's work of one feed: the current book's headings, the notes, the other books' headings, and the current
    /// book (link name) with its notes' placement checks, which are only built once its summaries are settled, and the
    /// count of settled headings taken before the store was read.
    /// </summary>
    private readonly record struct FeedWork(
        IReadOnlyList<UnderstandingItem> Current, IReadOnlyList<UnderstandingItem> Notes, IReadOnlyList<UnderstandingItem> Others,
        string? Book, IReadOnlyList<UnderstandingItem> Placements, bool PlacementsReady, int Settled);

    /// <summary>What the notes' status is put together from: the parts, the notes' snapshot and items, and the current book.</summary>
    private sealed class StatusSource
    {
        /// <param name="noteItems">The notes' core-statement items.</param>
        /// <param name="placementItems">The placement checks the understanding was given for the book.</param>
        public StatusSource(UnderstandingParts parts, IReadOnlyList<NoteSnapshot> notes, Book? book,
            IReadOnlyList<UnderstandingItem> noteItems, IReadOnlyList<UnderstandingItem> placementItems)
        {
            Parts = parts;
            Notes = notes;
            Book = book;
            _noteItems = noteItems;
            _placementItems = placementItems;
            NoteOf = notes.DistinctBy(note => note.Key).ToDictionary(note => note.Key);
            CoreOf = noteItems.DistinctBy(item => item.Key).ToDictionary(item => item.Key, item => item.Fingerprint);
            KeysOf = noteItems.Concat(placementItems)
                .GroupBy(item => item.Fingerprint, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<Guid>)[.. group.Select(item => item.Key)], StringComparer.Ordinal);
        }

        public UnderstandingParts Parts { get; }

        public IReadOnlyList<NoteSnapshot> Notes { get; }

        public Book? Book { get; }

        public IReadOnlyDictionary<Guid, NoteSnapshot> NoteOf { get; }

        /// <summary>The fingerprint of the note's core statement, for notes long enough to have one.</summary>
        public IReadOnlyDictionary<Guid, string> CoreOf { get; }

        /// <summary>The notes whose core statement or placement check has the fingerprint.</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<Guid>> KeysOf { get; }

        private readonly IReadOnlyList<UnderstandingItem> _noteItems;
        private readonly IReadOnlyList<UnderstandingItem> _placementItems;

        /// <summary>Whether this source is made of these very snapshots.</summary>
        public bool Of(UnderstandingParts parts, IReadOnlyList<NoteSnapshot> notes, Book? book,
            IReadOnlyList<UnderstandingItem> noteItems, IReadOnlyList<UnderstandingItem> placementItems) =>
            ReferenceEquals(Parts, parts) && ReferenceEquals(Notes, notes) && ReferenceEquals(Book, book)
            && ReferenceEquals(_noteItems, noteItems) && ReferenceEquals(_placementItems, placementItems);
    }

    /// <summary>The notes' status inputs by key, read with <paramref name="Parts"/> from <paramref name="Source"/>.</summary>
    private sealed record NoteStatuses(UnderstandingParts Parts, StatusSource Source, IReadOnlyDictionary<Guid, NoteStatusInput> Inputs);

    /// <summary>What changed since the notes' status was last put together.</summary>
    private sealed class StatusChanges
    {
        public HashSet<string> Fingerprints { get; } = new(StringComparer.Ordinal);

        public HashSet<Guid> Keys { get; } = [];

        public bool All { get; set; }

        /// <summary>Notes were embedded: the known inputs' embeddings are read again.</summary>
        public bool Embeddings { get; set; }
    }

    private enum StatusBuild { Idle, Waiting, Building, BuildingAgain }

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
