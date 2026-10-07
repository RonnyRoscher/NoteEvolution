using Microsoft.Extensions.Logging;
using NoteEvolution.AI.Embeddings;
using NoteEvolution.AI.Model;

namespace NoteEvolution.UI.State;

/// <summary>
/// The local AI model of the app (a singleton): whether it is installed, its download, and the embedder, which is shared
/// by every vault session and owned here (disposed with the runtime at app exit, after the last session).
/// <para>
/// Threads: all members may be used from any thread. <see cref="Embedder"/> loads the model on first access, which takes
/// a moment: call it off the UI thread. The state properties never wait for that load.
/// </para>
/// </summary>
public sealed class AiRuntime : IDisposable
{
    private readonly ModelStore _store;
    private readonly ModelDownloader _downloader;
    private readonly Func<IEmbedder> _loadEmbedder;
    private readonly ILogger _logger;

    /// <summary>Guards the state flags below.</summary>
    private readonly Lock _gate = new();

    /// <summary>Held while the embedder is loaded (and by <see cref="Dispose"/>), so it is loaded once.</summary>
    private readonly Lock _loadGate = new();

    private bool _installed;
    private bool _downloading;
    private volatile bool _disposed;
    private volatile bool _loadAttempted;
    private volatile IEmbedder? _embedder;
    private volatile string? _loadError;

    /// <param name="loadEmbedder">Loads the embedder for <paramref name="model"/> from <paramref name="store"/>.</param>
    /// <param name="model">The model; <see cref="ModelCatalog.E5Small"/> if <c>null</c>.</param>
    public AiRuntime(ModelStore store, ModelDownloader downloader, Func<IEmbedder> loadEmbedder, ILogger<AiRuntime> logger,
        ModelInfo? model = null)
    {
        _store = store;
        _downloader = downloader;
        _loadEmbedder = loadEmbedder;
        _logger = logger;
        Model = model ?? ModelCatalog.E5Small;
        _installed = store.IsInstalled(Model);
    }

    public ModelInfo Model { get; }

    /// <summary>Every model file is in place (checked at start and after each download).</summary>
    public bool ModelInstalled
    {
        get
        {
            lock (_gate)
            {
                return _installed;
            }
        }
    }

    public bool IsDownloading
    {
        get
        {
            lock (_gate)
            {
                return _downloading;
            }
        }
    }

    /// <summary>Why the embedder could not be loaded; <c>null</c> if it loaded or was not tried yet.</summary>
    public string? LoadError => _loadError;

    /// <summary>
    /// The embedder, loaded on first access once the model is installed; <c>null</c> while the model is missing, after
    /// <see cref="Dispose"/>, or when loading failed. A failed load is logged (<see cref="LoadError"/>) and not tried again.
    /// </summary>
    public IEmbedder? Embedder
    {
        get
        {
            if (_disposed)
            {
                return null;
            }

            if (_loadAttempted)
            {
                return _embedder;
            }

            if (!ModelInstalled)
            {
                return null;
            }

            lock (_loadGate)
            {
                if (_disposed)
                {
                    return null;
                }

                if (!_loadAttempted)
                {
                    Load();
                }

                return _embedder;
            }
        }
    }

    /// <summary>
    /// The runtime's part of the AI status: <see cref="AiState.Downloading"/>, <see cref="AiState.ModelMissing"/>,
    /// <see cref="AiState.Failed"/> after a failed load, otherwise <see cref="AiState.Ready"/> (the model can be used).
    /// </summary>
    public AiStatus Status
    {
        get
        {
            lock (_gate)
            {
                if (_downloading) return AiStatus.Of(AiState.Downloading);
                if (!_installed) return AiStatus.Of(AiState.ModelMissing);
            }

            return _loadError is { } error ? AiStatus.Failed(error) : AiStatus.Of(AiState.Ready);
        }
    }

    /// <summary>
    /// <see cref="ModelInstalled"/> or <see cref="IsDownloading"/> changed: a download started or ended. Raised on the
    /// thread of the download; handlers must not throw (a throwing handler is logged).
    /// </summary>
    public event Action? ModelChanged;

    /// <summary>
    /// Downloads the model's missing files (only after the user confirmed it). Afterwards <see cref="ModelInstalled"/>
    /// tells whether it worked; <see cref="ModelChanged"/> is raised when it starts and when it ends.
    /// </summary>
    /// <exception cref="ModelDownloadException">A file could not be downloaded or failed its checksum.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    /// <exception cref="InvalidOperationException">A download is already running.</exception>
    public async Task DownloadAsync(IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_downloading)
            {
                throw new InvalidOperationException("The model is already being downloaded.");
            }

            _downloading = true;
        }

        RaiseModelChanged();
        try
        {
            await _downloader.DownloadAsync(Model, progress, ct);
            _logger.LogInformation("Model {Model} downloaded", Model.Id);
        }
        finally
        {
            var installed = _store.IsInstalled(Model);
            lock (_gate)
            {
                _downloading = false;
                _installed = installed;
            }

            RaiseModelChanged();
        }
    }

    /// <summary>Disposes the embedder; call it after every session that uses it is disposed.</summary>
    public void Dispose()
    {
        lock (_loadGate)
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
            }

            (_embedder as IDisposable)?.Dispose();
        }
    }

    private void Load()
    {
        try
        {
            _embedder = _loadEmbedder();
            _logger.LogInformation("Model {Model} loaded", Model.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading the model {Model} failed", Model.Id);
            _loadError = ex.Message;
        }
        finally
        {
            _loadAttempted = true;
        }
    }

    private void RaiseModelChanged()
    {
        try
        {
            ModelChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A handler of the model change failed");
        }
    }
}
