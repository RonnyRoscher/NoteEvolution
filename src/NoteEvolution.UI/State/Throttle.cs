using Microsoft.Extensions.Logging;

namespace NoteEvolution.UI.State;

/// <summary>
/// Runs a piece of work at most once per delay: <see cref="Request"/> starts a timer of <c>delay</c> unless one is
/// pending; a request while the work runs starts the timer once more after it ended. The work runs on the timer's
/// thread until its first await; a failure is logged. Thread-safe; never throws.
/// </summary>
internal sealed class Throttle(TimeProvider time, TimeSpan delay, Func<Task> run, ILogger logger, string what) : IDisposable
{
    private readonly object _gate = new();
    private State _state;
    private ITimer? _timer;
    private bool _disposed;

    private enum State { Idle, Waiting, Running, RunningAgain }

    /// <summary>The work runs now; for tests.</summary>
    public bool Running
    {
        get
        {
            lock (_gate)
            {
                return _state is State.Running or State.RunningAgain;
            }
        }
    }

    public void Request()
    {
        lock (_gate)
        {
            switch (_state)
            {
                case State.Idle when !_disposed:
                    _state = State.Waiting;
                    StartTimer();
                    break;
                case State.Running:
                    _state = State.RunningAgain;
                    break;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
        }
    }

    /// <summary>Call it under <see cref="_gate"/>.</summary>
    private void StartTimer()
    {
        if (_timer is { } timer)
        {
            timer.Change(delay, Timeout.InfiniteTimeSpan);
            return;
        }

        _timer = time.CreateTimer(_ => OnTimer(), null, delay, Timeout.InfiniteTimeSpan);
    }

    private void OnTimer()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _state = State.Running;
        }

        Task task;
        try
        {
            task = run();
        }
        catch (Exception ex)
        {
            task = Task.FromException(ex);
        }

        task.ContinueWith(
            t =>
            {
                if (t.Exception is { } ex)
                {
                    logger.LogError(ex, "{What} failed", what);
                }

                lock (_gate)
                {
                    var again = _state == State.RunningAgain && !_disposed;
                    _state = again ? State.Waiting : State.Idle;
                    if (again)
                    {
                        StartTimer();
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
