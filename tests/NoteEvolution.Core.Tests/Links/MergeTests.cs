using System.Text;
using NoteEvolution.Core.Links;
using NoteEvolution.Core.Storage;
using NoteEvolution.TestSupport;

namespace NoteEvolution.Core.Tests.Links;

public class MergeTests
{
    private const string B1 = "6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70";
    private const string B2 = "92c1b3d4-6d3f-4a0c-8e5b-3c4d5e6f7081";
    private const string B3 = "a3d2c4e5-7e40-4b1d-9f6c-4d5e6f708192";
    private const string N1 = "7f3a91c2-4b1d-4e8a-9c3f-1a2b3c4d5e6f";
    private const string N2 = "81bb02aa-5c2e-4f9b-8d4a-2b3c4d5e6f71";
    private const string N3 = "b4e3d5f6-8f51-4c2e-8a7d-5e6f70819203";

    private const string PlainBook =
        "title:: Buch: Test\n" +
        "type:: book\n" +
        "\n" +
        "- # Kapitel\n" +
        "\t- Erster Text\n" +
        "\t  zweite Zeile\n" +
        "\t\t- Detail 1\n" +
        "\t- Zweiter Text\n" +
        "\t\t- Detail 2\n" +
        "\t\t\t- Tiefer\n" +
        "\t- Dritter Text\n";

    // Text A and Text B are linked; B has two sources and a detail.
    private const string LinkedBook =
        "title:: Buch: Test\n" +
        "type:: book\n" +
        "\n" +
        "- # Kapitel\n" +
        "\t- Text A\n" +
        $"\t  id:: {B1}\n" +
        $"\t  source:: (({N1}))\n" +
        "\t\t- Absatz A\n" +
        "\t- Text B\n" +
        $"\t  id:: {B2}\n" +
        $"\t  source:: (({N2})), (({N3})), (({N1}))\n" +
        "\t\t- Absatz B\n" +
        "\t- Anderer Text\n";

    // CRLF, a lower-case page link and a non-canonical separator: undo must still restore the bytes.
    private const string LinkedJournal =
        "- Erste Quelle\r\n" +
        $"  id:: {N1}\r\n" +
        $"  used-in:: [[Buch - Test]] (({B1})), [[Buch - Test]] (({B2}))\r\n" +
        "- Zweite Quelle\r\n" +
        $"  id:: {N2}\r\n" +
        $"  used-in:: [[Buch - Test]] (({B2})),[[Anderes Buch]]\r\n" +
        "- Dritte Quelle\r\n" +
        $"  id:: {N3}\r\n" +
        $"  used-in:: [[buch - test]] (({B2}))\r\n";

    [Fact]
    public void Merge_TwoBlocks_TextJoinedWithBlankLine_DetailsAppended()
    {
        using var s = new LinkSetup(PlainBook);
        var first = s.Text("Erster Text\nzweite Zeile").Key;

        var key = s.Links.MergeTextBlocks(s.Book, [[first, s.Text("Zweiter Text").Key]]);

        Assert.Equal(first, key);
        var expected =
            "title:: Buch: Test\ntype:: book\n\n- # Kapitel\n" +
            "\t- Erster Text\n\t  zweite Zeile\n\n\t  Zweiter Text\n" +
            "\t\t- Detail 1\n\t\t- Detail 2\n\t\t\t- Tiefer\n" +
            "\t- Dritter Text\n";
        Assert.Equal(expected, s.ReadBook());
        AssertChangedLines(PlainBook, s.ReadBook(), removed: [7], added: [6, 7]);
        Assert.Equal(LinkSetup.DefaultJournal, s.ReadJournal());
        Assert.Equal("Zusammenfügen", s.Undo.NextDescription);

        // The merged block is one text block with three details, also when read again.
        s.Reopen();
        var merged = s.Text("Erster Text\nzweite Zeile\n\nZweiter Text");
        Assert.Equal(["Detail 1", "Detail 2", "Tiefer"], merged.Paragraphs.Select(p => p.Block.Content));
    }

    [Fact]
    public void Merge_SourcesUnion_UsedInRewritten()
    {
        using var s = new LinkSetup(LinkedBook, LinkedJournal);

        s.Links.MergeTextBlocks(s.Book, [[s.Text("Text A").Key, s.Text("Text B").Key]]);

        Assert.Equal(
            "title:: Buch: Test\ntype:: book\n\n- # Kapitel\n" +
            "\t- Text A\n" +
            $"\t  id:: {B1}\n" +
            $"\t  source:: (({N1})), (({N2})), (({N3}))\n" +
            "\n" +
            "\t  Text B\n" +
            "\t\t- Absatz A\n" +
            "\t\t- Absatz B\n" +
            "\t- Anderer Text\n",
            s.ReadBook());
        AssertChangedLines(LinkedBook, s.ReadBook(), removed: [6, 8, 9, 10], added: [6, 7, 8]);
        var journal =
            "- Erste Quelle\r\n" +
            $"  id:: {N1}\r\n" +
            $"  used-in:: [[Buch - Test]] (({B1}))\r\n" +
            "- Zweite Quelle\r\n" +
            $"  id:: {N2}\r\n" +
            $"  used-in:: [[Buch - Test]] (({B1})), [[Anderes Buch]]\r\n" +
            "- Dritte Quelle\r\n" +
            $"  id:: {N3}\r\n" +
            $"  used-in:: [[Buch - Test]] (({B1}))\r\n";
        Assert.Equal(journal, s.ReadJournal());
        AssertChangedLines(LinkedJournal, s.ReadJournal(), removed: [2, 5, 8], added: [2, 5, 8]);
        Assert.Empty(s.Pending.Load());
    }

