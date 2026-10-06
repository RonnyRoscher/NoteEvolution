using Microsoft.Extensions.Logging;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Platform;

/// <summary>
/// Keeps the window open until the editor has saved its pending text (<see cref="AppState.FlushEditor"/>), so text typed
/// within the last second before closing is not lost. The shell calls <see cref="OnClosing"/> from its window-closing
/// event; the first close is held back, the flush runs on the UI dispatcher, then the window is closed for real.
/// </summary>
/// <param name="dispatch">Runs a function on the UI (Blazor) dispatcher.</param>
/// <param name="close">Closes the window again.</param>
/// <param name="time">The clock for <see cref="Timeout"/>.</param>
public sealed class EditorCloseGuard(
    AppState state, Func<Func<Task>, Task> dispatch, Action close, TimeProvider time, ILogger logger)
{
    /// <summary>A flush that takes longer than this does not keep the window open.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private bool _flushing;
    private bool _flushed;

    /// <returns><c>true</c> to keep the window open for now (the flush is running), <c>false</c> to let it close.</returns>
    public bool OnClosing()
    {
        if (_flushed || state.FlushEditor is null)
        {
            return false;
        }

        if (!_flushing)
        {
            _flushing = true;
            _ = FlushThenCloseAsync();
        }

        return true;
    }

    private async Task FlushThenCloseAsync()
    {
        try
        {
            var flush = dispatch(() => state.FlushEditor?.Invoke() ?? Task.CompletedTask);
            if (await Task.WhenAny(flush, Task.Delay(Timeout, time)) == flush)
            {
                await flush;
            }
            else
            {
                logger.LogWarning("Saving the editor before closing took longer than {Timeout}", Timeout);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Saving the editor before closing failed");
        }

        _flushed = true;
        close();
    }
}
