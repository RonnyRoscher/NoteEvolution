using System.Net;

namespace NoteEvolution.UI.Tests;

/// <summary>Serves the test model's single file to the model downloader; can fail or hang until the download is cancelled.</summary>
public sealed class FakeModelHandler(byte[] data) : HttpMessageHandler
{
    private int _requests;

    /// <summary>The status every request is answered with; only <see cref="HttpStatusCode.OK"/> carries the file.</summary>
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    /// <summary>When true, a request waits until it is cancelled.</summary>
    public bool Hang { get; set; }

    public int Requests => Volatile.Read(ref _requests);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);
        if (Hang)
        {
            await Task.Delay(Timeout.Infinite, ct);
        }

        return Status == HttpStatusCode.OK
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }
            : new HttpResponseMessage(Status);
    }
}
