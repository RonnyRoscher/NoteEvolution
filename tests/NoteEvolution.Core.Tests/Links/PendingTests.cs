using NoteEvolution.Core.Links;
using NoteEvolution.TestSupport;

namespace NoteEvolution.Core.Tests.Links;

public class PendingTests
{
    [Fact]
    public void Adopt_NoteWriteFails_RecordsPending_RetryCompletes()
    {
        using var s = new LinkSetup();
        s.Writer.FailNext(s.JournalPath);

        var r = s.Links.Adopt(s.Book, s.Note("Kern").Key, new InsertPosition.After(s.Text("Erster Textblock").Key));

        var noteId = s.Note("Kern").Id!.Value;
        Assert.True(r.NoteUpdatePending);
        Assert.Contains($"source:: (({noteId}))", s.ReadBook());
        Assert.Equal(LinkSetup.DefaultJournal, s.ReadJournal());
        var entry = Assert.Single(s.Pending.Load());
        Assert.Equal(new PendingNoteUpdate(
            noteId,
            new NoteLocator(LinkSetup.JournalFile, [0], Sha256Hex("Kern")),
            "Buch - Test",
            r.TextBlockId,
            Remove: false), entry);
        Assert.True(File.Exists(Path.Combine(s.Tv.Root, ".noteevolution", "pending.json")));

        Assert.Equal(1, s.Links.RetryPending());

        Assert.Equal(
            $"- Kern\n  collapsed:: true\n  id:: {noteId}\n  used-in:: [[Buch - Test]] (({r.TextBlockId}))\n\t- Detail\n- Zweite Notiz\n",
            s.ReadJournal());
        Assert.Empty(s.Pending.Load());
        Assert.Empty(new PendingStore(s.Tv.Root).Load());
    }

    [Fact]
    public void Retry_AfterRestart_LocatesNoteByTreePathAndHash()
    {
        using var s = new LinkSetup();
        s.Writer.FailNext(s.JournalPath);
        var r = s.Links.Adopt(s.Book, s.Note("Detail").Key, new InsertPosition.After(s.Text("Erster Textblock").Key));
        var noteId = s.Note("Detail").Id!.Value;

        s.Reopen();
        Assert.Null(s.Note("Detail").Id);
        Assert.Equal([0, 0], Assert.Single(s.Pending.Load()).Locator.TreePath);

        Assert.Equal(1, s.Links.RetryPending());

        Assert.Equal(
            $"- Kern\n  collapsed:: true\n\t- Detail\n\t  id:: {noteId}\n\t  used-in:: [[Buch - Test]] (({r.TextBlockId}))\n- Zweite Notiz\n",
            s.ReadJournal());
        Assert.Empty(s.Pending.Load());
    }

    [Fact]
    public void Retry_NoteContentChangedMeanwhile_KeepsEntry()
    {
        using var s = new LinkSetup();
        s.Writer.FailNext(s.JournalPath);
        s.Links.Adopt(s.Book, s.Note("Detail").Key, new InsertPosition.After(s.Text("Erster Textblock").Key));
        File.WriteAllText(s.JournalPath, "- Kern\n  collapsed:: true\n\t- Detail, in Logseq geändert\n- Zweite Notiz\n");

        s.Reopen();

        Assert.Equal(0, s.Links.RetryPending());
        Assert.Single(s.Pending.Load());
        Assert.Equal("- Kern\n  collapsed:: true\n\t- Detail, in Logseq geändert\n- Zweite Notiz\n", s.ReadJournal());
    }

    [Fact]
    public void Retry_RunsAfterNextSuccessfulOperation()
    {
        using var s = new LinkSetup();
        s.Writer.FailNext(s.JournalPath);
        var first = s.Links.Adopt(s.Book, s.Note("Kern").Key, new InsertPosition.After(s.Text("Erster Textblock").Key));

        s.Links.Adopt(s.Book, s.Note("Zweite Notiz").Key, new InsertPosition.SectionEnd(Guid.Empty));

        Assert.Empty(s.Pending.Load());
        Assert.Contains($"  used-in:: [[Buch - Test]] (({first.TextBlockId}))\n", s.ReadJournal());
    }

    [Fact]
    public void Undo_AdoptWhileNoteUpdatePending_DropsPendingEntry()
    {
        using var s = new LinkSetup();
        s.Writer.FailNext(s.JournalPath);
        s.Links.Adopt(s.Book, s.Note("Kern").Key, new InsertPosition.After(s.Text("Erster Textblock").Key));
        var noteId = s.Note("Kern").Id;

        s.Undo.Undo();

        Assert.Equal(LinkSetup.DefaultBook, s.ReadBook());
        Assert.Equal($"- Kern\n  collapsed:: true\n  id:: {noteId}\n\t- Detail\n- Zweite Notiz\n", s.ReadJournal());
        Assert.Empty(s.Pending.Load());
    }

    [Fact]
    public void Store_MissingOrCorruptFile_IsEmpty()
    {
        using var dir = new TempDir();
        Assert.Empty(new PendingStore(dir.Path).Load());

        dir.Write(".noteevolution/pending.json", "{ kein json");
        Assert.Empty(new PendingStore(dir.Path).Load());
    }

    [Fact]
    public void Store_AddRemove_PersistsAsCamelCaseJson()
    {
        using var dir = new TempDir();
        var update = new PendingNoteUpdate(
            Guid.Parse("7f3a91c2-4b1d-4e8a-9c3f-1a2b3c4d5e6f"),
            new NoteLocator("journals/2026_03_01.md", [1, 0], new string('a', 64)),
            "Buch - Test",
            Guid.Parse("6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70"),
            Remove: true);

        new PendingStore(dir.Path).Add(update);

        var json = File.ReadAllText(Path.Combine(dir.Path, ".noteevolution", "pending.json"));
        Assert.Contains("\"noteBlockId\"", json);
        Assert.Contains("\"treePath\"", json);
        var reloaded = new PendingStore(dir.Path);
        Assert.Equal(update, Assert.Single(reloaded.Load()));

        reloaded.Remove(update with { Locator = update.Locator with { TreePath = [1, 0] } });
        Assert.Empty(new PendingStore(dir.Path).Load());
    }

    private static string Sha256Hex(string text) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
}
