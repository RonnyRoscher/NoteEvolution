using NoteEvolution.UI.Platform;

namespace NoteEvolution.UI.Tests;

/// <summary>The computations of the system meter's readings (spec 11.2), apart from the platform calls.</summary>
public class SystemReadingsTests
{
    [Fact]
    public void SystemReadings_CpuPercentFromTimes()
    {
        // 1000 ticks passed, 370 of them idle: 63 % busy.
        Assert.Equal(63, SystemReadings.CpuPercent(5_000, 20_000, 5_370, 21_000)!.Value, 6);
        Assert.Equal(0, SystemReadings.CpuPercent(0, 0, 100, 100)!.Value, 6);
        Assert.Equal(100, SystemReadings.CpuPercent(0, 0, 0, 100)!.Value, 6);

        // No time passed, or the counters went backwards: unknown. Idle beyond the total is clamped.
        Assert.Null(SystemReadings.CpuPercent(5_000, 20_000, 5_000, 20_000));
        Assert.Null(SystemReadings.CpuPercent(5_000, 20_000, 4_000, 19_000));
        Assert.Equal(0, SystemReadings.CpuPercent(0, 0, 200, 100)!.Value, 6);

        // A process: 2 s of processor time in 1 s on 4 processors is half of the computer.
        Assert.Equal(50, SystemReadings.ProcessCpuPercent(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), 4)!.Value, 6);
        Assert.Equal(100, SystemReadings.ProcessCpuPercent(TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(1), 4)!.Value, 6);
        Assert.Null(SystemReadings.ProcessCpuPercent(TimeSpan.FromSeconds(1), TimeSpan.Zero, 4));
        Assert.Null(SystemReadings.ProcessCpuPercent(TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(1), 4));

        Assert.Equal(1.5, SystemReadings.Gigabytes(3L << 29), 6);
    }

    [Fact]
    public void SystemReadings_ParseMeminfo()
    {
        const string meminfo = """
            MemTotal:       16384000 kB
            MemFree:         1024000 kB
            MemAvailable:    6144000 kB
            Buffers:          204800 kB
            """;

        var (used, total) = SystemReadings.ParseMeminfo(meminfo)!.Value;

        Assert.Equal(16_384_000 / 1048576.0, total, 6);
        Assert.Equal((16_384_000 - 6_144_000) / 1048576.0, used, 6);

        // Without the available memory (or the total) nothing is known.
        Assert.Null(SystemReadings.ParseMeminfo("MemTotal:       16384000 kB\nMemFree:  1024000 kB\n"));
        Assert.Null(SystemReadings.ParseMeminfo("MemAvailable:    6144000 kB\n"));
        Assert.Null(SystemReadings.ParseMeminfo(""));
        Assert.Null(SystemReadings.ParseMeminfo("MemTotal: viel kB\nMemAvailable: 1 kB\n"));
    }

    [Fact]
    public void SystemReadings_ParseProcStat()
    {
        // user nice system idle iowait irq softirq steal guest guest_nice; guest time is counted in user already.
        const string stat = "cpu  100 20 30 400 50 6 7 8 90 10\ncpu0 50 10 15 200 25 3 3 4 45 5\nintr 12345\n";

        var (idle, total) = SystemReadings.ParseProcStat(stat)!.Value;

        Assert.Equal(400 + 50, idle);
        Assert.Equal(100 + 20 + 30 + 400 + 50 + 6 + 7 + 8, total);

        // Older kernels have fewer columns; without the idle column or the cpu line nothing is known.
        Assert.Equal((400L, 550L), SystemReadings.ParseProcStat("cpu  100 20 30 400\n"));
        Assert.Null(SystemReadings.ParseProcStat("cpu  100 20 30\n"));
        Assert.Null(SystemReadings.ParseProcStat("cpu0 100 20 30 400\n"));
        Assert.Null(SystemReadings.ParseProcStat("cpu  100 x 30 400\n"));
        Assert.Null(SystemReadings.ParseProcStat(""));
    }

    [Fact]
    public void SystemReadings_AnalysisBytes_SumsFilesAndToleratesMissingOrLocked()
    {
        var root = Path.Combine(Path.GetTempPath(), "ne-analysis-" + Guid.NewGuid().ToString("N"));
        try
        {
            // No folder at all: 0.
            Assert.Equal(0, SystemReadings.AnalysisBytes(root));

            var folder = Directory.CreateDirectory(Path.Combine(root, ".noteevolution")).FullName;
            File.WriteAllBytes(Path.Combine(folder, "vectors.db"), new byte[1000]);
            File.WriteAllBytes(Path.Combine(folder, "vectors.db-wal"), new byte[200]);
            File.WriteAllBytes(Path.Combine(folder, "understanding.db-shm"), new byte[30]);
            File.WriteAllBytes(Path.Combine(folder, "other.db"), new byte[5000]);

            // Missing files (understanding.db, vectors.db-shm, ...) count as 0; other files do not count.
            Assert.Equal(1230, SystemReadings.AnalysisBytes(root));

            // A file held open exclusively is no error.
            using var locked = new FileStream(Path.Combine(folder, "vectors.db"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.InRange(SystemReadings.AnalysisBytes(root), 230, 1230);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void SystemReadings_PowerSupplyStatus()
    {
        Assert.True(SystemReadings.OnBattery(["Discharging"]));
        Assert.True(SystemReadings.OnBattery(["Unknown", "Discharging\n"]));
        Assert.False(SystemReadings.OnBattery(["Charging", "Full\n", "Not charging"]));

        // No power supply with a status (a desktop without battery): not recognizable.
        Assert.Null(SystemReadings.OnBattery([]));
    }
}
