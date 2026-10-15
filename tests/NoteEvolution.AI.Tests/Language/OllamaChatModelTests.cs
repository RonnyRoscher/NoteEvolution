using System.Net;
using System.Text;
using System.Text.Json;
using NoteEvolution.AI.Language;

namespace NoteEvolution.AI.Tests.Language;

public class OllamaChatModelTests
{
    private sealed record Recorded(HttpMethod Method, string Path, string Body);

    private sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Recorded> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new Recorded(request.Method, request.RequestUri!.AbsolutePath, body));
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static OllamaChatModel Model(FakeHttpHandler handler) =>
        new(new HttpClient(handler), new Uri("http://127.0.0.1:11434"));

    private sealed class ListProgress : IProgress<PullProgress>
    {
        public List<PullProgress> Reports { get; } = [];
        public void Report(PullProgress value) => Reports.Add(value);
    }

    [Fact]
    public async Task Version_Reachable_ReturnsVersion()
    {
        var handler = new FakeHttpHandler(_ => Json("""{"version":"0.40.4"}"""));

        var version = await Model(handler).VersionAsync(CancellationToken.None);

        Assert.Equal("0.40.4", version);
        Assert.Equal("/api/version", handler.Requests.Single().Path);
        Assert.Equal(HttpMethod.Get, handler.Requests.Single().Method);
    }

    [Fact]
    public async Task Version_ConnectionRefused_ReturnsNull()
    {
        var handler = new FakeHttpHandler(_ => throw new HttpRequestException("refused"));

        Assert.Null(await Model(handler).VersionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Models_ParsesNamesAndSizes()
    {
        var handler = new FakeHttpHandler(_ => Json(
            """{"models":[{"name":"qwen2.5:7b","size":4683087332},{"name":"llama3:8b","size":42}]}"""));

        var models = await Model(handler).ModelsAsync(CancellationToken.None);

        Assert.Equal([new ChatModelInfo("qwen2.5:7b", 4683087332), new ChatModelInfo("llama3:8b", 42)], models);
        Assert.Equal("/api/tags", handler.Requests.Single().Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Chat_SendsModelSystemUserAndOptions_ReturnsContent(bool jsonMode)
    {
        var handler = new FakeHttpHandler(_ => Json("""{"message":{"role":"assistant","content":"Antwort"}}"""));

        var answer = await Model(handler).ChatAsync("qwen2.5:7b", "sys", "usr",
            new ChatOptions(600, 0.5, jsonMode), CancellationToken.None);

        Assert.Equal("Antwort", answer);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/chat", request.Path);
        using var doc = JsonDocument.Parse(request.Body);
        var root = doc.RootElement;
        Assert.Equal("qwen2.5:7b", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        var messages = root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("sys", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("usr", messages[1].GetProperty("content").GetString());
        Assert.Equal(600, root.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.Equal(0.5, root.GetProperty("options").GetProperty("temperature").GetDouble());
        if (jsonMode)
            Assert.Equal("json", root.GetProperty("format").GetString());
        else
            Assert.False(root.TryGetProperty("format", out _));
    }

    [Fact]
    public async Task Chat_ServerError_ThrowsChatModelUnavailable()
    {
        var handler = new FakeHttpHandler(_ => Json("""{"error":"boom"}""", HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<ChatModelUnavailableException>(() =>
            Model(handler).ChatAsync("m", "s", "u", new ChatOptions(10), CancellationToken.None));
    }

    [Fact]
    public async Task Chat_ConnectionRefused_ThrowsChatModelUnavailable()
    {
        var handler = new FakeHttpHandler(_ => throw new HttpRequestException("refused"));

        await Assert.ThrowsAsync<ChatModelUnavailableException>(() =>
            Model(handler).ChatAsync("m", "s", "u", new ChatOptions(10), CancellationToken.None));
    }

    [Fact]
    public async Task Chat_Cancelled_ThrowsOperationCanceled()
    {
        var handler = new FakeHttpHandler(_ => Json("""{"message":{"content":"x"}}"""));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Model(handler).ChatAsync("m", "s", "u", new ChatOptions(10), cts.Token));
    }

    [Fact]
    public async Task Pull_ReportsProgressLines_EndsOnSuccess()
    {
        const string lines =
            "{\"status\":\"pulling manifest\"}\n" +
            "{\"status\":\"downloading\",\"total\":1000,\"completed\":250}\n" +
            "{\"status\":\"downloading\",\"total\":1000,\"completed\":1000}\n" +
            "{\"status\":\"success\"}\n";
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(lines) });
        var progress = new ListProgress();

        await Model(handler).PullAsync("qwen2.5:7b", progress, CancellationToken.None);

        Assert.Equal(
        [
            new PullProgress("pulling manifest", 0, 0),
            new PullProgress("downloading", 250, 1000),
            new PullProgress("downloading", 1000, 1000),
            new PullProgress("success", 0, 0),
        ], progress.Reports);
        var request = handler.Requests.Single();
        Assert.Equal("/api/pull", request.Path);
        using var doc = JsonDocument.Parse(request.Body);
        Assert.Equal("qwen2.5:7b", doc.RootElement.GetProperty("model").GetString());
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task Pull_ErrorLine_Throws()
    {
        const string lines = "{\"status\":\"pulling manifest\"}\n{\"error\":\"pull model manifest: file does not exist\"}\n";
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(lines) });

        var ex = await Assert.ThrowsAsync<ChatModelUnavailableException>(() =>
            Model(handler).PullAsync("nope", null, CancellationToken.None));

        Assert.Contains("file does not exist", ex.Message);
    }
}
