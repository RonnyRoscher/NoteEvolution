using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using NoteEvolution.AI.Embeddings;
using NoteEvolution.AI.Model;
using NoteEvolution.AI.Tests;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

public sealed class AiRuntimeTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempDir _dir = new();
    private readonly FakeModelHandler _server = new(UiTestContext.TestModelData);
    private readonly HttpClient _http;
    private readonly ModelStore _store;
    private readonly ModelInfo _a = UiTestContext.TestModel("a");
    private readonly ModelInfo _b = UiTestContext.TestModel("b");

    /// <summary>The models the embedder factory was called with, in order.</summary>
    private readonly List<ModelInfo> _loads = [];

    /// <summary>Every embedder the factory returned.</summary>
    private readonly List<DisposableEmbedder> _embedders = [];

    public AiRuntimeTests()
    {
        _http = new HttpClient(_server, disposeHandler: false);
        _store = new ModelStore(_dir.Path);
    }

    [Fact]
    public async Task DownloadOther_InstallsIt_ActiveUnchanged()
    {
        UiTestContext.Install(_store, _a);
        using var ai = Create();
        var changes = 0;
        ai.ModelChanged += () => changes++;

        await ai.DownloadAsync(_b, null, CancellationToken.None);

        Assert.True(_store.IsInstalled(_b));
        Assert.Same(_a, ai.Model);
        Assert.True(ai.ModelInstalled);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task DownloadOther_Cancelled_DeletesTargetFolder_ActiveUnchanged()
    {
        UiTestContext.Install(_store, _a);
        using var ai = Create();
        _server.Hang = true;
        using var cts = new CancellationTokenSource();

        var download = ai.DownloadAsync(_b, null, cts.Token);
        var until = DateTime.UtcNow + Wait;
        while (_server.Requests == 0)
        {
            Assert.True(DateTime.UtcNow < until, "The download did not start in time.");
            await Task.Delay(5);
        }

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.False(Directory.Exists(_store.DirectoryOf(_b)));
        Assert.True(Directory.Exists(_store.DirectoryOf(_a)));
        Assert.Same(_a, ai.Model);
        Assert.True(ai.ModelInstalled);
    }

    [Fact]
    public async Task DownloadOther_Fails_DeletesTargetFolder()
    {
        UiTestContext.Install(_store, _a);
        using var ai = Create();
        _server.Status = HttpStatusCode.NotFound;

        await Assert.ThrowsAsync<ModelDownloadException>(() => ai.DownloadAsync(_b, null, CancellationToken.None));

        Assert.False(Directory.Exists(_store.DirectoryOf(_b)));
        Assert.True(ai.ModelInstalled);
    }

    [Fact]
    public async Task DownloadActive_NotInstalled_FailureKeepsNothingButDoesNotThrowOnCleanup()
    {
        using var ai = Create();
        _server.Status = HttpStatusCode.NotFound;

        await Assert.ThrowsAsync<ModelDownloadException>(() => ai.DownloadAsync(_a, null, CancellationToken.None));

        Assert.False(ai.ModelInstalled);
        Assert.False(ai.IsDownloading);
    }

    [Fact]
    public async Task Activate_DisposesOldEmbedder_LoadsNewOnNextAccess_DeletesOldFolder_RaisesModelChanged()
    {
        UiTestContext.Install(_store, _a);
        using var ai = Create();
        Assert.NotNull(ai.Embedder);
        Assert.Equal([_a], _loads);
        await ai.DownloadAsync(_b, null, CancellationToken.None);
        var changes = 0;
        ai.ModelChanged += () => changes++;

        ai.Activate(_b);

        Assert.True(_embedders[0].Disposed);
        Assert.Same(_b, ai.Model);
        Assert.True(ai.ModelInstalled);
        Assert.False(Directory.Exists(_store.DirectoryOf(_a)));
        Assert.Equal(1, changes);

        var embedder = ai.Embedder;
        Assert.Equal([_a, _b], _loads);
        Assert.Same(_embedders[1], embedder);
    }

    [Fact]
    public void Activate_NotInstalled_Throws_SameModel_NoOp()
    {
        UiTestContext.Install(_store, _a);
        using var ai = Create();
        Assert.NotNull(ai.Embedder);
        var changes = 0;
        ai.ModelChanged += () => changes++;

        Assert.Throws<InvalidOperationException>(() => ai.Activate(_b));
        ai.Activate(_a);

        Assert.Same(_a, ai.Model);
        Assert.Equal(0, changes);
        Assert.False(_embedders[0].Disposed);
        Assert.Same(_embedders[0], ai.Embedder);
        Assert.Single(_loads);
    }

    [Fact]
    public async Task Activate_ActiveInstalled_DoesNotWaitForRunningLoad()
    {
        UiTestContext.Install(_store, _a);
        using var loading = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var ai = new AiRuntime(
            _store,
            new ModelDownloader(_http, _store),
            _ =>
            {
                loading.Set();
                release.Wait(Wait);
                return new DisposableEmbedder();
            },
            NullLogger<AiRuntime>.Instance,
            [_a, _b]);

        // A session loads the embedder (a long ONNX load) while the UI thread activates the active model.
        var load = Task.Run(() => ai.Embedder);
        try
        {
            Assert.True(loading.Wait(Wait));

            var activate = Task.Run(() => ai.Activate(_a));

            // Times out if Activate waits for the running load.
            await activate.WaitAsync(Wait);
            Assert.Same(_a, ai.Model);
        }
        finally
        {
            release.Set();
        }

        Assert.NotNull(await load);
    }

    [Fact]
    public async Task Activate_ResetsLoadError()
    {
        UiTestContext.Install(_store, _a);
        using var ai = Create(throwFor: "a");
        Assert.Null(ai.Embedder);
        Assert.NotNull(ai.LoadError);
        await ai.DownloadAsync(_b, null, CancellationToken.None);

        ai.Activate(_b);

        Assert.NotNull(ai.Embedder);
        Assert.Null(ai.LoadError);
    }

    [Fact]
    public void Start_InstalledActive_DeletesOtherFolders()
    {
        UiTestContext.Install(_store, _a);
        UiTestContext.Install(_store, _b);

        using var ai = Create(activeModelId: "a");

        Assert.Same(_a, ai.Model);
        Assert.True(Directory.Exists(_store.DirectoryOf(_a)));
        Assert.False(Directory.Exists(_store.DirectoryOf(_b)));
    }

    [Fact]
    public void Start_UnknownId_UsesDefault_MissingActive_DeletesNothing()
    {
        UiTestContext.Install(_store, _b);

        using var ai = Create(activeModelId: "zzz");

        Assert.Same(_a, ai.Model);
        Assert.False(ai.ModelInstalled);
        Assert.True(Directory.Exists(_store.DirectoryOf(_b)));
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
        _dir.Dispose();
    }

    /// <summary>A runtime over the catalog [a, b]; its embedder factory counts and throws for the model <paramref name="throwFor"/>.</summary>
    private AiRuntime Create(string? activeModelId = null, string? throwFor = null) =>
        new(
            _store,
            new ModelDownloader(_http, _store),
            model =>
            {
                _loads.Add(model);
                if (model.Id == throwFor) throw new InvalidOperationException("load failed");
                var embedder = new DisposableEmbedder();
                _embedders.Add(embedder);
                return embedder;
            },
            NullLogger<AiRuntime>.Instance,
            [_a, _b],
            activeModelId);

    private sealed class DisposableEmbedder : IEmbedder, IDisposable
    {
        private readonly FakeEmbedder _inner = new();

        public bool Disposed { get; private set; }

        public int Dimensions => _inner.Dimensions;

        public IReadOnlyList<float[]> Embed(IReadOnlyList<string> texts, CancellationToken ct = default) => _inner.Embed(texts, ct);

        public void Dispose() => Disposed = true;
    }
}
