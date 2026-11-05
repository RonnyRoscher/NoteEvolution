using Microsoft.Extensions.Logging.Abstractions;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Links;
using NoteEvolution.Core.Model;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.History;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>Restoring a version of a text block and a deleted text block from the change history (spec 5, 6).</summary>
public class HistoryRestoreTests : UiTestContext
{
    private const string BookPath = "pages/Buch.md";
    private const string BookName = "Buch";
    private const string JournalPath = "journals/2026_03_01.md";
    private const string BlockId = "11111111-1111-4111-8111-111111111111";
    private const string PointId = "22222222-2222-4222-8222-222222222222";
    private const string NoteOne = "33333333-3333-4333-8333-333333333333";
    private const string NoteTwo = "44444444-4444-4444-8444-444444444444";

    private const string BookText =
        "title:: Buch\ntype:: book\n\n" +
        "- # Eins\n" +
        "\t- Vorher\n" +
        "\t  id:: 77777777-7777-4777-8777-777777777777\n" +
        "\t- Erster Text\n" +
        $"\t  id:: {BlockId}\n" +
        "\t  status:: fertig\n" +
        $"\t  source:: (({NoteOne}))\n" +
        "\t\t- Punkt A\n" +
        "\t\t- Punkt B\n" +
        $"\t\t  id:: {PointId}\n" +
        $"\t\t  source:: (({NoteTwo}))\n" +
        "\t- Zweiter Text\n" +
        "- # Zwei\n" +
        "\t- Text zwei\n";

    private const string Journal =
        $"- Eine Quelle\n  id:: {NoteOne}\n  used-in:: [[Buch]] (({BlockId}))\n" +
        $"- Andere Quelle\n  id:: {NoteTwo}\n  used-in:: [[Buch]] (({PointId}))\n";

    private static readonly DateTimeOffset At = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    private static TestVault VaultOf(string book = BookText) => TestVault.Create((BookPath, book), (JournalPath, Journal));

    private static Book BookOf(VaultSession session) => session.Vault.FindBook(BookName)!;

    private static TextBlock Find(VaultSession session, string start) =>
        BookOf(session).Page.AllBlocks().Select(b => BookOf(session).FindTextBlock(b.Key)).OfType<TextBlock>()
            .First(text => text.Text.StartsWith(start, StringComparison.Ordinal));

    private static HistoryVersion VersionOf(string text, params (string Text, int Depth)[] paragraphs) =>
        new(1, Guid.Parse(BlockId), At, HistoryReason.Pause,
            new BlockStand(text, [.. paragraphs.Select(p => new StandParagraph(p.Text, p.Depth, BlockStatus.Draft))], BlockStatus.Draft, []));

    private static string UsedIn(TestVault vault, string note) =>
        vault.Read(JournalPath).Split('\n').SkipWhile(line => !line.Contains(note)).Skip(1).First();

