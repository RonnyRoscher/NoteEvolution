using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>
/// The commands of the repetitions assistant: a reference in one or both directions
/// (<see cref="ManuscriptCommands.AppendReferenceAsync"/>), "Gewollt" (<see cref="ManuscriptCommands.SetRepetitionAcceptedAsync"/>)
/// and showing a pair in both halves (<see cref="ManuscriptCommands.OpenRepetitionAsync"/>).
/// </summary>
public class RepetitionCommandsTests : UiTestContext
{
    private const string BookPath = "pages/Buch - Alpha.md";

    private const string Head = "title:: Alpha\ntype:: book\n\n";

    private const string BookText = Head + "- # Eins\n\t- Erster Text\n\t- Zweiter Text\n- # Zwei\n\t- Dritter Text\n";

    private const string WithIds = Head
        + "- # Eins\n\t- Erster Text\n\t  id:: 00000000-0000-0000-0000-00000000000a\n"
        + "\t- Zweiter Text\n\t  id:: 00000000-0000-0000-0000-00000000000b\n";

    private readonly List<TestVault> _vaults = [];

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _vaults.ForEach(v => v.Dispose());
        }
    }

    private async Task<TestVault> OpenAsync(string bookText = BookText)
    {
        var vault = TestVault.Create((BookPath, bookText));
        _vaults.Add(vault);
        State.Session?.Dispose();
        await OpenSessionAsync(vault);
        return vault;
    }

    private Book Book => State.CurrentBook!;

    private VaultSession Session => State.Session!;

    private string Text(string key) => Services.GetRequiredService<IStringLocalizer<Strings>>()[key].Value;

    private Guid KeyOf(string content) => Book.Page.AllBlocks().Single(b => b.Content == content).Key;

    private Task<AdoptMessage?> AppendAsync(string from, string to, bool both) =>
        ManuscriptCommands.AppendReferenceAsync(State, KeyOf(from), KeyOf(to), both, Text("UndoAppendReference"), NullLogger.Instance);

    private Task<AdoptMessage?> AcceptAsync(string a, string b, bool accepted) =>
        ManuscriptCommands.SetRepetitionAcceptedAsync(
            State, KeyOf(a), KeyOf(b), accepted, Text("UndoRepetitionAccepted"), NullLogger.Instance);

    private IReadOnlySet<(Guid, Guid)> Accepted() => Session.AcceptedRepetitions.Of(Book.LinkName);

    [Fact]
    public async Task AppendReference_OneDirection_SavesAndUndoRestoresFile()
    {
        var vault = await OpenAsync();
        var first = KeyOf("Erster Text");
        var second = KeyOf("Zweiter Text");

        Assert.Null(await AppendAsync("Erster Text", "Zweiter Text", both: false));

        Assert.NotEqual(BookText, vault.Read(BookPath));
        Assert.True(ManuscriptEditor.RefersTo(Book, first, second));
        Assert.False(ManuscriptEditor.RefersTo(Book, second, first));
        Assert.Equal("Verweis einfügen", Session.Undo.NextDescription);

        Assert.True(Session.TryUndo(out var error), error?.ToString());
        AssertSameBesidesNewIds(BookText, vault.Read(BookPath));
        Assert.False(Session.Undo.CanUndo);
    }

    [Fact]
    public async Task AppendReference_Both_IsOneUndoStep()
    {
        var vault = await OpenAsync();
        var first = KeyOf("Erster Text");
        var second = KeyOf("Zweiter Text");

        Assert.Null(await AppendAsync("Erster Text", "Zweiter Text", both: true));

        Assert.True(ManuscriptEditor.RefersTo(Book, first, second));
        Assert.True(ManuscriptEditor.RefersTo(Book, second, first));
        Assert.True(Session.TryUndo(out var error), error?.ToString());
        AssertSameBesidesNewIds(BookText, vault.Read(BookPath));
        Assert.False(Session.Undo.CanUndo);
    }

    [Fact]
    public async Task AppendReference_BlockGone_ReturnsMessage_FileUnchanged()
    {
        var vault = await OpenAsync();

        var message = await ManuscriptCommands.AppendReferenceAsync(
            State, KeyOf("Erster Text"), Guid.NewGuid(), both: true, Text("UndoAppendReference"), NullLogger.Instance);

        Assert.Equal(new AdoptMessage("RepetitionGone", true), message);
        Assert.Equal("Diese Stelle gibt es nicht mehr.", Text("RepetitionGone"));
        Assert.Equal(BookText, vault.Read(BookPath));
        Assert.False(Session.Undo.CanUndo);
    }

    [Fact]
    public async Task Accept_BlocksWithoutId_GetIds_AndPairIsStored()
    {
        var vault = await OpenAsync();

        Assert.Null(await AcceptAsync("Erster Text", "Zweiter Text", accepted: true));

        var first = Book.FindTextBlock(KeyOf("Erster Text"))!.Block;
        var second = Book.FindTextBlock(KeyOf("Zweiter Text"))!.Block;
        Assert.NotNull(first.Id);
        Assert.NotNull(second.Id);
        Assert.Contains("id:: ", vault.Read(BookPath));
        Assert.True(Session.AcceptedRepetitions.Contains(Book.LinkName, first.Id!.Value, second.Id!.Value));
        Assert.Equal("Wiederholung als gewollt abhaken", Session.Undo.NextDescription);

        // The tracker was refreshed; the test waits until it is idle again.
        await Session.RepetitionsIdle();
    }

    [Fact]
    public async Task Accept_BlocksWithIds_FileUnchanged_NoUndoStep()
    {
        var vault = await OpenAsync(WithIds);
        var a = Guid.Parse("00000000-0000-0000-0000-00000000000a");
        var b = Guid.Parse("00000000-0000-0000-0000-00000000000b");

        Assert.Null(await AcceptAsync("Erster Text", "Zweiter Text", accepted: true));

        Assert.Equal(WithIds, vault.Read(BookPath));
        Assert.False(Session.Undo.CanUndo);
        Assert.True(Session.AcceptedRepetitions.Contains(Book.LinkName, a, b));
    }

    [Fact]
    public async Task Unaccept_RemovesPair()
    {
        await OpenAsync(WithIds);
        var a = Guid.Parse("00000000-0000-0000-0000-00000000000a");
        var b = Guid.Parse("00000000-0000-0000-0000-00000000000b");
        await AcceptAsync("Erster Text", "Zweiter Text", accepted: true);

        Assert.Null(await AcceptAsync("Erster Text", "Zweiter Text", accepted: false));

        Assert.False(Session.AcceptedRepetitions.Contains(Book.LinkName, a, b));
        Assert.Empty(Accepted());
    }

    [Fact]
    public async Task Accept_BlockGone_ReturnsMessage_NothingChanges()
    {
        var vault = await OpenAsync();

        var message = await ManuscriptCommands.SetRepetitionAcceptedAsync(
            State, KeyOf("Erster Text"), Guid.NewGuid(), accepted: true, Text("UndoRepetitionAccepted"), NullLogger.Instance);

        Assert.Equal(new AdoptMessage("RepetitionGone", true), message);
        Assert.Equal(BookText, vault.Read(BookPath));
        Assert.Empty(Accepted());
    }

    [Fact]
    public async Task CanChange_FalseWithConflict_AndWhenLocked()
    {
        var vault = await OpenAsync();
        Assert.True(ManuscriptCommands.CanChangeRepetition(State));

        var path = Path.Combine(vault.Root, BookPath);
        var external = BookText.Replace("- # Zwei\n", "- # Zwei extern\n");
        OutlineEditor.Rename(Book, KeyOf("# Zwei"), "Zwei lokal");
        File.WriteAllText(path, external);
        Session.HandleExternalChange(path);
        Assert.True(Session.HasOpenConflict(path));
        Assert.False(ManuscriptCommands.CanChangeRepetition(State));
    }

    [Fact]
    public async Task CanChange_FalseWhenLocked()
    {
        // An unclosed code fence makes the book read-only.
        await OpenAsync(BookText + "- ```\n  offen\n");

        Assert.True(Book.Page.IsReadOnly);
        Assert.False(ManuscriptCommands.CanChangeRepetition(State));
    }

    [Fact]
    public async Task Open_SplitsAndRevealsBoth()
    {
        await OpenAsync();
        var first = KeyOf("Erster Text");
        var third = KeyOf("Dritter Text");

        await ManuscriptCommands.OpenRepetitionAsync(State, first, third);

        Assert.True(State.IsSplit);
        Assert.Equal(first, State.Views[0].PendingReveal);
        Assert.Equal(third, State.Views[1].PendingReveal);
        Assert.Equal(first, State.Views[0].Cursor!.Key);
        Assert.Equal(third, State.Views[1].Cursor!.Key);
        Assert.Equal(KeyOf("# Zwei"), State.Views[1].CurrentSectionKey);
    }

    [Fact]
    public async Task Accept_StoreCannotBeWritten_TakesTheIdsBack_NoUndoStepRemains()
    {
        var vault = await OpenAsync();
        // A directory where the store file belongs: writing it fails.
        Directory.CreateDirectory(Path.Combine(vault.Root, ".noteevolution", "repetitions.json"));

        var message = await AcceptAsync("Erster Text", "Zweiter Text", accepted: true);

        Assert.Equal(new AdoptMessage("SectionCommandFailed", true), message);
        AssertSameBesidesNewIds(BookText, vault.Read(BookPath));
        Assert.False(Session.Undo.CanUndo);
        Assert.Empty(Accepted());
    }

    [Fact]
    public async Task Accept_IdsCannotBeSaved_StoreUntouched_FileUnchanged()
    {
        var vault = await OpenAsync();
        var path = Path.Combine(vault.Root, BookPath);
        // A read-only file cannot be replaced: saving the ids fails.
        File.SetAttributes(path, FileAttributes.ReadOnly);

        AdoptMessage? message;
        try
        {
            message = await AcceptAsync("Erster Text", "Zweiter Text", accepted: true);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Assert.NotNull(message);
        Assert.Equal(BookText, vault.Read(BookPath));
        Assert.False(Session.Undo.CanUndo);
        Assert.Empty(Accepted());
        Assert.False(File.Exists(Path.Combine(vault.Root, ".noteevolution", "repetitions.json")));
    }

    [Fact]
    public async Task AppendReference_ExistingDirectionIsSkipped_NothingLeft_NothingChanges()
    {
        var vault = await OpenAsync();
        var first = KeyOf("Erster Text");
        var second = KeyOf("Zweiter Text");
        var pushed = 0;
        Session.Undo.Pushed += _ => pushed++;
        await AppendAsync("Erster Text", "Zweiter Text", both: false);

        // Only the way back is missing: one step, and the first block still refers once.
        Assert.Null(await ManuscriptCommands.AppendReferenceAsync(State, first, second, both: true, "x", NullLogger.Instance));
        Assert.Equal(2, pushed);
        Assert.True(ManuscriptEditor.RefersTo(Book, second, first));
        var text = vault.Read(BookPath);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(Book.Page.AllBlocks().Single(b => b.Key == first).Content, @"\(\("));

        // Both exist: nothing to append, no change, no undo step.
        Assert.Null(await ManuscriptCommands.AppendReferenceAsync(State, first, second, both: true, "x", NullLogger.Instance));
        Assert.Equal(text, vault.Read(BookPath));
        Assert.Equal(2, pushed);
    }

    [Fact]
    public async Task Open_BlockGone_NoSplit_ReturnsMessage()
    {
        await OpenAsync();

        var message = await ManuscriptCommands.OpenRepetitionAsync(State, KeyOf("Erster Text"), Guid.NewGuid());

        Assert.Equal(new AdoptMessage("RepetitionGone", true), message);
        Assert.False(State.IsSplit);
        Assert.Null(State.Views[0].PendingReveal);
    }

    [Fact]
    public async Task Open_FirstHalfCannotBeActivated_SecondRevealIsNotDoneInIt()
    {
        await OpenAsync();
        State.SetSplit(true);
        await State.ActivateAsync(State.Views[1]);
        // The lower half holds text it could not save: the upper half cannot become active.
        State.Views[1].HasFailedEditorText = () => true;
        var third = KeyOf("Dritter Text");

        Assert.Null(await ManuscriptCommands.OpenRepetitionAsync(State, KeyOf("Erster Text"), third));

        Assert.Same(State.Views[1], State.ActiveView);
        Assert.NotEqual(third, State.Views[1].PendingReveal);
        Assert.Null(State.Views[0].PendingReveal);
    }
}
