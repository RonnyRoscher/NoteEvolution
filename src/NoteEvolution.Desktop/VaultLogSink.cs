using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace NoteEvolution.Desktop;

/// <summary>
/// Writes the log to <c>noteevolution-&lt;date&gt;.log</c> (one file per day) in a folder that can change at run time:
/// first the user data folder, then the open vault's <c>.noteevolution/logs</c>.
/// </summary>
internal sealed class VaultLogSink : ILogEventSink, IDisposable
{
    private readonly object _gate = new();
    private string _directory;
    private Logger _file;

    public VaultLogSink(string directory)
    {
        _directory = Path.GetFullPath(directory);
        _file = CreateFileLogger(_directory);
    }

    /// <summary>Continues the log in <paramref name="directory"/>; <c>false</c> if that is the current folder already.</summary>
    public bool UseDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        lock (_gate)
        {
            if (string.Equals(fullPath, _directory, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var previous = _file;
            _file = CreateFileLogger(fullPath);
            _directory = fullPath;
            previous.Dispose();
            return true;
        }
    }

    public void Emit(LogEvent logEvent)
    {
        lock (_gate)
        {
            _file.Write(logEvent);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _file.Dispose();
        }
    }

    private static Logger CreateFileLogger(string directory) => new LoggerConfiguration()
        .MinimumLevel.Verbose()
        .WriteTo.File(Path.Combine(directory, "noteevolution-.log"), rollingInterval: RollingInterval.Day)
        .CreateLogger();
}
