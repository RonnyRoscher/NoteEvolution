using System.Net;
using Microsoft.Extensions.Logging;
using NoteEvolution.AI.Language;
using NoteEvolution.AI.Understanding;

namespace NoteEvolution.UI.State;

/// <summary>Where the language model stands: switched off, server not reachable, model not installed, being pulled, usable.</summary>
public enum LanguageModelState { Off, Unreachable, ModelMissing, Pulling, Ready }

/// <summary>
/// The local language model of the app (a singleton, spec 7): the settings in <see cref="UiSettings"/> (switch, address,
/// model), the connection to the server at that address, its version, and pulling the model. <see cref="RefreshAsync"/>
/// asks the server; while the model is switched on but the server is unreachable or the model missing, it asks again
/// every <see cref="RetryInterval"/> (spec 8). Vault sessions start their background work when the state is
/// <see cref="LanguageModelState.Ready"/> and follow <see cref="Changed"/>.
/// <para>Threads: all members may be used from any thread; <see cref="Changed"/> is raised on the thread of the refresh or pull.</para>
/// </summary>
public sealed class LanguageModelRuntime
{
    /// <summary>How often an unreachable server (or a missing model) is asked again while the model is switched on.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(60);

    /// <summary>A server that does not answer the version or model list within this time counts as unreachable.</summary>
    public static readonly TimeSpan CheckTimeout = UnderstandingWorker.CheckTimeout;

    private readonly UiSettings _settings;
    private readonly Func<Uri, IChatModel> _connect;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    /// <summary>Guards the fields below.</summary>
    private readonly Lock _gate = new();

    private LanguageModelState _state = LanguageModelState.Off;
    private string? _version;
    private IChatModel? _client;

    /// <summary>The client created for the configured address; reused while the address stays.</summary>
    private (Uri Address, IChatModel Client)? _connection;
    private bool _pulling;
    private ITimer? _retry;

    /// <summary>Counts the checks started; only the newest one's result is taken (an older one may end later).</summary>
    private long _generation;

    /// <param name="settings">The app's settings; the language model's values are read on every <see cref="RefreshAsync"/>.</param>
    /// <param name="connect">Creates the client for a server address (called once per address).</param>
    /// <param name="time">The clock for <see cref="RetryInterval"/> and <see cref="CheckTimeout"/>.</param>
    public LanguageModelRuntime(UiSettings settings, Func<Uri, IChatModel> connect, TimeProvider time, ILogger<LanguageModelRuntime> logger)
    {
        _settings = settings;
        _connect = connect;
        _time = time;
        _logger = logger;
    }

    /// <summary>The state found by the last <see cref="RefreshAsync"/> (<see cref="LanguageModelState.Pulling"/> while a pull runs).</summary>
    public LanguageModelState State
    {
        get
        {
            lock (_gate) return _pulling ? LanguageModelState.Pulling : _state;
        }
    }

    /// <summary>The server's version from the last <see cref="RefreshAsync"/> (also while switched off); null when it was not reachable.</summary>
    public string? Version
    {
        get
        {
            lock (_gate) return _version;
        }
    }

    /// <summary>The client of the configured server while <see cref="State"/> is <see cref="LanguageModelState.Ready"/>, else null.</summary>
    public IChatModel? Chat
    {
        get
        {
            lock (_gate) return !_pulling && _state == LanguageModelState.Ready ? _client : null;
        }
    }

    /// <summary>The settings the runtime reads (the vault session takes the model name and the pause switch from them).</summary>
    internal UiSettings Settings => _settings;

