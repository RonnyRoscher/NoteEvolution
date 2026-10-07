using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using NoteEvolution.Core.Storage;
using NoteEvolution.Core.Vaults;
using NoteEvolution.TestSupport;

namespace NoteEvolution.Core.Tests.Storage;

public class VaultWatcherTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);
    private static readonly DateTimeOffset Now = new(2026, 3, 15, 14, 30, 5, TimeSpan.Zero);

    private sealed class Setup : IDisposable
    {
        public Setup()
        {
            Tv = TestVault.Create(("pages/Seite.md", "- eins\n"), ("journals/2026_03_01.md", "- notiz\n"));
            Vault = Tv.Open();
            Watcher = new VaultWatcher(Vault, Registry, Time);
            Watcher.ExternalChange += path => Events.Enqueue(path);
        }

        public TestVault Tv { get; }

        public Vault Vault { get; }

        public SelfWriteRegistry Registry { get; } = new();

        public FakeTimeProvider Time { get; } = new(Now);

        public VaultWatcher Watcher { get; }

        public ConcurrentQueue<string> Events { get; } = new();

        public string Full(string relPath) => Path.GetFullPath(Path.Combine(Tv.Root, relPath));

        public void Write(string relPath, string content) => File.WriteAllText(Full(relPath), content, Utf8NoBom);

        /// <summary>Lets fake time pass until <paramref name="condition"/> holds; at most 5 s of real time.</summary>
        public bool WaitFor(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(5))
            {
                Time.Advance(Debounce);
                if (condition())
                {
                    return true;
                }

                Thread.Sleep(20);
            }

            return false;
        }

        public void Dispose()
        {
            Watcher.Dispose();
            Tv.Dispose();
        }
    }

    [Fact]
    public void Start_ExternalWrite_RaisesExternalChange()
    {
        using var s = new Setup();
        s.Watcher.Start();

        s.Write("pages/Seite.md", "- eins extern\n");

        Assert.True(s.WaitFor(() => s.Events.Contains(s.Full("pages/Seite.md"))));
    }

    [Fact]
    public void Handle_OwnWrite_IsIgnoredByWatcher()
    {
        using var s = new Setup();
        var writer = new PageWriter(s.Vault, new BackupService(s.Vault.Root, new FakeClock(Now)), s.Registry);
        s.Watcher.Start();
        var page = s.Vault.FindPageByPath(s.Full("pages/Seite.md"))!;
        page.Roots[0].SetContent("eins gespeichert");

        writer.Save(page);
        s.Write("journals/2026_03_01.md", "- notiz extern\n");

        Assert.True(s.WaitFor(() => s.Events.Contains(s.Full("journals/2026_03_01.md"))));
        Assert.DoesNotContain(s.Full("pages/Seite.md"), s.Events);
    }

    [Fact]
    public void Start_RenamedFile_RaisesOldAndNewPath()
    {
        using var s = new Setup();
        s.Watcher.Start();

        File.Move(s.Full("pages/Seite.md"), s.Full("pages/Umbenannt.md"));

        Assert.True(s.WaitFor(() =>
            s.Events.Contains(s.Full("pages/Seite.md")) && s.Events.Contains(s.Full("pages/Umbenannt.md"))));
    }

    [Fact]
    public void Notify_RepeatedWithinDebounce_RaisesOnceAfter300Ms()
    {
        using var s = new Setup();
        var path = s.Full("pages/Seite.md");

        s.Watcher.Notify(path);
        s.Time.Advance(TimeSpan.FromMilliseconds(200));
        s.Watcher.Notify(path);
        s.Time.Advance(TimeSpan.FromMilliseconds(299));
        Assert.Empty(s.Events);

        s.Time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal([path], s.Events);

        s.Time.Advance(TimeSpan.FromSeconds(5));
        Assert.Single(s.Events);
    }

    [Fact]
    public void Notify_DebouncesPerPath()
    {
        using var s = new Setup();
        var page = s.Full("pages/Seite.md");
        var journal = s.Full("journals/2026_03_01.md");

        s.Watcher.Notify(page);
        s.Time.Advance(TimeSpan.FromMilliseconds(200));
        s.Watcher.Notify(journal);
        s.Time.Advance(TimeSpan.FromMilliseconds(100));

        Assert.Equal([page], s.Events);
        s.Time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal([page, journal], s.Events);
    }

    [Theory]
    [InlineData(".noteevolution/backups/2026-03-15/pages/Seite.md")]
    [InlineData("pages/.Seite.md.ne-tmp")]
    [InlineData("pages/.versteckt.md")]
    [InlineData("assets/Bild.md")]
    [InlineData("pages/Seite.txt")]
    [InlineData("pages/.noteevolution/x.md")]
    public void Notify_IgnoredPath_RaisesNothing(string relPath)
    {
        using var s = new Setup();

        s.Watcher.Notify(s.Full(relPath));
        s.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Empty(s.Events);
    }

    [Fact]
    public void Notify_OwnWriteContent_IsIgnored_LaterExternalContentIsNot()
    {
        using var s = new Setup();
        var path = s.Full("pages/Seite.md");
        var own = Utf8NoBom.GetBytes("- eigen\n");
        File.WriteAllBytes(path, own);
        s.Registry.Record(path, own);

        s.Watcher.Notify(path);
        s.Time.Advance(Debounce);
        Assert.Empty(s.Events);

        s.Write("pages/Seite.md", "- extern\n");
        s.Watcher.Notify(path);
        s.Time.Advance(Debounce);
        Assert.Equal([path], s.Events);
    }

    [Fact]
    public void Notify_DeletedFile_Raises()
    {
        using var s = new Setup();
        var path = s.Full("pages/Seite.md");
        File.Delete(path);

        s.Watcher.Notify(path);
        s.Time.Advance(Debounce);

        Assert.Equal([path], s.Events);
    }

    [Fact]
    public void Dispose_PendingEvent_IsNotRaised()
    {
        using var s = new Setup();
        s.Watcher.Notify(s.Full("pages/Seite.md"));

        s.Watcher.Dispose();
        s.Time.Advance(Debounce);

        Assert.Empty(s.Events);
    }
}
