using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NoteEvolution.Core.Vaults;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;
using static NoteEvolution.UI.Tests.NotesTestData;

namespace NoteEvolution.UI.Tests;

/// <summary>The active note block in the notes pane (spec B): a click makes it active, its frame and range, and the bar's actions.</summary>
public class NoteBarTests : UiTestContext
{
    private const string BookPath = "pages/Buch - Alpha.md";

    private const string JournalPath = "journals/2026_10_01.md";

    private const string PagePath = "pages/Ideen.md";

    private const string PageNoteId = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee";

    private const string BookText = "title:: Alpha\ntype:: book\n\n- # Eins\n\t- Erster Text\n\t- Zweiter Text\n";

    private const string JournalText =
        "- Haupt\n" +
        "\t- Unter A\n" +
        "\t\t- Tiefer\n" +
        "\t- Unter B\n" +
        "- Andere\n";

    private readonly TestVault _vault = TestVault.Create(
        (BookPath, BookText),
        (JournalPath, JournalText),
        (PagePath, $"- Seitennotiz\n  id:: {PageNoteId}\n"));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _vault.Dispose();
        }
    }

    private async Task<VaultSession> OpenAsync()
    {
        var session = await OpenSessionAsync(_vault);
        State.CurrentSectionKey = State.CurrentBook!.Root.Children.First().Key;
        return session;
    }

    private Guid Key(string content) => State.Session!.Notes.All().Single(n => n.Block.Content == content).Key;

    private string Text(string key) => Services.GetRequiredService<IStringLocalizer<Strings>>()[key].Value;

    /// <summary>The own texts of the blocks in the frame, in reading order.</summary>
    private static List<string> InRange(IRenderedComponent<NotesPane> cut) => [.. cut.FindAll(".ne-note-in-range").Select(OwnText)];

    /// <summary>The own texts of all blocks shown.</summary>
    private static List<string> Blocks(IRenderedComponent<NotesPane> cut) => [.. cut.FindAll(".ne-note-block").Select(OwnText)];

    private List<string> SectionTexts() => [.. State.CurrentBook!.Root.Children.First().TextBlocks.Select(t => t.Text)];

    private void CursorAt(string text) =>
        SetCursorInTextBlock(State.CurrentBook!.Root.Children.First().TextBlocks.Single(t => t.Text == text).Key);

    private static void AltKey(IRenderedComponent<NotesPane> cut, string key) =>
        cut.Find(".ne-notes-pane").KeyDown(new KeyboardEventArgs { Key = key, AltKey = true });

    [Fact]
    public async Task Click_SelectsBlock_FrameAndBarShown_NoButtonsOnCards()
    {
        await OpenAsync();
        var cut = Render<NotesPane>();

        // The cards carry no buttons of their own (there is no used block here, so no ✓ either).
        Assert.Empty(cut.FindAll(".ne-note-card button"));
        Assert.Empty(cut.FindAll(".ne-note-bar"));

        cut.ClickBlock("Unter A");

        Assert.Equal(new NoteSelection(Key("Haupt"), Key("Unter A"), 0), State.ActiveNote);
        Assert.Equal(["Unter A", "Tiefer"], InRange(cut));

        // The frame is the box of "Unter A"; the bar stands at its bottom, below its last block "Tiefer".
        var frame = cut.Find(".ne-note-range-frame");
        Assert.Equal("Unter A", OwnText(frame.QuerySelector(":scope > .ne-note-child-row")!));
        Assert.Contains("ne-note-bar", frame.LastElementChild!.ClassList);
        var bar = Assert.Single(cut.FindAll(".ne-note-bar"));
        Assert.Equal(
            ["ne-note-range-up", "ne-note-range-down", "ne-note-adopt-cursor", "ne-note-adopt-after", "ne-note-adopt-under",
                "ne-note-adopt-heading", "ne-note-adopt-subheading", "ne-note-logseq"],
            bar.QuerySelectorAll("button").Select(b => b.ClassName));
        Assert.Equal(
            [Text("AdoptAtCursor"), Text("AdoptAfter"), Text("AdoptBelow"), Text("AdoptAsHeading"), Text("AdoptAsSubheading")],
            bar.QuerySelectorAll("[class^='ne-note-adopt-']").Select(b => b.TextContent.Trim()));
        Assert.Equal(["ne-note-bar-not-relevant", "ne-note-bar-later"], bar.QuerySelectorAll("input").Select(i => i.ClassName));
        Assert.DoesNotContain(cut.FindAll(".ne-note-card button"), b => b.Closest(".ne-note-bar") is null);

        // Another block becomes the only active one; a block without sub-points offers no heading.
        cut.ClickBlock("Andere");

        Assert.Equal(["Andere"], InRange(cut));
        bar = Assert.Single(cut.FindAll(".ne-note-bar"));
        Assert.Empty(bar.QuerySelectorAll(".ne-note-adopt-heading, .ne-note-adopt-subheading"));
        Assert.Equal("Andere", OwnText(bar.Closest(".ne-note-card")!.QuerySelector(".ne-note-text")!));
    }

    [Fact]
    public async Task TextSelection_DoesNotSelect()
    {
        await OpenAsync();
        var cut = Render<NotesPane>();

        // A click that ends a marking of text leaves the selection as it was, so the text can be copied.
        TextSelection.SetResult(true);
        cut.ClickBlock("Haupt");

        Assert.Null(State.ActiveNote);
        Assert.Empty(cut.FindAll(".ne-note-bar"));

        TextSelection.SetResult(false);
        cut.ClickBlock("Haupt");

        Assert.Equal(Key("Haupt"), State.ActiveNote!.BlockKey);
        Assert.Single(cut.FindAll(".ne-note-bar"));
    }

    [Fact]
    public async Task RangeButtons_And_AltArrows_ChangeTheFrame()
    {
        await OpenAsync();
        var cut = Render<NotesPane>();
        cut.ClickBlock("Tiefer");
        Assert.Equal(["Tiefer"], InRange(cut));

        cut.Find(".ne-note-range-up").Click();
        Assert.Equal(["Unter A", "Tiefer"], InRange(cut));

        AltKey(cut, "ArrowUp");
        Assert.Equal(["Haupt", "Unter A", "Tiefer", "Unter B"], InRange(cut));
        var up = cut.Find(".ne-note-range-up");
        Assert.True(up.HasAttribute("disabled"));
        Assert.Equal(Text("NoteRangeUpLimit"), up.GetAttribute("title"));

        // An arrow without Alt does nothing.
        cut.Find(".ne-notes-pane").KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        Assert.Equal(["Haupt", "Unter A", "Tiefer", "Unter B"], InRange(cut));

        AltKey(cut, "ArrowDown");
        Assert.Equal(["Unter A", "Tiefer"], InRange(cut));
        cut.Find(".ne-note-range-down").Click();
        Assert.Equal(["Tiefer"], InRange(cut));
        Assert.False(State.ActiveNoteRange!.OwnOnly);

        // A block without sub-points has no level −1: alone it is the same as with its subtree.
        Assert.True(cut.Find(".ne-note-range-down").HasAttribute("disabled"));
        AltKey(cut, "ArrowDown");
        Assert.Equal(0, State.ActiveNote!.Level);

        // Level −1: the block alone; its row is the frame now, the bar follows it.
        cut.ClickBlock("Unter A");
        cut.Find(".ne-note-range-down").Click();
        Assert.Equal(["Unter A"], InRange(cut));
        Assert.True(State.ActiveNoteRange!.OwnOnly);
        var down = cut.Find(".ne-note-range-down");
        Assert.True(down.HasAttribute("disabled"));
        Assert.Equal(Text("NoteRangeDownLimit"), down.GetAttribute("title"));
        var frame = cut.Find(".ne-note-range-frame");
        Assert.Contains("ne-note-child-row", frame.ClassList);
        Assert.Contains("ne-note-bar", frame.NextElementSibling!.ClassList);
    }

    [Fact]
    public async Task Adopt_FromBar_TakesTheRange_OwnOnlyWithoutSubPoints()
    {
        await OpenAsync();
        CursorAt("Erster Text");
        var cut = Render<NotesPane>();
        cut.ClickBlock("Haupt");
        cut.Find(".ne-note-range-down").Click();

        // The block alone: no heading, as it takes no sub-points along.
        Assert.True(State.ActiveNoteRange!.OwnOnly);
        Assert.Empty(cut.FindAll(".ne-note-adopt-heading, .ne-note-adopt-subheading"));
        cut.Find(".ne-note-adopt-after").Click();

        Assert.Equal(["Erster Text", "Haupt", "Zweiter Text"], SectionTexts());
        Assert.DoesNotContain("Unter", _vault.Read(BookPath));
        var journal = _vault.Read(JournalPath);
        Assert.Equal(1, journal.Split("used-in::").Length - 1);
        Assert.StartsWith("- Haupt\n  id:: ", journal);

        // At level 0 a sub-point takes its own sub-points along.
        CursorAt("Zweiter Text");
        cut.ClickBlock("Unter A");
        cut.Find(".ne-note-adopt-after").Click();

        Assert.Equal(["Erster Text", "Haupt", "Zweiter Text", "Unter A"], SectionTexts());
        Assert.Contains("Tiefer", _vault.Read(BookPath));
        Assert.DoesNotContain("Unter B", _vault.Read(BookPath));
    }

    [Fact]
    public async Task Adopt_HideUsed_SelectionClearedCleanly()
    {
        await OpenAsync();
        var cut = Render<NotesPane>();
        cut.Find(".ne-hide-used").Change(true);

        // A sub-point goes from its card, which stays.
        cut.ClickBlock("Unter B");
        cut.Find(".ne-note-adopt-cursor").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("Unter B", Blocks(cut));
            Assert.Null(State.ActiveNote);
        });
        Assert.Empty(cut.FindAll(".ne-note-in-range, .ne-note-range-frame, .ne-note-bar, .ne-note-error"));
        Assert.Equal(["Haupt", "Unter A", "Tiefer", "Andere"], Blocks(cut));

        // A main point goes at once with its card (the journal reads its notes again after adopting), and so does the
        // selection; no other card becomes active.
        cut.ClickBlock("Andere");
        cut.Find(".ne-note-adopt-cursor").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(["Haupt", "Unter A", "Tiefer"], Blocks(cut));
            Assert.Null(State.ActiveNote);
        });
        Assert.Empty(cut.FindAll(".ne-note-in-range, .ne-note-range-frame, .ne-note-bar, .ne-note-error"));
        Assert.Contains("Andere", string.Join("|", SectionTexts()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Adopt_OwnOnly_SubPointsStayVisibleAndNotGrey(bool hideUsed)
    {
        var session = await OpenAsync();
        var cut = Render<NotesPane>();
        if (hideUsed)
        {
            cut.Find(".ne-hide-used").Change(true);
        }

        List<string> Grey() => [.. cut.FindAll(".ne-note-used-text").Select(e => e.TextContent.Trim())];

        // A sub-point alone: it is used (grey), its own sub-point stays unused and shown; with "Verwendete ausblenden"
        // the used block stays as the head of its shown sub-point.
        cut.ClickBlock("Unter A");
        cut.Find(".ne-note-range-down").Click();
        cut.Find(".ne-note-adopt-after").Click();

        cut.WaitForAssertion(() => Assert.Equal(["Unter A"], Grey()));
        Assert.Equal(["Haupt", "Unter A", "Tiefer", "Unter B", "Andere"], Blocks(cut));

        // The main point alone: its card stays with its sub-points; only the main point is used.
        cut.ClickBlock("Haupt");
        cut.Find(".ne-note-range-down").Click();
        cut.Find(".ne-note-adopt-after").Click();

        cut.WaitForAssertion(() => Assert.Equal(["Haupt", "Unter A"], Grey()));
        Assert.Equal(["Haupt", "Unter A", "Tiefer", "Unter B", "Andere"], Blocks(cut));
        Assert.False(Note(session, "Unter B").IsUsed);
        Assert.True(Note(session, "Haupt").IsUsed);

        // The search's index knows it too: the sub-point is found with "Verwendete ausblenden".
        var hits = session.Search.Search(new NoteEvolution.AI.Search.SearchQuery("Unter", new NoteFilter(HideUsed: true, null, null)));
        Assert.Contains(hits, hit => hit.NoteBlockKey == Key("Unter B"));
    }

    [Fact]
    public async Task Adopt_WithSubPoints_UsesThemAsBefore()
    {
        var session = await OpenAsync();
        var cut = Render<NotesPane>();
        cut.Find(".ne-hide-used").Change(true);

        cut.ClickBlock("Unter A");
        cut.Find(".ne-note-adopt-after").Click();

        cut.WaitForAssertion(() => Assert.Equal(["Haupt", "Unter B", "Andere"], Blocks(cut)));
        Assert.True(Note(session, "Tiefer").IsUsed);
        Assert.DoesNotContain("used-without-children", _vault.Read(JournalPath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HideUsed_AdoptWithSubPoints_LeavesOutTheHiddenUsedOnes(bool hideUsed)
    {
        var session = await OpenAsync();
        var cut = Render<NotesPane>();
        cut.Find(".ne-hide-used").Change(hideUsed);
        int InBook(string text) => _vault.Read(BookPath).Split($"- {text}\n").Length - 1;

        // "Unter A" goes into the book with its sub-point first.
        cut.ClickBlock("Unter A");
        cut.Find(".ne-note-adopt-after").Click();
        cut.WaitForAssertion(() => Assert.Equal(1, InBook("Unter A")));

        // The main point: with "Verwendete ausblenden" the frame and the book take the shown blocks only.
        cut.ClickBlock("Haupt");
        Assert.Equal(hideUsed ? ["Haupt", "Unter B"] : ["Haupt", "Unter A", "Tiefer", "Unter B"], InRange(cut));
        Assert.Equal(hideUsed ? [Key("Haupt"), Key("Unter B")] : [Key("Haupt"), Key("Unter A"), Key("Tiefer"), Key("Unter B")],
            State.ActiveNoteRange!.Keys);
        cut.Find(".ne-note-adopt-after").Click();

        cut.WaitForAssertion(() => Assert.Equal(1, InBook("Haupt")));
        Assert.Equal(hideUsed ? 1 : 2, InBook("Unter A"));
        Assert.Equal(hideUsed ? 1 : 2, InBook("Tiefer"));
        Assert.Equal(1, InBook("Unter B"));
        Assert.True(Note(session, "Unter B").IsUsed);
    }

    [Fact]
    public async Task HideUsed_AllSubPointsHidden_NoHeadingVariantsAndNoLevelMinus1()
    {
        await OpenAsync();
        var cut = Render<NotesPane>();
        cut.Find(".ne-hide-used").Change(true);
        foreach (var block in new[] { "Unter A", "Unter B" })
        {
            cut.ClickBlock(block);
            cut.Find(".ne-note-adopt-after").Click();
            cut.WaitForAssertion(() => Assert.DoesNotContain(block, Blocks(cut)));
        }

        cut.ClickBlock("Haupt");

        Assert.Equal(["Haupt"], InRange(cut));
        Assert.Empty(cut.FindAll(".ne-note-adopt-heading, .ne-note-adopt-subheading"));
        Assert.True(cut.Find(".ne-note-range-down").HasAttribute("disabled"));

        // With the used ones shown again, the sub-points count again.
        cut.Find(".ne-hide-used").Change(false);
        cut.ClickBlock("Haupt");

        Assert.Equal(2, cut.FindAll(".ne-note-adopt-heading, .ne-note-adopt-subheading").Count);
        Assert.False(cut.Find(".ne-note-range-down").HasAttribute("disabled"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HideUsed_AdoptThenUndo_TheNoteIsShownAndFoundAgain(bool fromHeader)
    {
        var session = await OpenAsync();
        var cut = Render<NotesPane>();
        var toast = Render<UndoToast>();
        var header = Render<HeaderBar>();
        cut.Find(".ne-hide-used").Change(true);
        List<Guid> Found() =>
            [.. session.Search.Search(new NoteEvolution.AI.Search.SearchQuery("Andere", new NoteFilter(HideUsed: true, null, null)))
                .Select(hit => hit.NoteBlockKey)];
        var key = Key("Andere");
        Assert.Contains(key, Found());

        cut.ClickBlock("Andere");
        cut.Find(".ne-note-adopt-after").Click();
        cut.WaitForAssertion(() => Assert.DoesNotContain("Andere", Blocks(cut)));
        Assert.DoesNotContain(key, Found());

        if (fromHeader)
        {
            header.Find(".ne-undo").Click();
        }
        else
        {
            toast.WaitForAssertion(() => Assert.Single(toast.FindAll(".ne-toast")));
            toast.Find(".ne-toast-undo").Click();
        }

        cut.WaitForAssertion(() => Assert.Contains("Andere", Blocks(cut)));
        Assert.DoesNotContain("used-in::", _vault.Read(JournalPath));
        Assert.Contains(key, Found());
    }

    [Fact]
    public async Task SameNoteTwice_OnlyTheClickedCardIsFramed()
    {
        await OpenAsync();
        State.FocusedNoteKey = Key("Haupt");
        var cut = Render<NotesPane>();
        IElement Focused() => cut.Find(".ne-note-focused");
        IElement InJournal() => cut.Find(".ne-journal-day");

        // Clicked in the journal: only the journal's card has the frame and the bar.
        BlockRow(InJournal(), "Unter A").Click();

        Assert.Empty(Focused().QuerySelectorAll(".ne-note-in-range, .ne-note-bar"));
        Assert.Single(InJournal().QuerySelectorAll(".ne-note-bar"));
        Assert.Equal(2, InJournal().QuerySelectorAll(".ne-note-in-range").Length);

        // Closing the card on top keeps it.
        cut.Find(".ne-note-focused-close").Click();
        await Task.Delay(50);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-note-focused")));
        Assert.Equal(Key("Unter A"), State.ActiveNote?.BlockKey);
        Assert.Single(cut.FindAll(".ne-note-bar"));

        // Clicked on top: only that card; closing it lets the selection go.
        await cut.InvokeAsync(() =>
        {
            State.FocusedNoteKey = Key("Haupt");
            State.Notify();
        });
        BlockRow(Focused(), "Unter A").Click();

        Assert.True(State.ActiveNote!.InFocusedCard);
        Assert.Single(Focused().QuerySelectorAll(".ne-note-bar"));
        Assert.Empty(InJournal().QuerySelectorAll(".ne-note-in-range, .ne-note-bar"));

        cut.Find(".ne-note-focused-close").Click();

        cut.WaitForAssertion(() => Assert.Null(State.ActiveNote));
        Assert.Empty(cut.FindAll(".ne-note-in-range, .ne-note-bar"));
    }

    [Fact]
    public async Task DayMarks_InBar_SameAsOnCard()
    {
        await OpenAsync();
        var cut = Render<NotesPane>();
        cut.ClickBlock("Tiefer");
        IElement CardLater() => cut.Find(".ne-journal-day-head .ne-day-later");
        IElement CardNotRelevant() => cut.Find(".ne-journal-day-head .ne-day-not-relevant");

        // The same labels and tooltips as at the day.
        Assert.Equal(
            cut.FindAll(".ne-journal-day-head .ne-day-mark").Select(l => (l.TextContent.Trim(), l.GetAttribute("title"))),
            cut.FindAll(".ne-note-bar .ne-day-mark").Select(l => (l.TextContent.Trim(), l.GetAttribute("title"))));

        cut.Find(".ne-note-bar-later").Change(true);

        Assert.Equal("later:: true\n\n" + JournalText, _vault.Read(JournalPath));
        Assert.True(CardLater().HasAttribute("checked"));

        // Set at the day, it shows in the bar.
        CardNotRelevant().Change(true);

        Assert.Equal("later:: true\nnot-relevant-for:: [[Buch - Alpha]]\n\n" + JournalText, _vault.Read(JournalPath));
        Assert.True(cut.Find(".ne-note-bar-not-relevant").HasAttribute("checked"));

        cut.Find(".ne-note-bar-not-relevant").Change(false);
        cut.Find(".ne-note-bar-later").Change(false);

        Assert.Equal(JournalText, _vault.Read(JournalPath));
        Assert.False(CardLater().HasAttribute("checked"));
        Assert.False(CardNotRelevant().HasAttribute("checked"));
        Assert.Empty(cut.FindAll(".ne-note-bar .ne-note-message"));
    }

    [Fact]
    public async Task DayMarks_NotForANoteOfAPage()
    {
        await OpenAsync();
        State.FocusedNoteKey = Key("Seitennotiz");
        var cut = Render<NotesPane>();

        cut.ClickBlock("Seitennotiz");

        Assert.Single(cut.FindAll(".ne-note-focused .ne-note-bar"));
        Assert.Empty(cut.FindAll(".ne-note-bar .ne-day-mark"));
    }

    [Fact]
    public async Task Logseq_InBar_OpensThePage()
    {
        await OpenAsync();
        State.FocusedNoteKey = Key("Seitennotiz");
        var cut = Render<NotesPane>();
        var graph = $"logseq://graph/{Uri.EscapeDataString(Path.GetFileName(_vault.Root))}";

        // A block with an id opens as that block; a journal day without ids by its page.
        cut.ClickBlock("Seitennotiz");
        cut.Find(".ne-note-logseq").Click();
        cut.ClickBlock("Unter B");
        cut.Find(".ne-note-logseq").Click();

        var page = State.Session!.Notes.Get(Key("Haupt"))!.Page;
        Assert.Equal([$"{graph}?block-id={PageNoteId}", $"{graph}?page={Uri.EscapeDataString(page.Name)}"], Platform.OpenedUris);
    }

    [Fact]
    public async Task Blocked_TooltipNamesReason()
    {
        var session = await OpenAsync();
        var book = State.CurrentBook!;
        State.CurrentBook = null;
        var cut = Render<NotesPane>();
        cut.ClickBlock("Haupt");

        // Without a book nothing can be adopted; the range still moves.
        var adopt = cut.FindAll("[class^='ne-note-adopt-']");
        Assert.Equal(5, adopt.Count);
        Assert.All(adopt, b =>
        {
            Assert.True(b.HasAttribute("disabled"));
            Assert.Equal(Text("NoteBarNoBook"), b.GetAttribute("title"));
        });
        Assert.False(cut.Find(".ne-note-range-down").HasAttribute("disabled"));
        Assert.Equal(Text("JournalDayNotRelevantNoBook"), cut.Find(".ne-note-bar-not-relevant").ParentElement!.GetAttribute("title"));

        // An open conflict of the book.
        await cut.InvokeAsync(() =>
        {
            State.CurrentBook = book;
            book.Root.Children.First().TextBlocks.First().Block.SetContent("Lokal geändert");
            File.WriteAllText(Path.Combine(_vault.Root, BookPath), "title:: Alpha\ntype:: book\n\n- # Eins\n\t- Extern geändert\n");
            session.HandleExternalChange(book.Page.FilePath);
            State.Notify();
        });
        Assert.True(session.HasOpenConflict(book.Page.FilePath));

        cut.WaitForAssertion(() => Assert.All(cut.FindAll("[class^='ne-note-adopt-']"), b =>
        {
            Assert.True(b.HasAttribute("disabled"));
            Assert.Equal(Text("NoteBarConflict"), b.GetAttribute("title"));
        }));
    }

    [Fact]
    public async Task Blocked_ReadOnlyNote_TooltipNamesReason()
    {
        using var broken = TestVault.Create(
            (BookPath, BookText),
            ("pages/Kaputt.md", "- Gedächtnis kaputt\n\t- unten\n  ```\n  offener Codeblock\n"));
        var session = await OpenSessionAsync(broken);
        Assert.True(session.Vault.Pages.Single(p => p.Name == "Kaputt").IsReadOnly);
        var cut = Render<NoteCard>(p => p.Add(c => c.Note, session.Notes.All().First(n => n.Page.IsReadOnly)));

        cut.Find(".ne-note-text").Click();

        Assert.All(cut.FindAll("[class^='ne-note-adopt-']"), b =>
        {
            Assert.True(b.HasAttribute("disabled"));
            Assert.Equal(Text("NoteBarNoteReadOnly"), b.GetAttribute("title"));
        });
        Assert.NotEmpty(cut.FindAll(".ne-note-hint"));
    }

    [Fact]
    public async Task Reload_SelectionSurvivesWhileTheBlockExists_ClearedWhenItIsGone()
    {
        var session = await OpenAsync();
        var cut = Render<NotesPane>();
        cut.ClickBlock("Unter A");
        var selection = State.ActiveNote;
        var path = Path.Combine(_vault.Root, JournalPath);

        // Another program adds a block to the day: the page is read again, its blocks keep their keys.
        File.WriteAllText(path, JournalText + "- Neu von außen\n");
        await cut.InvokeAsync(() => session.HandleExternalChange(path));

        cut.WaitForAssertion(() => Assert.Contains("Neu von außen", Blocks(cut)));
        Assert.Equal(selection, State.ActiveNote);
        Assert.Equal(["Unter A", "Tiefer"], InRange(cut));
        Assert.Single(cut.FindAll(".ne-note-bar"));

        // Now the block goes: the frame and the bar go with it.
        File.WriteAllText(path, "- Haupt\n\t- Unter B\n- Andere\n");
        await cut.InvokeAsync(() => session.HandleExternalChange(path));

        cut.WaitForAssertion(() => Assert.DoesNotContain("Unter A", Blocks(cut)));
        Assert.Empty(cut.FindAll(".ne-note-in-range, .ne-note-range-frame, .ne-note-bar"));
        Assert.Null(State.ActiveNote);
    }

    [Fact]
    public async Task TabSwitch_ClearsTheSelection()
    {
        await OpenAsync();
        var cut = Render<NotesPane>();
        cut.ClickBlock("Haupt");

        cut.Find(".ne-tab-journal").Click();
        Assert.NotNull(State.ActiveNote);

        cut.Find(".ne-tab-search").Click();
        Assert.Null(State.ActiveNote);
        cut.Find(".ne-tab-journal").Click();
        Assert.Empty(cut.FindAll(".ne-note-in-range, .ne-note-bar"));
    }
}
