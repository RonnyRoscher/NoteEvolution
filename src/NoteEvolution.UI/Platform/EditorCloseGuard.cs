using Microsoft.Extensions.Logging;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Platform;

/// <summary>
/// Keeps the window open until the editor has saved its pending text (<see cref="AppState.FlushEditor"/>), so text typed
/// within the last second before closing is not lost. The shell calls <see cref="OnClosing"/> from its window-closing
/// event; the first close is held back, the flush runs on the UI dispatcher, then the window is closed for real.
/// <para>
/// If the editor still holds text it could not save (<see cref="AppState.WarnUnsavedText"/>, which tells the user), the
/// window stays open once (ruling R33). The next close request tries to save once more and then closes in any case.
/// </para>
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
    private bool _warned;

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
        var finished = false;
        try
        {
            var flush = dispatch(() => state.FlushEditor?.Invoke() ?? Task.CompletedTask);
            if (await Task.WhenAny(flush, Task.Delay(Timeout, time)) == flush)
            {
                finished = true;
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

        if (finished && !_warned && await StillUnsavedAsync())
        {
            // The user has been told; the window stays open for now.
            _warned = true;
            _flushing = false;
            return;
        }

        _flushed = true;
        close();
    }

    /// <summary>Asks the editor on the UI dispatcher whether text is left unsaved (it then shows why the window stays open).</summary>
    private async Task<bool> StillUnsavedAsync()
    {
        if (state.WarnUnsavedText is null)
        {
            return false;
        }

        var unsaved = false;
        try
        {
            await dispatch(() =>
            {
                unsaved = state.WarnUnsavedText?.Invoke() ?? false;
                return Task.CompletedTask;
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Checking the editor for unsaved text before closing failed");
        }

        if (unsaved)
        {
            logger.LogWarning("The window stays open: the editor holds text it could not save");
        }

        return unsaved;
    }
}
