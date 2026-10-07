using NoteEvolution.Core.Vaults;

namespace NoteEvolution.Core.Storage;

/// <summary>
/// Watches the vault's note folders for <c>*.md</c> files changed, created, deleted or renamed by other programs
/// (Logseq, sync tools). Events are debounced per path (300 ms after the last one); hidden files, everything below
/// <c>.noteevolution</c> and NoteEvolution's own writes (<see cref="SelfWriteRegistry"/>, compared with the file's
/// content when the debounce ends) are ignored. A rename counts as deletion of the old and creation of the new path.
/// </summary>
public sealed class VaultWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    private readonly IVault _vault;
    private readonly SelfWriteRegistry _registry;
    private readonly TimeProvider _time;
    private readonly IReadOnlyList<string> _folders;
    private readonly object _gate = new();
    private readonly Dictionary<string, ITimer> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = [];
    private bool _disposed;

    /// <param name="vault">The vault whose <see cref="VaultSettings.NoteFolders"/> are watched.</param>
    /// <param name="registry">The registry the <see cref="PageWriter"/> records its writes in.</param>
    /// <param name="timeProvider">The clock for the debounce; <see cref="TimeProvider.System"/> if <c>null</c>.</param>
    public VaultWatcher(IVault vault, SelfWriteRegistry registry, TimeProvider? timeProvider = null)
    {
        _vault = vault;
        _registry = registry;
        _time = timeProvider ?? TimeProvider.System;
        _folders =
        [
            .. vault.Settings.NoteFolders
                .Select(f => Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(vault.Root, f))))
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    /// The full path of an externally changed, created or deleted note file. Raised on a background thread:
    /// consumers marshal to the UI thread (e.g. before calling <see cref="ExternalChangeHandler.Handle"/>) and must
    /// not throw.
    /// </summary>
    public event Action<string>? ExternalChange;

    /// <summary>Starts watching the note folders that exist now; calling it again does nothing.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_watchers.Count > 0)
            {
                return;
            }

            foreach (var folder in _folders.Where(Directory.Exists))
            {
                var watcher = new FileSystemWatcher(folder, "*.md")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Changed += (_, e) => Notify(e.FullPath);
                watcher.Created += (_, e) => Notify(e.FullPath);
                watcher.Deleted += (_, e) => Notify(e.FullPath);
                watcher.Renamed += (_, e) =>
                {
                    Notify(e.OldFullPath);
                    Notify(e.FullPath);
                };
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
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
            foreach (var watcher in _watchers)
            {
                watcher.Dispose();
            }

            foreach (var timer in _pending.Values)
            {
                timer.Dispose();
            }

            _watchers.Clear();
            _pending.Clear();
        }
    }

    /// <summary>A file system event for <paramref name="path"/>: (re)starts its debounce if the path is watched.</summary>
    internal void Notify(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!IsWatched(fullPath))
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_pending.TryGetValue(fullPath, out var timer))
            {
                timer.Change(Debounce, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _pending[fullPath] = _time.CreateTimer(Elapsed, fullPath, Debounce, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void Elapsed(object? state)
    {
        var path = (string)state!;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_pending.Remove(path, out var timer))
            {
                timer.Dispose();
            }
        }

        if (!IsOwnWrite(path))
        {
            ExternalChange?.Invoke(path);
        }
    }

    /// <summary>The file's current content is what NoteEvolution wrote last (the writer records it after writing).</summary>
    private bool IsOwnWrite(string path)
    {
        try
        {
            return File.Exists(path) && _registry.IsOwnWrite(path, File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool IsWatched(string fullPath) =>
        fullPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        && Vault.IsNoteFile(_vault.Root, fullPath)
        && _folders.Any(f => fullPath.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
}
