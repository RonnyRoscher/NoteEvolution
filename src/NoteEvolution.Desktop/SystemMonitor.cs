using System.Diagnostics;
using System.Runtime.InteropServices;
using NoteEvolution.UI.Platform;

namespace NoteEvolution.Desktop;

/// <summary>
/// Reads the computer's load with what the operating system offers without extra tools (spec 11.2): on Windows the
/// system times, the memory status and the power status of <c>kernel32</c>; on Linux <c>/proc/stat</c>,
/// <c>/proc/meminfo</c> and <c>/sys/class/power_supply</c>; the shares of Ollama's processes and of this one on both;
/// the free space of the vault's drive everywhere (macOS shows only that). No WMI, nothing that starts a process. The
/// computations are in <see cref="SystemReadings"/>; every read is guarded, a failing one leaves its value unknown.
/// </summary>
internal sealed class SystemMonitor(TimeProvider time) : ISystemMonitor
{
    private readonly Lock _gate = new();

    /// <summary>The system's idle and total times at the previous reading; guarded by <see cref="_gate"/>.</summary>
    private (long Idle, long Total)? _systemTimes;

    /// <summary>When the processes were read last, and their processor times then (by process id).</summary>
    private long _processesAt;
    private Dictionary<int, TimeSpan>? _processTimes;

    /// <summary>How long the size of the analysis files is used before it is measured again.</summary>
    private static readonly TimeSpan AnalysisInterval = TimeSpan.FromSeconds(30);

    private string? _analysisRoot;
    private long _analysisAt;
    private long _analysisBytes;

    public SystemSnapshot Read(string? vaultRoot)
    {
        lock (_gate)
        {
            double? cpu = null;
            (double UsedGb, double TotalGb)? memory = null;
            bool? onBattery = null;
            var shares = new Shares(null, null, null, null);
            if (OperatingSystem.IsWindows())
            {
                cpu = Cpu(Guard(WindowsSystemTimes));
                memory = Guard(WindowsMemory);
                onBattery = Guard(WindowsOnBattery);
                shares = ProcessShares();
            }
            else if (OperatingSystem.IsLinux())
            {
                cpu = Cpu(Guard(() => SystemReadings.ParseProcStat(File.ReadAllText("/proc/stat"))));
                memory = Guard(() => SystemReadings.ParseMeminfo(File.ReadAllText("/proc/meminfo")));
                onBattery = Guard(LinuxOnBattery);
                shares = ProcessShares();
            }

            return new SystemSnapshot(
                cpu, memory?.UsedGb, memory?.TotalGb, DiskFree(vaultRoot),
                shares.OllamaCpu, shares.OllamaRam, shares.AppCpu, shares.AppRam, onBattery, AnalysisBytes(vaultRoot));
        }
    }

    /// <summary>The size of the vault's analysis files, measured at most every 30 s (and at once for another vault).</summary>
    private long? AnalysisBytes(string? vaultRoot)
    {
        if (vaultRoot is null)
        {
            _analysisRoot = null;
            return null;
        }

        var now = time.GetTimestamp();
        if (_analysisRoot != vaultRoot || time.GetElapsedTime(_analysisAt, now) >= AnalysisInterval)
        {
            _analysisRoot = vaultRoot;
            _analysisAt = now;
            _analysisBytes = SystemReadings.AnalysisBytes(vaultRoot);
        }

        return _analysisBytes;
    }

    private readonly record struct Shares(double? OllamaCpu, double? OllamaRam, double? AppCpu, double? AppRam);

    /// <summary>The load since the previous reading of the system's times, which <paramref name="times"/> replaces.</summary>
    private double? Cpu((long Idle, long Total)? times)
    {
        var previous = _systemTimes;
        _systemTimes = times;
        return previous is { } p && times is { } t ? SystemReadings.CpuPercent(p.Idle, p.Total, t.Idle, t.Total) : null;
    }

