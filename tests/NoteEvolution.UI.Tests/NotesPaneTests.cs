using AngleSharp.Dom;
using Bunit;
using NoteEvolution.Core.Storage;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.State;
using static NoteEvolution.UI.Tests.NotesTestData;

namespace NoteEvolution.UI.Tests;

public class NotesPaneTests : UiTestContext
{
    private async Task<VaultSession> OpenAlphaAsync(TestVault vault)
    {
        var session = await OpenSessionAsync(vault);
        State.CurrentBook = session.Vault.FindBook("Buch - Alpha");
        State.CurrentSectionKey = State.CurrentBook!.Root.Children.First().Key;
        return session;
    }

    private static List<string> Texts(IRenderedComponent<NotesPane> cut) =>
        [.. cut.FindAll(".ne-note-card > .ne-note-body > .ne-note-text").Select(e => e.TextContent.Trim())];

    private static IElement CardOf(IRenderedComponent<NotesPane> cut, string text) =>
        cut.FindAll(".ne-note-card").Single(c => c.QuerySelector(".ne-note-text")!.TextContent.Trim() == text);

    private static void ShowJournal(IRenderedComponent<NotesPane> cut) => cut.Find(".ne-tab-journal").Click();

    private void Search(IRenderedComponent<NotesPane> cut, string text)
    {
        cut.Find(".ne-search-input").Input(text);
        Time.Advance(TimeSpan.FromMilliseconds(300));
        cut.WaitForState(() => cut.FindAll(".ne-note-card").Count > 0 || cut.FindAll(".ne-search-empty").Count > 0);
    }

    [Fact]
    public void NotesPane_NoVault_ShowsPlaceholder()
    {
        var cut = Render<NotesPane>();

        Assert.NotEmpty(cut.FindAll(".ne-placeholder"));
        Assert.Empty(cut.FindAll(".ne-tab"));
    }

    [Fact]
    public async Task NotesPane_Journal_ListsEntriesNewestDayFirst()
    {
        using var tv = Create();
        await OpenAlphaAsync(tv);
        var cut = Render<NotesPane>();

        ShowJournal(cut);

        Assert.Equal(
            ["Gedächtnis im Alltag beobachten", "Eine ganz andere Idee", "Gedächtnis und Musik", "Septemberidee"],
            Texts(cut));
        Assert.Equal(["01.10.2026", "01.10.2026", "15.09.2026", "15.09.2026"], cut.FindAll(".ne-note-origin").Select(e => e.TextContent));
        Assert.Empty(cut.FindAll(".ne-note-score"));
    }

    [Fact]
    public async Task NotesPane_Search_RunsOnlyAfter300MsWithoutInput()
    {
        using var tv = Create();
        await OpenAlphaAsync(tv);
        var cut = Render<NotesPane>();

        cut.Find(".ne-search-input").Input("Schl");
        Time.Advance(TimeSpan.FromMilliseconds(200));
        cut.Find(".ne-search-input").Input("Schlaf");
        Time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Empty(cut.FindAll(".ne-note-card"));

        Time.Advance(TimeSpan.FromMilliseconds(100));

        cut.WaitForAssertion(() => Assert.Equal(["Gedächtnis braucht Schlaf", "Unterpunkt Träume"], Texts(cut).Order().ToList()));
    }

    [Fact]
    public async Task NotesPane_Search_ScoresAreNormalizedToBestHit()
    {
        using var tv = Create();
        await OpenAlphaAsync(tv);
        var cut = Render<NotesPane>();

        Search(cut, "Gedächtnis");

        var scores = cut.FindAll(".ne-note-score").Select(e => e.TextContent).ToList();
        Assert.Equal(5, scores.Count);
        Assert.Equal("100 %", scores[0]);
        Assert.All(scores, s => Assert.Matches(@"^\d{1,3} %$", s));
    }

    [Fact]
    public async Task NotesPane_Search_NoHits_ShowsEmptyHint()
    {
        using var tv = Create();
        await OpenAlphaAsync(tv);
        var cut = Render<NotesPane>();

        Search(cut, "Nichtvorhanden");

        Assert.Empty(cut.FindAll(".ne-note-card"));
        Assert.NotEmpty(cut.FindAll(".ne-search-empty"));
    }

    [Fact]
    public async Task NotesPane_HideUsedFiltersBothTabs()
    {
        using var tv = Create();
        await OpenAlphaAsync(tv);
        var cut = Render<NotesPane>();
        Search(cut, "Gedächtnis");
        Assert.Equal(5, cut.FindAll(".ne-note-card").Count);

        cut.Find(".ne-hide-used").Change(true);

        cut.WaitForAssertion(() => Assert.Equal(
            ["Gedächtnis braucht Schlaf", "Gedächtnis im Alltag beobachten", "Unterpunkt Träume"], Texts(cut).Order().ToList()));
        Assert.True(Settings.HideUsed);
        Assert.True(UiSettings.Load(Platform.UserDataDirectory).HideUsed);

        ShowJournal(cut);

        Assert.Equal(["Gedächtnis im Alltag beobachten", "Eine ganz andere Idee", "Septemberidee"], Texts(cut));
    }

