using System.Security.Cryptography;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NoteEvolution.AI.Embeddings;
using NoteEvolution.AI.Model;
using NoteEvolution.AI.Tests;
using NoteEvolution.Core.Storage;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Editor;
using NoteEvolution.UI.Platform;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>A bUnit context with the services the components expect, all fake or in memory.</summary>
public abstract class UiTestContext : BunitContext
{
    protected UiTestContext()
    {
        Services.AddLocalization();
        Services.AddLogging();
        Services.AddSingleton<IPlatformServices>(Platform);
        Services.AddSingleton(Settings);
        Services.AddSingleton(State);
        Services.AddSingleton<IClock>(Clock);
        Services.AddSingleton<TimeProvider>(Time);
        Services.AddSingleton<IEditorInterop>(Editor);
    }

    /// <summary>The editor every rendered <c>EditorPane</c> talks to.</summary>
    protected FakeEditorInterop Editor { get; } = new();

    protected FakePlatformServices Platform { get; } = new();

    protected UiSettings Settings { get; } = new();

    protected AppState State { get; } = new();

    protected FakeClock Clock { get; } = new(new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.FromHours(2)));

    /// <summary>Never advances on its own, so the vault watcher's debounce timers only fire when a test says so.</summary>
    protected FakeTimeProvider Time { get; } = new();

    /// <summary>The AI runtime registered by <see cref="UseAi"/>; <c>null</c> without one (the AI is off).</summary>
    protected AiRuntime? Ai { get; private set; }

    /// <summary>Serves the test model to <see cref="Ai"/>'s downloads.</summary>
    protected FakeModelHandler ModelServer { get; } = new(TestModelData);

    private static readonly byte[] TestModelData = [.. Enumerable.Range(0, 3_000).Select(i => (byte)(i * 7))];

    private TempDir? _models;
    private HttpClient? _http;

    /// <summary>
    /// Registers an AI runtime for a one-file test model (call it before the first render). With
    /// <paramref name="installed"/> the file is in place already; otherwise <see cref="ModelServer"/> serves it to a download.
    /// </summary>
    protected AiRuntime UseAi(bool installed, Func<IEmbedder> loadEmbedder)
    {
        _models = new TempDir();
        var store = new ModelStore(_models.Path);
        var model = new ModelInfo(
            "test-model",
            "Test model",
            [new ModelFile("model.bin", "https://models.test/model.bin", Convert.ToHexStringLower(SHA256.HashData(TestModelData)), TestModelData.Length)],
            FakeEmbedder.Dims,
            512);
        if (installed)
        {
            var path = store.PathOf(model, model.Files[0]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, TestModelData);
        }

        _http = new HttpClient(ModelServer, disposeHandler: false);
        Ai = new AiRuntime(store, new ModelDownloader(_http, store), loadEmbedder, NullLogger<AiRuntime>.Instance, model);
        Services.AddSingleton(Ai);
        return Ai;
    }

    /// <summary>Opens <paramref name="vault"/> as the app's session (dispatching inline) and selects its first book.</summary>
    protected async Task<VaultSession> OpenSessionAsync(TestVault vault, AiRuntime? ai = null)
    {
        var session = await VaultSession.OpenAsync(vault.Root, Clock, timeProvider: Time, ai: ai);
        State.Session = session;
        State.CurrentBook = session.Vault.Books.FirstOrDefault();
        State.CurrentSectionKey = State.CurrentBook?.Root.Key ?? Guid.Empty;
        return session;
    }

    /// <summary>
    /// Opens <paramref name="vault"/> with an installed test model and a working <paramref name="embedder"/> (a
    /// <see cref="FakeEmbedder"/> by default), and waits until the notes are indexed (the AI is ready).
    /// </summary>
    protected async Task<VaultSession> OpenWithAiAsync(TestVault vault, FakeEmbedder? embedder = null)
    {
        embedder ??= new FakeEmbedder();
        var session = await OpenSessionAsync(vault, UseAi(installed: true, () => embedder));
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (session.AiStatus.State != AiState.Ready)
        {
            Assert.True(DateTime.UtcNow < until, "The AI did not become ready in time.");
            await Task.Delay(10);
        }

        return session;
    }

    /// <summary>
    /// Waits until the shell has finished opening a vault (other than <paramref name="previous"/>): the session is set
    /// before the opening ends, so the test waits for the editor to be shown again as well.
    /// </summary>
    protected void WaitForVaultOpened(IRenderedComponent<Components.Shell> cut, VaultSession? previous = null) =>
        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(State.Session);
            Assert.NotSame(previous, State.Session);
            Assert.Single(cut.FindAll(".ne-editor-pane"));
        });

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // The session lets go of the shared embedder before the runtime disposes it.
            State.Session?.Dispose();
            Ai?.Dispose();
            _http?.Dispose();
            ModelServer.Dispose();
            _models?.Dispose();
            Platform.Dispose();
        }

        base.Dispose(disposing);
    }
}
