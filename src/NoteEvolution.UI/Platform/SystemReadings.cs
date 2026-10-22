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

    /// <summary>
    /// The size of the vault's analysis files in bytes: <c>vectors.db</c> and <c>understanding.db</c> in
    /// <c>.noteevolution</c> with their <c>-wal</c>/<c>-shm</c> files. A missing or unreadable (locked) file counts as 0.
    /// </summary>
    public static long AnalysisBytes(string vaultRoot)
    {
        long sum = 0;
        foreach (var name in new[] { "vectors.db", "understanding.db" })
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try
                {
                    var file = new FileInfo(Path.Combine(vaultRoot, ".noteevolution", name + suffix));
                    if (file.Exists)
                    {
                        sum += file.Length;
                    }
                }
                catch (Exception)
                {
                    // locked or vanished meanwhile: counts as 0
                }
            }
        }

        return sum;
    }

    /// <summary><paramref name="bytes"/> in GB (<see cref="BytesPerGb"/>).</summary>
    public static double Gigabytes(long bytes) => bytes / BytesPerGb;
}
