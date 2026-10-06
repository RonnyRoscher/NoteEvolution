using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Storage;
using NoteEvolution.Core.Tests.Links;
using NoteEvolution.Core.Vaults;
using NoteEvolution.TestSupport;

namespace NoteEvolution.Core.Tests.Storage;

public class ExternalChangeTests
{
    private const string PageFile = "pages/Seite.md";
    private const string IdA = "6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70";
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly DateTimeOffset Now = new(2026, 3, 15, 14, 30, 5, TimeSpan.Zero);

    private const string Original =
        "- eins\n" +
        "- zwei\n" +
        $"  id:: {IdA}\n" +
        "- drei\n";

    private sealed class Setup : IDisposable
    {
        public Setup(string content = Original)
        {
            Tv = TestVault.Create((PageFile, content));
            Vault = Tv.Open();
            Handler = new ExternalChangeHandler(Vault);
            Writer = new PageWriter(Vault, new BackupService(Vault.Root, new FakeClock(Now)), new SelfWriteRegistry());
        }

        public TestVault Tv { get; }

        public Vault Vault { get; }

        public ExternalChangeHandler Handler { get; }

        public PageWriter Writer { get; }

        public string PagePath => Path.GetFullPath(Path.Combine(Tv.Root, PageFile));

        public Page Page => Vault.FindPageByPath(PagePath)!;

        public void WriteExternally(string content) => File.WriteAllText(PagePath, content, Utf8NoBom);

        public void Dispose() => Tv.Dispose();
    }

    private static string Text(Page page) => Encoding.UTF8.GetString(PageSerializer.Serialize(page));

    private static Block B(Page page, string content) => page.AllBlocks().Single(b => b.Content == content);

    [Fact]
    public void Handle_NoLocalChanges_ReloadsSilently_KeepsKeys()
    {
        using var s = new Setup();
        var before = s.Page;
        var keys = before.AllBlocks().ToDictionary(b => b.Content, b => b.Key);
        Page? replaced = null;
        s.Vault.PageReplaced += p => replaced = p;
        s.WriteExternally("- eins\n- zwei extern\n" + $"  id:: {IdA}\n" + "- drei\n- vier\n");

        var outcome = s.Handler.Handle(s.PagePath);

        var reloaded = Assert.IsType<ExternalChangeOutcome.Reloaded>(outcome).Page;
        Assert.NotSame(before, reloaded);
        Assert.Same(reloaded, s.Page);
        Assert.Same(reloaded, replaced);
        Assert.False(reloaded.IsDirty);
        Assert.Equal(keys["eins"], B(reloaded, "eins").Key);
        Assert.Equal(keys["zwei"], B(reloaded, "zwei extern").Key);
        Assert.Equal(keys["drei"], B(reloaded, "drei").Key);
        Assert.DoesNotContain(B(reloaded, "vier").Key, keys.Values);
    }

    [Fact]
    public void Handle_ExternalEditToBook_ThenSync_NoDuplicateBlocks()
    {
        using var s = new LinkSetup();
        var handler = new ExternalChangeHandler(s.Vault);
        var snapshot = BookSnapshot.Create(s.Book, s.Vault, s.SectionKey("Abschnitt"), includeSubsections: true);
        // Changes outside the shown section (the editor's snapshot of it is still current), shifting all tree paths.
        var external = LinkSetup.DefaultBook.Replace("- Vorspann\n", "- Ganz neu oben\n- Vorspann extern\n") + "- Anhang aus Logseq\n";
        File.WriteAllText(s.BookPath, external, Utf8NoBom);

        Assert.IsType<ExternalChangeOutcome.Reloaded>(handler.Handle(s.BookPath));
        var book = s.Book;
        var count = book.Page.AllBlocks().Count();
        var result = BookSync.Apply(book, snapshot);

        Assert.Equal(count, book.Page.AllBlocks().Count());
        Assert.Equal(external, Text(book.Page));
        Assert.False(result.Changed);
    }

    [Fact]
    public void Handle_DeletedFile_RemovesPage()
    {
        using var s = new Setup();
        File.Delete(s.PagePath);

        var outcome = s.Handler.Handle(s.PagePath);

        Assert.Equal(s.PagePath, Assert.IsType<ExternalChangeOutcome.Removed>(outcome).Path);
        Assert.Null(s.Vault.FindPageByPath(s.PagePath));
    }

