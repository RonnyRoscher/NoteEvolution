using Bunit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Model;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Editor;
using NoteEvolution.UI.History;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>When the versions of text blocks arise, with a real session on a real vault.</summary>
public class BlockHistoryTrackerTests : UiTestContext
{
    private const string BookPath = "pages/Buch.md";
    private const string BookName = "Buch";
    private const string FirstId = "11111111-1111-4111-8111-111111111111";
    private const string SecondId = "22222222-2222-4222-8222-222222222222";

    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private const string BookText =
        "title:: Buch\ntype:: book\n\n" +
        "- # Eins\n" +
        "\t- Erster Text\n" +
        "\t\t- Punkt A\n" +
        "\t- Zweiter Text\n" +
        "\t- ## Eins-A\n" +
        "\t\t- Dritter Text\n";

    private static string BookWith(string block) => $"title:: Buch\ntype:: book\n\n- # Eins\n\t- {block}\n\t- Anderer Text\n";

    private static TestVault VaultOf(string book = BookText, params (string Path, string Content)[] more) =>
        TestVault.Create([(BookPath, book), .. more]);

    private static Book BookOf(VaultSession session) => session.Vault.FindBook(BookName)!;

    private static TextBlock Find(VaultSession session, string start) =>
        Blocks(BookOf(session).Root).First(text => text.Text.StartsWith(start, StringComparison.Ordinal));

    private static IEnumerable<TextBlock> Blocks(OutlineNode node) => node.Items.SelectMany(item => item switch
    {
        OutlineNode child => Blocks(child),
        TextBlock text => [text],
        _ => Array.Empty<TextBlock>(),
    });

    /// <summary>Changes the text of the block and saves the page, as every way of changing a block ends.</summary>
    private static void Write(VaultSession session, Block block, string text)
    {
        block.SetContent(text);
        Assert.True(session.TrySave(session.Vault.FindPageByPath(Path.Combine(session.Vault.Root, BookPath))!, out _));
    }

    private static IReadOnlyList<HistoryVersion> Versions(VaultSession session, Block block) =>
        session.History!.Versions(block.Id!.Value);

    private static string Chars(int count) => new('x', count);

    [Fact]
    public async Task Typing_NoVersionBeforePause_OneAfterTenQuietMinutes()
    {
        using var vault = VaultOf();
        var session = await OpenSessionAsync(vault);
        var block = Find(session, "Erster").Block;

        Write(session, block, "Erster Text, geändert");
        var first = Assert.Single(Versions(session, block));
        Assert.Equal(HistoryReason.First, first.Reason);
        Assert.Equal("Erster Text", first.Stand.Text);
        Assert.Equal("Punkt A", Assert.Single(first.Stand.Paragraphs).Text);

        Time.Advance(9 * Minute);
        Write(session, block, "Erster Text, noch einmal geändert");
        Time.Advance(9 * Minute);
        Assert.Single(Versions(session, block));

        Time.Advance(Minute);
        var versions = Versions(session, block);
        Assert.Equal(2, versions.Count);
        Assert.Equal(HistoryReason.Pause, versions[0].Reason);
        Assert.Equal("Erster Text, noch einmal geändert", versions[0].Stand.Text);
    }

    [Fact]
    public async Task FirstChange_AssignsIdInTheSameSave()
    {
        using var vault = VaultOf();
        var session = await OpenSessionAsync(vault);
        var block = Find(session, "Erster").Block;

        Write(session, block, "Geändert");

        var id = block.Id!.Value;
        var expected = BookText.Replace("\t- Erster Text\n", $"\t- Geändert\n\t  id:: {id:D}\n");
        Assert.Equal(expected, vault.Read(BookPath));
    }

    [Fact]
    public async Task UntouchedBlocks_FileUnchanged()
    {
        using var vault = VaultOf();
        var session = await OpenSessionAsync(vault);
        var page = BookOf(session).Page;

        Assert.True(session.TrySave(page, out _));

        Assert.Equal(BookText, vault.Read(BookPath));
    }

    [Fact]
    public async Task PropertiesOnly_NoVersion()
    {
        using var vault = VaultOf();
        var session = await OpenSessionAsync(vault);
        var book = BookOf(session);
        var text = Find(session, "Erster");

        ManuscriptEditor.SetStatus(book, BookElements.Find(book, text.Key)!, BlockStatus.Done, Clock.Now);
        Assert.True(session.TrySave(book.Page, out _));
        Time.Advance(11 * Minute);

        Assert.Null(text.Block.Id);
        Assert.DoesNotContain("id::", vault.Read(BookPath));
        Assert.Empty(session.History!.BlocksWithHistory());
    }

