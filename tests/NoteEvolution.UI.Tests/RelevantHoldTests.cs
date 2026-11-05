using Bunit;
using Microsoft.Extensions.Logging.Abstractions;
using NoteEvolution.AI.Tests;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Links;
using NoteEvolution.Core.Vaults;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>The Relevant list stays as it is while the user works in the area of a text adopted from it.</summary>
public class RelevantHoldTests : UiTestContext
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(1500);

    private const string Sleep = "Schlaf Träume Nummer";
    private const string Music = "Musik Gitarre Klavier Nummer";
    private const string Soup = "Suppe Gemüse kochen Nummer";

    private readonly List<TestVault> _vaults = [];

    protected override void Dispose(bool disposing)
    {
        // The session (and its vector cache in the vault) is closed first, then the vault folders are deleted.
        base.Dispose(disposing);
        if (disposing)
        {
            _vaults.ForEach(v => v.Dispose());
        }
    }

    /// <summary>"Schlafen" with the sub-section "Musizieren", then "Kochen"; notes about each, a prologue before them.</summary>
    private TestVault Vault()
    {
        var notes = string.Concat(
            Enumerable.Range(1, 15).Select(i => $"- {Sleep} {i}\n")
            .Concat(Enumerable.Range(1, 15).Select(i => $"- {Music} {i}\n"))
            .Concat(Enumerable.Range(1, 10).Select(i => $"- {Soup} {i}\n")));
        var vault = TestVault.Create(
            ("pages/Buch - Alpha.md",
                "title:: Alpha\ntype:: book\n\n" +
                "- Vorwort des Buchs\n" +
                "- # Schlafen\n\t- Schlaf und Träume im Alltag\n" +
                "\t- ## Musizieren\n\t\t- Musik Gitarre Klavier spielen\n" +
                "- # Kochen\n\t- Suppe kochen mit Gemüse\n"),
            ("pages/Ideen.md", notes));
        _vaults.Add(vault);
        return vault;
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var until = DateTime.UtcNow + RealTimeWait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "The condition was not met in time.");
            await Task.Delay(10);
        }
    }

    private async Task<VaultSession> OpenAsync()
    {
        var session = await OpenSessionAsync(Vault(), UseAi(installed: true, () => new FakeEmbedder()));
        await Eventually(() => session.AiStatus.State == AiState.Ready);
        State.CurrentSectionKey = Schlafen.Key;
        State.Notify();
        return session;
    }

    private Book CurrentBook => State.CurrentBook!;

    private OutlineNode Schlafen => CurrentBook.Root.Children.ElementAt(0);

    private OutlineNode Musizieren => Schlafen.Children.Single();

    private OutlineNode Kochen => CurrentBook.Root.Children.ElementAt(1);

    private static List<string> Texts(IRenderedComponent<NotesPane> cut) =>
        [.. cut.FindAll(".ne-relevant-list .ne-note-card > .ne-note-body > .ne-note-text").Select(e => e.TextContent.Trim())];

    /// <summary>The pane on the Relevant tab with its first list about "Schlafen".</summary>
    private IRenderedComponent<NotesPane> RenderList()
    {
        var cut = Render<NotesPane>();
        cut.Find(".ne-tab-relevant").Click();
        cut.WaitForAssertion(() => Assert.Equal(30, Texts(cut).Count));
        Assert.Contains(Sleep, Texts(cut)[0]);
        return cut;
    }

    /// <summary>Clicks a way to adopt in the bar of the card at <paramref name="index"/> and waits until the note is used.</summary>
    private void Adopt(IRenderedComponent<NotesPane> cut, VaultSession session, int index, string button = ".ne-note-adopt-after")
    {
        var content = Texts(cut)[index];
        var note = session.Notes.All().Single(n => n.Block.Content == content);
        cut.FindAll(".ne-relevant-list .ne-note-card")[index].QuerySelector(".ne-note-text")!.Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.FindAll(".ne-relevant-list .ne-note-card")[index].QuerySelector(button)));
        cut.FindAll(".ne-relevant-list .ne-note-card")[index].QuerySelector(button)!.Click();
        cut.WaitForAssertion(() => Assert.True(note.IsUsed));
        cut.WaitForAssertion(() => Assert.NotNull(State.RelevantHold));
    }

    /// <summary>
    /// A jump to <paramref name="section"/> as the app does it: the state is told while the reveal is pending; the editor
    /// then takes it, and its report of the same cursor notifies nobody.
    /// </summary>
    private void MoveTo(IRenderedComponent<NotesPane> cut, OutlineNode section)
    {
        cut.InvokeAsync(() =>
        {
            State.RevealElement(section.Key);
            State.Notify();
            State.ActiveView.TakePendingReveal();
        });
    }

    /// <summary>Gives a computation the tab would start the time to show.</summary>
    private async Task LetTimePass()
    {
        for (var i = 0; i < 10; i++)
        {
            Time.Advance(Debounce);
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Adopted_MovingWithinTheArea_ListStays_LeavingIt_ComputedAtOnce()
    {
        var session = await OpenAsync();
        var cut = RenderList();
        var shown = Texts(cut);

        Adopt(cut, session, 2);
        await LetTimePass();
        Assert.Equal(shown, Texts(cut));

        // The sub-section is in the area: without the hold this would be a list about music.
        MoveTo(cut, Musizieren);
        await LetTimePass();
        Assert.Equal(shown, Texts(cut));
        MoveTo(cut, Schlafen);
        await LetTimePass();
        Assert.Equal(shown, Texts(cut));

        // Leaving is enough; the debounce is not waited for.
        MoveTo(cut, Kochen);
        cut.WaitForAssertion(() => Assert.Contains(Soup, Texts(cut)[0]));
        Assert.Null(State.RelevantHold);
    }

    [Fact]
    public async Task Adopted_WithoutHideUsed_TheNoteStaysAtItsPlace_Grey()
    {
        var session = await OpenAsync();
        var cut = RenderList();
        var shown = Texts(cut);

        Adopt(cut, session, 2);

        cut.WaitForAssertion(() => Assert.NotNull(cut.FindAll(".ne-relevant-list .ne-note-card")[2].QuerySelector(".ne-note-used")));
        Assert.Contains("ne-note-used-text", cut.FindAll(".ne-relevant-list .ne-note-card")[2].QuerySelector(".ne-note-text")!.ClassName);
        Assert.Equal(shown, Texts(cut));
    }

    [Fact]
    public async Task Adopted_WithHideUsed_OnlyTheNoteLeaves_TheOthersKeepTheirOrder()
    {
        var session = await OpenAsync();
        var cut = RenderList();
        cut.Find(".ne-hide-used").Change(true);
        Time.Advance(Debounce);
        await LetTimePass();
        var shown = Texts(cut);
        var adopted = shown[2];

        Adopt(cut, session, 2);
        await LetTimePass();

        Assert.Equal(shown.Where(text => text != adopted), Texts(cut));
    }

    [Fact]
    public async Task AdoptionUndone_TheNoteIsBack_ListComputedAtOnce()
    {
        var session = await OpenAsync();
        var cut = RenderList();
        cut.Find(".ne-hide-used").Change(true);
        await LetTimePass();
        var adopted = Texts(cut)[2];
        Adopt(cut, session, 2);
        cut.WaitForAssertion(() => Assert.DoesNotContain(adopted, Texts(cut)));

        // Whichever way it is undone: the usage goes and the notes pane is told.
        await cut.InvokeAsync(() =>
        {
            Assert.True(session.TryUndo(out _));
            State.RefreshBook();
            State.NotifyNotesChanged();
            State.Notify();
        });

        cut.WaitForAssertion(() => Assert.Contains(adopted, Texts(cut)));
        Assert.Null(State.RelevantHold);
    }

    [Fact]
    public async Task NotesPageReadAgainUnderNewKeys_ListComputedAtOnce()
    {
        var session = await OpenAsync();
        var cut = RenderList();
        Adopt(cut, session, 2);
        var path = Path.Combine(_vaults.Single().Root, "pages", "Ideen.md");
        File.WriteAllText(path, "- Schlaf Träume ganz neu\n");

        await cut.InvokeAsync(() => session.HandleExternalChange(path));
        Time.Advance(VaultSession.PageIndexDelay);

        AdvanceUntil(cut, () => Assert.Equal(["Schlaf Träume ganz neu"], Texts(cut)), Debounce);
        Assert.Null(State.RelevantHold);
    }

    [Fact]
    public async Task HideUsedSwitched_ListComputedAtOnce()
    {
        var session = await OpenAsync();
        var cut = RenderList();
        Adopt(cut, session, 2);
        var adopted = session.Notes.All().Single(n => n.IsUsed).Block.Content;
        Assert.Contains(adopted, Texts(cut));

        cut.Find(".ne-hide-used").Change(true);

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(30, Texts(cut).Count);
            Assert.DoesNotContain(adopted, Texts(cut));
        });
        Assert.Null(State.RelevantHold);
    }

    [Fact]
    public async Task EditorStillOnItsWay_ItsOldCursorDoesNotEndTheHold()
    {
        var session = await OpenAsync();
        var cut = RenderList();
        var shown = Texts(cut);
        Adopt(cut, session, 2);
        Assert.NotNull(State.PendingReveal);

        // The editor loads the new document and reports the cursor near its old place before it reveals the new block.
        await cut.InvokeAsync(() =>
        {
            State.CurrentSectionKey = Kochen.Key;
            State.Notify();
        });
        await LetTimePass();

        Assert.Equal(shown, Texts(cut));
        Assert.NotNull(State.RelevantHold);
    }

    [Fact]
    public async Task SecondAdoption_TheAreaIsTheNewOne()
    {
        var session = await OpenAsync();
        var cut = RenderList();
        var shown = Texts(cut);
        Adopt(cut, session, 2);

        // Dragged into another section: the area follows, the list stays.
        var note = session.Notes.All().Single(n => n.Block.Content == shown[3]);
        await cut.InvokeAsync(() => NoteAdoption.AdoptAsync(
            State, note.Key, _ => new InsertPosition.SectionEnd(Kochen.Key), NullLogger.Instance, fromRelevant: true));
        await LetTimePass();
        Assert.Equal(shown, Texts(cut));

        MoveTo(cut, Kochen);
        await LetTimePass();
        Assert.Equal(shown, Texts(cut));

        // The first area no longer counts.
        MoveTo(cut, Schlafen);
        cut.WaitForAssertion(() => Assert.Null(State.RelevantHold));
    }

    [Fact]
    public async Task AdoptedFromTheCardOnTop_NoHold()
    {
        var session = await OpenAsync();
        var cut = RenderList();
        var note = session.Notes.All().First(n => n.Block.Content.Contains(Soup, StringComparison.Ordinal));
        await cut.InvokeAsync(() =>
        {
            State.FocusedNoteKey = note.Key;
            State.Notify();
        });

        cut.WaitForAssertion(() => cut.Find(".ne-note-focused .ne-note-text").Click());
        cut.WaitForAssertion(() => cut.Find(".ne-note-focused .ne-note-adopt-after").Click());

        cut.WaitForAssertion(() => Assert.True(note.IsUsed));
        Assert.Null(State.RelevantHold);
    }

    [Fact]
    public async Task AdoptedInTheSearchTab_NoHold()
    {
        var session = await OpenAsync();
        var note = session.Notes.All().First();

        // No card of the "Relevant" tab: the way every other adoption comes.
        await Render<NotesPane>().InvokeAsync(() => NoteAdoption.AdoptAsync(
            State, note.Key, AdoptVariant.After, "Ohne Titel", NullLogger.Instance));

        Assert.True(note.IsUsed);
        Assert.Null(State.RelevantHold);
    }

    [Fact]
    public async Task AnotherBook_EndsTheHold()
    {
        await OpenAsync();
        var cut = RenderList();

        await cut.InvokeAsync(() =>
        {
            State.RelevantHold = new RelevantHold("Buch - Anderes", Schlafen.Key);
            State.Notify();
        });

        cut.WaitForAssertion(() => Assert.Null(State.RelevantHold));
    }

    [Fact]
    public async Task LeavingTheTab_EndsTheHold()
    {
        var session = await OpenAsync();
        var cut = RenderList();
        Adopt(cut, session, 2);
        Assert.NotNull(State.RelevantHold);

        cut.Find(".ne-tab-journal").Click();

        cut.WaitForAssertion(() => Assert.Null(State.RelevantHold));
    }

    [Fact]
    public async Task Area_TextBlockInASection_IsThatSection_WithItsSubSections()
    {
        await OpenAsync();
        var hold = RelevantHold.Around(CurrentBook, Schlafen.TextBlocks.Single().Key)!;

        Assert.Equal(Schlafen.Key, hold.AreaKey);
        Assert.True(hold.Covers(CurrentBook, Schlafen.Key));
        Assert.True(hold.Covers(CurrentBook, Musizieren.Key));
        Assert.False(hold.Covers(CurrentBook, Kochen.Key));
        Assert.False(hold.Covers(CurrentBook, CurrentBook.Root.Key));
    }

    [Fact]
    public async Task Area_SubHeading_IsTheSectionItStandsIn()
    {
        await OpenAsync();
        var hold = RelevantHold.Around(CurrentBook, Musizieren.Key)!;

        Assert.Equal(Schlafen.Key, hold.AreaKey);
        Assert.True(hold.Covers(CurrentBook, Schlafen.Key));
    }

    [Fact]
    public async Task Area_TopLevelHeading_IsOnlyItsOwnSection()
    {
        await OpenAsync();
        var hold = RelevantHold.Around(CurrentBook, Schlafen.Key)!;

        Assert.Equal(Schlafen.Key, hold.AreaKey);
        Assert.False(hold.Covers(CurrentBook, Kochen.Key));
    }

    [Fact]
    public async Task Area_Prologue_DoesNotCoverTheWholeBook()
    {
        await OpenAsync();
        var hold = RelevantHold.Around(CurrentBook, CurrentBook.Root.TextBlocks.Single().Key)!;

        Assert.True(hold.Covers(CurrentBook, CurrentBook.Root.Key));
        Assert.False(hold.Covers(CurrentBook, Schlafen.Key));
    }

    [Fact]
    public async Task Area_GoneFromTheBook_CoversNothing()
    {
        await OpenAsync();
        var hold = new RelevantHold(CurrentBook.LinkName, Guid.NewGuid());

        Assert.False(hold.Covers(CurrentBook, Schlafen.Key));
    }
}
