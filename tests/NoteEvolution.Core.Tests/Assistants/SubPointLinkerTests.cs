using System.Text.RegularExpressions;
using NoteEvolution.Core.Assistants;
using NoteEvolution.Core.Links;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Storage;
using NoteEvolution.Core.Tests.Links;

namespace NoteEvolution.Core.Tests.Assistants;

/// <summary>The assistant „Verknüpfungen der Unterpunkte nachtragen“ (spec 7): old adoptions linked only the top block.</summary>
public class SubPointLinkerTests
{
    private const string TextId = "6f1c2a3b-0000-4000-8000-000000000001";
    private const string KernId = "6f1c2a3b-0000-4000-8000-000000000002";
    private const string SecondId = "6f1c2a3b-0000-4000-8000-000000000003";

    /// <summary>An old adoption: only the text block has <c>source::</c>, its details have none.</summary>
    private const string Book =
        "title:: Buch: Test\n" +
        "type:: book\n" +
        "\n" +
        "- Vorspann\n" +
        "- # Kapitel\n" +
        "\t- ## Abschnitt\n" +
        "\t\t- Kern\n" +
        $"\t\t  id:: {TextId}\n" +
        $"\t\t  source:: (({KernId}))\n" +
        "\t\t\t- Unter A\n" +
        "\t\t\t\t- Tief A1\n" +
        "\t\t\t- Unter B\n";

    /// <summary>Only the adopted note block has <c>used-in::</c>; its sub-points inherit it.</summary>
    private const string Journal =
        "- Kern\n" +
        $"  id:: {KernId}\n" +
        $"  used-in:: [[Buch - Test]] (({TextId}))\n" +
        "\t- Unter A\n" +
        "\t\t- Tief A1\n" +
        "\t- Unter B\n" +
        "- Zweite Notiz\n";

    private static readonly string[] SubPoints = ["Unter A", "Tief A1", "Unter B"];

    [Fact]
    public void Find_UniqueText_Matches()
    {
        // A text block without source: its details are neither matched nor counted.
        using var s = new LinkSetup(Book + "\t\t- Eigener Text\n\t\t\t- Unter B\n", Journal);

        var plan = Linker(s).Find();

        Assert.Equal(3, plan.Matches.Count);
        foreach (var content in SubPoints)
        {
            Assert.Contains(plan.Matches, m => m.DetailKey == FirstBookBlock(s, content).Key && m.NoteBlockKey == s.Note(content).Key);
        }

        Assert.All(plan.Matches, m => Assert.Equal("Buch - Test", m.Book.LinkName));
        Assert.Equal([("Buch - Test", 3, 0)], plan.PerBook);
        Assert.Equal(0, plan.Unmatched);
    }

    [Fact]
    public void Find_DuplicateSubPointText_NoMatch()
    {
        // Two details of the block carry the text of the same sub-point: neither is linked.
        var book = Book.Replace("\t\t\t- Unter B\n", "\t\t\t- Unter A\n");
        using var s = new LinkSetup(book, Journal);

        var plan = Linker(s).Find();

        var match = Assert.Single(plan.Matches);
        Assert.Equal(s.Note("Tief A1").Key, match.NoteBlockKey);
        Assert.Equal([("Buch - Test", 1, 2)], plan.PerBook);
        Assert.Equal(2, plan.Unmatched);
    }

    [Fact]
    public void Find_DuplicateTextInNote_NoMatch()
    {
        // Two sub-points of the note have the detail's text: it is not clear which one it came from.
        var journal = Journal.Replace("\t- Unter B\n", "\t- Unter B\n\t- Unter B\n");
        using var s = new LinkSetup(Book, journal);

        var plan = Linker(s).Find();

        Assert.Equal(2, plan.Matches.Count);
        Assert.DoesNotContain(plan.Matches, m => m.DetailKey == BookBlock(s, "Unter B").Key);
        Assert.Equal(1, plan.Unmatched);
    }

    [Fact]
    public void Find_RewordedDetail_Unmatched()
    {
        var book = Book.Replace("\t\t\t- Unter B\n", "\t\t\t- Unter B, umformuliert\n");
        using var s = new LinkSetup(book, Journal);

        var plan = Linker(s).Find();

        Assert.Equal(2, plan.Matches.Count);
        Assert.DoesNotContain(plan.Matches, m => m.DetailKey == BookBlock(s, "Unter B, umformuliert").Key);
        Assert.Equal([("Buch - Test", 2, 1)], plan.PerBook);
        Assert.Equal(1, plan.Unmatched);
    }