    [Fact]
    public async Task LargeDeletion_SavesLongestStandAtOnce()
    {
        using var vault = VaultOf(BookWith(Chars(299)));
        var session = await OpenSessionAsync(vault);
        var block = Find(session, "xxx").Block;

        Write(session, block, Chars(300));
        Write(session, block, Chars(50));

        var versions = Versions(session, block);
        Assert.Equal([HistoryReason.BeforeDelete, HistoryReason.First], versions.Select(v => v.Reason));
        Assert.Equal(300, versions[0].Stand.Text.Length);

        // What is written afterwards becomes a version of its own after the pause.
        Time.Advance(10 * Minute);
        Assert.Equal(HistoryReason.Pause, session.History!.Latest(block.Id!.Value)!.Reason);
        Assert.Equal(50, session.History.Latest(block.Id.Value)!.Stand.Text.Length);
    }

    [Fact]
    public async Task LargeDeletion_FirstChange_TheOldStandIsTheFirstVersion()
    {
        using var vault = VaultOf(BookWith(Chars(300)));
        var session = await OpenSessionAsync(vault);
        var block = Find(session, "xxx").Block;

        Write(session, block, Chars(50));

        var version = Assert.Single(Versions(session, block));
        Assert.Equal(HistoryReason.First, version.Reason);
        Assert.Equal(300, version.Stand.Text.Length);
    }

    [Theory]
    [InlineData(100, 70, true)] // a quarter and more
    [InlineData(100, 76, false)] // less than a quarter
    [InlineData(40, 30, true)] // the shortest block that counts
    [InlineData(39, 1, false)] // too short to count
    [InlineData(1000, 800, true)] // 200 characters are less than a quarter here
    [InlineData(1000, 801, false)]
    public async Task QuarterDeletion_Counts_ShortBlockDoesNot(int longest, int after, bool counts)
    {
        using var vault = VaultOf(BookWith(Chars(longest - 1)));
        var session = await OpenSessionAsync(vault);
        var block = Find(session, "x").Block;

        Write(session, block, Chars(longest));
        Write(session, block, Chars(after));

        var versions = Versions(session, block);
        Assert.Equal(counts, versions.Any(v => v.Reason == HistoryReason.BeforeDelete));
        if (counts)
        {
            Assert.Equal(longest, versions[0].Stand.Text.Length);
        }
    }

    [Fact]
    public async Task SlowDeletion_OverSeveralSaves_Counts()
    {
        using var vault = VaultOf(BookWith(Chars(299)));
        var session = await OpenSessionAsync(vault);
        var block = Find(session, "xxx").Block;

        Write(session, block, Chars(300));
        for (var length = 280; length >= 80; length -= 20)
        {
            Write(session, block, Chars(length));
        }

        Assert.Contains(Versions(session, block), v => v.Reason == HistoryReason.BeforeDelete && v.Stand.Text.Length == 300);
    }

    [Fact]
    public async Task SameTextAgain_NoNewVersion()
    {
        using var vault = VaultOf();
        var session = await OpenSessionAsync(vault);
        var block = Find(session, "Erster").Block;

        Write(session, block, "Geändert");
        Write(session, block, "Erster Text");
        Time.Advance(11 * Minute);

        Assert.Single(Versions(session, block));
    }

    [Fact]
    public async Task NewBlock_VersionAfterPause_NoFirst()
    {
        using var vault = VaultOf();
        var session = await OpenSessionAsync(vault);
        var page = BookOf(session).Page;
        var block = Block.CreateDetached("Neuer Text");
        page.InsertBlock(Find(session, "Zweiter").Block.Parent, 0, block);

        Assert.True(session.TrySave(page, out _));
        Assert.NotNull(block.Id);
        Assert.Empty(Versions(session, block));

        Time.Advance(10 * Minute);

        var version = Assert.Single(Versions(session, block));
        Assert.Equal(HistoryReason.Pause, version.Reason);
        Assert.Equal("Neuer Text", version.Stand.Text);
    }

