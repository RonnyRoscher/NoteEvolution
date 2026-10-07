using System.Net;
using NoteEvolution.AI.Model;
using NoteEvolution.TestSupport;

namespace NoteEvolution.AI.Tests.Model;

public class ModelDownloaderTests
{
    private sealed class FakeHandler(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            ct.ThrowIfCancellationRequested();
            var key = request.RequestUri!.AbsolutePath.TrimStart('/');
            return Task.FromResult(files.TryGetValue(key, out var data)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class ListProgress : IProgress<DownloadProgress>
    {
        public List<DownloadProgress> Reports { get; } = [];
        public void Report(DownloadProgress value) => Reports.Add(value);
    }

    private sealed class CancelOnFirstReport(CancellationTokenSource cts) : IProgress<DownloadProgress>
    {
        public bool Reported { get; private set; }
        public void Report(DownloadProgress value)
        {
            if (value.BytesDone > 0 && !Reported)
            {
                Reported = true;
                cts.Cancel();
            }
        }
    }

    private static byte[] Bytes(int n, byte seed) => [.. Enumerable.Range(0, n).Select(i => (byte)(i * 7 + seed))];

    private static string[] Leftovers(string dir) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, "*.part", SearchOption.AllDirectories) : [];

    [Fact]
    public async Task Download_WritesFilesAtomically_ReportsProgress_IsInstalled()
    {
        using var dir = new TempDir();
        var a = Bytes(2_500_000, 1);
        var b = Bytes(300, 2);
        var model = ModelStoreTests.TestModel(("onnx/a.bin", a), ("b.bin", b));
        var handler = new FakeHandler(new() { ["onnx/a.bin"] = a, ["b.bin"] = b });
        var store = new ModelStore(dir.Path);
        var progress = new ListProgress();

        await new ModelDownloader(new HttpClient(handler), store).DownloadAsync(model, progress, CancellationToken.None);

        Assert.True(store.IsInstalled(model));
        Assert.Equal(a, File.ReadAllBytes(store.PathOf(model, model.Files[0])));
        Assert.Equal(b, File.ReadAllBytes(store.PathOf(model, model.Files[1])));
        Assert.Empty(Leftovers(store.DirectoryOf(model)));

        var total = a.Length + b.Length;
        Assert.All(progress.Reports, r => Assert.Equal(total, r.BytesTotal));
        Assert.Equal(new DownloadProgress(total, total), progress.Reports[^1]);
        Assert.Equal(progress.Reports.Select(r => r.BytesDone).Order(), progress.Reports.Select(r => r.BytesDone));
        // 2.5 MB in the first file: at least a report per MB, not only at the end.
        Assert.True(progress.Reports.Count(r => r.BytesDone > 0 && r.BytesDone < a.Length) >= 2);
    }

    [Fact]
    public async Task Download_ShaMismatch_Throws_LeavesNoFile()
    {
        using var dir = new TempDir();
        var good = Bytes(100, 3);
        var model = ModelStoreTests.TestModel(("onnx/a.bin", good));
        var tampered = (byte[])good.Clone();
        tampered[50] ^= 0xFF;
        var handler = new FakeHandler(new() { ["onnx/a.bin"] = tampered });
        var store = new ModelStore(dir.Path);

        var ex = await Assert.ThrowsAsync<ModelDownloadException>(() =>
            new ModelDownloader(new HttpClient(handler), store).DownloadAsync(model, null, CancellationToken.None));

        Assert.Contains("a.bin", ex.Message);
        Assert.False(File.Exists(store.PathOf(model, model.Files[0])));
        Assert.Empty(Leftovers(store.DirectoryOf(model)));
        Assert.False(store.IsInstalled(model));
    }

    [Fact]
    public async Task Download_ShaMismatch_DoesNotReplaceExistingFile()
    {
        using var dir = new TempDir();
        var model = ModelStoreTests.TestModel(("a.bin", Bytes(100, 3)));
        var store = new ModelStore(dir.Path);
        Directory.CreateDirectory(store.DirectoryOf(model));
        var existing = new byte[7];                                   // wrong size -> will be fetched again
        File.WriteAllBytes(store.PathOf(model, model.Files[0]), existing);
        var handler = new FakeHandler(new() { ["a.bin"] = Bytes(100, 9) });

        await Assert.ThrowsAsync<ModelDownloadException>(() =>
            new ModelDownloader(new HttpClient(handler), store).DownloadAsync(model, null, CancellationToken.None));

        Assert.Equal(existing, File.ReadAllBytes(store.PathOf(model, model.Files[0])));
        Assert.Empty(Leftovers(store.DirectoryOf(model)));
    }

    [Fact]
    public async Task Download_HttpError_ThrowsDownloadException()
    {
        using var dir = new TempDir();
        var model = ModelStoreTests.TestModel(("missing.bin", Bytes(10, 1)));
        var handler = new FakeHandler([]);

        var ex = await Assert.ThrowsAsync<ModelDownloadException>(() =>
            new ModelDownloader(new HttpClient(handler), new ModelStore(dir.Path)).DownloadAsync(model, null, CancellationToken.None));

        Assert.Contains("missing.bin", ex.Message);
    }

    [Fact]
    public async Task Download_Cancelled_LeavesNoPartFile_NotInstalled()
    {
        using var dir = new TempDir();
        var a = Bytes(3_000_000, 1);
        var model = ModelStoreTests.TestModel(("onnx/a.bin", a));
        var handler = new FakeHandler(new() { ["onnx/a.bin"] = a });
        var store = new ModelStore(dir.Path);
        using var cts = new CancellationTokenSource();
        var progress = new CancelOnFirstReport(cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ModelDownloader(new HttpClient(handler), store).DownloadAsync(model, progress, cts.Token));

        Assert.True(progress.Reported);
        Assert.Empty(Leftovers(store.DirectoryOf(model)));
        Assert.False(File.Exists(store.PathOf(model, model.Files[0])));
        Assert.False(store.IsInstalled(model));
    }

    [Fact]
    public async Task Download_SkipsAlreadyCompleteFiles()
    {
        using var dir = new TempDir();
        var a = Bytes(1000, 1);
        var b = Bytes(500, 2);
        var model = ModelStoreTests.TestModel(("a.bin", a), ("b.bin", b));
        var store = new ModelStore(dir.Path);
        Directory.CreateDirectory(store.DirectoryOf(model));
        File.WriteAllBytes(store.PathOf(model, model.Files[0]), a);
        var handler = new FakeHandler(new() { ["a.bin"] = a, ["b.bin"] = b });
        var progress = new ListProgress();

        await new ModelDownloader(new HttpClient(handler), store).DownloadAsync(model, progress, CancellationToken.None);

        Assert.Equal(["https://example.test/b.bin"], handler.Requests);
        Assert.True(store.IsInstalled(model));
        Assert.Equal(new DownloadProgress(1500, 1500), progress.Reports[^1]);

        handler.Requests.Clear();
        await new ModelDownloader(new HttpClient(handler), store).DownloadAsync(model, null, CancellationToken.None);
        Assert.Empty(handler.Requests);
    }
}
