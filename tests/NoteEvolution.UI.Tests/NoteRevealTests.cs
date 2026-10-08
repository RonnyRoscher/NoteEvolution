using Bunit;
using NoteEvolution.Core.Storage;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>Jumping from a source (or a chip) to the note in the notes pane.</summary>
public class NoteRevealTests : UiTestContext
{
    private const string TodayId = "11111111-aaaa-4aaa-8aaa-111111111111";
    private const string TodayChildId = "11111111-aaaa-4aaa-8aaa-222222222222";
    private const string SeptemberId = "22222222-bbbb-4bbb-8bbb-111111111111";
    private const string PageNoteId = "33333333-cccc-4ccc-8ccc-111111111111";

    private static TestVault Create() => TestVault.Create(
        ("pages/Buch - Alpha.md",
            "title:: Alpha\ntype:: book\n\n" +
            "- # Eins\n" +
            "\t- Erster Text\n" +
            "\t  source:: ((" + SeptemberId + "))\n"),
        ("pages/Ideen.md",
            "- Seitennotiz ohne Datum\n" +
            "  id:: " + PageNoteId + "\n"),
        ("journals/2026_10_01.md",
            "- Heutiger Eintrag\n" +
            "  id:: " + TodayId + "\n" +
            "\t- Unterpunkt heute\n" +
            "\t  id:: " + TodayChildId + "\n"),
        ("journals/2026_09_15.md",
            "- Septemberidee\n" +
            "  id:: " + SeptemberId + "\n"));

    private async Task<IRenderedComponent<NotesPane>> RenderPaneAsync(TestVault vault)
    {
        await OpenSessionAsync(vault);
        return Render<NotesPane>();
    }

    private Task RevealAsync(IRenderedComponent<NotesPane> cut, string id) =>
        cut.InvokeAsync(() => State.RevealNote(Guid.Parse(id)));

    private static List<string> Highlighted(IRenderedComponent<NotesPane> cut) =>
        [.. cut.FindAll(".ne-note-card.ne-note-highlight .ne-note-text").Select(e => e.TextContent.Trim())];

    [Fact]
    public async Task RevealNote_JournalNote_SwitchesTab_JumpsToDate_Highlights()
    {
        using var tv = Create();
        var cut = await RenderPaneAsync(tv);
        Assert.Empty(cut.FindAll(".ne-journal"));

        await RevealAsync(cut, SeptemberId);

        Assert.NotEmpty(cut.FindAll(".ne-tab-journal.active"));
        Assert.Equal("2026-09-15", cut.Find(".ne-journal-jump").GetAttribute("value"));
        Assert.Equal(["Septemberidee"], Highlighted(cut));
        Assert.Empty(cut.FindAll(".ne-note-focused"));
        Assert.Null(State.FocusedNoteKey);
        Assert.Equal(1, JSInterop.Invocations.Count(i => i.Identifier == "Blazor._internal.domWrapper.focus"));
    }

    [Fact]
    public async Task RevealNote_SubBulletSource_HighlightsRootNote()
    {
        using var tv = Create();
        var cut = await RenderPaneAsync(tv);

        await RevealAsync(cut, TodayChildId);

        Assert.Equal(["Heutiger Eintrag"], Highlighted(cut));
        Assert.Equal("2026-10-01", cut.Find(".ne-journal-jump").GetAttribute("value"));
        Assert.Empty(cut.FindAll(".ne-note-focused"));
    }

    [Fact]
    public async Task RevealNote_HiddenByFilter_ShowsFocusedCard()
    {
        using var tv = Create();
        var cut = await RenderPaneAsync(tv);
        cut.Find(".ne-filter-from").Change("2026-10-01");

        await RevealAsync(cut, SeptemberId);

        Assert.Contains("Septemberidee", cut.Find(".ne-note-focused .ne-note-card").TextContent);
        Assert.Empty(cut.FindAll(".ne-journal"));
        Assert.Empty(cut.FindAll(".ne-note-highlight"));
    }

    [Fact]
    public async Task RevealNote_NoteWithoutDate_ShowsFocusedCard()
    {
        using var tv = Create();
        var cut = await RenderPaneAsync(tv);

        await RevealAsync(cut, PageNoteId);

        Assert.Contains("Seitennotiz ohne Datum", cut.Find(".ne-note-focused .ne-note-card").TextContent);
        Assert.Empty(cut.FindAll(".ne-journal"));
        Assert.Equal(State.Session!.Vault.FindBlockById(Guid.Parse(PageNoteId))!.Value.Block.Key, State.FocusedNoteKey);
    }

    [Fact]
    public async Task Highlight_RemovedAfter2s()
    {
        using var tv = Create();
        var cut = await RenderPaneAsync(tv);
        await RevealAsync(cut, SeptemberId);

        Time.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.Equal(["Septemberidee"], Highlighted(cut));

        Time.Advance(TimeSpan.FromMilliseconds(1));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-note-highlight")));
        Assert.Single(cut.FindAll(".ne-note-card"));
    }

    [Fact]
    public async Task Highlight_SecondReveal_RestartsTheTimer()
    {
        using var tv = Create();
        var cut = await RenderPaneAsync(tv);
        await RevealAsync(cut, SeptemberId);
        Time.Advance(TimeSpan.FromSeconds(1.5));

        await RevealAsync(cut, SeptemberId);
        Time.Advance(TimeSpan.FromSeconds(1.5));

        Assert.Equal(["Septemberidee"], Highlighted(cut));

        Time.Advance(TimeSpan.FromMilliseconds(500));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-note-highlight")));
    }

    [Fact]
    public async Task ChipClick_RevealsNote()
    {
        using var tv = Create();
        var session = await OpenSessionAsync(tv);
        State.CurrentBook = session.Vault.FindBook("Buch - Alpha");
        State.CurrentSectionKey = State.CurrentBook!.Root.Children.First().Key;
        var editor = Render<EditorPane>();
        editor.WaitForAssertion(() => Assert.NotEmpty(Editor.Documents));
        var notes = Render<NotesPane>();

        await editor.InvokeAsync(() => Editor.Callbacks!.OnChipClicked(Guid.Parse(SeptemberId)));

        Assert.Equal(["Septemberidee"], Highlighted(notes));
        Assert.Equal("2026-09-15", notes.Find(".ne-journal-jump").GetAttribute("value"));
    }
}
