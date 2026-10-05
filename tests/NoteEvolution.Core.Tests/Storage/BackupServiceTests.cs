using NoteEvolution.Core.Storage;
using NoteEvolution.TestSupport;

namespace NoteEvolution.Core.Tests.Storage;

public class BackupServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 15, 14, 30, 5, TimeSpan.FromHours(1));

    private static string BackupsDir(TestVault tv) => Path.Combine(tv.Root, ".noteevolution", "backups");

    [Fact]
    public void EnsureDailyBackup_CopiesFileOncePerDay()
    {
        using var tv = TestVault.Create(("pages/Idee.md", "- alt\n"));
        var service = new BackupService(tv.Root, new FakeClock(Now));
        var source = Path.Combine(tv.Root, "pages", "Idee.md");

        service.EnsureDailyBackup(source);
        File.WriteAllText(source, "- neu\n");
        service.EnsureDailyBackup(source);

        var backup = Path.Combine(BackupsDir(tv), "2026-03-15", "pages", "Idee.md");
        Assert.Equal("- alt\n", File.ReadAllText(backup));
    }

    [Fact]
    public void EnsureDailyBackup_NewDay_BacksUpAgain()
    {
        using var tv = TestVault.Create(("pages/Idee.md", "- alt\n"));
        var clock = new FakeClock(Now);
        var service = new BackupService(tv.Root, clock);
        var source = Path.Combine(tv.Root, "pages", "Idee.md");

        service.EnsureDailyBackup(source);
        File.WriteAllText(source, "- neu\n");
        clock.Advance(TimeSpan.FromDays(1));
        service.EnsureDailyBackup(source);

        Assert.Equal("- neu\n", File.ReadAllText(Path.Combine(BackupsDir(tv), "2026-03-16", "pages", "Idee.md")));
    }

    [Fact]
    public void EnsureDailyBackup_MissingSource_DoesNothing()
    {
        using var tv = TestVault.Create(("pages/Idee.md", "- x\n"));
        var service = new BackupService(tv.Root, new FakeClock(Now));

        service.EnsureDailyBackup(Path.Combine(tv.Root, "pages", "Neu.md"));

        Assert.False(Directory.Exists(BackupsDir(tv)));
    }

    [Fact]
    public void EnsureDailyBackup_PathOutsideVault_Throws()
    {
        using var tv = TestVault.Create(("pages/Idee.md", "- x\n"));
        using var other = new TempDir();
        var outside = other.Write("Fremd.md", "- y\n");
        var service = new BackupService(tv.Root, new FakeClock(Now));

        Assert.Throws<ArgumentException>(() => service.EnsureDailyBackup(outside));
    }

    [Fact]
    public void CreateBackup_Forced_AddsTimestampedCopy()
    {
        using var tv = TestVault.Create(("pages/Idee.md", "- alt\n"));
        var service = new BackupService(tv.Root, new FakeClock(Now));
        var source = Path.Combine(tv.Root, "pages", "Idee.md");

        service.EnsureDailyBackup(source);
        File.WriteAllText(source, "- neu\n");
        var backup = service.CreateBackup(source);

        Assert.Equal(Path.Combine(BackupsDir(tv), "2026-03-15", "pages", "Idee.md.143005.bak"), backup);
        Assert.Equal("- neu\n", File.ReadAllText(backup));
        Assert.Equal("- alt\n", File.ReadAllText(Path.Combine(BackupsDir(tv), "2026-03-15", "pages", "Idee.md")));
    }

    [Fact]
    public void CreateBackup_SameSecondTwice_KeepsBoth()
    {
        using var tv = TestVault.Create(("pages/Idee.md", "- eins\n"));
        var service = new BackupService(tv.Root, new FakeClock(Now));
        var source = Path.Combine(tv.Root, "pages", "Idee.md");

        var first = service.CreateBackup(source);
        File.WriteAllText(source, "- zwei\n");
        var second = service.CreateBackup(source);

        Assert.NotEqual(first, second);
        Assert.Equal("- eins\n", File.ReadAllText(first));
        Assert.Equal("- zwei\n", File.ReadAllText(second));
    }

    [Fact]
    public void Cleanup_DeletesFoldersOlderThan30Days()
    {
        using var tv = TestVault.Create(
            (".noteevolution/backups/2026-02-12/pages/Alt.md", "- 31 Tage\n"),
            (".noteevolution/backups/2026-02-13/pages/Grenze.md", "- 30 Tage\n"),
            (".noteevolution/backups/2026-03-15/pages/Heute.md", "- heute\n"),
            (".noteevolution/backups/2026-03-20/pages/Zukunft.md", "- Zukunft\n"),
            (".noteevolution/backups/manuell/pages/Eigen.md", "- kein Datum\n"));
        var service = new BackupService(tv.Root, new FakeClock(Now));

        service.Cleanup();

        var remaining = Directory.GetDirectories(BackupsDir(tv)).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["2026-02-13", "2026-03-15", "2026-03-20", "manuell"], remaining);
    }

    [Fact]
    public void Cleanup_NoBackupsFolder_DoesNothing()
    {
        using var tv = TestVault.Create(("pages/Idee.md", "- x\n"));
        var service = new BackupService(tv.Root, new FakeClock(Now));

        service.Cleanup();

        Assert.False(Directory.Exists(BackupsDir(tv)));
    }
}
