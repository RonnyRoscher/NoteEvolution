using Bunit;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;

namespace NoteEvolution.UI.Tests;

/// <summary>The appendix in the outline: its sections stay out of the tree and the words, a row at the bottom opens its dialogs.</summary>
public class AppendixOutlineTests : UiTestContext
{
    private const string BookPath = "pages/Buch - Alpha.md";

    private const string Text =
        "title:: Alpha\ntype:: book\n\n" +
        "- # Eins\n" +
        "\t- Drei Wörter hier\n" +
        "- # Glossar\n" +
        "  anhang:: glossar\n" +
        "\t- Achtsamkeit\n" +
        "\t  Bewusstes Wahrnehmen des Augenblicks.\n";

    [Fact]
    public async Task AppendixRow_ShowsThreeButtons_RaisesTheRequest()
    {
        using var tv = TestVault.Create((BookPath, Text));
        await OpenSessionAsync(tv);
        var requests = new List<(AppendixKind Kind, Guid? Entry)>();
        State.AppendixDialogRequested += (kind, entry) => requests.Add((kind, entry));
        var cut = Render<OutlinePane>();

        var row = cut.Find(".ne-outline-appendix");
        Assert.Equal("Anhang:", row.QuerySelector(".ne-outline-appendix-label")!.TextContent);
        Assert.Equal("Glossar", cut.Find(".ne-appendix-glossary").TextContent);
        Assert.Equal("Menschen", cut.Find(".ne-appendix-people").TextContent);
        Assert.Equal("Quellen", cut.Find(".ne-appendix-sources").TextContent);

        cut.Find(".ne-appendix-glossary").Click();
        cut.Find(".ne-appendix-people").Click();
        cut.Find(".ne-appendix-sources").Click();

        Assert.Equal(
            [(AppendixKind.Glossary, (Guid?)null), (AppendixKind.People, null), (AppendixKind.Sources, null)], requests);
    }

    [Fact]
    public async Task AppendixRow_AlsoShownForABookWithoutAppendix()
    {
        using var tv = TestVault.Create((BookPath, "title:: Alpha\ntype:: book\n\n- # Eins\n"));
        await OpenSessionAsync(tv);

        var cut = Render<OutlinePane>();

        Assert.Single(cut.FindAll(".ne-outline-appendix"));
    }

    [Fact]
    public async Task Outline_HidesAppendixSections_BookRowWordsWithoutAppendix()
    {
        using var tv = TestVault.Create((BookPath, Text));
        await OpenSessionAsync(tv);

        var cut = Render<OutlinePane>();

        Assert.Equal(["Eins"], cut.FindAll(".ne-outline-tree .ne-outline-row .ne-outline-title").Select(t => t.TextContent));
        // "Drei Wörter hier" only: the glossary entry does not count.
        Assert.Equal("3 W", cut.Find(".ne-outline-book .ne-outline-words").TextContent);
    }
}
