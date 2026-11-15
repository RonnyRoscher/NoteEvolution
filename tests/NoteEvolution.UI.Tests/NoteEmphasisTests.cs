using Bunit;
using NoteEvolution.Core.Vaults;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;

namespace NoteEvolution.UI.Tests;

/// <summary>Bold and italic in the text of a note card (journal, relevant, search) are shown, not their stars.</summary>
public class NoteEmphasisTests : UiTestContext
{
    private readonly TestVault _vault = TestVault.Create(
        ("pages/Buch - Alpha.md", "title:: Alpha\ntype:: book\n\n- # Eins\n\t- Text\n"),
        ("pages/Ideen.md",
            "- Ein **fetter** und ein *kursiver* Teil, dazu ***beides*** und 2 * 3 * 4\n" +
            "\t- Unterpunkt mit **Fett**\n" +
            "- Ohne Auszeichnung <b>kein HTML</b>\n"));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _vault.Dispose();
        }
    }

    private async Task<IRenderedComponent<NoteCard>> RenderAsync(string start)
    {
        var session = await OpenSessionAsync(_vault);
        State.CurrentBook = session.Vault.FindBook("Buch - Alpha");
        var note = session.Notes.All().Single(n => n.Block.Content.StartsWith(start, StringComparison.Ordinal));
        return Render<NoteCard>(p => p.Add(c => c.Note, note));
    }

    [Fact]
    public async Task Card_ShowsBoldAndItalic_WithoutTheStars()
    {
        var cut = await RenderAsync("Ein ");

        var text = cut.Find(".ne-note-text");
        Assert.Equal("Ein fetter und ein kursiver Teil, dazu beides und 2 * 3 * 4", text.TextContent);
        Assert.Equal(["fetter", "beides"], text.QuerySelectorAll("strong").Select(e => e.TextContent));
        Assert.Equal(["kursiver", "beides"], text.QuerySelectorAll("em").Select(e => e.TextContent));
    }

    [Fact]
    public async Task SubPoint_ShowsBold()
    {
        var cut = await RenderAsync("Ein ");

        var child = cut.Find(".ne-note-child-text");
        Assert.Equal("Unterpunkt mit Fett", child.TextContent);
        Assert.Equal("Fett", child.QuerySelector("strong")!.TextContent);
    }

    [Fact]
    public async Task Card_WithoutEmphasis_ShowsTheTextAsItIs_HtmlStaysText()
    {
        var cut = await RenderAsync("Ohne ");

        var text = cut.Find(".ne-note-text");
        Assert.Equal("Ohne Auszeichnung <b>kein HTML</b>", text.TextContent);
        Assert.Empty(text.QuerySelectorAll("b, strong, em"));
    }
}