    [Fact]
    public async Task DeletedBlock_Listed_WithSectionPathAndPredecessor()
    {
        var book =
            "title:: Buch\ntype:: book\n\n" +
            "- # Eins\n" +
            "\t- ## Unten\n" +
            $"\t\t- Davor\n\t\t  id:: {FirstId}\n" +
            "\t\t- Verschwindet\n";
        using var vault = VaultOf(book);
        var session = await OpenSessionAsync(vault);

        session.Links.DeleteTextBlock(BookOf(session), Find(session, "Verschwindet").Key);

        var deleted = Assert.Single(session.History!.Deleted(BookName));
        Assert.Equal("Eins › Unten", deleted.SectionPath);
        Assert.Equal(Guid.Parse(FirstId), deleted.AfterBlockId);
        Assert.Equal("Verschwindet", deleted.Stand.Text);
        Assert.Equal("Verschwindet", session.History.Latest(deleted.BlockId)!.Stand.Text);
    }

    [Fact]
    public async Task DeletedBlock_WithoutText_NotListed()
    {
        using var vault = VaultOf(BookWith(""));
        var session = await OpenSessionAsync(vault);
        var empty = Blocks(BookOf(session).Root).First();

        session.Links.DeleteTextBlock(BookOf(session), empty.Key);

        Assert.Empty(session.History!.Deleted(BookName));
    }

    [Fact]
    public async Task MergedBlock_NotListed()
    {
        using var vault = VaultOf();
        var session = await OpenSessionAsync(vault);
        var book = BookOf(session);
        var first = Find(session, "Erster");
        var second = Find(session, "Zweiter");

        session.Links.MergeTextBlocks(book, [[first.Key, second.Key]]);

        Assert.Empty(session.History!.Deleted(BookName));
        Assert.Equal("Erster Text", Versions(session, first.Block).Last().Stand.Text);
    }

    [Fact]
    public async Task BlockTurnedIntoHeading_NotListed()
    {
        using var vault = VaultOf();
        var session = await OpenSessionAsync(vault);
        var block = Find(session, "Zweiter").Block;

        block.SetContent("## Zweiter als Überschrift");
        Assert.True(session.TrySave(BookOf(session).Page, out _));

        Assert.Empty(session.History!.Deleted(BookName));
    }

    [Fact]
    public async Task RemovedHeading_UndoTakesTheTitleBlockAway_NotListed()
    {
        using var vault = VaultOf();
        var session = await OpenSessionAsync(vault);
        var heading = BookOf(session).Root.Children.Single().Children.Single();
        State.Cursor = new CursorInfo(ElementKind.Heading, heading.Key, null, 0);

        Assert.Null(await ManuscriptCommands.RunAsync(State, SectionCommand.RemoveHeading, "Entfernen", NullLogger.Instance));
        Assert.True(session.TryUndo(out var error), error?.ToString());

        Assert.Contains("\t- ## Eins-A\n", vault.Read(BookPath));
        Assert.Empty(session.History!.Deleted(BookName));
    }

    [Fact]
    public async Task MovedBlock_KeepsHistory()
    {
        using var vault = VaultOf();
        var session = await OpenSessionAsync(vault);
        var block = Find(session, "Erster").Block;
        var section = Find(session, "Dritter").Block.Parent!;
        Write(session, block, "Geändert");
        var id = block.Id;

        var page = BookOf(session).Page;
        page.MoveBlock(block, section, 0);
        Assert.True(session.TrySave(page, out _));

        Assert.Equal(id, block.Id);
        Assert.Empty(session.History!.Deleted(BookName));
        Assert.Single(session.History.Versions(id!.Value));
    }

    [Fact]
    public async Task Undo_OfFirstChange_KeepsHistory_NotListed()
    {
        using var vault = VaultOf();
        var session = await OpenSessionAsync(vault);
        var text = Find(session, "Zweiter");
        SetCursorInTextBlock(text.Key);

        await ManuscriptCommands.RunAsync(State, SectionCommand.AddRemark, "Anmerkung", NullLogger.Instance);
        var id = text.Block.Id;
        Assert.NotNull(id);
        Assert.Equal(HistoryReason.First, Assert.Single(session.History!.Versions(id.Value)).Reason);

        Assert.True(session.TryUndo(out _));

        Assert.Equal(id, text.Block.Id);
        Assert.Contains($"id:: {id:D}", vault.Read(BookPath));
        Assert.Empty(session.History.Deleted(BookName));
        Time.Advance(11 * Minute);
        Assert.Single(session.History.Versions(id.Value));
    }