    [Fact]
    public void Handle_DeletedFile_LocalDirty_Conflict_ResolveMineKeepsLocal()
    {
        using var s = new Setup();
        var local = s.Page;
        B(local, "eins").SetContent("eins lokal");
        File.Delete(s.PagePath);

        var conflict = Assert.IsType<ExternalChangeOutcome.Conflict>(s.Handler.Handle(s.PagePath));

        Assert.Same(local, conflict.Local);
        Assert.Null(conflict.External);
        Assert.Same(B(local, "eins lokal"), Assert.Single(conflict.Conflicts).Local);
        Assert.Same(local, s.Page);

        s.Handler.Resolve(conflict, conflict.Conflicts.ToDictionary(c => c.Local.Key, _ => ConflictChoice.Mine));
        Assert.Same(local, s.Page);
        s.Writer.Save(s.Page);
        Assert.Equal(Original.Replace("- eins\n", "- eins lokal\n"), s.Tv.Read(PageFile));
    }

    [Fact]
    public void Handle_DeletedFile_LocalDirty_ResolveTheirsRemovesPage()
    {
        using var s = new Setup();
        B(s.Page, "eins").SetContent("eins lokal");
        File.Delete(s.PagePath);
        var conflict = Assert.IsType<ExternalChangeOutcome.Conflict>(s.Handler.Handle(s.PagePath));

        s.Handler.Resolve(conflict, conflict.Conflicts.ToDictionary(c => c.Local.Key, _ => ConflictChoice.Theirs));

        Assert.Null(s.Vault.FindPageByPath(s.PagePath));
    }

    [Fact]
    public void Handle_NewFile_AddsPage()
    {
        using var s = new Setup();
        var path = Path.GetFullPath(Path.Combine(s.Tv.Root, "pages", "Neu.md"));
        File.WriteAllText(path, "- neu\n", Utf8NoBom);

        var page = Assert.IsType<ExternalChangeOutcome.Reloaded>(s.Handler.Handle(path)).Page;

        Assert.Same(page, s.Vault.FindPageByPath(path));
        Assert.Equal("neu", Assert.Single(page.Roots).Content);
    }

    [Fact]
    public void Handle_FileUnchanged_KeepsPageInstance()
    {
        using var s = new Setup();
        var before = s.Page;
        B(before, "eins").SetContent("eins lokal");
        var replaced = false;
        s.Vault.PageReplaced += _ => replaced = true;

        var outcome = s.Handler.Handle(s.PagePath);

        Assert.Same(before, Assert.IsType<ExternalChangeOutcome.Reloaded>(outcome).Page);
        Assert.False(replaced);
        Assert.True(before.IsDirty);
    }

    [Fact]
    public void Handle_LocalDirty_OtherBlockChangedExternally_MergesSilently()
    {
        using var s = new Setup();
        var eins = B(s.Page, "eins");
        eins.SetContent("eins lokal");
        s.WriteExternally(Original.Replace("- drei\n", "- drei extern\n"));

        var merged = Assert.IsType<ExternalChangeOutcome.Reloaded>(s.Handler.Handle(s.PagePath)).Page;

        Assert.Same(merged, s.Page);
        Assert.True(merged.IsDirty);
        Assert.Equal(eins.Key, B(merged, "eins lokal").Key);
        s.Writer.Save(merged);
        Assert.Equal(Original.Replace("- eins\n", "- eins lokal\n").Replace("- drei\n", "- drei extern\n"), s.Tv.Read(PageFile));
    }

    [Fact]
    public void Handle_LocalDirty_SameBlockChangedExternally_Conflict_ResolveBoth()
    {
        using var s = new Setup();
        var local = s.Page;
        var zwei = B(local, "zwei");
        zwei.SetContent("zwei lokal");
        var external = Original.Replace("- zwei\n", "- zwei extern\n");
        s.WriteExternally(external);

        var conflict = Assert.IsType<ExternalChangeOutcome.Conflict>(s.Handler.Handle(s.PagePath));

        Assert.Same(local, conflict.Local);
        Assert.Equal(external, Text(conflict.External!));
        var block = Assert.Single(conflict.Conflicts);
        Assert.Same(zwei, block.Local);
        Assert.Equal("zwei extern", block.External!.Content);
        Assert.Same(local, s.Page);

        Assert.Throws<FileChangedExternallyException>(() => s.Writer.Save(local));
        Assert.Equal(external, s.Tv.Read(PageFile));

        s.Handler.Resolve(conflict, new Dictionary<Guid, ConflictChoice> { [zwei.Key] = ConflictChoice.Both });

        var resolved = s.Page;
        Assert.NotSame(local, resolved);
        Assert.True(resolved.IsDirty);
        Assert.Equal(zwei.Key, B(resolved, "zwei lokal").Key);
        s.Writer.Save(resolved);
        Assert.Equal($"- eins\n- zwei lokal\n  id:: {IdA}\n- zwei extern\n- drei\n", s.Tv.Read(PageFile));
    }

