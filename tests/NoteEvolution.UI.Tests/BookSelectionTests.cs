using Bunit;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>Choosing the book in the header: books, convertible pages, the draft assistant and the remembered choice.</summary>
public class BookSelectionTests : UiTestContext
{
    private const string Draft = "- Kapitel\n\t- Abschnitt\n\t\t- Ein Satz hier.\n";

    private static TestVault Mixed() => TestVault.Create(
        ("pages/Buch - Beta.md", "title:: Beta\ntype:: book\n\n- # Zwei\n"),
        ("pages/Buch - Alpha.md", "title:: Alpha\ntype:: book\n\n- # Eins\n"),
        ("pages/Zettel.md", "- Lose Idee\n"),
        ("pages/Entwurf.md", Draft),
        ("pages/Kaputt.md", "- Kapitel\n```\nnicht geschlossen\n"),
        ("journals/2026_03_01.md", "- Eine Notiz\n"));

    private static IEnumerable<string> Texts(IRenderedComponent<Shell> cut, string selector) =>
        cut.FindAll(selector).Select(e => e.TextContent);

    private IRenderedComponent<Shell> RenderShellWith(TestVault vault)
    {
        Settings.LastVault = vault.Root;
        Settings.LogseqHintShown = true;
        var cut = Render<Shell>();
        WaitForVaultOpened(cut);

        // The header re-renders on its own State.Changed dispatch, after the shell.
        cut.WaitForElement(".ne-book-select");
        return cut;
    }

    private static string PageValue(IRenderedComponent<Shell> cut, string name) =>
        cut.FindAll(".ne-book-select .ne-page-option").Single(o => o.TextContent == name).GetAttribute("value")!;

    [Fact]
    public void Selection_ListsBooksAndConvertiblePages_SortedWithoutJournalsAndReadOnly()
    {
        using var tv = Mixed();

        var cut = RenderShellWith(tv);

        Assert.Equal(["Alpha", "Beta"], Texts(cut, ".ne-book-select .ne-book-option"));
        Assert.Equal(["Entwurf", "Zettel"], Texts(cut, ".ne-book-select .ne-page-option"));
        Assert.Empty(cut.FindAll(".ne-book-select .ne-book-placeholder"));
        Assert.Equal("Alpha", State.CurrentBook!.Title);
    }

    [Fact]
    public void Selection_NoBookYet_ShowsThePlaceholder()
    {
        using var tv = TestVault.Create(("pages/Entwurf.md", Draft));

        var cut = RenderShellWith(tv);

        Assert.Null(State.CurrentBook);
        Assert.Single(cut.FindAll(".ne-book-select .ne-book-placeholder"));
        Assert.Empty(cut.FindAll(".ne-book-select .ne-book-option"));
        Assert.Equal(["Entwurf"], Texts(cut, ".ne-book-select .ne-page-option"));
    }

    [Fact]
    public void ChoosingAPage_OpensTheDraftAssistantForIt_KeepsTheCurrentBook()
    {
        using var tv = Mixed();
        var cut = RenderShellWith(tv);

        cut.Find(".ne-book-select").Change(PageValue(cut, "Entwurf"));

        Assert.Single(cut.FindAll(".ne-draft"));
        Assert.Single(cut.FindAll(".ne-draft-tree"));
        Assert.Contains("Entwurf", cut.Find(".ne-draft .ne-dialog-hint").TextContent);
        Assert.Equal("Alpha", State.CurrentBook!.Title);
        Assert.Equal(Draft, tv.Read("pages/Entwurf.md"));
    }

    [Fact]
    public void ChoosingAPage_Converted_BecomesTheCurrentBook_AndIsRemembered()
    {
        using var tv = Mixed();
        var cut = RenderShellWith(tv);
        cut.Find(".ne-book-select").Change(PageValue(cut, "Entwurf"));

        cut.Find(".ne-draft-convert").Click();
        cut.Find(".ne-draft-yes").Click();

        cut.WaitForAssertion(() => Assert.Equal("Entwurf", State.CurrentBook?.LinkName));
        Assert.Equal(State.CurrentBook!.Root.Key, State.CurrentSectionKey);
        Assert.Contains("type:: book", tv.Read("pages/Entwurf.md"));
        Assert.Equal("pages/Entwurf.md", UiSettings.Load(Platform.UserDataDirectory).LastBookOf(tv.Root));
        cut.WaitForAssertion(() => Assert.Equal(["Alpha", "Beta", "Entwurf"], Texts(cut, ".ne-book-select .ne-book-option")));
    }

    [Fact]
    public void ChoosingAPage_Cancelled_KeepsThePreviousBookShown_FileUnchanged()
    {
        using var tv = Mixed();
        var cut = RenderShellWith(tv);
        var alpha = State.CurrentBook!;

        cut.Find(".ne-book-select").Change(PageValue(cut, "Entwurf"));
        cut.Find(".ne-draft-close").Click();

        Assert.Empty(cut.FindAll(".ne-draft"));
        Assert.Same(alpha, State.CurrentBook);
        Assert.Equal(alpha.LinkName, cut.Find(".ne-book-select").GetAttribute("value"));
        Assert.Equal(Draft, tv.Read("pages/Entwurf.md"));
        Assert.Null(UiSettings.Load(Platform.UserDataDirectory).LastBookOf(tv.Root));
    }

    [Fact]
    public void ChoosingABook_IsRemembered()
    {
        using var tv = Mixed();
        var cut = RenderShellWith(tv);
        var beta = State.Session!.Vault.Books.Single(b => b.Title == "Beta");

        cut.Find(".ne-book-select").Change(beta.LinkName);

        Assert.Same(beta, State.CurrentBook);
        Assert.Equal("pages/Buch - Beta.md", UiSettings.Load(Platform.UserDataDirectory).LastBookOf(tv.Root));
    }

    [Fact]
    public void Startup_SelectsTheRememberedBook()
    {
        using var tv = Mixed();
        Settings.RememberBook(tv.Root, Path.Combine(tv.Root, "pages", "Buch - Beta.md"));

        RenderShellWith(tv);

        Assert.Equal("Beta", State.CurrentBook!.Title);
        Assert.Equal(State.CurrentBook.Root.Key, State.CurrentSectionKey);
    }

    [Theory]
    [InlineData("pages/Weg.md")]
    [InlineData("pages/Zettel.md")]
    public void Startup_RememberedBookGoneOrNoLongerABook_FallsBackToTheFirstBook(string remembered)
    {
        using var tv = Mixed();
        Settings.RememberBook(tv.Root, Path.Combine(tv.Root, remembered));

        RenderShellWith(tv);

        Assert.Equal("Alpha", State.CurrentBook!.Title);
    }
}