    [Fact]
    public async Task Undo_OfDelete_ClearsDeleted()
    {
        using var vault = VaultOf();
        var session = await OpenSessionAsync(vault);
        var block = Find(session, "Zweiter").Block;

        session.Links.DeleteTextBlock(BookOf(session), block.Key);
        var deleted = Assert.Single(session.History!.Deleted(BookName));

        Assert.True(session.TryUndo(out _));

        Assert.Empty(session.History.Deleted(BookName));
        Assert.Equal(deleted.BlockId, Find(session, "Zweiter").Block.Id);
        Assert.Contains($"id:: {deleted.BlockId:D}", vault.Read(BookPath));
    }

    [Fact]
    public async Task ReturningBlock_WithSameText_NewKey_GetsItsIdBack()
    {
        using var vault = VaultOf();
        var session = await OpenSessionAsync(vault);
        var block = Find(session, "Zweiter").Block;
        var parent = block.Parent;
        session.Links.DeleteTextBlock(BookOf(session), block.Key);
        var deleted = Assert.Single(session.History!.Deleted(BookName));

        var again = Block.CreateDetached("Zweiter Text");
        BookOf(session).Page.InsertBlock(parent, 0, again);
        Assert.True(session.TrySave(BookOf(session).Page, out _));

        Assert.Equal(deleted.BlockId, again.Id);
        Assert.Empty(session.History.Deleted(BookName));
    }

    [Fact]
    public async Task DisposeSession_WritesOpenStands_OnClose()
    {
        using var vault = VaultOf();
        var session = await OpenSessionAsync(vault);
        var block = Find(session, "Erster").Block;
        Write(session, block, "Geändert");
        var id = block.Id!.Value;

        session.Dispose();

        using var store = HistoryStore.Open(vault.Root, NullLogger.Instance);
        var version = store.Latest(id)!;
        Assert.Equal(HistoryReason.OnClose, version.Reason);
        Assert.Equal("Geändert", version.Stand.Text);
    }

    [Fact]
    public async Task Reopen_FileDiffersFromLatest_OnOpenVersion()
    {
        using var vault = VaultOf();
        var session = await OpenSessionAsync(vault);
        var block = Find(session, "Erster").Block;
        Write(session, block, "Geändert");
        var id = block.Id!.Value;
        session.Dispose();
        var count = CountVersions(vault, id);

        var path = Path.Combine(vault.Root, BookPath);
        File.WriteAllText(path, File.ReadAllText(path).Replace("Geändert", "In Logseq geändert"));
        var reopened = await OpenSessionAsync(vault);

        var versions = reopened.History!.Versions(id);
        Assert.Equal(count + 1, versions.Count);
        Assert.Equal(HistoryReason.OnOpen, versions[0].Reason);
        Assert.Equal("In Logseq geändert", versions[0].Stand.Text);
        reopened.Dispose();
    }

    [Fact]
    public async Task Reopen_FileAsLatest_NoVersion()
    {
        using var vault = VaultOf();
        var session = await OpenSessionAsync(vault);
        var block = Find(session, "Erster").Block;
        Write(session, block, "Geändert");
        var id = block.Id!.Value;
        session.Dispose();
        var count = CountVersions(vault, id);

        var reopened = await OpenSessionAsync(vault);

        Assert.Equal(count, reopened.History!.Versions(id).Count);
        reopened.Dispose();
    }

    [Fact]
    public async Task ExternalChange_WithId_Compared_WithoutId_BaselineOnly()
    {
        var book =
            "title:: Buch\ntype:: book\n\n- # Eins\n" +
            $"\t- Mit Kennung\n\t  id:: {FirstId}\n" +
            "\t- Ohne Kennung\n";
        using var vault = VaultOf(book);
        var session = await OpenSessionAsync(vault);
        var path = Path.Combine(vault.Root, BookPath);

        File.WriteAllText(path, book.Replace("Mit Kennung", "Mit Kennung, außen geändert").Replace("Ohne Kennung", "Ohne, außen geändert"));
        session.HandleExternalChange(path);

        var version = Assert.Single(session.History!.Versions(Guid.Parse(FirstId)));
        Assert.Equal(HistoryReason.First, version.Reason);
        Assert.Equal("Mit Kennung", version.Stand.Text);
        Assert.Equal([Guid.Parse(FirstId)], session.History.BlocksWithHistory());
        Assert.Empty(session.History.Deleted(BookName));
        var without = Find(session, "Ohne");
        Assert.Null(without.Block.Id);

        // From its next change in the app on it has a history, starting with the text read from the file.
        Write(session, without.Block, "Ohne, in der App geändert");
        Assert.Equal("Ohne, außen geändert", Assert.Single(Versions(session, without.Block)).Stand.Text);
    }

