using System.Text;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Links;
using NoteEvolution.Core.Vaults;
using NoteEvolution.TestSupport;

namespace NoteEvolution.Core.Tests.Vaults;

public class NoteRepositoryTests
{
    private static readonly DateOnly D1 = new(2026, 3, 1);
    private static readonly DateOnly D2 = new(2026, 3, 2);
    private static readonly DateOnly D3 = new(2026, 3, 3);

    private static NoteRepository Repo(TestVault tv, out Vault vault)
    {
        vault = tv.Open();
        return new NoteRepository(vault);
    }

    [Fact]
    public void NoteBlock_DateFromJournalFileName_NullForPages()
    {
        using var tv = TestVault.Create(
            ("journals/2026_03_01.md", "- tagebuch\n"),
            ("journals/2026_13_01.md", "- ungueltiges Datum\n"),
            ("journals/2026-03-01.md", "- falsches Format\n"),
            ("pages/2026_03_01.md", "- Seite mit Datumsnamen\n"),
            ("pages/Idee.md", "- idee\n"),
            ("journals/Archiv/2026_03_05.md", "- im Unterordner\n"));
        var repo = Repo(tv, out _);

        var dates = repo.All().ToDictionary(n => n.Block.Content, n => n.Date);

        Assert.Equal(D1, dates["tagebuch"]);
        Assert.Null(dates["ungueltiges Datum"]);
        Assert.Null(dates["falsches Format"]);
        Assert.Null(dates["Seite mit Datumsnamen"]);
        Assert.Null(dates["idee"]);
        Assert.Null(dates["im Unterordner"]);
    }

    [Fact]
    public void NoteBlock_JournalFolderNameIgnoresCase()
    {
        using var tv = TestVault.Create(("Journals/2026_03_06.md", "- gross\n"));
        new VaultSettings { NoteFolders = ["Journals"] }.Save(tv.Root);
        var repo = Repo(tv, out _);

        Assert.Equal(new DateOnly(2026, 3, 6), repo.All().Single().Date);
    }

    [Fact]
    public void NoteBlock_IsUsed_WhenAncestorHasUsedIn()
    {
        using var tv = TestVault.Create(("pages/Idee.md",
            "- eltern\n  used-in:: [[Buch - X]] ((7f3a91c2-4b1d-4e8a-9c3f-1a2b3c4d5e6f))\n" +
            "\t- kind\n\t\t- enkel\n" +
            "- frei\n\t- eigenes\n\t  used-in:: [[Buch - Y]]\n\t\t- darunter\n"));
        var repo = Repo(tv, out _);

        var notes = repo.All().ToDictionary(n => n.Block.Content);

        Assert.True(notes["eltern"].IsUsed);
        Assert.True(notes["kind"].IsUsed);
        Assert.True(notes["enkel"].IsUsed);
        Assert.False(notes["frei"].IsUsed);
        Assert.True(notes["eigenes"].IsUsed);
        Assert.True(notes["darunter"].IsUsed);
        Assert.Equal(
            [new UsedInEntry("Buch - X", Guid.Parse("7f3a91c2-4b1d-4e8a-9c3f-1a2b3c4d5e6f"))],
            notes["eltern"].Usages);
        Assert.Empty(notes["kind"].Usages);
        Assert.Equal(["Buch - Y"], notes["eigenes"].Usages.Select(u => u.PageName));
    }

    [Fact]
    public void NoteBlock_ContextPath_FirstLinesOfAncestorsOutermostFirst()
    {
        using var tv = TestVault.Create(("pages/Idee.md", "- oben\n  zweite Zeile\n\t- mitte\n\t\t- unten\n- Wurzel\n"));
        var repo = Repo(tv, out _);

        var notes = repo.All().ToDictionary(n => n.Block.Content.Split('\n')[0]);

        Assert.Equal(["oben", "mitte"], notes["unten"].ContextPath);
        Assert.Equal(["oben"], notes["mitte"].ContextPath);
        Assert.Empty(notes["Wurzel"].ContextPath);
    }