    [Fact]
    public async Task NotesPane_DateRange_FiltersBothTabs()
    {
        using var tv = Create();
        await OpenAlphaAsync(tv);
        var cut = Render<NotesPane>();

        cut.Find(".ne-filter-from").Change("2026-10-01");
        ShowJournal(cut);
        Assert.Equal(["Gedächtnis im Alltag beobachten", "Eine ganz andere Idee"], Texts(cut));

        cut.Find(".ne-filter-from").Change("");
        cut.Find(".ne-filter-to").Change("2026-09-30");
        Assert.Equal(["Gedächtnis und Musik", "Septemberidee"], Texts(cut));

        cut.Find(".ne-tab-search").Click();
        Search(cut, "Gedächtnis");
        Assert.Equal(["Gedächtnis und Musik"], Texts(cut));
    }

    [Fact]
    public async Task NotesPane_JournalDateJump_StartsAtTheChosenDay()
    {
        using var tv = Create();
        await OpenAlphaAsync(tv);
        var cut = Render<NotesPane>();
        ShowJournal(cut);

        cut.Find(".ne-journal-jump").Change("2026-09-30");

        Assert.Equal(["Gedächtnis und Musik", "Septemberidee"], Texts(cut));
    }

    [Fact]
    public async Task NotesPane_NoCursor_AdoptsAtSectionEnd()
    {
        using var tv = Create();
        await OpenAlphaAsync(tv);
        var cut = Render<NotesPane>();
        Search(cut, "Schlaf");

        CardOf(cut, "Gedächtnis braucht Schlaf").QuerySelector(".ne-note-adopt")!.Click();

        var eins = State.CurrentBook!.Root.Children.First();
        Assert.Equal(["Erster Text", "Zweiter Text", "Gedächtnis braucht Schlaf"], eins.TextBlocks.Select(t => t.Text));
        Assert.Equal(["Dritter Text"], eins.Children.Single().TextBlocks.Select(t => t.Text));
        Assert.Contains("used-in:: [[Buch - Alpha]]", tv.Read(NotesPath));
    }

    [Fact]
    public async Task NotesPane_AdoptFromJournal_UsesCursorPosition()
    {
        using var tv = Create();
        await OpenAlphaAsync(tv);
        SetCursorInTextBlock(State.CurrentBook!.Root.Children.First().TextBlocks.First().Key);
        var cut = Render<NotesPane>();
        ShowJournal(cut);

        cut.FindAll(".ne-note-adopt")[1].Click();

        Assert.Equal(
            ["Erster Text", "Eine ganz andere Idee", "Zweiter Text"],
            State.CurrentBook!.Root.Children.First().TextBlocks.Select(t => t.Text));
        Assert.Contains("used-in:: [[Buch - Alpha]]", tv.Read("journals/2026_10_01.md"));
    }

    [Fact]
    public async Task NotesPane_FocusedNote_ShownAboveTheListUntilClosed()
    {
        using var tv = Create();
        var session = await OpenAlphaAsync(tv);
        State.FocusedNoteKey = Note(session, "Gedächtnis braucht").Key;
        var cut = Render<NotesPane>();
        ShowJournal(cut);

        var focused = cut.Find(".ne-note-focused .ne-note-card");
        Assert.Contains("Gedächtnis braucht Schlaf", focused.TextContent);
        Assert.True(cut.Find(".ne-note-focused").CompareDocumentPosition(cut.Find(".ne-journal-list")).HasFlag(DocumentPositions.Following));

        cut.Find(".ne-note-focused-close").Click();

        Assert.Null(State.FocusedNoteKey);
        Assert.Empty(cut.FindAll(".ne-note-focused"));
    }

    [Fact]
    public async Task NotesPane_ExternalPageChange_RefreshesTheLists()
    {
        using var tv = Create();
        var session = await OpenAlphaAsync(tv);
        var cut = Render<NotesPane>();
        ShowJournal(cut);
        var path = Path.Combine(tv.Root, "journals", "2026_10_01.md");
        File.WriteAllText(path, "- Neuer externer Eintrag\n");

        session.HandleExternalChange(path);

        cut.WaitForAssertion(() => Assert.Contains("Neuer externer Eintrag", Texts(cut)));
        Assert.DoesNotContain("Eine ganz andere Idee", Texts(cut));
    }
}
