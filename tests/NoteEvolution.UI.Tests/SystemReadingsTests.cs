using NoteEvolution.UI.Platform;

namespace NoteEvolution.UI.Tests;

/// <summary>The computations of the system meter's readings (spec 11.2), apart from the platform calls.</summary>
public class SystemReadingsTests
{
    [Theory]
    [InlineData("ollama", true)]
    [InlineData("ollama app", true)]
    [InlineData("ollama_llama_server", true)]
    [InlineData("llama-server", true)]
    [InlineData("LLAMA-SERVER", true)]
    [InlineData("llama_server", true)]
    [InlineData("NoteEvolution", false)]
    [InlineData("llama", false)]
    [InlineData("chrome", false)]
    public void SystemReadings_OllamaProcesses_IncludeTheModelServer(string name, bool expected) =>
        Assert.Equal(expected, SystemReadings.IsOllamaProcess(name));

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
    public void SystemReadings_DataUsage_BreaksDownTheVaultFolderAndCountsTheBook()
    {
        var root = Path.Combine(Path.GetTempPath(), "ne-vault-data-" + Guid.NewGuid().ToString("N"));
        var userData = Path.Combine(Path.GetTempPath(), "ne-user-data-" + Guid.NewGuid().ToString("N"));
        try
        {
            static void Write(string path, int bytes)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, new byte[bytes]);
            }

            var folder = Path.Combine(root, ".noteevolution");
            Write(Path.Combine(folder, "vectors.db"), 1000);
            Write(Path.Combine(folder, "vectors.db-wal"), 200);
            Write(Path.Combine(folder, "book-vectors.db"), 300);
            Write(Path.Combine(folder, "book-vectors.db-shm"), 32);
            Write(Path.Combine(folder, "understanding.db-shm"), 30);
            Write(Path.Combine(folder, "history.db"), 4000);
            Write(Path.Combine(folder, "history.db-wal"), 400);
            Write(Path.Combine(folder, "backups", "2026-11-16", "pages", "Buch - Probe.md"), 500);
            Write(Path.Combine(folder, "logs", "noteevolution-20261116.log"), 60);
            Write(Path.Combine(folder, "repetitions.json"), 7);
            Write(Path.Combine(folder, "other.db"), 5000);
            Write(Path.Combine(folder, "history.db.defekt-20261105120000"), 7000);
            var book = Path.Combine(root, "pages", "Buch - Probe.md");
            Write(book, 900);
            Write(Path.Combine(root, "pages", "Notiz.md"), 100);
            Write(Path.Combine(root, "journals", "2026_11_16.md"), 50);
            Write(Path.Combine(root, ".obsidian", "workspace.md"), 4444);
            Write(Path.Combine(root, "pages", "bild.png"), 333);
            var models = Path.Combine(userData, "models");
            Write(Path.Combine(models, "e5-small", "model.onnx"), 2000);

            var usage = SystemReadings.DataUsage(root, book, models);

            long Of(DataPartKind kind) => usage.Parts.Single(p => p.Kind == kind).Bytes;
            Assert.Equal(1200, Of(DataPartKind.NoteVectors));
            Assert.Equal(332, Of(DataPartKind.BookVectors));
            Assert.Equal(30, Of(DataPartKind.Understanding));
            Assert.Equal(4400, Of(DataPartKind.History));
            Assert.Equal(500, Of(DataPartKind.Backups));
            Assert.Equal(60, Of(DataPartKind.Logs));
            // Everything else below .noteevolution, also a history moved aside and files the app adds later.
            Assert.Equal(12_007, Of(DataPartKind.Other));
            Assert.Equal(900, Of(DataPartKind.Book));
            Assert.Equal("Buch - Probe.md", usage.Parts.Single(p => p.Kind == DataPartKind.Book).Name);
            // The vault's Markdown pages and journals, without hidden folders and other files; not in the sum.
            Assert.Equal(1050, Of(DataPartKind.VaultPages));
            Assert.Equal(2000, Of(DataPartKind.Model));

            // The sum: the .noteevolution folder and the book; not the vault's pages, not the model.
            Assert.Equal(19_429, usage.Total);
            Assert.All(usage.Parts.Where(p => p.Kind is DataPartKind.VaultPages or DataPartKind.Model), p => Assert.False(p.Counted));
            Assert.All(usage.Parts.Where(p => p.Kind is not (DataPartKind.VaultPages or DataPartKind.Model)), p => Assert.True(p.Counted));

            // Without a book and a models folder their parts are left out.
            var noBook = SystemReadings.DataUsage(root, null, null);
            Assert.DoesNotContain(noBook.Parts, p => p.Kind is DataPartKind.Book or DataPartKind.Model);
            Assert.Equal(18_529, noBook.Total);

            // A file held open exclusively is no error.
            using (new FileStream(Path.Combine(folder, "vectors.db"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.InRange(SystemReadings.DataUsage(root, book, models).Total, 18_429, 19_429);
            }
        }
        finally
        {
            foreach (var dir in new[] { root, userData })
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
        }
    }

    [Fact]
    public void SystemReadings_DataUsage_MissingFoldersAndBook_CountAsZero()
    {
        var root = Path.Combine(Path.GetTempPath(), "ne-vault-data-" + Guid.NewGuid().ToString("N"));

        var usage = SystemReadings.DataUsage(root, Path.Combine(root, "pages", "fehlt.md"), Path.Combine(root, "models"));

        Assert.Equal(0, usage.Total);
        Assert.All(usage.Parts, p => Assert.Equal(0, p.Bytes));
        Assert.Contains(usage.Parts, p => p.Kind == DataPartKind.Book);
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