    [Fact]
    public void Handle_Unparseable_NoLocalChanges_LoadsReadOnly()
    {
        using var s = new Setup();
        s.WriteExternally("- ```\n  offen\n");

        var page = Assert.IsType<ExternalChangeOutcome.Reloaded>(s.Handler.Handle(s.PagePath)).Page;

        Assert.True(page.IsReadOnly);
        Assert.Same(page, s.Page);
    }

    [Fact]
    public void Handle_Unparseable_LocalDirty_LoadsReadOnly_ReportsConflictWithoutExternal()
    {
        using var s = new Setup();
        var local = s.Page;
        var neu = Block.CreateDetached("neu");
        local.InsertBlock(null, 3, neu);
        B(local, "eins").SetContent("eins lokal");
        s.WriteExternally("- ```\n  offen\n");

        var conflict = Assert.IsType<ExternalChangeOutcome.Conflict>(s.Handler.Handle(s.PagePath));

        Assert.True(s.Page.IsReadOnly);
        Assert.Same(local, conflict.Local);
        Assert.Null(conflict.External);
        Assert.Equal(["eins lokal", "neu"], conflict.Conflicts.Select(c => c.Local.Content));
        Assert.All(conflict.Conflicts, c => Assert.Null(c.External));

        s.Handler.Resolve(conflict, conflict.Conflicts.ToDictionary(c => c.Local.Key, _ => ConflictChoice.Theirs));
        Assert.True(s.Page.IsReadOnly);

        s.Handler.Resolve(conflict, conflict.Conflicts.ToDictionary(c => c.Local.Key, _ => ConflictChoice.Mine));
        Assert.Same(local, s.Page);
        Assert.IsType<ExternalChangeOutcome.Reloaded>(s.Handler.Handle(s.PagePath));
        Assert.Same(local, s.Page);

        s.Writer.Save(s.Page);

        Assert.Equal($"- eins lokal\n- zwei\n  id:: {IdA}\n- drei\n- neu\n", s.Tv.Read(PageFile));
        Assert.False(s.Page.IsDirty);
    }

    [Fact]
    public void Handle_UnparseableAgainAfterSave_IsNotIgnored()
    {
        using var s = new Setup();
        var local = s.Page;
        B(local, "eins").SetContent("eins lokal");
        s.WriteExternally("- ```\n  offen\n");
        var conflict = Assert.IsType<ExternalChangeOutcome.Conflict>(s.Handler.Handle(s.PagePath));
        s.Handler.Resolve(conflict, conflict.Conflicts.ToDictionary(c => c.Local.Key, _ => ConflictChoice.Mine));
        s.Writer.Save(s.Page);

        s.WriteExternally("- ```\n  offen\n");

        Assert.IsType<ExternalChangeOutcome.Reloaded>(s.Handler.Handle(s.PagePath));
        Assert.True(s.Page.IsReadOnly);
    }

    [Fact]
    public void Carry_ByIdElseSamePathOrSameLines_ChangedBlockGetsNewKey()
    {
        var from = Parse($"- a\n- b\n  id:: {IdA}\n- c\n\t- c1\n- d\n");
        var to = Parse($"- a\n- c\n\t- c1\n- b neu\n  id:: {IdA}\n- d geändert\n");

        RuntimeKeys.Carry(from, to);

        Assert.Equal(B(from, "a").Key, B(to, "a").Key);
        Assert.Equal(B(from, "b").Key, B(to, "b neu").Key);
        Assert.Equal(B(from, "c").Key, B(to, "c").Key);
        Assert.Equal(B(from, "c1").Key, B(to, "c1").Key);
        Assert.DoesNotContain(B(to, "d geändert").Key, from.AllBlocks().Select(b => b.Key));
    }

    private static Page Parse(string text) => LogseqParser.Parse("Seite.md", Encoding.UTF8.GetBytes(text));
}
