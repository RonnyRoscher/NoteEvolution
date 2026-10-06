using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NoteEvolution.UI.Platform;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

public class EditorCloseGuardTests
{
    private readonly AppState _state = new();
    private readonly FakeTimeProvider _time = new();
    private readonly List<string> _log = [];

    private EditorCloseGuard Guard() => new(
        _state,
        work =>
        {
            _log.Add("dispatch");
            return work();
        },
        () => _log.Add("close"),
        _time,
        NullLogger.Instance);

    [Fact]
    public void NoEditor_ClosesAtOnce()
    {
        Assert.False(Guard().OnClosing());
        Assert.Empty(_log);
    }

    [Fact]
    public void Editor_HoldsTheCloseUntilFlushed_ThenClosesAndLetsItThrough()
    {
        var flush = new TaskCompletionSource();
        _state.FlushEditor = () =>
        {
            _log.Add("flush");
            return flush.Task;
        };
        var guard = Guard();

        Assert.True(guard.OnClosing());
        Assert.True(guard.OnClosing()); // a second close request while flushing starts no second flush
        Assert.Equal(["dispatch", "flush"], _log);

        flush.SetResult();

        Assert.Equal(["dispatch", "flush", "close"], _log);
        Assert.False(guard.OnClosing());
    }

    [Fact]
    public void FailingFlush_StillCloses()
    {
        _state.FlushEditor = () => Task.FromException(new IOException("gesperrt"));
        var guard = Guard();

        Assert.True(guard.OnClosing());

        Assert.Equal(["dispatch", "close"], _log);
        Assert.False(guard.OnClosing());
    }

    [Fact]
    public void TextStillUnsavedAfterTheFlush_KeepsTheWindowOpenOnce_SecondCloseCloses()
    {
        _state.FlushEditor = () =>
        {
            _log.Add("flush");
            return Task.CompletedTask;
        };
        _state.WarnUnsavedText = () =>
        {
            _log.Add("warn");
            return true;
        };
        var guard = Guard();

        Assert.True(guard.OnClosing());

        Assert.Equal(["dispatch", "flush", "dispatch", "warn"], _log);

        // The user closes again: one more try to save, then the window closes whatever is left.
        Assert.True(guard.OnClosing());

        Assert.Equal(["dispatch", "flush", "dispatch", "warn", "dispatch", "flush", "close"], _log);
        Assert.False(guard.OnClosing());
    }

    [Fact]
    public void FailingFlushWithUnsavedText_KeepsTheWindowOpen()
    {
        _state.FlushEditor = () => Task.FromException(new IOException("gesperrt"));
        _state.WarnUnsavedText = () => true;
        var guard = Guard();

        Assert.True(guard.OnClosing());

        Assert.DoesNotContain("close", _log);
        Assert.True(guard.OnClosing());
        Assert.Contains("close", _log);
        Assert.False(guard.OnClosing());
    }

    [Fact]
    public void NothingUnsavedAfterTheFlush_Closes()
    {
        _state.FlushEditor = () => Task.CompletedTask;
        _state.WarnUnsavedText = () =>
        {
            _log.Add("warn");
            return false;
        };
        var guard = Guard();

        Assert.True(guard.OnClosing());

        Assert.Equal(["dispatch", "dispatch", "warn", "close"], _log);
        Assert.False(guard.OnClosing());
    }

    [Fact]
    public void HangingFlush_ClosesAfterTheTimeout()
    {
        _state.FlushEditor = () => new TaskCompletionSource().Task;
        var guard = Guard();

        Assert.True(guard.OnClosing());
        _time.Advance(EditorCloseGuard.Timeout - TimeSpan.FromMilliseconds(1));
        Assert.DoesNotContain("close", _log);

        _time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Contains("close", _log);
    }
}
