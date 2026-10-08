using System.Net;

namespace NoteEvolution.UI.Tests;

/// <summary>Serves the test model's single file to the model downloader; can fail, or hang until released or cancelled.</summary>
public sealed class FakeModelHandler(byte[] data) : HttpMessageHandler
{
    private readonly Lock _gate = new();
    private int _requests;
    private bool _hang;
    private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The status every request is answered with; only <see cref="HttpStatusCode.OK"/> carries the file.</summary>
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    /// <summary>When true, a request waits until it is cancelled or this is set to false again (which lets it go on).</summary>
    public bool Hang
    {
        get
        {
            lock (_gate)
            {
                return _hang;
            }
        }
        set
        {
            lock (_gate)
            {
                _hang = value;
                if (!value)
                {
                    _release.TrySetResult();
                }
                else if (_release.Task.IsCompleted)
                {
                    _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
        }
    }

    public int Requests => Volatile.Read(ref _requests);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);
        Task release;
        lock (_gate)
        {
            release = _hang ? _release.Task : Task.CompletedTask;
        }

        await release.WaitAsync(ct);

        return Status == HttpStatusCode.OK
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }
            : new HttpResponseMessage(Status);
    }
}