    /// <summary>
    /// The processor shares (since the previous reading) and the memory of Ollama's processes (<c>ollama</c>,
    /// <c>ollama app</c>, <c>ollama_llama_server</c>, …) and of this process. Ollama's values are unknown while it does not
    /// run; a process that started since the previous reading counts from the next one.
    /// </summary>
    private Shares ProcessShares()
    {
        var now = time.GetTimestamp();
        var elapsed = _processTimes is null ? TimeSpan.Zero : time.GetElapsedTime(_processesAt, now);
        var previous = _processTimes ?? [];
        var times = new Dictionary<int, TimeSpan>();
        var ownId = Environment.ProcessId;
        TimeSpan ollamaTime = TimeSpan.Zero, ownTime = TimeSpan.Zero;
        long ollamaMemory = 0, ownMemory = 0;
        bool ollamaFound = false, ownFound = false;

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception)
        {
            return new Shares(null, null, null, null);
        }

        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    var own = process.Id == ownId;
                    if (!own && !process.ProcessName.StartsWith("ollama", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var memory = process.WorkingSet64;
                    var cpuTime = process.TotalProcessorTime;
                    times[process.Id] = cpuTime;
                    var delta = previous.TryGetValue(process.Id, out var before) ? cpuTime - before : TimeSpan.Zero;
                    if (own)
                    {
                        ownFound = true;
                        ownTime += delta;
                        ownMemory += memory;
                    }
                    else
                    {
                        ollamaFound = true;
                        ollamaTime += delta;
                        ollamaMemory += memory;
                    }
                }
                catch (Exception)
                {
                    // ended meanwhile, or not readable (another user's process): it does not count
                }
            }
        }

        var first = _processTimes is null;
        _processesAt = now;
        _processTimes = times;
        double? Share(TimeSpan cpuTime) => first ? null : SystemReadings.ProcessCpuPercent(cpuTime, elapsed, Environment.ProcessorCount);
        return new Shares(
            ollamaFound ? Share(ollamaTime) : null,
            ollamaFound ? SystemReadings.Gigabytes(ollamaMemory) : null,
            ownFound ? Share(ownTime) : null,
            ownFound ? SystemReadings.Gigabytes(ownMemory) : null);
    }

    /// <summary>The free space of the drive that holds <paramref name="vaultRoot"/>, in GB.</summary>
    private static double? DiskFree(string? vaultRoot)
    {
        if (vaultRoot is null)
        {
            return null;
        }

        try
        {
            // Windows wants the drive's root; elsewhere the folder itself names its file system.
            var full = Path.GetFullPath(vaultRoot);
            var drive = new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(full)! : full);
            return SystemReadings.Gigabytes(drive.AvailableFreeSpace);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static (long Idle, long Total)? WindowsSystemTimes() =>
        GetSystemTimes(out var idle, out var kernel, out var user) ? (idle, kernel + user) : null; // kernel time includes idle

    private static (double UsedGb, double TotalGb)? WindowsMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            return null;
        }

        return (SystemReadings.Gigabytes((long)(status.TotalPhys - status.AvailPhys)), SystemReadings.Gigabytes((long)status.TotalPhys));
    }

    /// <summary>On battery when the power line is off; unknown (255) is unknown.</summary>
    private static bool? WindowsOnBattery() =>
        GetSystemPowerStatus(out var status) && status.AcLineStatus is 0 or 1 ? status.AcLineStatus == 0 : null;

    /// <summary>The status files of the batteries among the power supplies.</summary>
    private static bool? LinuxOnBattery()
    {
        const string supplies = "/sys/class/power_supply";
        if (!Directory.Exists(supplies))
        {
            return null;
        }

        var statuses = new List<string>();
        foreach (var supply in Directory.EnumerateDirectories(supplies))
        {
            var type = Path.Combine(supply, "type");
            var status = Path.Combine(supply, "status");
            if (File.Exists(type) && File.ReadAllText(type).Trim() == "Battery" && File.Exists(status))
            {
                statuses.Add(File.ReadAllText(status));
            }
        }

        return SystemReadings.OnBattery(statuses);
    }

    private static T? Guard<T>(Func<T?> read) where T : struct
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
