using System.Globalization;

namespace NoteEvolution.UI.Platform;

/// <summary>
/// The computations behind <see cref="ISystemMonitor"/>, apart from the platform calls that read the numbers: the
/// processor load from two readings of the system's times, and the Linux files <c>/proc/stat</c>, <c>/proc/meminfo</c>
/// and <c>/sys/class/power_supply/*/status</c>.
/// </summary>
public static class SystemReadings
{
    /// <summary>Bytes per GB as the system meter counts them (2^30, as the operating systems show memory).</summary>
    public const double BytesPerGb = 1024.0 * 1024 * 1024;

    /// <summary>
    /// Whether a process (its name without ".exe") belongs to Ollama: "ollama", "ollama app", "ollama_llama_server", and
    /// "llama-server", which newer versions start to run the model and which does the actual work.
    /// </summary>
    public static bool IsOllamaProcess(string processName) =>
        processName.StartsWith("ollama", StringComparison.OrdinalIgnoreCase)
        || processName.StartsWith("llama-server", StringComparison.OrdinalIgnoreCase)
        || processName.StartsWith("llama_server", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The processor load between two readings of the system's times (any unit): the share of the time that passed
    /// that was not idle, 0–100; <c>null</c> when no time passed (or the counters went backwards).
    /// </summary>
    public static double? CpuPercent(long prevIdle, long prevTotal, long idle, long total)
    {
        var passed = total - prevTotal;
        if (passed <= 0)
        {
            return null;
        }

        var idlePassed = idle - prevIdle;
        if (idlePassed < 0)
        {
            return null;
        }

        return Math.Clamp(100.0 * (passed - idlePassed) / passed, 0, 100);
    }

    /// <summary>
    /// The share of the computer a process (or several) used: <paramref name="cpuTime"/> processor time during
    /// <paramref name="elapsed"/> on <paramref name="processorCount"/> processors, 0–100; <c>null</c> when no time
    /// passed or the processor time went backwards.
    /// </summary>
    public static double? ProcessCpuPercent(TimeSpan cpuTime, TimeSpan elapsed, int processorCount)
    {
        if (elapsed <= TimeSpan.Zero || cpuTime < TimeSpan.Zero || processorCount <= 0)
        {
            return null;
        }

        return Math.Clamp(100.0 * cpuTime.Ticks / (elapsed.Ticks * (double)processorCount), 0, 100);
    }

    /// <summary>
    /// The idle and total time of all processors from <c>/proc/stat</c>'s <c>cpu</c> line (user, nice, system, idle,
    /// iowait, irq, softirq, steal; guest time is part of user time). Idle includes iowait. <c>null</c> without that line
    /// or with fewer than four numbers.
    /// </summary>
    public static (long Idle, long Total)? ParseProcStat(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts[0] != "cpu")
            {
                continue;
            }

            var values = new List<long>();
            foreach (var part in parts.Skip(1).Take(8))
            {
                if (!long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                {
                    return null;
                }

                values.Add(value);
            }

            if (values.Count < 4)
            {
                return null;
            }

            var idle = values[3] + (values.Count > 4 ? values[4] : 0);
            return (idle, values.Sum());
        }

        return null;
    }

    /// <summary>
    /// The memory in use (total less available) and the total, in GB, from <c>/proc/meminfo</c>; <c>null</c> without
    /// <c>MemTotal</c> or <c>MemAvailable</c>.
    /// </summary>
    public static (double UsedGb, double TotalGb)? ParseMeminfo(string text)
    {
        long? total = null;
        long? available = null;
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Split([' ', ':'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var kilobytes))
            {
                continue;
            }

            switch (parts[0])
            {
                case "MemTotal":
                    total = kilobytes;
                    break;
                case "MemAvailable":
                    available = kilobytes;
                    break;
            }
        }

        if (total is not { } t || available is not { } a)
        {
            return null;
        }

