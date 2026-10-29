using Microsoft.Extensions.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Links;
using NoteEvolution.UI.Editor;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>The active note block in <see cref="AppState"/>, its range levels, and adopting a block without its sub-points.</summary>
public class NoteSelectionTests : UiTestContext
{
    private const string BookPath = "pages/Buch - Alpha.md";

    private const string NotePath = "journals/2026_03_01.md";

    private const string BookText = "title:: Alpha\ntype:: book\n\n- # Eins\n\t- Erster Text\n";

    private const string NoteText =
        "- Haupt\n" +
        "\t- Unter A\n" +
        "\t\t- Tiefer\n" +
        "\t- Unter B\n" +
        "- Andere\n";

    private readonly TestVault _vault = TestVault.Create((BookPath, BookText), (NotePath, NoteText));

    private readonly List<TestVault> _otherVaults = [];

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _vault.Dispose();
            _otherVaults.ForEach(v => v.Dispose());
        }
    }

    private Guid NoteKey(string content) => State.Session!.Notes.All().Single(n => n.Block.Content == content).Key;

    [Fact]
    public async Task Select_SetsLevel0_AndNotifies()
    {
        await OpenSessionAsync(_vault);
        var notified = 0;
        State.Changed += () => notified++;

        State.SelectNoteBlock(NoteKey("Haupt"), NoteKey("Unter A"));

        Assert.Equal(new NoteSelection(NoteKey("Haupt"), NoteKey("Unter A"), 0), State.ActiveNote);
        Assert.Equal([NoteKey("Unter A"), NoteKey("Tiefer")], State.ActiveNoteRange!.Keys);
        Assert.Equal(1, notified);
    }

    [Fact]
    public async Task Select_WithoutSelection_HasNoRange()
    {
        await OpenSessionAsync(_vault);

        Assert.Null(State.ActiveNote);
        Assert.Null(State.ActiveNoteRange);
    }

    [Fact]
    public async Task ChangeRange_IsBoundedByTheLevelBounds_AndNotifiesOnChange()
    {
        await OpenSessionAsync(_vault);
        State.SelectNoteBlock(NoteKey("Haupt"), NoteKey("Tiefer"));
        var notified = 0;
        State.Changed += () => notified++;

        State.ChangeNoteRange(-1);
        Assert.Equal(-1, State.ActiveNote!.Level);
        Assert.True(State.ActiveNoteRange!.OwnOnly);

        State.ChangeNoteRange(-1);
        Assert.Equal(-1, State.ActiveNote.Level);
        Assert.Equal(1, notified);

        State.ChangeNoteRange(10);
        Assert.Equal(2, State.ActiveNote.Level);
        Assert.Equal(NoteKey("Haupt"), State.ActiveNoteRange!.HeadKey);
        Assert.Equal(
            [NoteKey("Haupt"), NoteKey("Unter A"), NoteKey("Tiefer"), NoteKey("Unter B")], State.ActiveNoteRange.Keys);

        State.ChangeNoteRange(1);
        Assert.Equal(2, State.ActiveNote.Level);
        Assert.Equal(2, notified);
    }

    [Fact]
    public async Task ChangeRange_WithoutSelection_DoesNothing()
    {
        await OpenSessionAsync(_vault);
        var notified = 0;
        State.Changed += () => notified++;

        State.ChangeNoteRange(1);

        Assert.Null(State.ActiveNote);
        Assert.Equal(0, notified);
    }

    [Fact]
    public async Task Clear_RemovesTheSelection_AndNotifiesOnce()
    {
        await OpenSessionAsync(_vault);
        State.SelectNoteBlock(NoteKey("Haupt"), NoteKey("Haupt"));
        var notified = 0;
        State.Changed += () => notified++;

        State.ClearNoteSelection();
        State.ClearNoteSelection();

        Assert.Null(State.ActiveNote);
        Assert.Equal(1, notified);
    }

    [Fact]
    public async Task Select_BlockOutsideTheNote_ClearsTheSelection()
    {
        await OpenSessionAsync(_vault);
        State.SelectNoteBlock(NoteKey("Haupt"), NoteKey("Haupt"));

        State.SelectNoteBlock(NoteKey("Haupt"), NoteKey("Andere"));

        Assert.Null(State.ActiveNote);
    }

    [Fact]
    public async Task VaultChange_ClearsTheSelection()
    {
        await OpenSessionAsync(_vault);
        State.SelectNoteBlock(NoteKey("Haupt"), NoteKey("Unter A"));

        var other = TestVault.Create((BookPath, BookText), (NotePath, NoteText));
        _otherVaults.Add(other);
        await OpenSessionAsync(other);

        Assert.Null(State.ActiveNote);
        Assert.Null(State.ActiveNoteRange);
    }

    [Fact]
    public async Task Reload_BlockGone_ClearsTheSelection()
    {
        var session = await OpenSessionAsync(_vault);
        State.SelectNoteBlock(NoteKey("Haupt"), NoteKey("Unter A"));
        var fullPath = Path.Combine(_vault.Root, NotePath);

        File.WriteAllText(fullPath, "- Haupt\n\t- Unter B\n- Andere\n");
        session.HandleExternalChange(fullPath);

        Assert.Null(State.ActiveNote);
        Assert.Null(State.ActiveNoteRange);
    }

    [Fact]
    public async Task NoteBars_SelectionGoesOnlyWhenNoBarShowsIt()
    {
        await OpenSessionAsync(_vault);
        State.SelectNoteBlock(NoteKey("Haupt"), NoteKey("Unter A"));
        var selection = State.ActiveNote!;

        // A card shown at another place (e.g. the other group of "Relevant"): the new bar comes, the old one goes.
        State.ShowNoteBar(selection);
        State.ShowNoteBar(selection);
        Assert.False(State.HideNoteBar(selection));
        State.ClearNoteSelectionIfNotShown(selection);
        Assert.Equal(selection, State.ActiveNote);

        // The last bar goes: the block is no longer shown.
        Assert.True(State.HideNoteBar(selection));
        State.ClearNoteSelectionIfNotShown(selection);
        Assert.Null(State.ActiveNote);
    }

    [Fact]
    public async Task NoteBars_AnotherSelectionMeanwhile_IsKept()
    {
        await OpenSessionAsync(_vault);
        State.SelectNoteBlock(NoteKey("Haupt"), NoteKey("Unter A"));
        var old = State.ActiveNote!;
        State.ShowNoteBar(old);
        State.ChangeNoteRange(1);

        Assert.True(State.HideNoteBar(old));
        State.ClearNoteSelectionIfNotShown(old);

        Assert.Equal(1, State.ActiveNote!.Level);
    }

    [Fact]
    public async Task Adopt_WithoutChildren_BookGetsOnlyTheBlockText_NoteStaysUnusedBelow()
    {
        await OpenSessionAsync(_vault);
        var text = State.CurrentBook!.Page.AllBlocks().Single(b => b.Content == "Erster Text").Key;

        var message = await NoteAdoption.AdoptAsync(
            State, NoteKey("Haupt"), _ => new InsertPosition.After(text), NullLogger.Instance, includeChildren: false);

        Assert.Null(message);
        var book = _vault.Read(BookPath);
        Assert.Contains("\t- Haupt\n", book);
        Assert.DoesNotContain("Unter", book);
        var note = _vault.Read(NotePath);
        Assert.Equal(1, note.Split("used-in::").Length - 1);
        Assert.StartsWith("- Haupt\n", note);
    }

    [Theory]
    [InlineData(AdoptVariant.AtCursor)]
    [InlineData(AdoptVariant.After)]
    [InlineData(AdoptVariant.Below)]
    [InlineData(AdoptVariant.AsHeading)]
    [InlineData(AdoptVariant.AsSubheading)]
    public async Task AdoptVariant_WithoutChildren_NeverBringsSubPointsIntoTheBook(AdoptVariant variant)
    {
        await OpenSessionAsync(_vault);
        var text = BookElements.Find(State.CurrentBook!, State.CurrentBook!.Page.AllBlocks().Single(b => b.Content == "Erster Text").Key)!;
        State.Cursor = new CursorInfo(text.Kind, text.Key, text.Key, 0);
        var untitled = Services.GetRequiredService<IStringLocalizer<Strings>>()["OutlineUntitled"].Value;

        var message = await NoteAdoption.AdoptAsync(State, NoteKey("Haupt"), variant, untitled, NullLogger.Instance, includeChildren: false);

        Assert.Null(message);
        var book = _vault.Read(BookPath);
        Assert.Contains("Haupt", book);
        Assert.DoesNotContain("Unter", book);
        Assert.DoesNotContain("Tiefer", book);
    }
}