    [Fact]
    public async Task ExternalChange_DeletedBlockWithId_Listed()
    {
        var book =
            "title:: Buch\ntype:: book\n\n- # Eins\n" +
            $"\t- Mit Kennung\n\t  id:: {FirstId}\n" +
            "\t- Ohne Kennung\n";
        using var vault = VaultOf(book);
        var session = await OpenSessionAsync(vault);
        var path = Path.Combine(vault.Root, BookPath);

        File.WriteAllText(path, "title:: Buch\ntype:: book\n\n- # Eins\n\t- Ohne Kennung\n");
        session.HandleExternalChange(path);

        Assert.Equal(Guid.Parse(FirstId), Assert.Single(session.History!.Deleted(BookName)).BlockId);
    }

    [Fact]
    public async Task SaveFailed_IdNeverInTheFile_NotListedAfterReading()
    {
        using var vault = VaultOf();
        var session = await OpenSessionAsync(vault);
        var page = BookOf(session).Page;
        var path = Path.Combine(vault.Root, BookPath);
        Find(session, "Erster").Block.SetContent("Geändert");

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(session.TrySave(page, out _));
        }

        PageReload.FromDisk(session, page);

        Assert.Empty(session.History!.Deleted(BookName));
    }

    [Fact]
    public async Task Appendix_AndNotePages_Ignored()
    {
        var book =
            "title:: Buch\ntype:: book\n\n- # Eins\n\t- Text\n" +
            "- # Glossar\n  anhang:: glossar\n\t- Begriff im Anhang\n";
        using var vault = VaultOf(book, ("pages/Notiz.md", "- Eine Notiz\n"));
        var session = await OpenSessionAsync(vault);
        var note = session.Vault.FindPageByPath(Path.Combine(vault.Root, "pages/Notiz.md"))!;
        var entry = Blocks(BookOf(session).Root).First(text => text.Text.StartsWith("Begriff", StringComparison.Ordinal)).Block;

        note.Roots[0].SetContent("Eine andere Notiz");
        Assert.True(session.TrySave(note, out _));
        entry.SetContent("Anderer Begriff");
        Assert.True(session.TrySave(BookOf(session).Page, out _));
        Time.Advance(11 * Minute);

        Assert.Equal("- Eine andere Notiz\n", vault.Read("pages/Notiz.md"));
        Assert.DoesNotContain("id::", vault.Read(BookPath));
        Assert.Empty(session.History!.BlocksWithHistory());
    }

    [Fact]
    public async Task HeadingsChange_NoVersion()
    {
        using var vault = VaultOf();
        var session = await OpenSessionAsync(vault);
        var page = BookOf(session).Page;

        page.Roots[0].SetContent("# Geänderte Überschrift");
        Assert.True(session.TrySave(page, out _));

        Assert.DoesNotContain("id::", vault.Read(BookPath));
        Assert.Empty(session.History!.BlocksWithHistory());
    }

    [Fact]
    public async Task DuplicateId_OnlyFirstCounts()
    {
        var book =
            "title:: Buch\ntype:: book\n\n- # Eins\n" +
            $"\t- Original\n\t  id:: {FirstId}\n" +
            $"\t- Kopie\n\t  id:: {FirstId}\n" +
            $"\t- Andere\n\t  id:: {SecondId}\n";
        using var vault = VaultOf(book);
        var session = await OpenSessionAsync(vault);

        Write(session, Find(session, "Kopie").Block, "Kopie, geändert");
        Assert.Empty(session.History!.BlocksWithHistory());

        Write(session, Find(session, "Original").Block, "Original, geändert");
        Assert.Equal("Original", Assert.Single(session.History.Versions(Guid.Parse(FirstId))).Stand.Text);

        session.Links.DeleteTextBlock(BookOf(session), Find(session, "Kopie").Key);
        Assert.Empty(session.History.Deleted(BookName));
    }

    [Fact]
    public async Task DuplicateId_LoggedOncePerSession()
    {
        var book =
            "title:: Buch\ntype:: book\n\n- # Eins\n" +
            $"\t- Original\n\t  id:: {FirstId}\n" +
            $"\t- Kopie\n\t  id:: {FirstId}\n";
        using var vault = VaultOf(book);
        var log = new ListLogger();
        var session = await OpenSessionAsync(vault, logger: log);

        Write(session, Find(session, "Kopie").Block, "Kopie, geändert");

        Assert.Single(log.Warnings);
    }

    [Fact]
    public async Task Undo_TwoStepsBack_TheIdGivenBackDoesNotBlockTheSecond()
    {
        using var vault = VaultOf();
        var session = await OpenSessionAsync(vault);
        var text = Find(session, "Zweiter");
        SetCursorInTextBlock(text.Key);
        Assert.Null(await ManuscriptCommands.SetStatusAsync(State, BlockStatus.Done, "Status", NullLogger.Instance));
        SetCursorInTextBlock(text.Key);
        Assert.Null(await ManuscriptCommands.RunAsync(State, SectionCommand.AddRemark, "Anmerkung", NullLogger.Instance));

        Assert.True(session.TryUndo(out var first), first?.ToString());
        Assert.True(session.TryUndo(out var second), second?.ToString());

        AssertSameBesidesNewIds(BookText, vault.Read(BookPath));
        Assert.Empty(session.History!.Deleted(BookName));
    }

    [Fact]
    public async Task Undo_AfterALaterTextChange_StaysRefused()
    {
        using var vault = VaultOf();
        var session = await OpenSessionAsync(vault);
        var text = Find(session, "Zweiter");
        SetCursorInTextBlock(text.Key);
        Assert.Null(await ManuscriptCommands.SetStatusAsync(State, BlockStatus.Done, "Status", NullLogger.Instance));

        Write(session, Find(session, "Erster").Block, "Später geändert");

        Assert.False(session.TryUndo(out _));
        Assert.Contains("Später geändert", vault.Read(BookPath));
    }

    [Fact]
    public async Task BrokenStore_BookStillSaved_ProblemShown()
    {
        using var vault = VaultOf();
        var session = await OpenSessionAsync(vault);
        var cut = Render<HeaderBar>();
        Assert.False(session.HistoryProblem);
        Assert.Empty(cut.FindAll(".ne-history-problem"));
        var block = Find(session, "Erster").Block;

        session.History!.Dispose();
        block.SetContent("Geändert");

        Assert.True(session.TrySave(BookOf(session).Page, out _));
        Assert.Contains("Geändert", vault.Read(BookPath));
        Assert.True(session.HistoryProblem);
        cut.WaitForAssertion(() => Assert.Contains("Änderungshistorie", cut.Find(".ne-history-problem").TextContent));
    }

    [Fact]
    public async Task UnusableStore_BookStillSaved_NoIds_ProblemShown()
    {
        using var vault = VaultOf();
        // A folder where the database should be: it can neither be opened nor moved into use.
        Directory.CreateDirectory(Path.Combine(vault.Root, ".noteevolution", "history.db", "belegt"));
        var session = await OpenSessionAsync(vault);

        Assert.Null(session.History);
        Assert.True(session.HistoryProblem);

        var book = BookOf(session);
        Find(session, "Erster").Block.SetContent("Geändert");
        Assert.True(session.TrySave(book.Page, out _));
        Assert.Equal(BookText.Replace("Erster Text", "Geändert"), vault.Read(BookPath));
    }

    [Fact]
    public async Task SaveBeforeRestore_StoresTheCurrentStand()
    {
        using var vault = VaultOf();
        var session = await OpenSessionAsync(vault);
        var block = Find(session, "Erster").Block;
        Write(session, block, "Geändert");

        session.HistoryTracker.SaveBeforeRestore(block.Id!.Value, BookName);
        session.HistoryTracker.SaveBeforeRestore(block.Id.Value, BookName);

        var versions = Versions(session, block);
        Assert.Equal([HistoryReason.BeforeRestore, HistoryReason.First], versions.Select(v => v.Reason));
        Assert.Equal("Geändert", versions[0].Stand.Text);
    }

    private static int CountVersions(TestVault vault, Guid id)
    {
        using var store = HistoryStore.Open(vault.Root, NullLogger.Instance);
        return store.Versions(id).Count;
    }

    /// <summary>Collects the warnings of the session.</summary>
    private sealed class ListLogger : ILogger
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Enqueue(formatter(state, exception));
            }
        }
    }
}