        return (Gigabytes((t - a) * 1024), Gigabytes(t * 1024));
    }

    /// <summary>
    /// Whether the computer runs on battery, from the <c>status</c> files of its power supplies: one is discharging;
    /// <c>null</c> when there is no status (no battery, or it cannot be read).
    /// </summary>
    public static bool? OnBattery(IEnumerable<string> powerSupplyStatuses)
    {
        bool? onBattery = null;
        foreach (var status in powerSupplyStatuses)
        {
            if (status.Trim() == "Discharging")
            {
                return true;
            }

            onBattery = false;
        }

        return onBattery;
    }

    /// <summary>The databases below <c>.noteevolution</c> that the breakdown names, each with its <c>-wal</c>/<c>-shm</c> files.</summary>
    private static readonly (string Name, DataPartKind Kind)[] Databases =
    [
        ("vectors.db", DataPartKind.NoteVectors),
        ("book-vectors.db", DataPartKind.BookVectors),
        ("understanding.db", DataPartKind.Understanding),
        ("history.db", DataPartKind.History),
    ];

    /// <summary>
    /// The app's data on disk, part by part. Counted: everything below <c>&lt;vault&gt;/.noteevolution</c> (the named
    /// databases with their <c>-wal</c>/<c>-shm</c> files, the <c>backups</c> and <c>logs</c> folders, every other file,
    /// also ones the app adds later) and the open book's file <paramref name="bookFile"/> (left out if <c>null</c>). Not
    /// counted: the vault's Markdown pages and journals (outside hidden folders) and the models folder
    /// <paramref name="modelsDirectory"/> (left out if <c>null</c>). A missing folder or a missing or unreadable (locked)
    /// file counts as 0. Walks the folders: call it off the UI thread, and not more often than every few seconds.
    /// </summary>
    public static DataUsage DataUsage(string vaultRoot, string? bookFile, string? modelsDirectory)
    {
        var folder = Path.Combine(vaultRoot, ".noteevolution");
        var bytes = new Dictionary<DataPartKind, long>();
        foreach (var file in Files(folder))
        {
            var relative = Path.GetRelativePath(folder, file.FullName);
            var kind = KindOf(relative);
            bytes[kind] = bytes.GetValueOrDefault(kind) + Length(file);
        }

        var parts = new List<DataPart>();
        foreach (var kind in new[]
                 {
                     DataPartKind.NoteVectors, DataPartKind.BookVectors, DataPartKind.Understanding, DataPartKind.History,
                     DataPartKind.Backups, DataPartKind.Logs, DataPartKind.Other,
                 })
        {
            parts.Add(new DataPart(kind, bytes.GetValueOrDefault(kind), Counted: true));
        }

        if (bookFile is not null)
        {
            parts.Add(new DataPart(DataPartKind.Book, Length(new FileInfo(bookFile)), Counted: true, Path.GetFileName(bookFile)));
        }

        var pages = Files(vaultRoot, skip: dir => Path.GetFileName(dir).StartsWith('.'))
            .Where(f => f.Extension.Equals(".md", StringComparison.OrdinalIgnoreCase) && !f.Name.StartsWith('.'))
            .Sum(Length);
        parts.Add(new DataPart(DataPartKind.VaultPages, pages, Counted: false));
        if (modelsDirectory is not null)
        {
            parts.Add(new DataPart(DataPartKind.Model, Files(modelsDirectory).Sum(Length), Counted: false));
        }

        return new DataUsage(parts);
    }

    /// <summary>The part a file below <c>.noteevolution</c> belongs to, by its path relative to that folder.</summary>
    private static DataPartKind KindOf(string relative)
    {
        var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        if (relative.Length > first.Length)
        {
            return first switch
            {
                "backups" => DataPartKind.Backups,
                "logs" => DataPartKind.Logs,
                _ => DataPartKind.Other,
            };
        }

        foreach (var (name, kind) in Databases)
        {
            if (first == name || first == name + "-wal" || first == name + "-shm")
            {
                return kind;
            }
        }

        return DataPartKind.Other;
    }

    /// <summary>
    /// The files below <paramref name="root"/>, skipping folders <paramref name="skip"/> names; a missing or unreadable
    /// folder gives none.
    /// </summary>
    private static IEnumerable<FileInfo> Files(string root, Func<string, bool>? skip = null)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            FileInfo[] files;
            string[] subdirs;
            try
            {
                var info = new DirectoryInfo(dir);
                if (!info.Exists)
                {
                    continue;
                }

                files = info.GetFiles();
                subdirs = Directory.GetDirectories(dir);
            }
            catch (Exception)
            {
                // unreadable or vanished meanwhile: nothing below it counts
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            foreach (var sub in subdirs)
            {
                if (skip?.Invoke(sub) != true)
                {
                    pending.Push(sub);
                }
            }
        }
    }

    /// <summary>A file's size; a missing or unreadable (locked) file counts as 0.</summary>
    private static long Length(FileInfo file)
    {
        try
        {
            file.Refresh();
            return file.Exists ? file.Length : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary><paramref name="bytes"/> in GB (<see cref="BytesPerGb"/>).</summary>
    public static double Gigabytes(long bytes) => bytes / BytesPerGb;
}
