using Microsoft.Extensions.Logging;
using NoteEvolution.AI.Embeddings;
using NoteEvolution.AI.Model;

namespace NoteEvolution.UI.State;

/// <summary>
/// The local AI model of the app (a singleton): the active model of the catalog, whether it is installed, downloads, and
/// the embedder, which is shared by every vault session and owned here (disposed when another model is activated and
/// with the runtime at app exit, after the last session). Only the active model's folder is kept on disk.
/// <para>
/// Threads: all members may be used from any thread. <see cref="Embedder"/> loads the model on first access, which takes
/// a moment: call it off the UI thread. The state properties never wait for that load.
/// </para>
/// </summary>
public sealed class AiRuntime : IDisposable
{
    private readonly ModelStore _store;
    private readonly ModelDownloader _downloader;
    private readonly Func<ModelInfo, IEmbedder> _loadEmbedder;
    private readonly ILogger _logger;

    /// <summary>Guards the state flags below.</summary>
    private readonly Lock _gate = new();

    /// <summary>Held while the embedder is loaded (and by <see cref="Activate"/> and <see cref="Dispose"/>), so it is loaded once.</summary>
    private readonly Lock _loadGate = new();

    private ModelInfo _model;
    private bool _installed;
    private bool _downloading;
    private volatile bool _disposed;
    private volatile bool _loadAttempted;
    private volatile IEmbedder? _embedder;
    private volatile string? _loadError;

    /// <param name="loadEmbedder">Loads the embedder for the given model from <paramref name="store"/>.</param>
    /// <param name="catalog">The selectable models; <see cref="ModelCatalog.All"/> if <c>null</c>.</param>
    /// <param name="activeModelId">The model in use (the saved choice); the first of <paramref name="catalog"/> if <c>null</c> or unknown.</param>
    public AiRuntime(ModelStore store, ModelDownloader downloader, Func<ModelInfo, IEmbedder> loadEmbedder, ILogger<AiRuntime> logger,
        IReadOnlyList<ModelInfo>? catalog = null, string? activeModelId = null)
    {
        _store = store;
        _downloader = downloader;
        _loadEmbedder = loadEmbedder;
        _logger = logger;
        Models = catalog ?? ModelCatalog.All;
        _model = Models.FirstOrDefault(m => m.Id == activeModelId) ?? Models[0];
        _installed = store.IsInstalled(_model);

        // Leftovers of other models (a switch or download that did not finish) are removed once the active one is there.
        if (_installed)
        {
            DeleteOtherModels(_model);
        }
    }

    /// <summary>The selectable models.</summary>
    public IReadOnlyList<ModelInfo> Models { get; }

    /// <summary>The active model: the one <see cref="Embedder"/> loads; changed by <see cref="Activate"/>.</summary>
    public ModelInfo Model
    {
        get
        {
            lock (_gate)
            {
                return _model;
            }
        }
    }

    /// <summary>Every file of the active model is in place (checked at start, after each download and on activation).</summary>
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
    /// <see cref="Model"/>, <see cref="ModelInstalled"/> or <see cref="IsDownloading"/> changed: a download started or
    /// ended, or another model was activated. Raised on the thread of the download or activation; handlers must not throw
    /// (a throwing handler is logged).
    /// </summary>
    public event Action? ModelChanged;

    /// <summary>
    /// Downloads the missing files of <paramref name="model"/>, the active one or another (only after the user confirmed
    /// it). For the active model <see cref="ModelInstalled"/> tells afterwards whether it worked; another model is not
    /// activated, and its folder is deleted again if its download fails or is cancelled. <see cref="ModelChanged"/> is
    /// raised when the download starts and when it ends.
    /// </summary>
    /// <exception cref="ModelDownloadException">A file could not be downloaded or failed its checksum.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    /// <exception cref="InvalidOperationException">A download is already running.</exception>
    public async Task DownloadAsync(ModelInfo model, IProgress<DownloadProgress>? progress, CancellationToken ct)
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
            await _downloader.DownloadAsync(model, progress, ct);
            _logger.LogInformation("Model {Model} downloaded", model.Id);
        }
        catch when (model.Id != Model.Id)
        {
            // A model that is not in use leaves nothing behind.
            if (_store.Delete(model) is { } error)
            {
                _logger.LogWarning("Removing the incomplete model {Model} failed: {Error}", model.Id, error);
            }

            throw;
        }
        finally
        {
            lock (_gate)
            {
                _downloading = false;
            }

            UpdateInstalled();
            RaiseModelChanged();
        }
    }

    /// <summary>
    /// Makes the installed <paramref name="model"/> the active one: the current embedder is disposed (call it after every
    /// session that uses it is disposed), the new model's embedder is loaded on the next access to <see cref="Embedder"/>,
    /// and the folders of all other models are deleted. <see cref="ModelChanged"/> is raised. Nothing happens for the
    /// active, installed model.
    /// </summary>
    /// <exception cref="InvalidOperationException">The files of <paramref name="model"/> are not in place.</exception>
    public void Activate(ModelInfo model)
    {
        // Checked before waiting for _loadGate: a session may hold it for the whole load of the embedder, and the
        // dialog calls this on the UI thread.
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (model.Id == _model.Id && _installed)
            {
                return;
            }
        }

        lock (_loadGate)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (model.Id == _model.Id && _installed)
                {
                    return;
                }

                if (!_store.IsInstalled(model))
                {
                    throw new InvalidOperationException($"The model {model.Id} is not installed.");
                }

                (_embedder as IDisposable)?.Dispose();
                _embedder = null;
                _loadError = null;
                _loadAttempted = false;
                _model = model;
                _installed = true;
            }
        }

        _logger.LogInformation("Model {Model} activated", model.Id);
        DeleteOtherModels(model);
        RaiseModelChanged();
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
            _embedder = _loadEmbedder(Model);
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

    /// <summary>Checks the files of the active model again; an activation in between has set the flag for its model already.</summary>
    private void UpdateInstalled()
    {
        var model = Model;
        var installed = _store.IsInstalled(model);
        lock (_gate)
        {
            if (ReferenceEquals(model, _model))
            {
                _installed = installed;
            }
        }
    }

    private void DeleteOtherModels(ModelInfo keep)
    {
        foreach (var error in _store.DeleteAllExcept(keep))
        {
            _logger.LogWarning("Removing an unused model folder failed: {Error}", error);
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
