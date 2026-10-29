using System.Data.Common;
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

    /// <summary>How long after a change the coverage (spec 6.4) is computed again.</summary>
    public static readonly TimeSpan CoverageDelay = TimeSpan.FromSeconds(1);

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

    /// <summary>The <see cref="_feedTimer"/> is started and has not fired yet; guarded by <see cref="_gate"/>.</summary>
    private bool _feedPending;

    /// <summary>Feeds the worker after the current section changed (debounced); guarded by <see cref="_gate"/>.</summary>
    private ITimer? _focusTimer;

    // On the UI thread: the current book (link name) and section the worker puts first, and the notes' snapshot (newest
    // first; taken again after page changes).
    private string? _focusBook;
    private Guid _focusSection;
    private IReadOnlyList<NoteSnapshot>? _noteSnapshot;

    /// <summary>The number of the last feed started; only its result is applied (under <see cref="_feedGate"/>).</summary>
    private int _feedsStarted;

    /// <summary>The book and notes' snapshots of the last feed started; a placement build for others is abandoned.</summary>
    private volatile FeedInputs? _feedInputs;
    private readonly object _feedGate = new();
    private int _feedsApplied;

    /// <summary>The notes' status, put together from the snapshots of the last work applied.</summary>
    private readonly NoteStatusTracker _noteStatus;

    /// <summary>The current book's coverage, computed while an owner watches it.</summary>
    private readonly CoverageTracker _coverage;

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
        Clock = clock;
        var vault = Core.Vaults.Vault.Open(root);
        var registry = new SelfWriteRegistry();
        _backups = new BackupService(vault.Root, clock);
        _search = new FullTextSearchService();
        Vault = vault;
        Notes = new NoteRepository(vault);
        Writer = new PageWriter(vault, _backups, registry);
        Undo = new UndoManager();
        Links = new LinkService(vault, Writer, Undo, new PendingStore(vault.Root), clock);
        Checker = new LinkChecker(vault, Links, Writer);
        Changes = new ExternalChangeHandler(vault);
        Handled = new HandledConverter(Notes, Writer, Links);
        Drafts = new DraftConverter(_backups, Writer);
        Watcher = new VaultWatcher(vault, registry, timeProvider);
        _noteStatus = new NoteStatusTracker(timeProvider, logger, language is not null, () => _understanding, () => _aiParts?.Index,
            Dispatch);
        _coverage = new CoverageTracker(timeProvider, logger, dispatch, vault, Notes, () => _focusBook, () => _understanding);
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

        vault.PageReplaced += OnPageReplacedForCoverage;
        PagesChanged += _ => _coverage.Schedule();
        PlacementsChanged += _coverage.Schedule;
    }

    public IVault Vault { get; }

    /// <summary>The app clock, e.g. for the dates of the book's text blocks.</summary>
    public IClock Clock { get; }

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
    public IRelevanceService Relevance
    {
        get
        {
            var relevance = _understanding is { ServerAway: false } understanding
                ? understanding.Relevance
                : (IRelevanceService?)_aiParts?.Relevance ?? UnavailableRelevance.Instance;
            return WrapRelevance?.Invoke(relevance) ?? relevance;
        }
    }

    /// <summary>For tests: wraps <see cref="Relevance"/> (e.g. to hold its answers back); <c>null</c>: as it is.</summary>
    internal Func<IRelevanceService, IRelevanceService>? WrapRelevance { get; set; }

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
    internal int NoteStatusReads => _noteStatus.Reads;

    /// <summary>How many status builds ended; for tests.</summary>
    internal int NoteStatusBuilds => _noteStatus.Builds;

    /// <summary>Called in the background when a status build is done, just before <see cref="NoteStatusChanged"/> is dispatched; for tests.</summary>
    internal Action? NoteStatusRaising
    {
        get => _noteStatus.Raising;
        set => _noteStatus.Raising = value;
    }

    /// <summary>The notes' status is being put together now; for tests.</summary>
    internal bool NoteStatusBuilding => _noteStatus.Building;

    /// <summary><see cref="Understanding"/> may have changed (also from or to <c>null</c>); raised on the UI thread.</summary>
    public event Action? UnderstandingChanged;

    /// <summary>A placement was stored or the current book's notes got new placement checks (<see cref="Placement"/>); raised on the UI thread.</summary>
    public event Action? PlacementsChanged;

    /// <summary>Called in the background before a note's placement check is built (the book and the note's key); for tests.</summary>
    internal Action<Book, Guid>? PlacementPromptBuilding { get; set; }

    /// <summary>Called in the background when a feed found the book's summaries not settled yet (before it is applied); for tests.</summary>
    internal Action<Book>? PlacementReadinessChecked { get; set; }

    /// <summary>
    /// Test hook: false leaves the placement checks out of the background work (for tests about other work). Set it right
    /// after opening a fresh vault, before the book's summaries are settled.
    /// </summary>
    internal bool BuildsPlacementChecks { get; set; } = true;

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
    public NoteStatusInput? NoteStatus(Guid noteKey) => _noteStatus.Of(noteKey);

    /// <summary>
    /// How many of the top-level notes are embedded, as of the last status build; null without a semantic index or until
    /// the status was put together (see <see cref="NoteStatusChanged"/>). Call it on the UI thread.
    /// </summary>
    public (int Done, int Total)? EmbeddingProgress => _noteStatus.EmbeddingProgress;

    /// <summary>
    /// <see cref="NoteStatus"/> may have changed (work was handed to the worker, something was stored, the worker's
    /// progress changed or notes were embedded again); raised on the UI thread, at most once per <see cref="NoteStatusDelay"/>.
    /// </summary>
    public event Action? NoteStatusChanged
    {
        add => _noteStatus.Changed += value;
        remove => _noteStatus.Changed -= value;
    }

    /// <summary>
    /// The current book's coverage (spec 6.1) as last computed; null before the first computation and without a current
    /// book. It is computed only while an owner watches it (<see cref="WatchCoverage"/>). Call it on the UI thread.
    /// </summary>
    public Coverage? Coverage
    {
        get => _coverage.Coverage;
        internal set => _coverage.Coverage = value;
    }

    /// <summary><see cref="Coverage"/> changed; raised on the UI thread, at most once per <see cref="CoverageDelay"/>.</summary>
    public event Action? CoverageChanged
    {
        add => _coverage.Changed += value;
        remove => _coverage.Changed -= value;
    }

    /// <summary>How many coverage computations started; for tests.</summary>
    internal int CoverageComputations => _coverage.Computations;

    /// <summary>Called in the background just before the coverage is computed; for tests.</summary>
    internal Action? CoverageComputing
    {
        get => _coverage.Computing;
        set => _coverage.Computing = value;
    }

    /// <summary>The coverage is being computed now; for tests.</summary>
    internal bool CoverageRunning => _coverage.Running;

    /// <summary>
    /// The <paramref name="owner"/> (a view showing the coverage) is <paramref name="visible"/> or no longer. While any
    /// owner is, the coverage is computed <see cref="CoverageDelay"/> after a change (book switch, pages, placements);
    /// a change while nobody watches is computed once someone does again. Call it on the UI thread.
    /// </summary>
    public void WatchCoverage(object owner, bool visible) => _coverage.Watch(owner, visible);

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
    /// directly and could recreate a deleted file or overwrite an unresolved conflict. Failures are logged. The note pages
    /// the undo wrote are indexed again (<see cref="Search"/>); views re-read the notes after
    /// <see cref="AppState.NotifyNotesChanged"/>, which the caller raises.
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

        // The note pages the undo writes (an adoption taken back) are indexed again: the search knows which notes are used.
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void OnWritten(Page page)
        {
            if (!Book.IsBook(page))
            {
                written.Add(page.FilePath);
            }
        }

        Vault.PageReplaced += OnWritten;
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
        finally
        {
            Vault.PageReplaced -= OnWritten;
            foreach (var path in written)
            {
                try
                {
                    Search.UpdatePage(Notes, path);
                }
                catch (Exception ex)
                {
                    // A stale index (an aid, rebuilt on opening) must not hide that the undo ran.
                    _logger.LogWarning(ex, "Updating the search index for {Path} after undoing failed", path);
                }
            }
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
            _noteStatus.Dispose();
            _coverage.Dispose();
        }

        Vault.PageReplaced -= OnPageReplacedForCoverage;

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
        _noteStatus.Schedule(all: true);
        RaiseAiStatusChanged();
    }

    /// <summary>The index embedded notes (once per batch while indexing; on its thread): only the notes' embeddings changed.</summary>
    private void OnIndexStatusChanged()
    {
        _noteStatus.Schedule(embeddings: true);
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
                    _noteStatus.Schedule(embeddings: true);
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

        // Read here, on the UI thread: the identity the verdict is stored under (none for a note not in the vault), and
        // the note's core statement item (none for a short note).
        var note = Notes.Get(noteKey);
        var identity = note is null ? null : UnderstandingItems.NoteIdentity(note.Block.Content, note.Page.FilePath);
        var core = note is null ? null : UnderstandingItems.ForNote(note, understanding.ChatModel);
        PlacementCheckPrompt prompt;
        try
        {
            // Only snapshots (the book, the note's text and key) go to the background. The prompt is the background's
            // (same builder and cache), so the verdict is stored under the fingerprint the background asks for; one built
            // before the book's summaries or the note's core statement are there is not remembered for the background.
            prompt = await Task.Run(
                () => understanding.Placements.Prompt(book, noteKey, text,
                    remember: understanding.Placements.Ready(understanding.Understanding.Items(book))
                        && understanding.Placements.CoresSettled()(core)),
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
        if (bookChanged)
        {
            _coverage.Schedule();
        }

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
            chat, model, ai.EmbedModel, store, understanding, new RelevanceService(ai.Index, understanding), worker, _logger);
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
    /// Only a <paramref name="debounced"/> feed (after a page change or a settled summary) builds placement checks.
    /// </summary>
    private void Feed(bool notesChanged, bool debounced = false)
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
            _feedInputs = new FeedInputs(book, notes);
            var feed = Interlocked.Increment(ref _feedsStarted);

            // Taken when the feed starts, before the store is read (also when the background work starts late): a summary
            // or core settled after it re-feeds once the work is applied (see Apply).
            var settled = parts.Settled;
            if (Work(parts, book, books, notes, settled, debounced, cachedOnly: true) is { } known)
            {
                Apply(parts, feed, section, notes, book, known);
                return;
            }

            Observe(
                () => Task.Run(() =>
                    Apply(parts, feed, section, notes, book, Work(parts, book, books, notes, settled, debounced, cachedOnly: false)!.Value)),
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
    /// book's unused notes come once its summaries are settled (<see cref="PlacementWork.Ready"/>, spec 3.1); the cached path
    /// cannot read the store, so it takes them only when they were built for these snapshots already.
    /// </summary>
    private FeedWork? Work(UnderstandingParts parts, Book? book, IReadOnlyList<Book> books,
        IReadOnlyList<NoteSnapshot> notes, int settled, bool debounced, bool cachedOnly)
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
        IReadOnlyDictionary<Guid, string> waitingForCores = PlacementWork.NoNotes;
        var withoutTopics = PlacementWork.NoKeys;
        var ready = false;
        var deferred = false;

        if (book is not null && !BuildsPlacementChecks)
        {
            ready = true;
        }
        else if (book is not null)
        {
            if (parts.Placements.Cached(book, notes) is { } built)
            {
                (placements, ready) = (built, true);
            }
            else if (cachedOnly)
            {
                return null;
            }
            else if (parts.Placements.Ready(current))
            {
                // A feed at once (opening, a book or section switch) leaves building them to a debounced one: so they
                // always start a debounce after the summaries are there, also when those were done before the switch.
                if (!debounced)
                {
                    deferred = true;
                }
                else if (parts.Placements.Build(book, notes, noteItems,
                    superseded: () => _feedInputs is not { } latest || !ReferenceEquals(latest.Book, book)
                        || !ReferenceEquals(latest.Notes, notes),
                    stopped: () => IsDisposed || !ReferenceEquals(_understanding, parts), PlacementPromptBuilding) is { } fresh)
                {
                    (placements, waitingForCores, withoutTopics, ready) = (fresh.Items, fresh.WaitingForCores, fresh.WithoutTopics, true);
                }
            }
            else
            {
                PlacementReadinessChecked?.Invoke(book);
            }
        }

        return new FeedWork(current, noteItems, others, book?.LinkName, placements, waitingForCores, withoutTopics, ready, settled,
            deferred);
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


            // The checks are taken only from a feed during which no summary or core statement settled: one that ran
            // while one did (started before, or slow) leaves them to the feed scheduled below, so they come a debounce
            // after the last of them, as after a page change. Until then the worker keeps the checks last applied for the
            // same book (they were built with settled summaries and cores), so its placements do not vanish meanwhile
            // and its plan is not pruned without them; another book's are replaced by none.
            var ready = work.PlacementsReady && parts.Settled == work.Settled;
            var otherBook = parts.Placements.IsOtherBook(work.Book);
            var (checks, waitingForCores, withoutTopics) = ready
                ? (work.Placements, work.WaitingForCores, work.WithoutTopics)
                : (otherBook ? [] : parts.Placements.PlacementItems ?? [], PlacementWork.NoNotes, PlacementWork.NoKeys);

            // Until the book's summaries are settled its last placement map stays (another book's is replaced by none).
            var placementsChanged = false;
            // The same checks again (a focus change) change nothing.
            if (work.Book is { } bookName
                && (otherBook || (ready && !ReferenceEquals(parts.Placements.PlacementItems, checks))))
            {
                parts.Placements.UpdateMap(bookName, checks, waitingForCores, withoutTopics);
                placementsChanged = true;
            }

            // Waiting is marked first, then the summaries settled since the feed started are looked at (the handlers count
            // first, then look at waiting): one of the two sees the other, so no wake-up is lost. A feed that left building
            // the checks to a debounced one schedules it, without putting off one pending already (moving through the
            // book faster than the debounce would otherwise never build them).
            var waiting = work.Book is not null && (!ready || waitingForCores.Count > 0);
            parts.PlacementsWaiting = waiting;
            if (waiting && parts.Settled != work.Settled)
            {
                ScheduleFeedFor(parts);
            }
            else if (waiting && work.Deferred)
            {
                ScheduleFeedFor(parts, putOff: false);
            }

            parts.Worker.SetWork(work.Current, section, work.Notes, work.Others, checks);
            Interlocked.Increment(ref _feedsApplied);
            _noteStatus.Use(parts, notes, book, work.Notes, parts.Placements.PlacementItems ?? []);

            _noteStatus.Schedule();
            if (placementsChanged)
            {
                RaisePlacementsChanged();
            }
        }
    }

    /// <summary>
    /// The store stored something (on the writer's thread; never throws): a placement is shown, and a summary of the
    /// current book may settle its summaries, or a core statement (or its vector) a note's, so the placement checks are
    /// built (debounced like a page change).
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
            kind = parts.Store.KindOf(fingerprint);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or DbException)
        {
            return;
        }

        _noteStatus.Schedule(fingerprint);
        if (kind == UnderstandingKind.Placement)
        {
            RaisePlacementsChanged();
        }
        else if (kind is UnderstandingKind.Heading or UnderstandingKind.Note && parts.Settle())
        {
            ScheduleFeedFor(parts);
        }
    }

    /// <summary>Schedules a feed (as after a page change) while <paramref name="parts"/> are the current ones; any thread.</summary>
    private void ScheduleFeedFor(UnderstandingParts parts, bool putOff = true)
    {
        lock (_gate)
        {
            if (!_disposed && ReferenceEquals(_understanding, parts))
            {
                ScheduleFeed(putOff);
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

    /// <summary>
    /// (Re)starts the debounce of feeding the worker after a page change; without <paramref name="putOff"/> a feed pending
    /// already keeps its time. Call it under <see cref="_gate"/>.
    /// </summary>
    private void ScheduleFeed(bool putOff = true)
    {
        if (_language is null || (!putOff && _feedPending))
        {
            return;
        }

        _feedPending = true;
        if (_feedTimer is { } timer)
        {
            timer.Change(PageIndexDelay, Timeout.InfiniteTimeSpan);
            return;
        }

        _feedTimer = _time.CreateTimer(_ => OnFeedDue(), null, PageIndexDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Raised on a timer thread.</summary>
    private void OnFeedDue()
    {
        lock (_gate)
        {
            _feedPending = false;
        }

        Dispatch(() => Feed(notesChanged: true, debounced: true), "the language model's work");
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

        // A heading or core statement given up settles as a stored one does, but the store reports no entry for it.
        var kinds = parts.Worker.Progress.Kinds;
        var failed = (kinds?.GetValueOrDefault(UnderstandingKind.Heading)?.Failed ?? 0)
            + (kinds?.GetValueOrDefault(UnderstandingKind.Note)?.Failed ?? 0);
        if (parts.SwapFailures(failed) != failed && parts.Settle())
        {
            ScheduleFeedFor(parts);
        }

        // The note whose request ended (its attempt is recorded after the request ends, so it is looked at again with the
        // next report as well) and the one whose request started.
        var current = parts.Worker.Progress.Current?.Key;
        var previous = parts.SwapLastRequest(current);
        _noteStatus.Schedule(keys: [previous, current], all: changed);
        RaiseUnderstandingChanged();
        if (changed)
        {
            RaiseAiStatusChanged();
        }
    }

    /// <summary>Raised on the thread that replaced the page (the UI thread, or the opening's thread).</summary>
    private void OnPageReplacedForCoverage(Page page) => _coverage.Schedule();

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

    /// <summary>
    /// The worker's work of one feed: the current book's headings, the notes, the other books' headings, and the current
    /// book (link name) with its notes' placement checks, which are only built once its summaries are settled (and a
    /// note's once its core statement is: the notes still waiting for theirs, by key with their note identity), and the
    /// count of settled headings and cores taken when the feed started, and whether it left building the checks (they
    /// could be) to a debounced feed.
    /// </summary>
    private readonly record struct FeedWork(
        IReadOnlyList<UnderstandingItem> Current, IReadOnlyList<UnderstandingItem> Notes, IReadOnlyList<UnderstandingItem> Others,
        string? Book, IReadOnlyList<UnderstandingItem> Placements, IReadOnlyDictionary<Guid, string> WaitingForCores,
        IReadOnlySet<Guid> WithoutTopics, bool PlacementsReady, int Settled, bool Deferred);

    /// <summary>What a feed takes its work from: the current book's instance (if any) and the notes' snapshot.</summary>
    private sealed record FeedInputs(Book? Book, IReadOnlyList<NoteSnapshot> Notes);

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

        public IReadOnlyList<SearchHit> Sources(Book book, Guid textBlockKey, Func<Guid, bool> include, int limit = 5) => [];
    }
}