    [Fact]
    public void Find_TwoSourceNotes_OnlyUniqueAcrossBoth()
    {
        var book = Book
            .Replace($"source:: (({KernId}))", $"source:: (({KernId})), (({SecondId}))")
            + "\t\t\t- Nur zwei\n";
        var journal = Journal.Replace(
            "- Zweite Notiz\n",
            $"- Zweite Notiz\n  id:: {SecondId}\n  used-in:: [[Buch - Test]] (({TextId}))\n\t- Unter B\n\t- Nur zwei\n");
        using var s = new LinkSetup(book, journal);

        var plan = Linker(s).Find();

        // "Unter B" is a sub-point of both source notes: no match. The others belong to exactly one of them.
        Assert.Equal(3, plan.Matches.Count);
        Assert.DoesNotContain(plan.Matches, m => m.DetailKey == BookBlock(s, "Unter B").Key);
        Assert.Contains(plan.Matches, m => m.DetailKey == BookBlock(s, "Nur zwei").Key && m.NoteBlockKey == s.Note("Nur zwei").Key);
        Assert.Contains(plan.Matches, m => m.DetailKey == BookBlock(s, "Unter A").Key && m.NoteBlockKey == s.Note("Unter A").Key);
        Assert.Equal(1, plan.Unmatched);
    }

    [Fact]
    public void Find_TagInDetailText_StillMatches()
    {
        // An adoption removes #notiz only from the adopted note block itself; a sub-point's text is copied as it is.
        var book = Book.Replace("\t\t\t- Unter A\n", "\t\t\t- Unter A #notiz\n");
        var journal = Journal.Replace("- Kern\n", "- Kern #notiz\n").Replace("\t- Unter A\n", "\t- Unter A #notiz\n");
        using var s = new LinkSetup(book, journal);

        var plan = Linker(s).Find();

        Assert.Equal(3, plan.Matches.Count);
        Assert.Contains(
            plan.Matches, m => m.DetailKey == BookBlock(s, "Unter A #notiz").Key && m.NoteBlockKey == s.Note("Unter A #notiz").Key);
    }

    [Fact]
    public void Find_SecondUsageInOtherBlock_NotADuplicate()
    {
        // The same note adopted twice into the book (two blocks): each block's details are matched on their own.
        var book = Book + "\t\t- Kern\n\t\t  id:: 6f1c2a3b-0000-4000-8000-000000000009\n" +
                   $"\t\t  source:: (({KernId}))\n\t\t\t- Unter B\n";
        using var s = new LinkSetup(book, Journal);

        var plan = Linker(s).Find();

        Assert.Equal(4, plan.Matches.Count);
        Assert.Equal(2, plan.Matches.Count(m => m.NoteBlockKey == s.Note("Unter B").Key));
    }

    [Fact]
    public void Apply_LinksAndMarksCompleteNote()
    {
        using var s = new LinkSetup(Book, Journal);
        var linker = Linker(s);

        linker.Apply(linker.Find());

        foreach (var content in SubPoints)
        {
            var detail = BookBlock(s, content);
            var note = s.Note(content);
            Assert.NotNull(detail.Id);
            Assert.NotNull(note.Id);
            Assert.Equal($"(({note.Id}))", detail.GetProperty("source"));
            Assert.Equal($"[[Buch - Test]] (({detail.Id}))", note.GetProperty(UsedInValue.Key));
        }

        // Every sub-point has its own entry now: the old usage passes nothing on, the main point is taken alone.
        Assert.Equal($"[[Buch - Test]] (({TextId}))", s.Note("Kern").GetProperty(UsedInValue.Key));
        Assert.Equal("true", s.Note("Kern").GetProperty(UsedInValue.WithoutChildrenKey));
        Assert.Equal("true", s.Note("Unter A").GetProperty(UsedInValue.WithoutChildrenKey));
        Assert.Null(s.Note("Tief A1").GetProperty(UsedInValue.WithoutChildrenKey));
        Assert.Null(s.Note("Unter B").GetProperty(UsedInValue.WithoutChildrenKey));

        // Only property lines changed, on both sides.
        Assert.Equal(WithoutAddedProperties(Book), WithoutAddedProperties(s.ReadBook()));
        Assert.Equal(WithoutAddedProperties(Journal), WithoutAddedProperties(s.ReadJournal()));
        Assert.Contains($"  used-in:: [[Buch - Test]] (({TextId}))\n  used-without-children:: true\n\t- Unter A\n", s.ReadJournal());
        Assert.Empty(s.Pending.Load());
    }

    [Fact]
    public void Apply_PartialMatch_NoMark()
    {
        var book = Book.Replace("\t\t\t- Unter B\n", "\t\t\t- Unter B, umformuliert\n");
        using var s = new LinkSetup(book, Journal);
        var linker = Linker(s);

        linker.Apply(linker.Find());

        // "Unter B" has no entry of its own: it stays used through the main point, which keeps passing its usage on.
        Assert.Null(s.Note("Kern").GetProperty(UsedInValue.WithoutChildrenKey));
        Assert.Null(s.Note("Unter B").GetProperty(UsedInValue.Key));
        Assert.True(NoteEvolution.Core.Vaults.NoteBlock.IsUsedBlock(s.Note("Unter B")));
        // "Unter A" with all its sub-points linked takes only its own text.
        Assert.Equal("true", s.Note("Unter A").GetProperty(UsedInValue.WithoutChildrenKey));
        Assert.NotNull(s.Note("Tief A1").GetProperty(UsedInValue.Key));
    }

