using NoteEvolution.AI.Search;
using NoteEvolution.Core.Vaults;
using NoteEvolution.TestSupport;

namespace NoteEvolution.AI.Tests.Search;

public class FullTextSearchTests
{
    private static readonly NoteFilter All = new(false, null, null);

    private static (NoteRepository Notes, Vault Vault) Open(TestVault tv)
    {
        var vault = tv.Open();
        return (new NoteRepository(vault), vault);
    }

    private static List<string> Contents(NoteRepository notes, IEnumerable<SearchHit> hits) =>
        [.. hits.Select(h => notes.Get(h.NoteBlockKey)!.Block.Content)];

    [Fact]
    public void Search_FindsByPrefixAndUmlautInsensitive()
    {
        using var tv = TestVault.Create(("journals/2026_03_01.md", "- Das Vertrauen wächst\n- Meine Ängste lösen sich\n- Etwas anderes\n"));
        var (notes, _) = Open(tv);
        using var search = new FullTextSearchService();
        search.Rebuild(notes);

        Assert.Equal(["Das Vertrauen wächst"], Contents(notes, search.Search(new("vertr", All))));
        Assert.Equal(["Meine Ängste lösen sich"], Contents(notes, search.Search(new("angst", All))));
        Assert.Equal(["Meine Ängste lösen sich"], Contents(notes, search.Search(new("ÄNGSTE", All))));
    }

    [Fact]
    public void Search_MatchesAncestorContext_AndRanksOwnContentHigher()
    {
        using var tv = TestVault.Create(("pages/Idee.md", "- Reise nach Berlin\n- Urlaub\n\t- Zug\n- Ziel\n\t- Reise\n"));
        var (notes, _) = Open(tv);
        using var search = new FullTextSearchService();
        search.Rebuild(notes);

        var hits = search.Search(new("urlaub", All));

        Assert.Equal(["Urlaub", "Zug"], Contents(notes, hits).Order());
        Assert.Equal("Urlaub", Contents(notes, hits)[0]);
        Assert.True(hits[0].Score > hits[1].Score);
    }

    [Fact]
    public void Search_HideUsed_ExcludesUsedAndDescendantsOfUsed()
    {
        using var tv = TestVault.Create(("journals/2026_03_01.md",
            "- wort frei\n- wort eltern\n  used-in:: [[Buch - X]]\n\t- wort kind\n- wort eigen\n\t- wort offen\n"));
        var (notes, _) = Open(tv);
        using var search = new FullTextSearchService();
        search.Rebuild(notes);

        Assert.Equal(5, search.Search(new("wort", All)).Count);
        Assert.Equal(
            ["wort eigen", "wort frei", "wort offen"],
            Contents(notes, search.Search(new("wort", new(true, null, null)))).Order());
    }

