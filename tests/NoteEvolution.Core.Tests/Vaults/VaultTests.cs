using System.Text;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Vaults;
using NoteEvolution.TestSupport;

namespace NoteEvolution.Core.Tests.Vaults;

public class VaultTests
{
    private const string IdA = "7f3a91c2-4b1d-4e8a-9c3f-1a2b3c4d5e6f";
    private const string BookFile = "pages/Buch - Test.md";

    private static TestVault Sample() => TestVault.Create(
        ("journals/2026_03_01.md", "- erste Notiz\n"),
        (BookFile, "title:: Test\ntype:: book\n\n- # Kapitel\n"),
        ("pages/Idee.md", "- eine Idee\n"));

    private static Core.Model.Page Parse(string path, string text) =>
        LogseqParser.Parse(path, Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Open_ReadsNoteFolders_SeparatesBooks()
    {
        using var tv = Sample();
        var vault = tv.Open();
        Assert.Equal(3, vault.Pages.Count);
        var book = Assert.Single(vault.Books);
        Assert.Equal("Buch - Test", book.LinkName);
        Assert.Same(vault.Pages.Single(p => p.Name == "Buch - Test"), book.Page);
        Assert.Equal(["erste Notiz", "eine Idee"], new NoteRepository(vault).All().Select(n => n.Block.Content));
    }

    [Fact]
    public void Open_PathsAreFullAndRootIsFull()
    {
        using var tv = Sample();
        var vault = tv.Open();
        Assert.All(vault.Pages, p => Assert.True(Path.IsPathFullyQualified(p.FilePath)));
        Assert.Equal(Path.GetFullPath(tv.Root), vault.Root);
    }

    [Fact]
    public void Open_CustomNoteFolders_FromSettings()
    {
        using var tv = TestVault.Create(
            ("journals/2026_03_01.md", "- j\n"),
            ("pages/Idee.md", "- p\n"),
            ("extra/sub/Weit.md", "- w\n"),
            ("extra/readme.txt", "kein Markdown"));
        new VaultSettings { NoteFolders = ["extra", "gibt-es-nicht"] }.Save(tv.Root);

        var vault = tv.Open();

        Assert.Equal(["extra", "gibt-es-nicht"], vault.Settings.NoteFolders);
        Assert.Equal(["Weit"], vault.Pages.Select(p => p.Name));
    }

    [Fact]
    public void Open_SkipsHiddenFilesAndNoteEvolutionFolder()
    {
        using var tv = TestVault.Create(
            ("pages/Idee.md", "- p\n"),
            ("pages/.Idee.md.ne-tmp", "- halb\n"),
            ("pages/.versteckt.md", "- v\n"),
            (".noteevolution/x.md", "- x\n"));
        new VaultSettings { NoteFolders = ["pages", "."] }.Save(tv.Root);

        var vault = tv.Open();

        Assert.Equal(["Idee"], vault.Pages.Select(p => p.Name));
    }

    [Fact]
    public void Open_UnparsableFile_IsListedReadOnly()
    {
        using var tv = Sample();
        File.WriteAllBytes(Path.Combine(tv.Root, "pages", "Kaputt.md"), [0x2D, 0x20, 0xFF, 0xFE, 0x0A]);

        var vault = tv.Open();

        var page = vault.Pages.Single(p => p.Name == "Kaputt");
        Assert.True(page.IsReadOnly);
        Assert.NotNull(page.ParseError);
    }

    [Fact]
    public void Settings_MissingOrCorrupt_GivesDefaults_SaveRoundTrips()
    {
        using var tv = TestVault.Create();
        Assert.Equal(["journals", "pages"], VaultSettings.Load(tv.Root).NoteFolders);

        Directory.CreateDirectory(Path.Combine(tv.Root, ".noteevolution"));
        File.WriteAllText(Path.Combine(tv.Root, ".noteevolution", "settings.json"), "{ nicht json");
        Assert.Equal(["journals", "pages"], VaultSettings.Load(tv.Root).NoteFolders);

        new VaultSettings { NoteFolders = ["a", "b/c"] }.Save(tv.Root);
        Assert.Equal(["a", "b/c"], VaultSettings.Load(tv.Root).NoteFolders);
        Assert.Contains("\"noteFolders\"", tv.Read(".noteevolution/settings.json"));
    }

    [Fact]
    public void FindBook_IgnoresCase()
    {
        using var tv = Sample();
        var vault = tv.Open();
        Assert.NotNull(vault.FindBook("buch - TEST"));
        Assert.Null(vault.FindBook("Idee"));
    }

    [Fact]
    public void FindPageByPath_ComparesFullPathsIgnoringCase()
    {
        using var tv = Sample();
        var vault = tv.Open();
        var path = Path.Combine(tv.Root, "pages", "..", "pages", "idee.MD");
        Assert.Equal("Idee", vault.FindPageByPath(path)?.Name);
        Assert.Null(vault.FindPageByPath(Path.Combine(tv.Root, "pages", "Nix.md")));
    }

    [Fact]
    public void FindBlockById_FindsAcrossPages()
    {
        using var tv = TestVault.Create(
            ("journals/2026_03_01.md", "- a\n"),
            ("pages/Idee.md", $"- x\n\t- tief\n\t  id:: {IdA}\n"));
        var vault = tv.Open();

        var (page, block) = vault.FindBlockById(Guid.Parse(IdA))!.Value;

        Assert.Equal("Idee", page.Name);
        Assert.Equal("tief", block.Content);
        Assert.Same(block, vault.FindBlockByKey(block.Key)!.Value.Block);
        Assert.Null(vault.FindBlockById(Guid.NewGuid()));
        Assert.Null(vault.FindBlockByKey(Guid.NewGuid()));
    }

    [Fact]
    public void ReplacePage_SwapsPageRebuildsBooksAndRaisesEvent()
    {
        using var tv = Sample();
        var vault = tv.Open();
        var raised = new List<string>();
        vault.PageReplaced += p => raised.Add(p.Name + ":" + vault.Books.Single().Title + ":" + vault.Pages.Count);
        var path = Path.Combine(tv.Root, BookFile);
        var oldBook = vault.Books.Single();

        var edited = Parse(path, "title:: Neu\ntype:: book\n\n- # Anderes\n");
        vault.ReplacePage(edited);

        Assert.Equal(["Buch - Test:Neu:3"], raised);
        Assert.Equal(3, vault.Pages.Count);
        Assert.Same(edited, vault.FindPageByPath(path));
        var book = vault.Books.Single();
        Assert.NotSame(oldBook, book);
        Assert.Same(edited, book.Page);
    }

    [Fact]
    public void ReplacePage_NewPath_AddsPage_BookStatusFollowsContent()
    {
        using var tv = Sample();
        var vault = tv.Open();
        var path = Path.Combine(tv.Root, "pages", "Buch - Zwei.md");

        vault.ReplacePage(Parse(path, "type:: book\n\n- # A\n"));

        Assert.Equal(4, vault.Pages.Count);
        Assert.Equal(2, vault.Books.Count);

        vault.ReplacePage(Parse(path, "- kein Buch mehr\n"));

        Assert.Equal(4, vault.Pages.Count);
        Assert.Single(vault.Books);
    }

    [Fact]
    public void RemovePage_RemovesPageAndBook()
    {
        using var tv = Sample();
        var vault = tv.Open();

        vault.RemovePage(Path.Combine(tv.Root, BookFile));

        Assert.Equal(2, vault.Pages.Count);
        Assert.Empty(vault.Books);
        vault.RemovePage(Path.Combine(tv.Root, "pages", "gibt-es-nicht.md"));
        Assert.Equal(2, vault.Pages.Count);
    }
}