    [Fact]
    public void Merge_NoteUsedByTwoBlocks_SingleUsedInEntry()
    {
        // The first block is not linked to the note; both later blocks are.
        var book =
            "title:: Buch: Test\n" +
            "type:: book\n" +
            "\n" +
            "- # Kapitel\n" +
            "\t- Text A\n" +
            $"\t  id:: {B1}\n" +
            $"\t  source:: (({N2}))\n" +
            "\t- Text B\n" +
            $"\t  id:: {B2}\n" +
            $"\t  source:: (({N1}))\n" +
            "\t- Text C\n" +
            $"\t  id:: {B3}\n" +
            $"\t  source:: (({N1}))\n";
        var journal =
            "- Erste Quelle\n" +
            $"  id:: {N1}\n" +
            $"  used-in:: [[Buch - Test]] (({B2})), [[Anderes Buch]], [[Buch - Test]] (({B3}))\n" +
            "- Zweite Quelle\n" +
            $"  id:: {N2}\n" +
            $"  used-in:: [[Buch - Test]] (({B1}))\n";
        using var s = new LinkSetup(book, journal);

        s.Links.MergeTextBlocks(s.Book, [[s.Text("Text A").Key, s.Text("Text B").Key, s.Text("Text C").Key]]);

        Assert.Equal(
            "title:: Buch: Test\ntype:: book\n\n- # Kapitel\n" +
            $"\t- Text A\n\t  id:: {B1}\n\t  source:: (({N2})), (({N1}))\n\n\t  Text B\n\n\t  Text C\n",
            s.ReadBook());
        Assert.Equal(
            journal.Replace($"[[Buch - Test]] (({B2})), [[Anderes Buch]], [[Buch - Test]] (({B3}))", $"[[Buch - Test]] (({B1})), [[Anderes Buch]]"),
            s.ReadJournal());
        AssertChangedLines(journal, s.ReadJournal(), removed: [2], added: [2]);
    }

    [Fact]
    public void Merge_SeveralGroups()
    {
        // Group 1: a linked first block, an unlinked second one. Group 2: the linked block is the second one, so the
        // first gets an id.
        var book =
            "title:: Buch: Test\n" +
            "type:: book\n" +
            "\n" +
            "- # Kapitel\n" +
            "\t- Text A\n" +
            $"\t  id:: {B1}\n" +
            $"\t  source:: (({N1}))\n" +
            "\t- Text B\n" +
            "\t- ## Abschnitt\n" +
            "\t\t- Text C\n" +
            "\t\t- Text D\n" +
            $"\t\t  id:: {B2}\n" +
            $"\t\t  source:: (({N2}))\n" +
            "\t\t\t- Absatz D\n" +
            "\t\t- Text E\n";
        var journal =
            "- Erste Quelle\n" +
            $"  id:: {N1}\n" +
            $"  used-in:: [[Buch - Test]] (({B1}))\n" +
            "- Zweite Quelle\n" +
            $"  id:: {N2}\n" +
            $"  used-in:: [[Buch - Test]] (({B2}))\n";
        using var s = new LinkSetup(book, journal);
        var a = s.Text("Text A").Key;

        var key = s.Links.MergeTextBlocks(
            s.Book, [[a, s.Text("Text B").Key], [s.Text("Text C").Key, s.Text("Text D").Key, s.Text("Text E").Key]]);

        Assert.Equal(a, key);
        var newId = s.Text("Text C\n\nText D\n\nText E").Block.Id;
        Assert.NotNull(newId);
        Assert.Equal(
            "title:: Buch: Test\ntype:: book\n\n- # Kapitel\n" +
            $"\t- Text A\n\t  id:: {B1}\n\t  source:: (({N1}))\n\n\t  Text B\n" +
            "\t- ## Abschnitt\n" +
            $"\t\t- Text C\n\t\t  id:: {newId}\n\t\t  source:: (({N2}))\n\n\t\t  Text D\n\n\t\t  Text E\n" +
            "\t\t\t- Absatz D\n",
            s.ReadBook());
        AssertChangedLines(book, s.ReadBook(), removed: [7, 10, 11, 14], added: [7, 8, 11, 13, 14, 15, 16]);
        Assert.Equal(journal.Replace($"(({B2}))", $"(({newId}))"), s.ReadJournal());
        Assert.Equal("Zusammenfügen", s.Undo.NextDescription);

        s.Undo.Undo();

        Assert.Equal(book, s.ReadBook());
        Assert.Equal(journal, s.ReadJournal());
        Assert.False(s.Undo.CanUndo);
    }