    [Fact]
    public void Search_DateRange_ExcludesUndatedPages()
    {
        using var tv = TestVault.Create(
            ("journals/2026_03_01.md", "- notiz eins\n"),
            ("journals/2026_03_02.md", "- notiz zwei\n"),
            ("journals/2026_03_03.md", "- notiz drei\n"),
            ("pages/Frei.md", "- notiz ohne datum\n"));
        var (notes, _) = Open(tv);
        using var search = new FullTextSearchService();
        search.Rebuild(notes);

        var range = Contents(notes, search.Search(new("notiz", new(false, new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 3)))));
        Assert.Equal(["notiz drei", "notiz zwei"], range.Order());
        Assert.Equal(["notiz eins"], Contents(notes, search.Search(new("notiz", new(false, null, new DateOnly(2026, 3, 1))))));
        Assert.Equal(["notiz drei"], Contents(notes, search.Search(new("notiz", new(false, new DateOnly(2026, 3, 3), null)))));
        Assert.Equal(4, search.Search(new("notiz", All)).Count);
    }

    [Fact]
    public void UpdatePage_ReflectsNewUsedInWithoutRebuild()
    {
        using var tv = TestVault.Create(
            ("journals/2026_03_01.md", "- wort eltern\n\t- wort kind\n"),
            ("journals/2026_03_02.md", "- wort anderswo\n"));
        var (notes, vault) = Open(tv);
        using var search = new FullTextSearchService();
        search.Rebuild(notes);
        var hideUsed = new NoteFilter(true, null, null);
        Assert.Equal(3, search.Search(new("wort", hideUsed)).Count);

        var page = vault.Pages.Single(p => p.Name == "2026_03_01");
        page.Roots[0].SetProperty("used-in", "[[Buch - X]]");
        search.UpdatePage(notes, page.FilePath);

        Assert.Equal(["wort anderswo"], Contents(notes, search.Search(new("wort", hideUsed))));
        Assert.Equal(3, search.Search(new("wort", All)).Count);
    }

    [Fact]
    public void UpdatePage_MatchesPathCaseInsensitively_ReindexesContent_AndDropsRemovedPage()
    {
        using var tv = TestVault.Create(("pages/Idee.md", "- alpha\n- beta\n"));
        var (notes, vault) = Open(tv);
        using var search = new FullTextSearchService();
        search.Rebuild(notes);
        var page = vault.Pages.Single();

        page.Roots[0].SetContent("gamma");
        search.UpdatePage(notes, page.FilePath.ToUpperInvariant());

        Assert.Empty(search.Search(new("alpha", All)));
        Assert.Equal(["gamma"], Contents(notes, search.Search(new("gamma", All))));
        Assert.Equal(["beta"], Contents(notes, search.Search(new("beta", All))));

        vault.RemovePage(page.FilePath);
        search.UpdatePage(notes, page.FilePath);

        Assert.Empty(search.Search(new("beta", All)));
        Assert.Empty(search.Search(new("gamma", All)));
    }

    [Fact]
    public void Rebuild_ReplacesPreviousIndex()
    {
        using var tv = TestVault.Create(("pages/Idee.md", "- alpha\n"));
        var (notes, vault) = Open(tv);
        using var search = new FullTextSearchService();
        search.Rebuild(notes);
        vault.RemovePage(vault.Pages.Single().FilePath);

        search.Rebuild(notes);

        Assert.Empty(search.Search(new("alpha", All)));
    }

    [Fact]
    public void Search_Limit_IsApplied()
    {
        using var tv = TestVault.Create(("pages/Idee.md", "- wort a\n- wort b\n- wort c\n"));
        var (notes, _) = Open(tv);
        using var search = new FullTextSearchService();
        search.Rebuild(notes);

        Assert.Equal(2, search.Search(new("wort", All, 2)).Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]
    [InlineData("\"")]
    public void Search_NoUsableTokens_ReturnsEmpty(string text)
    {
        using var tv = TestVault.Create(("pages/Idee.md", "- wort\n"));
        var (notes, _) = Open(tv);
        using var search = new FullTextSearchService();
        search.Rebuild(notes);

        Assert.Empty(search.Search(new(text, All)));
    }

    [Theory]
    [InlineData("foo \"bar")]
    [InlineData("AND OR NEAR")]
    [InlineData("-x (y) *")]
    [InlineData("a:b NOT c^")]
    [InlineData("\"\"\"")]
    [InlineData("'; DROP TABLE notes; --")]
    public void Search_SpecialCharacters_DoNotThrow(string text)
    {
        using var tv = TestVault.Create(("pages/Idee.md", "- foo bar -x (y) AND OR\n"));
        var (notes, _) = Open(tv);
        using var search = new FullTextSearchService();
        search.Rebuild(notes);

        var hits = search.Search(new(text, All));

        Assert.NotNull(hits);
    }

    [Fact]
    public void Search_OperatorWords_AreMatchedAsText()
    {
        using var tv = TestVault.Create(("pages/Idee.md", "- this AND that\n- other\n"));
        var (notes, _) = Open(tv);
        using var search = new FullTextSearchService();
        search.Rebuild(notes);

        Assert.Equal(["this AND that"], Contents(notes, search.Search(new("and that", All))));
    }

    [Fact]
    public void Search_BeforeRebuild_ReturnsEmpty()
    {
        using var search = new FullTextSearchService();

        Assert.Empty(search.Search(new("wort", All)));
    }
}
