using AngleSharp.Dom;
using Bunit;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.State;
using static NoteEvolution.UI.Tests.NotesTestData;

namespace NoteEvolution.UI.Tests;

/// <summary>Used blocks on every level of a note card: hidden or grey, with a check mark and jump on each block's own usage.</summary>
public class UsedBlocksTests : UiTestContext
{
    private const string Day2 = "journals/2026_10_02.md";

    private readonly List<TestVault> _vaults = [];

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _vaults.ForEach(v => v.Dispose());
        }
    }

    /// <summary>
    /// Two books; 02.10. has an unused main point with an unused and a used sub-point (with a sub-point of its own), and a
    /// used main point with a sub-point; 01.10. has only a used main point; Ideen has an unused note with a used sub-point.
    /// </summary>
    private TestVault Vault()
    {
        var tv = TestVault.Create(
            (AlphaPath, "title:: Alpha\ntype:: book\n\n- # Eins\n\t- Erster Text\n"),
            ("pages/Buch - Beta.md",
                "title:: Beta\ntype:: book\n\n- # Beta Eins\n\t- ## Beta Unter\n\t\t- Beta Text\n\t\t  id:: " + BetaBlockId + "\n"),
            (Day2,
                "- Offener Hauptpunkt\n" +
                "\t- Offenes Kind\n" +
                "\t- Verwendetes Kind\n" +
                "\t  used-in:: [[" + BetaBook + "]] ((" + BetaBlockId + "))\n" +
                "\t\t- Enkel des verwendeten\n" +
                "- Verwendeter Hauptpunkt\n" +
                "  used-in:: [[Buch - Alpha]]\n" +
                "\t- Kind des verwendeten\n"),
            ("journals/2026_10_01.md",
                "- Ganz verwendeter Tag\n" +
                "  used-in:: [[Buch - Alpha]]\n" +
                "\t- Kind davon\n"),
            ("pages/Ideen.md",
                "- Suchwort Hauptpunkt\n" +
                "\t- Suchwort Kind verwendet\n" +
                "\t  used-in:: [[" + BetaBook + "]]\n"));
        _vaults.Add(tv);
        return tv;
    }

    private async Task<VaultSession> OpenAlphaAsync(TestVault vault)
    {
        var session = await OpenSessionAsync(vault);
        State.CurrentBook = session.Vault.FindBook("Buch - Alpha");
        State.CurrentSectionKey = State.CurrentBook!.Root.Children.First().Key;
        return session;
    }

    private static List<string> Texts(IRenderedComponent<NotesPane> cut) =>
        [.. cut.FindAll(".ne-note-text, .ne-note-child-text").Select(e => e.TextContent.Trim())];

    private static IElement ChildOf(IRenderedComponent<NotesPane> cut, string text) =>
        cut.FindAll(".ne-note-child").First(c => c.QuerySelector(":scope > .ne-note-child-row > .ne-note-child-text")!.TextContent.Trim() == text);

    [Fact]
    public async Task HideUsed_UsedSubPoint_Hidden_ParentShown()
    {
        using var tv = Vault();
        await OpenAlphaAsync(tv);
        var cut = Render<NotesPane>();
        cut.Find(".ne-tab-journal").Click();
        Assert.Contains("Verwendetes Kind", Texts(cut));

        cut.Find(".ne-hide-used").Change(true);

        // The used sub-point goes with its own sub-point; its unused parent and sibling stay.
        var texts = Texts(cut);
        Assert.Contains("Offener Hauptpunkt", texts);
        Assert.Contains("Offenes Kind", texts);
        Assert.DoesNotContain("Verwendetes Kind", texts);
        Assert.DoesNotContain("Enkel des verwendeten", texts);
    }

    [Fact]
    public async Task HideUsed_UsedMain_HiddenWithSubPoints()
    {
        using var tv = Vault();
        await OpenAlphaAsync(tv);
        var cut = Render<NotesPane>();
        cut.Find(".ne-tab-journal").Click();
        Assert.Contains("Kind des verwendeten", Texts(cut));

        cut.Find(".ne-hide-used").Change(true);

        var texts = Texts(cut);
        Assert.DoesNotContain("Verwendeter Hauptpunkt", texts);
        Assert.DoesNotContain("Kind des verwendeten", texts);
    }

    [Fact]
    public async Task HideUsed_WholeDayUsed_DayCardGone()
    {
        using var tv = Vault();
        await OpenAlphaAsync(tv);
        var cut = Render<NotesPane>();
        cut.Find(".ne-tab-journal").Click();
        Assert.Equal(["02.10.2026", "01.10.2026"], cut.FindAll(".ne-journal-day-date").Select(e => e.TextContent));

        cut.Find(".ne-hide-used").Change(true);

        // 01.10. has only a used main point: no card; 02.10. keeps its unused main point.
        Assert.Equal(["02.10.2026"], cut.FindAll(".ne-journal-day-date").Select(e => e.TextContent));
        Assert.DoesNotContain("Ganz verwendeter Tag", Texts(cut));
    }

    [Fact]
    public async Task ShowUsed_UsedBlocksGrey_IncludingInheritedSubPoints()
    {
        using var tv = Vault();
        await OpenAlphaAsync(tv);
        var cut = Render<NotesPane>();
        cut.Find(".ne-tab-journal").Click();

        bool Grey(string selector, string text) =>
            cut.FindAll(selector).Single(e => e.TextContent.Trim() == text).ClassList.Contains("ne-note-used-text");

        Assert.True(Grey(".ne-note-text", "Verwendeter Hauptpunkt"));
        Assert.True(Grey(".ne-note-child-text", "Kind des verwendeten"));
        Assert.True(Grey(".ne-note-child-text", "Verwendetes Kind"));
        Assert.True(Grey(".ne-note-child-text", "Enkel des verwendeten"));
        Assert.False(Grey(".ne-note-text", "Offener Hauptpunkt"));
        Assert.False(Grey(".ne-note-child-text", "Offenes Kind"));
    }

    [Fact]
    public async Task UsedSubPoint_ShowsCheckWithCount_ClickJumpsToTarget()
    {
        using var tv = Vault();
        var session = await OpenAlphaAsync(tv);
        var cut = Render<NotesPane>();
        cut.Find(".ne-tab-journal").Click();

        // Only blocks with their own used-in:: carry the mark: not the inherited grandchild, not the open sub-point.
        Assert.Empty(ChildOf(cut, "Offenes Kind").QuerySelectorAll(":scope > .ne-note-child-row .ne-note-used"));
        var used = ChildOf(cut, "Verwendetes Kind");
        Assert.Single(used.QuerySelectorAll(":scope > .ne-note-child-row .ne-note-used"));
        Assert.Empty(ChildOf(cut, "Enkel des verwendeten").QuerySelectorAll(".ne-note-used"));

        used.QuerySelector(":scope > .ne-note-child-row .ne-note-used")!.Click();

        var beta = session.Vault.FindBook(BetaBook)!;
        var section = beta.Root.Children.Single().Children.Single();
        Assert.Equal(BetaBook, State.CurrentBook!.LinkName);
        Assert.Equal(section.Key, State.CurrentSectionKey);
        Assert.Equal(section.TextBlocks.Single().Key, State.CursorTextBlockKey);
    }

    [Fact]
    public async Task UsedSubPoint_WithTwoEntries_ShowsTheCount()
    {
        using var tv = TestVault.Create(
            (AlphaPath, "title:: Alpha\ntype:: book\n\n- # Eins\n"),
            (Day2, "- Haupt\n\t- Kind\n\t  used-in:: [[Buch - Alpha]], [[Buch - Alpha]]\n"));
        _vaults.Add(tv);
        await OpenAlphaAsync(tv);
        var cut = Render<NotesPane>();
        cut.Find(".ne-tab-journal").Click();

        Assert.Equal("2", ChildOf(cut, "Kind").QuerySelector(".ne-note-used-count")!.TextContent);
    }

    [Fact]
    public async Task SearchAndRelevant_SameRule()
    {
        using var tv = Vault();
        var session = await OpenAlphaAsync(tv);

        // The cards of Suche and Relevant are the same component; a card of a hit shows its sub-points by the same rule.
        var note = Note(session, "Suchwort Hauptpunkt");
        var cut = Render<NoteCard>(p => p.Add(c => c.Note, note));
        Assert.Contains("ne-note-used-text", cut.Find(".ne-note-child-text").ClassList);
        Assert.NotNull(cut.Find(".ne-note-child .ne-note-used"));

        Settings.HideUsed = true;
        var hidden = Render<NoteCard>(p => p.Add(c => c.Note, note));
        Assert.Empty(hidden.FindAll(".ne-note-child"));
        Assert.Equal("Suchwort Hauptpunkt", hidden.Find(".ne-note-text").TextContent.Trim());

        // And in the tab itself: a used hit is not listed, the card of its unused parent shows no used sub-point.
        var pane = Render<NotesPane>();
        pane.Find(".ne-tab-search").Click();
        pane.Find(".ne-search-input").Input("Suchwort");
        Time.Advance(TimeSpan.FromMilliseconds(300));
        pane.WaitForState(() => pane.FindAll(".ne-note-card").Count > 0);
        Assert.Equal(["Suchwort Hauptpunkt"], Texts(pane));
    }
}
