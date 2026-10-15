using NoteEvolution.AI.Language;

namespace NoteEvolution.AI.Tests;

/// <summary>One recorded chat request of <see cref="FakeChatModel"/>.</summary>
public sealed record ChatCall(string Model, string System, string User, ChatOptions Options)
{
    /// <summary>Set when the request's token was cancelled while it waited at the gate.</summary>
    public bool Cancelled { get; set; }
}

/// <summary>
/// Stand-in for Ollama: records every chat call, answers with <see cref="Answer"/> (default "Antwort n." for the n-th call),
/// can fail per call (<see cref="Failure"/>) and can hold every call at a <see cref="Gate"/> until the test releases it.
/// Counts version checks and the highest number of calls running at once. Thread-safe.
/// </summary>
public sealed class FakeChatModel : IChatModel
{
    private readonly Lock _gate = new();
    private readonly List<ChatCall> _calls = [];
    private int _running;

    /// <summary>The server version; <c>null</c> means unreachable.</summary>
    public string? Version { get; set; } = "0.5.0";

    /// <summary>When set, a version check never answers (a server that accepts the connection but hangs) until its token is cancelled.</summary>
    public bool VersionHangs { get; set; }

    public List<ChatModelInfo> Models { get; set; } = [new("qwen2.5:7b", 4_700_000_000)];

    /// <summary>The answer to a call, given the call and its 1-based number.</summary>
    public Func<ChatCall, int, string> Answer { get; set; } = (_, n) => $"Antwort {n}.";

    /// <summary>An exception to throw for a call (after the gate), or <c>null</c> to answer.</summary>
    public Func<ChatCall, int, Exception?> Failure { get; set; } = (_, _) => null;

    /// <summary>When set, every call waits for one release of the semaphore (honouring its token).</summary>
    public SemaphoreSlim? Gate { get; set; }

    public int VersionCalls { get; private set; }

    public int MaxRunning { get; private set; }

    public IReadOnlyList<ChatCall> Calls
    {
        get { lock (_gate) return [.. _calls]; }
    }

    public Task<string?> VersionAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            VersionCalls++;
            return VersionHangs ? Hang(ct) : Task.FromResult(Version);
        }
    }

    private static async Task<string?> Hang(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return null;
    }

    public Task<IReadOnlyList<ChatModelInfo>> ModelsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (Version is null) throw new ChatModelUnavailableException("unreachable");
            return Task.FromResult<IReadOnlyList<ChatModelInfo>>([.. Models]);
        }
    }

    public async Task<string> ChatAsync(string model, string system, string user, ChatOptions options, CancellationToken ct)
    {
        var call = new ChatCall(model, system, user, options);
        int number;
        lock (_gate)
        {
            _calls.Add(call);
            number = _calls.Count;
            _running++;
            MaxRunning = Math.Max(MaxRunning, _running);
        }

        try
        {
            if (Gate is { } gate)
            {
                try
                {
                    await gate.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    call.Cancelled = true;
                    throw;
                }
            }

            if (Failure(call, number) is { } failure) throw failure;
            return Answer(call, number);
        }
        finally
        {
            lock (_gate) _running--;
        }
    }

    public Task PullAsync(string model, IProgress<PullProgress>? progress, CancellationToken ct) => Task.CompletedTask;
}