    [Fact]
    public void Apply_WritesEachPageOnce()
    {
        using var s = new LinkSetup(Book, Journal);
        var linker = Linker(s);

        linker.Apply(linker.Find());

        Assert.Equal(1, s.Writer.SavesOf(s.BookPath));
        Assert.Equal(1, s.Writer.SavesOf(s.JournalPath));
    }

    [Fact]
    public void Apply_SecondFindEmpty()
    {
        using var s = new LinkSetup(Book, Journal);
        var linker = Linker(s);
        linker.Apply(linker.Find());

        var again = Linker(s).Find();

        Assert.Empty(again.Matches);
        Assert.Empty(again.PerBook);
        Assert.Equal(0, again.Unmatched);

        // Also after reading everything again from the files.
        s.Reopen();
        Assert.Empty(Linker(s).Find().Matches);
    }

    [Fact]
    public void Apply_Undo_RestoresBytes()
    {
        using var s = new LinkSetup(Book, Journal);
        var linker = Linker(s);
        linker.Apply(linker.Find());
        Assert.Equal("Verknüpfungen nachtragen", s.Undo.NextDescription);

        s.Undo.Undo();

        Assert.Equal(Book, s.ReadBook());
        Assert.Equal(Journal, s.ReadJournal());
        Assert.False(s.Undo.CanUndo);
        Assert.Equal(3, Linker(s).Find().Matches.Count);
    }

    [Fact]
    public void Apply_Undo_ChangedPageStays_AndIsNamed()
    {
        using var s = new LinkSetup(Book, Journal);
        var linker = Linker(s);
        linker.Apply(linker.Find());
        s.Note("Zweite Notiz").SetContent("Zweite Notiz, geändert");
        s.Writer.Save(s.Journal);
        var journalChanged = s.ReadJournal();

        var ex = Assert.Throws<PagesNotRestoredException>(() => s.Undo.Undo());

        Assert.Equal([s.Journal.FilePath], ex.Paths);
        Assert.Contains(s.Journal.FilePath, ex.Message);
        Assert.Equal(Book, s.ReadBook());
        Assert.Equal(journalChanged, s.ReadJournal());
    }

    [Fact]
    public void Apply_ReadOnlyNotePage_Queued()
    {
        // An unclosed code fence: the page cannot be parsed safely and is never written.
        var journal = Journal + "- Kaputt\n  ```\n  nie geschlossen\n";
        using var s = new LinkSetup(Book, journal);
        Assert.True(s.Journal.IsReadOnly);
        var linker = Linker(s);
        var plan = linker.Find();
        Assert.Equal(3, plan.Matches.Count);

        linker.Apply(plan);

        Assert.Equal(journal, s.ReadJournal());
        Assert.Equal(WithoutAddedProperties(Book), WithoutAddedProperties(s.ReadBook()));
        var queued = s.Pending.Load();
        Assert.Equal(3, queued.Count);
        Assert.All(queued, update => Assert.False(update.Remove));
        foreach (var content in SubPoints)
        {
            var detail = BookBlock(s, content);
            var noteId = SourceValue.Parse(detail.GetProperty("source")!).Single();
            Assert.Contains(queued, u => u.NoteBlockId == noteId && u.BookBlockId == detail.Id);
        }

        // Undo takes the queued entries back with the book.
        s.Undo.Undo();
        Assert.Equal(Book, s.ReadBook());
        Assert.Empty(s.Pending.Load());
    }

    [Fact]
    public void Apply_BookChangedOnDisk_Throws_NothingWritten()
    {
        using var s = new LinkSetup(Book, Journal);
        var linker = Linker(s);
        var plan = linker.Find();
        var external = Book.Replace("- Vorspann\n", "- Vorspann extern\n");
        File.WriteAllText(s.BookPath, external);

        Assert.Throws<FileChangedExternallyException>(() => linker.Apply(plan));

        Assert.Equal(external, s.ReadBook());
        Assert.Equal(Journal, s.ReadJournal());
        Assert.Equal(0, s.Writer.SavesOf(s.JournalPath));
        Assert.False(s.Undo.CanUndo);
        Assert.Empty(s.Pending.Load());
    }

    private static SubPointLinker Linker(LinkSetup s) => new(s.Vault, s.Links);

    private static Block BookBlock(LinkSetup s, string content) => s.Book.Page.AllBlocks().Single(b => b.Content == content);

    private static Block FirstBookBlock(LinkSetup s, string content) => s.Book.Page.AllBlocks().First(b => b.Content == content);

    /// <summary>The file text without the property lines the assistant may add (on both sides of a comparison).</summary>
    private static string WithoutAddedProperties(string text) =>
        Regex.Replace(text, @"^[ \t]*(id|source|used-in|used-without-children):: [^\n]*\n", "", RegexOptions.Multiline);
}