    [Fact]
    public void Merge_InvalidGroup_Throws_NothingChanged()
    {
        var book = LinkedBook + "- # Zweites Kapitel\n\t- Text C\n";
        using var s = new LinkSetup(book, LinkedJournal);
        var a = s.Text("Text A").Key;
        var b = s.Text("Text B").Key;
        var c = s.Text("Text C").Key;

        Assert.Throws<ArgumentException>(() => s.Links.MergeTextBlocks(s.Book, [[a, Guid.NewGuid()]]));
        Assert.Throws<ArgumentException>(() => s.Links.MergeTextBlocks(s.Book, [[a]]));
        Assert.Throws<ArgumentException>(() => s.Links.MergeTextBlocks(s.Book, [[a, c]]));
        Assert.Throws<ArgumentException>(() => s.Links.MergeTextBlocks(s.Book, [[a, s.SectionKey("Kapitel")]]));
        // A valid group before an invalid one changes nothing either.
        Assert.Throws<ArgumentException>(() => s.Links.MergeTextBlocks(s.Book, [[a, b], [c]]));

        Assert.Equal(book, s.ReadBook());
        Assert.Equal(LinkedJournal, s.ReadJournal());
        Assert.False(s.Book.Page.IsDirty);
        Assert.False(s.Undo.CanUndo);
    }

    [Fact]
    public void Merge_ReadOnlyBook_Throws()
    {
        var book = LinkedBook + "- ```\n  offen\n";
        using var s = new LinkSetup(book, LinkedJournal);
        Assert.True(s.Book.Page.IsReadOnly);

        Assert.Throws<ReadOnlyPageException>(
            () => s.Links.MergeTextBlocks(s.Book, [[s.Text("Text A").Key, s.Text("Text B").Key]]));

        Assert.Equal(book, s.ReadBook());
        Assert.Equal(LinkedJournal, s.ReadJournal());
        Assert.False(s.Undo.CanUndo);
    }

    [Fact]
    public void Merge_NoteReadOnly_GoesPending()
    {
        var journal = $"- Zweite Quelle\n  id:: {N2}\n  used-in:: [[Buch - Test]] (({B2}))\n- ```\n  offen\n";
        using var s = new LinkSetup(LinkedBook, journal);
        Assert.True(s.Journal.IsReadOnly);

        s.Links.MergeTextBlocks(s.Book, [[s.Text("Text A").Key, s.Text("Text B").Key]]);

        Assert.Contains($"\t  source:: (({N1})), (({N2})), (({N3}))\n\n\t  Text B\n", s.ReadBook());
        Assert.Equal(journal, s.ReadJournal());
        var pending = s.Pending.Load().Where(p => p.NoteBlockId == Guid.Parse(N2)).ToList();
        Assert.Equal(2, pending.Count);
        Assert.Contains(pending, p => p.BookBlockId == Guid.Parse(B1) && !p.Remove && p.BookLinkName == "Buch - Test");
        Assert.Contains(pending, p => p.BookBlockId == Guid.Parse(B2) && p.Remove && p.BookLinkName == "Buch - Test");
        Assert.Equal(0, s.Links.RetryPending());
    }

    [Fact]
    public void Merge_Undo_RestoresBookAndNotesByteExact()
    {
        using var s = new LinkSetup(LinkedBook, LinkedJournal);
        s.Links.MergeTextBlocks(s.Book, [[s.Text("Text A").Key, s.Text("Text B").Key]]);

        s.Undo.Undo();

        Assert.Equal(LinkedBook, s.ReadBook());
        Assert.Equal(LinkedJournal, s.ReadJournal());
        Assert.NotNull(s.Book.FindTextBlockById(Guid.Parse(B2)));
        Assert.False(s.Undo.CanUndo);
        Assert.Empty(s.Pending.Load());
    }

    [Fact]
    public void Merge_Undo_AfterFurtherBookEdit_Refused()
    {
        using var s = new LinkSetup(LinkedBook, LinkedJournal);
        s.Links.MergeTextBlocks(s.Book, [[s.Text("Text A").Key, s.Text("Text B").Key]]);
        s.Book.Page.AllBlocks().Single(b => b.Content == "Absatz B").SetContent("Absatz B, geändert");
        s.Writer.Save(s.Book.Page);
        var book = s.ReadBook();
        var journal = s.ReadJournal();

        Assert.Throws<InvalidOperationException>(() => s.Undo.Undo());

        Assert.Equal(book, s.ReadBook());
        Assert.Contains("\t\t- Absatz B, geändert\n", book);
        Assert.Equal(journal, s.ReadJournal());
        Assert.False(s.Book.Page.IsDirty);
        Assert.False(s.Undo.CanUndo);
    }

    private static void AssertChangedLines(string before, string after, int[] removed, int[] added)
    {
        var (actualRemoved, actualAdded) = LineDiff.Changed(Encoding.UTF8.GetBytes(before), Encoding.UTF8.GetBytes(after));
        Assert.Equal(removed, actualRemoved);
        Assert.Equal(added, actualAdded);
    }
}
