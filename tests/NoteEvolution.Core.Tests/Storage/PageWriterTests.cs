using System.Text;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Storage;
using NoteEvolution.Core.Vaults;
using NoteEvolution.TestSupport;

namespace NoteEvolution.Core.Tests.Storage;

public class PageWriterTests
{
    private const string PageFile = "pages/Idee.md";
    private static readonly DateTimeOffset Now = new(2026, 3, 15, 14, 30, 5, TimeSpan.Zero);

    private sealed record Setup(TestVault Tv, Vault Vault, Page Page, SelfWriteRegistry Registry, PageWriter Writer) : IDisposable
    {
        public string BackupFile => Path.Combine(Tv.Root, ".noteevolution", "backups", "2026-03-15", "pages", "Idee.md");

        public void Dispose() => Tv.Dispose();
    }

    private static Setup Create(string content = "- eins\n- zwei\n")
    {
        var tv = TestVault.Create((PageFile, content));
        var vault = tv.Open();
        var registry = new SelfWriteRegistry();
        var writer = new PageWriter(vault, new BackupService(vault.Root, new FakeClock(Now)), registry);
        return new Setup(tv, vault, vault.Pages.Single(), registry, writer);
    }

    [Fact]
    public void Save_FirstWriteOfDay_CreatesBackupOnce()
    {
        using var s = Create();

        s.Page.Roots[0].SetContent("erste Änderung");
        s.Writer.Save(s.Page);
        s.Page.Roots[0].SetContent("zweite Änderung");
        s.Writer.Save(s.Page);

        Assert.Equal("- eins\n- zwei\n", File.ReadAllText(s.BackupFile));
        Assert.Equal("- zweite Änderung\n- zwei\n", s.Tv.Read(PageFile));
    }

    [Fact]
    public void Save_ReadOnlyPage_Throws_FileUntouched()
    {
        using var s = Create("- ```\n  offen\n");
        Assert.True(s.Page.IsReadOnly);
        var before = File.ReadAllBytes(s.Page.FilePath);

        var ex = Assert.Throws<ReadOnlyPageException>(() => s.Writer.Save(s.Page));

        Assert.IsAssignableFrom<InvalidOperationException>(ex);
        Assert.Contains(s.Page.FilePath, ex.Message);
        Assert.Equal(before, File.ReadAllBytes(s.Page.FilePath));
        Assert.False(Directory.Exists(Path.Combine(s.Tv.Root, ".noteevolution")));
    }

    [Fact]
    public void Save_ClearsDirty_AndUpdatesBaseLines()
    {
        using var s = Create();
        s.Page.Roots[0].SetContent("geändert");
        Assert.True(s.Page.IsDirty);

        s.Writer.Save(s.Page);

        Assert.False(s.Page.IsDirty);
        Assert.False(s.Page.Roots[0].IsDirty);
        Assert.Equal(s.Page.Roots[0].Lines, s.Page.Roots[0].BaseLines);
        Assert.Equal("- geändert", s.Page.Roots[0].BaseLines[0].Text);
    }

    [Fact]
    public void Save_ReplacesPageInVault_WithSameInstance()
    {
        using var s = Create();
        Page? replaced = null;
        s.Vault.PageReplaced += p => replaced = p;
        s.Page.Roots[0].SetContent("geändert");

        s.Writer.Save(s.Page);

        Assert.Same(s.Page, replaced);
        Assert.Same(s.Page, s.Vault.FindPageByPath(s.Page.FilePath));
    }

    [Fact]
    public void Save_RecordsOwnWrite()
    {
        using var s = Create();
        s.Page.Roots[0].SetContent("geändert");

        s.Writer.Save(s.Page);

        Assert.True(s.Registry.IsOwnWrite(s.Page.FilePath, File.ReadAllBytes(s.Page.FilePath)));
        Assert.False(s.Registry.IsOwnWrite(s.Page.FilePath, Encoding.UTF8.GetBytes("- fremd\n")));
    }

    [Fact]
    public void Save_UnchangedPage_KeepsFileBytesIdentical()
    {
        using var s = Create("﻿title:: X\r\n\r\n- eins\r\n\t- zwei");
        var before = File.ReadAllBytes(s.Page.FilePath);

        s.Writer.Save(s.Page);

        Assert.Equal(before, File.ReadAllBytes(s.Page.FilePath));
    }

    [Fact]
    public void Save_SerializedBytesNotParsable_ThrowsAndWritesNothing()
    {
        using var s = Create();
        // No public edit can produce this; an internal seam appends a bullet that opens a code fence never closed.
        s.Page.AddRoot(new Block([new RawLine("- ```", "\n")], 0));
        Assert.False(s.Page.IsReadOnly);
        var before = File.ReadAllBytes(s.Page.FilePath);

        var ex = Assert.Throws<InvalidOperationException>(() => s.Writer.Save(s.Page));

        Assert.IsNotType<ReadOnlyPageException>(ex);
        Assert.Contains(s.Page.FilePath, ex.Message);
        Assert.Contains("Codeblock", ex.Message);
        Assert.Equal(before, File.ReadAllBytes(s.Page.FilePath));
        Assert.False(Directory.Exists(Path.Combine(s.Tv.Root, ".noteevolution")));
        Assert.False(s.Registry.IsOwnWrite(s.Page.FilePath, before));
    }

    [Fact]
    public void Save_FileLockedBriefly_RetriesAfter200Ms_ThenSucceeds()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Replacing a file that another process holds open only fails on Windows.
        }

        using var s = Create();
        s.Page.Roots[0].SetContent("geändert");
        var delays = new List<TimeSpan>();
        var locked = new FileStream(s.Page.FilePath, FileMode.Open, FileAccess.Read, FileShare.None);
        s.Writer.Sleep = delay =>
        {
            delays.Add(delay);
            locked.Dispose();
        };

        try
        {
            s.Writer.Save(s.Page);
        }
        finally
        {
            locked.Dispose();
        }

        Assert.Equal([TimeSpan.FromMilliseconds(200)], delays);
        Assert.Equal("- geändert\n- zwei\n", s.Tv.Read(PageFile));
        Assert.False(s.Page.IsDirty);
    }

    [Fact]
    public void Save_FileStaysLocked_ThrowsIOExceptionAfterThreeAttempts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var s = Create();
        s.Page.Roots[0].SetContent("geändert");
        var delays = new List<TimeSpan>();
        s.Writer.Sleep = delays.Add;
        using var locked = new FileStream(s.Page.FilePath, FileMode.Open, FileAccess.Read, FileShare.None);

        var ex = Assert.Throws<IOException>(() => s.Writer.Save(s.Page));

        Assert.NotNull(ex.InnerException);
        Assert.Equal(2, delays.Count);
        Assert.True(s.Page.IsDirty);
        Assert.False(s.Registry.IsOwnWrite(s.Page.FilePath, Encoding.UTF8.GetBytes("- geändert\n- zwei\n")));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(s.Page.FilePath)!, ".*ne-tmp"));
    }
}