    [Fact]
    public async Task RestoreVersion_SavesCurrentFirst_ThenText_UndoBringsBack()
    {
        using var vault = VaultOf();
        var session = await OpenSessionAsync(vault);
        var block = Find(session, "Erster").Block;
        block.SetContent("Erster Text, geändert");
        Assert.True(session.TrySave(BookOf(session).Page, out _));
        var first = Assert.Single(session.History!.Versions(Guid.Parse(BlockId)));
        Assert.Equal("Erster Text", first.Stand.Text);
        var before = File.ReadAllBytes(Path.Combine(vault.Root, BookPath));

        var message = await ManuscriptCommands.RestoreVersionAsync(State, Guid.Parse(BlockId), first, "Wiederherstellen", NullLogger.Instance);

        Assert.Null(message);
        var versions = session.History.Versions(Guid.Parse(BlockId));
        Assert.Equal([HistoryReason.BeforeRestore, HistoryReason.First], versions.Select(v => v.Reason));
        Assert.Equal("Erster Text, geändert", versions[0].Stand.Text);
        var restored = Find(session, "Erster");
        Assert.Equal("Erster Text", restored.Text);
        Assert.Equal(BlockStatus.Done, restored.Status);
        Assert.Equal([Guid.Parse(NoteOne)], restored.Sources);
        Assert.Contains("\t- Erster Text\n\t  id::", vault.Read(BookPath));
        Assert.Equal("Wiederherstellen", session.Undo.NextDescription);

        Assert.True(session.TryUndo(out var error), error?.ToString());
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(vault.Root, BookPath)));
        Assert.Equal("Erster Text, geändert", Find(session, "Erster").Text);
    }

    [Fact]
    public async Task RestoreVersion_SameText_ChangesNothing()
    {
        using var vault = VaultOf();
        var session = await OpenSessionAsync(vault);

        var message = await ManuscriptCommands.RestoreVersionAsync(
            State, Guid.Parse(BlockId), VersionOf("Erster Text", ("Punkt A", 1), ("Punkt B", 1)), "Wiederherstellen", NullLogger.Instance);

        Assert.Null(message);
        Assert.False(session.Undo.CanUndo);
        Assert.Empty(session.History!.Versions(Guid.Parse(BlockId)));
        Assert.Equal(BookText, vault.Read(BookPath));
    }

    [Fact]
    public async Task RestoreVersion_LinkedParagraphRemoved_UsageFollows()
    {
        using var vault = VaultOf();
        var session = await OpenSessionAsync(vault);
        Assert.Contains($"(({PointId}))", UsedIn(vault, NoteTwo));

        var message = await ManuscriptCommands.RestoreVersionAsync(
            State, Guid.Parse(BlockId), VersionOf("Alter Text", ("Nur ein Punkt", 1)), "Wiederherstellen", NullLogger.Instance);

        Assert.Null(message);
        Assert.Equal(["Nur ein Punkt"], Find(session, "Alter").Paragraphs.Select(p => p.Text));
        Assert.DoesNotContain(PointId, vault.Read(BookPath));
        Assert.DoesNotContain("used-in::", UsedIn(vault, NoteTwo));
        // The block itself keeps its source and its usage.
        Assert.Contains($"(({BlockId}))", UsedIn(vault, NoteOne));
        Assert.Equal("Wiederherstellen", session.Undo.NextDescription);

        // The structure undo brings the sub-point back with its id:: and source::; the usage in the note is not re-entered.
        Assert.True(session.TryUndo(out var error), error?.ToString());
        Assert.Equal(BookText, vault.Read(BookPath));
        Assert.DoesNotContain("used-in::", UsedIn(vault, NoteTwo));
    }

    [Fact]
    public async Task RestoreDeleted_KeepsIdAndHistory_LeavesList_SourcesLinkedAgain()
    {
        using var vault = VaultOf();
        var session = await OpenSessionAsync(vault);
        var id = Guid.Parse(BlockId);
        var block = Find(session, "Erster").Block;
        block.SetContent("Erster Text, geändert");
        Assert.True(session.TrySave(BookOf(session).Page, out _));
        session.Links.DeleteTextBlock(BookOf(session), block.Key);
        var deleted = Assert.Single(session.History!.Deleted(BookName));
        Assert.DoesNotContain("used-in::", UsedIn(vault, NoteOne));
        var versions = session.History.Versions(id).Count;

        var message = await ManuscriptCommands.RestoreDeletedAsync(State, deleted, "Wiederherstellen", NullLogger.Instance);

        Assert.Null(message);
        Assert.Empty(session.History.Deleted(BookName));
        Assert.Equal(versions, session.History.Versions(id).Count);
        var restored = Find(session, "Erster");
        Assert.Equal(id, restored.Block.Id);
        Assert.Equal("Erster Text, geändert", restored.Text);
        Assert.Equal(BlockStatus.Done, restored.Status);
        Assert.Equal([Guid.Parse(NoteOne)], restored.Sources);
        Assert.Equal(["Punkt A", "Punkt B"], restored.Paragraphs.Select(p => p.Text));
        // Behind its former predecessor.
        Assert.Equal(["Vorher", "Erster Text, geändert", "Zweiter Text"], BookOf(session).Root.Children.First().TextBlocks.Select(t => t.Text));
        Assert.Contains($"(({BlockId}))", UsedIn(vault, NoteOne));
        Assert.Equal("Wiederherstellen", session.Undo.NextDescription);
    }

    [Fact]
    public async Task RestoreDeleted_SourceNoteGone_Skipped()
    {
        using var vault = VaultOf();
        var session = await OpenSessionAsync(vault);
        var gone = Guid.Parse("55555555-5555-4555-8555-555555555555");
        var deleted = new DeletedBlock(
            Guid.Parse("66666666-6666-4666-8666-666666666666"), BookName, At, "Eins", null,
            new BlockStand("Zurück", [], BlockStatus.Draft, [Guid.Parse(NoteTwo), gone]));

        var message = await ManuscriptCommands.RestoreDeletedAsync(State, deleted, "Wiederherstellen", NullLogger.Instance);

        Assert.Null(message);
        var restored = Find(session, "Zurück");
        Assert.Equal([Guid.Parse(NoteTwo)], restored.Sources);
        // No predecessor: the end of its section.
        Assert.Equal("Zurück", BookOf(session).Root.Children.First().Block!.Children[^1].Content);
        Assert.Contains($"(({restored.Block.Id}))", UsedIn(vault, NoteTwo));
        Assert.DoesNotContain(gone.ToString(), vault.Read(BookPath));
    }

    [Fact]
    public async Task Restore_ReadOnly_Refused()
    {
        using var vault = VaultOf(BookText + "- ```\n  offen\n");
        var session = await OpenSessionAsync(vault);
        Assert.True(BookOf(session).Page.IsReadOnly);

        Assert.False(ManuscriptCommands.CanRestore(State));
        Assert.Equal("EditorReadOnly", ManuscriptCommands.RestoreBlocked(State));
        var version = await ManuscriptCommands.RestoreVersionAsync(
            State, Guid.Parse(BlockId), VersionOf("Alter Text"), "Wiederherstellen", NullLogger.Instance);
        var deleted = await ManuscriptCommands.RestoreDeletedAsync(
            State, new DeletedBlock(Guid.NewGuid(), BookName, At, "", null, new BlockStand("Zurück", [], BlockStatus.Draft, [])),
            "Wiederherstellen", NullLogger.Instance);

        Assert.Equal(new AdoptMessage("EditorReadOnly", true), version);
        Assert.Equal(new AdoptMessage("EditorReadOnly", true), deleted);
        Assert.False(session.Undo.CanUndo);
    }

    [Fact]
    public async Task Restore_OpenConflict_Refused()
    {
        using var vault = VaultOf();
        var session = await OpenSessionAsync(vault);
        var path = Path.Combine(vault.Root, BookPath);
        Assert.True(ManuscriptCommands.CanRestore(State));
        Assert.Null(ManuscriptCommands.RestoreBlocked(State));

        // A local change of the heading collides with an external change of the same line.
        var heading = BookOf(session).Root.Children.First();
        OutlineEditor.Rename(BookOf(session), heading.Key, "Eins lokal");
        File.WriteAllText(path, BookText.Replace("- # Eins\n", "- # Eins extern\n"));
        session.HandleExternalChange(path);
        Assert.True(session.HasOpenConflict(path));

        Assert.False(ManuscriptCommands.CanRestore(State));
        Assert.Equal("EditorConflict", ManuscriptCommands.RestoreBlocked(State));
        var version = await ManuscriptCommands.RestoreVersionAsync(
            State, Guid.Parse(BlockId), VersionOf("Alter Text"), "Wiederherstellen", NullLogger.Instance);

        Assert.Equal(new AdoptMessage("EditorConflict", true), version);
        Assert.False(session.Undo.CanUndo);
    }
}
