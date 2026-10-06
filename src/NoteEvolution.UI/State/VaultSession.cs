using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NoteEvolution.AI.Search;
using NoteEvolution.Core.Links;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Storage;
using NoteEvolution.Core.Vaults;

namespace NoteEvolution.UI.State;

/// <summary>
/// An open vault with all services working on it. Pages are edited, saved and reconciled with external changes on
/// one thread, the UI thread: watcher events reach <see cref="HandleExternalChange"/> through the dispatcher given to
/// <see cref="OpenAsync"/>, and <see cref="PagesChanged"/> and <see cref="ConflictDetected"/> are raised there.
/// </summary>
public sealed class VaultSession : IDisposable
{
    private readonly ILogger _logger;
    private readonly Func<Action, Task> _dispatch;
    private readonly BackupService _backups;
    private readonly FullTextSearchService _search;
    private readonly HashSet<string> _openConflicts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private List<string>? _changesDuringOpen = [];
    private bool _disposed;

    private VaultSession(string root, IClock clock, ILogger logger, TimeProvider timeProvider, Func<Action, Task> dispatch)
    {
        _logger = logger;
        _dispatch = dispatch;
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
        Watcher = new VaultWatcher(vault, registry, timeProvider);
        Watcher.ExternalChange += OnWatcherChange;
    }

    public IVault Vault { get; }

    public INoteRepository Notes { get; }

    public IPageWriter Writer { get; }

    public ILinkService Links { get; }

    public UndoManager Undo { get; }

    public ISearchService Search => _search;

    public LinkChecker Checker { get; }

    public ExternalChangeHandler Changes { get; }

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
    /// updates, run the link check (<see cref="StartupReport"/>), build the search index, clean up old backups.
    /// A failing step after parsing is logged and does not stop the opening.
    /// </summary>
    /// <param name="logger">Receives errors that are handled here; none if <c>null</c>.</param>
    /// <param name="timeProvider">The clock for the watcher's debounce; <see cref="TimeProvider.System"/> if <c>null</c>.</param>
    /// <param name="dispatch">
    /// Runs an action on the UI thread (e.g. a component's <c>InvokeAsync</c>); watcher events are handled through it.
    /// If <c>null</c>, they are handled on the watcher's thread.
    /// </param>
    /// <exception cref="DirectoryNotFoundException"><paramref name="root"/> is not a folder.</exception>
    public static Task<VaultSession> OpenAsync(
        string root, IClock clock, ILogger? logger = null, TimeProvider? timeProvider = null, Func<Action, Task>? dispatch = null)
    {
        if (!Directory.Exists(root))
        {
            return Task.FromException<VaultSession>(new DirectoryNotFoundException($"The folder '{root}' does not exist."));
        }

        return Task.Run(() =>
        {
            var session = new VaultSession(
                root, clock, logger ?? NullLogger.Instance, timeProvider ?? TimeProvider.System, dispatch ?? RunInline);
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
        }

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

    private void Dispatch(string path)
    {
        try
        {
            _dispatch(() => HandleExternalChange(path)).ContinueWith(
                t => _logger.LogError(t.Exception, "Dispatching the external change of {Path} failed", path),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dispatching the external change of {Path} failed", path);
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
        PagesChanged?.Invoke(fullPath);
    }
}