    /// <summary>
    /// <see cref="State"/>, <see cref="Version"/>, <see cref="Chat"/> or the pause switch may have changed; handlers must
    /// not throw (a throwing one is logged).
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Asks the configured server for its version and installed models and sets <see cref="State"/>: <see cref="LanguageModelState.Off"/>
    /// while switched off (the version is still read, for the first-setup offer), otherwise unreachable, model missing or ready.
    /// Never throws for a failing server; only cancelling <paramref name="ct"/> throws. When checks overlap (a retry still
    /// waits for an answer while the settings were saved), the result of the one started last counts.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (await UpdateAsync(ct))
        {
            RaiseChanged();
        }
    }

    /// <summary>The work of <see cref="RefreshAsync"/>; returns whether anything changed.</summary>
    private async Task<bool> UpdateAsync(CancellationToken ct)
    {
        long generation;
        lock (_gate)
        {
            generation = ++_generation;
        }

        var enabled = _settings.LanguageModelEnabled;
        var model = _settings.LanguageModelName;
        var client = Client();

        string? version = null;
        var found = false;
        if (client is not null)
        {
            using var timeout = new CancellationTokenSource(CheckTimeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                version = await client.VersionAsync(linked.Token);
                if (version is not null && enabled)
                {
                    var models = await client.ModelsAsync(linked.Token);
                    found = models.Any(m => IsModel(m.Name, model));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogInformation(ex, "The language model server at {Address} did not answer", _settings.LanguageModelAddress);
                version = null;
            }
        }

        var state = !enabled ? LanguageModelState.Off
            : version is null ? LanguageModelState.Unreachable
            : found ? LanguageModelState.Ready
            : LanguageModelState.ModelMissing;

        bool changed;
        lock (_gate)
        {
            if (generation != _generation)
            {
                return false; // a newer check (e.g. after saving the settings) has started; its result counts
            }

            changed = state != _state || version != _version || !ReferenceEquals(client, _client);
            _state = state;
            _version = version;
            _client = client;
            _retry?.Dispose();
            _retry = state is LanguageModelState.Unreachable or LanguageModelState.ModelMissing
                ? _time.CreateTimer(_ => _ = RefreshAsync(CancellationToken.None), null, RetryInterval, Timeout.InfiniteTimeSpan)
                : null;
        }

        if (changed)
        {
            _logger.LogInformation("Language model {Model}: {State} (server {Version})", model, state, version ?? "–");
        }

        return changed;
    }

    /// <summary>
    /// Downloads the configured model (only after the user confirmed it); <see cref="State"/> is <see cref="LanguageModelState.Pulling"/>
    /// meanwhile and is refreshed afterwards, also when the pull fails or is cancelled.
    /// </summary>
    /// <exception cref="ChatModelUnavailableException">The server is unreachable, or the pull failed or ended without Ollama's <c>success</c>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    /// <exception cref="InvalidOperationException">A pull is already running.</exception>
    public async Task PullAsync(IProgress<PullProgress>? progress, CancellationToken ct)
    {
        var client = Client() ?? throw new ChatModelUnavailableException($"'{_settings.LanguageModelAddress}' is not a server address.");
        lock (_gate)
        {
            if (_pulling)
            {
                throw new InvalidOperationException("The language model is already being pulled.");
            }

            _pulling = true;
        }

        RaiseChanged();
        try
        {
            var status = new LastStatus(progress);
            await client.PullAsync(_settings.LanguageModelName, status, ct);
            if (status.Status != "success")
            {
                // Ollama ends a complete pull with this status; a stream that just stops is a broken download.
                throw new ChatModelUnavailableException($"The download of '{_settings.LanguageModelName}' ended without success.");
            }

            _logger.LogInformation("Language model {Model} pulled", _settings.LanguageModelName);
        }
        finally
        {
            lock (_gate)
            {
                _pulling = false;
            }

            await UpdateAsync(CancellationToken.None);
            RaiseChanged(); // Pulling ended in any case
        }
    }

    /// <summary>
    /// The user's pause switch for the background work (spec 4): sets <see cref="UiSettings.LanguageModelPaused"/> and
    /// raises <see cref="Changed"/>, so open vault sessions pass it to their worker at once. The caller saves the settings.
    /// </summary>
    public void SetPaused(bool paused)
    {
        _settings.LanguageModelPaused = paused;
        RaiseChanged();
    }

    /// <summary>
    /// The models installed on the configured server (for the settings' choice); empty when it is unreachable, does not
    /// answer within <see cref="CheckTimeout"/> or the address is invalid. Only cancelling <paramref name="ct"/> throws.
    /// </summary>
    public async Task<IReadOnlyList<ChatModelInfo>> InstalledModelsAsync(CancellationToken ct)
    {
        if (Client() is not { } client)
        {
            return [];
        }

        using var timeout = new CancellationTokenSource(CheckTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            return await client.ModelsAsync(linked.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogInformation(ex, "The language model server at {Address} did not list its models", _settings.LanguageModelAddress);
            return [];
        }
    }

    /// <summary>Whether the address points to this computer: <c>localhost</c>, <c>127.0.0.1</c> or <c>::1</c> (spec 7).</summary>
    public static bool IsLocal(Uri address)
    {
        if (!address.IsAbsoluteUri)
        {
            return false;
        }

        var host = address.IdnHost;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var ip) && (ip.Equals(IPAddress.Loopback) || ip.Equals(IPAddress.IPv6Loopback));
    }

    /// <summary>The client for the configured address (the same one while the address stays); null for an invalid address.</summary>
    private IChatModel? Client()
    {
        if (!Uri.TryCreate(_settings.LanguageModelAddress, UriKind.Absolute, out var address)
            || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        lock (_gate)
        {
            if (_connection is not { } connection || connection.Address != address)
            {
                connection = (address, _connect(address));
                _connection = connection;
            }

            return connection.Client;
        }
    }

    /// <summary>The installed name matches the configured one, also with the implicit <c>:latest</c> tag.</summary>
    private static bool IsModel(string name, string model) =>
        string.Equals(name, model, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, model + ":latest", StringComparison.OrdinalIgnoreCase);

    /// <summary>Passes a pull's reports on and keeps the last status (Ollama reports synchronously, before the pull returns).</summary>
    private sealed class LastStatus(IProgress<PullProgress>? inner) : IProgress<PullProgress>
    {
        private volatile string? _status;

        public string? Status => _status;

        public void Report(PullProgress value)
        {
            _status = value.Status;
            inner?.Report(value);
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A handler of the language model change failed");
        }
    }
}