    [Fact]
    public void All_ExcludesBooks_ReturnsEveryDepthInFileOrder_GetByKey()
    {
        using var tv = TestVault.Create(
            ("journals/2026_03_01.md", "- a\n\t- a1\n- b\n"),
            ("pages/Buch - Test.md", "type:: book\n\n- # Kapitel\n"),
            ("pages/Idee.md", "- c\n"));
        var repo = Repo(tv, out _);

        var all = repo.All().ToList();

        Assert.Equal(["a", "a1", "b", "c"], all.Select(n => n.Block.Content));
        Assert.All(all, n => Assert.Same(n, repo.Get(n.Key)));
        Assert.All(all, n => Assert.Equal(n.Block.Key, n.Key));
        Assert.Null(repo.Get(Guid.NewGuid()));
    }

    [Fact]
    public void Journal_HideUsedAndDateRange_NewestFirst()
    {
        using var tv = TestVault.Create(
            ("journals/2026_03_01.md", "- t1a\n- t1b\n"),
            ("journals/2026_03_03.md", "- t3a\n\t- t3kind\n- t3b\n  used-in:: [[Buch - X]]\n"),
            ("journals/2026_03_02.md", "- t2a\n"),
            ("pages/Idee.md", "- ohne Datum\n"));
        var repo = Repo(tv, out _);

        string[] Roots(NoteFilter f) => [.. repo.Journal(f).Select(n => n.Block.Content)];

        Assert.Equal(["t3a", "t3b", "t2a", "t1a", "t1b"], Roots(new(false, null, null)));
        Assert.Equal(["t3a", "t2a", "t1a", "t1b"], Roots(new(true, null, null)));
        Assert.Equal(["t3a", "t3b", "t2a"], Roots(new(false, D2, D3)));
        Assert.Equal(["t2a"], Roots(new(false, D2, D2)));
        Assert.Equal(["t2a", "t1a", "t1b"], Roots(new(true, null, D2)));
        Assert.Equal(["t3a", "t3b"], Roots(new(false, D3, null)));
    }

    [Fact]
    public void Matches_HideUsedAndInclusiveDates_DateFilterExcludesUndated()
    {
        using var tv = TestVault.Create(
            ("journals/2026_03_02.md", "- tag\n- benutzt\n  used-in:: [[Buch - X]]\n"),
            ("pages/Idee.md", "- ohne Datum\n"));
        var repo = Repo(tv, out _);
        var all = repo.All().ToDictionary(n => n.Block.Content);

        Assert.True(repo.Matches(all["tag"], new(true, D2, D2)));
        Assert.False(repo.Matches(all["tag"], new(false, D3, null)));
        Assert.False(repo.Matches(all["tag"], new(false, null, D1)));
        Assert.False(repo.Matches(all["benutzt"], new(true, null, null)));
        Assert.True(repo.Matches(all["benutzt"], new(false, null, null)));
        Assert.True(repo.Matches(all["ohne Datum"], new(true, null, null)));
        Assert.False(repo.Matches(all["ohne Datum"], new(false, D1, null)));
        Assert.False(repo.Matches(all["ohne Datum"], new(false, null, D3)));
    }

    [Fact]
    public void PageReplaced_InvalidatesCachedNotes()
    {
        using var tv = TestVault.Create(("pages/Idee.md", "- alt\n"));
        var repo = Repo(tv, out var vault);
        var oldNote = repo.All().Single();
        Assert.Same(oldNote, repo.All().Single());

        var path = Path.Combine(tv.Root, "pages", "Idee.md");
        vault.ReplacePage(LogseqParser.Parse(path, Encoding.UTF8.GetBytes("- neu\n  used-in:: [[Buch - X]]\n")));

        var note = repo.All().Single();
        Assert.Equal("neu", note.Block.Content);
        Assert.True(note.IsUsed);
        Assert.Null(repo.Get(oldNote.Key));
        Assert.Same(note, repo.Get(note.Key));
    }
}
