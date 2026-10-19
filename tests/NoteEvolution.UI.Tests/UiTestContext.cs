using System.Security.Cryptography;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NoteEvolution.AI.Embeddings;
using NoteEvolution.AI.Model;
using NoteEvolution.AI.Tests;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Storage;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Editor;
using NoteEvolution.UI.Platform;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>A bUnit context with the services the components expect, all fake or in memory.</summary>
public abstract class UiTestContext : BunitContext
{
    /// <summary>
    /// The suite runs its test projects in parallel, and the AI tests keep the CPU busy: opening a vault or computing
    /// with the AI in the background can then take longer than bUnit's default second. A wait that passes ends as soon
    /// as it passes; only a failing one takes the full time.
    /// </summary>
    static UiTestContext() => DefaultWaitTimeout = TimeSpan.FromSeconds(10);

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
        Services.AddSingleton<ISystemMonitor>(SystemMonitor);

        // The host page's splash screen (index.html), which the shell ends after the start.
        JSInterop.SetupVoid("neSplash.status", _ => true).SetVoidResult();
        JSInterop.SetupVoid("neSplash.done").SetVoidResult();

        // The width a pane shows (index.html); 0 means unknown, the splitter then starts from the stored width.
        JSInterop.Setup<double>("neLayout.paneWidth", _ => true).SetResult(0);
    }

    /// <summary>The editor every rendered <c>EditorPane</c> talks to.</summary>
    protected FakeEditorInterop Editor { get; } = new();

    protected FakePlatformServices Platform { get; } = new();

    /// <summary>The system meter's readings and the battery state of sessions opened by <see cref="OpenSessionAsync"/>.</summary>
    protected FakeSystemMonitor SystemMonitor { get; } = new();

    protected UiSettings Settings { get; } = new();

    protected AppState State { get; } = new();

    protected FakeClock Clock { get; } = new(new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.FromHours(2)));

    /// <summary>The <c>updated::</c> value an editor save stamps on a changed text block now.</summary>
    protected string Stamp => BlockDates.Format(Clock.Now);

    /// <summary>Never advances on its own, so the vault watcher's debounce timers only fire when a test says so.</summary>
    protected FakeTimeProvider Time { get; } = new();

    /// <summary>The AI runtime registered by <see cref="UseAi"/>; <c>null</c> without one (the AI is off).</summary>
    protected AiRuntime? Ai { get; private set; }

    /// <summary>The language model registered by <see cref="UseLanguageModel"/>; <c>null</c> without one.</summary>
    protected LanguageModelRuntime? LanguageModel { get; private set; }

    /// <summary>The model folders of <see cref="Ai"/>; <c>null</c> without one.</summary>
    protected ModelStore? Store { get; private set; }

    /// <summary>Serves the test model to <see cref="Ai"/>'s downloads.</summary>
    protected FakeModelHandler ModelServer { get; } = new(TestModelData);

    /// <summary>The single file of every <see cref="TestModel"/>.</summary>
    internal static readonly byte[] TestModelData = [.. Enumerable.Range(0, 3_000).Select(i => (byte)(i * 7))];

    private TempDir? _models;
    private HttpClient? _http;

    /// <summary>A one-file test model with the id <paramref name="id"/>; every test model has the same file (<see cref="TestModelData"/>).</summary>
    internal static ModelInfo TestModel(string id) => new(
        id,
        "Test model",
        [new ModelFile("model.bin", $"https://models.test/{id}/model.bin", Convert.ToHexStringLower(SHA256.HashData(TestModelData)), TestModelData.Length)],
        FakeEmbedder.Dims,
        512);

    /// <summary>Puts the file of <paramref name="model"/> (a <see cref="TestModel"/>) in place in <paramref name="store"/>.</summary>
    internal static void Install(ModelStore store, ModelInfo model)
    {
        var path = store.PathOf(model, model.Files[0]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, TestModelData);
    }

    /// <summary>
    /// Registers an AI runtime (call it before the first render) for <paramref name="catalog"/>, by default the one
    /// test model <c>test-model</c>; the first model is active. With <paramref name="installed"/> its file is in place
    /// already; otherwise <see cref="ModelServer"/> serves it to a download (as it serves every other test model).
    /// </summary>
    protected AiRuntime UseAi(bool installed, Func<IEmbedder> loadEmbedder, IReadOnlyList<ModelInfo>? catalog = null)
    {
        _models = new TempDir();
        var store = Store = new ModelStore(_models.Path);
        catalog ??= [TestModel("test-model")];
        if (installed)
        {
            Install(store, catalog[0]);
        }

        _http = new HttpClient(ModelServer, disposeHandler: false);
        Ai = new AiRuntime(store, new ModelDownloader(_http, store), _ => loadEmbedder(), NullLogger<AiRuntime>.Instance, catalog);
        Services.AddSingleton(Ai);
        return Ai;
    }

    /// <summary>
    /// Registers a language model runtime over <paramref name="chat"/> (call it before the first render) and switches the
    /// language model on in <see cref="Settings"/>; its state is <see cref="LanguageModelState.Off"/> until a refresh.
    /// Sessions opened by <see cref="OpenSessionAsync"/> use it.
    /// </summary>
    protected LanguageModelRuntime UseLanguageModel(FakeChatModel chat)
    {
        Settings.LanguageModelEnabled = true;
        LanguageModel = new LanguageModelRuntime(Settings, _ => chat, Time, NullLogger<LanguageModelRuntime>.Instance);
        Services.AddSingleton(LanguageModel);
        return LanguageModel;
    }

    /// <summary>Puts the editor cursor (as the editor would report it) into the text block with <paramref name="textBlockKey"/>.</summary>
    protected void SetCursorInTextBlock(Guid textBlockKey) =>
        State.Cursor = new CursorInfo(ElementKind.TextBlock, textBlockKey, textBlockKey, 0);

    /// <summary>
    /// Opens <paramref name="vault"/> as the app's session (dispatching inline, with <see cref="LanguageModel"/> if one is
    /// registered, and <see cref="SystemMonitor"/>) and selects its first book.
    /// </summary>
    protected async Task<VaultSession> OpenSessionAsync(TestVault vault, AiRuntime? ai = null, ILogger? logger = null)
    {
        var session = await VaultSession.OpenAsync(
            vault.Root, Clock, logger, timeProvider: Time, ai: ai, language: LanguageModel, monitor: SystemMonitor);
        State.Session = session;
        State.CurrentBook = session.Vault.Books.FirstOrDefault();
        State.CurrentSectionKey = State.CurrentBook?.Root.Key ?? Guid.Empty;
        return session;
    }

    /// <summary>
    /// Opens <paramref name="vault"/> with an installed test model and a working <paramref name="embedder"/> (a
    /// <see cref="FakeEmbedder"/> by default), and waits until the notes are indexed (the AI is ready).
    /// </summary>
    protected async Task<VaultSession> OpenWithAiAsync(TestVault vault, FakeEmbedder? embedder = null, ILogger? logger = null)
    {
        embedder ??= new FakeEmbedder();
        var session = await OpenSessionAsync(vault, UseAi(installed: true, () => embedder), logger);
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (session.AiStatus.State != AiState.Ready)
        {
            Assert.True(DateTime.UtcNow < until, "The AI did not become ready in time.");
            await Task.Delay(10);
        }

        return session;
    }

    /// <summary>
    /// Advances the fake time by <paramref name="step"/> at a time (15 times at most) until <paramref name="assertion"/>
    /// holds: work in the background (a status put together, a debounce restarted by an event) may finish only after an
    /// advance, so a single advance can race it.
    /// </summary>
    protected void AdvanceUntil<TComponent>(IRenderedComponent<TComponent> cut, Action assertion, TimeSpan step)
        where TComponent : Microsoft.AspNetCore.Components.IComponent
    {
        for (var i = 0; i < 15; i++)
        {
            Time.Advance(step);
            try
            {
                cut.WaitForAssertion(assertion, TimeSpan.FromMilliseconds(500));
                return;
            }
            catch (Bunit.Extensions.WaitForHelpers.WaitForFailedException)
            {
                // Not yet: another step.
            }
        }

        assertion();
    }

    /// <summary>
    /// Waits until the shell has finished opening a vault (other than <paramref name="previous"/>): the session is set
    /// before the opening ends, so the test waits for the editor to be shown again as well. It waits up to
    /// <paramref name="timeout"/> (bUnit's default if <c>null</c>).
    /// </summary>
    protected void WaitForVaultOpened(IRenderedComponent<Components.Shell> cut, VaultSession? previous = null, TimeSpan? timeout = null) =>
        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(State.Session);
            Assert.NotSame(previous, State.Session);
            Assert.Single(cut.FindAll(".ne-editor-pane"));
        }, timeout);

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
