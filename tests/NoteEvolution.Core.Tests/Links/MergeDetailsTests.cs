using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Links;
using NoteEvolution.Core.Model;
using NoteEvolution.TestSupport;

namespace NoteEvolution.Core.Tests.Links;

/// <summary>„Zusammenfügen“ on a text block or a detail with its details: the details' texts go into its own text.</summary>
public class MergeDetailsTests
{
    private const string B1 = "6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70";
    private const string B2 = "92c1b3d4-6d3f-4a0c-8e5b-3c4d5e6f7081";
    private const string N1 = "7f3a91c2-4b1d-4e8a-9c3f-1a2b3c4d5e6f";
    private const string N2 = "81bb02aa-5c2e-4f9b-8d4a-2b3c4d5e6f71";

    private const string Head = "title:: Buch: Test\ntype:: book\n\n- # Kapitel\n";

    private const string NestedBook =
        Head +
        "\t- Text A\n" +
        "\t\t- Detail 1\n" +
        "\t\t\t- Tiefer 1\n" +
        "\t\t- Detail 2\n" +
        "\t- Text B\n" +
        "\t\t- Bleibt\n";

    // Text A and its detail are linked; the detail's sources and usages go to Text A.
    private const string LinkedBook =
        Head +
        "\t- Text A\n" +
        $"\t  id:: {B1}\n" +
        $"\t  source:: (({N1}))\n" +
        "\t\t- Absatz\n" +
        $"\t\t  id:: {B2}\n" +
        $"\t\t  source:: (({N2})), (({N1}))\n" +
        "\t- Text B\n";

    private const string LinkedJournal =
        "- Erste Quelle\r\n" +
        $"  id:: {N1}\r\n" +
        $"  used-in:: [[Buch - Test]] (({B1})), [[Buch - Test]] (({B2}))\r\n" +
        "- Zweite Quelle\r\n" +
        $"  id:: {N2}\r\n" +
        $"  used-in:: [[Buch - Test]] (({B2})),[[Anderes Buch]]\r\n";

    [Fact]
    public void MergeDetails_TextBlock_DetailsOfAllDepthsJoinedIntoItsText()
    {
        using var s = new LinkSetup(NestedBook);
        var a = s.Text("Text A").Key;

        var key = s.Links.MergeDetails(s.Book, a);

        Assert.Equal(a, key);
        var expected =
            Head +
            "\t- Text A\n\n\t  Detail 1\n\n\t  Tiefer 1\n\n\t  Detail 2\n" +
            "\t- Text B\n" +
            "\t\t- Bleibt\n";
        Assert.Equal(expected, s.ReadBook());
        AssertChangedLines(NestedBook, s.ReadBook(), removed: [5, 6, 7], added: [5, 6, 7, 8, 9, 10]);
        Assert.Equal(LinkSetup.DefaultJournal, s.ReadJournal());
        Assert.Equal("Zusammenfügen", s.Undo.NextDescription);

        Assert.Empty(s.Text("Text A\n\nDetail 1\n\nTiefer 1\n\nDetail 2").Paragraphs);

        s.Undo.Undo();

        Assert.Equal(NestedBook, s.ReadBook());
        Assert.False(s.Undo.CanUndo);
    }

    [Fact]
    public void MergeDetails_NoteDetails_StayAsDetails_AlsoWhenNestedUnderAMergedDetail()
    {
        var book =
            Head +
            "\t- Text A\n" +
            "\t\t- Detail 1\n" +
            "\t\t\t- Gedanke #notiz\n" +
            "\t\t\t\t- Unter Gedanke\n" +
            "\t\t\t- Tiefer 1\n" +
            "\t\t- Hinweis #notiz\n" +
            "\t\t\t- Unter Hinweis\n" +
            "\t\t- Detail 2\n" +
            "\t- Text B\n";
        using var s = new LinkSetup(book);

        s.Links.MergeDetails(s.Book, s.Text("Text A").Key);

        // The note details keep their own sub-details (a plain one too) and come up to the text block, in file order.
        var expected =
            Head +
            "\t- Text A\n\n\t  Detail 1\n\n\t  Tiefer 1\n\n\t  Detail 2\n" +
            "\t\t- Gedanke #notiz\n" +
            "\t\t\t- Unter Gedanke\n" +
            "\t\t- Hinweis #notiz\n" +
            "\t\t\t- Unter Hinweis\n" +
            "\t- Text B\n";
        Assert.Equal(expected, s.ReadBook());
        AssertChangedLines(book, s.ReadBook(), removed: [5, 6, 7, 8, 11], added: [5, 6, 7, 8, 9, 10, 11, 12]);

        var merged = s.Text("Text A\n\nDetail 1\n\nTiefer 1\n\nDetail 2");
        Assert.Equal(
            [("Gedanke #notiz", 1), ("Unter Gedanke", 2), ("Hinweis #notiz", 1), ("Unter Hinweis", 2)],
            merged.Paragraphs.Select(p => (p.Block.Content, p.Depth)));

        s.Undo.Undo();

        Assert.Equal(book, s.ReadBook());
    }

    [Fact]
    public void MergeDetails_Detail_DeeperDetailsJoinedIntoIt()
    {
        var book =
            Head +
            "\t- Text A\n" +
            "\t\t- Detail 1\n" +
            "\t\t\t- Tiefer 1\n" +
            "\t\t\t\t- Noch tiefer\n" +
            "\t\t\t- Tiefer 2 #notiz\n" +
            "\t\t- Detail 2\n" +
            "\t\t\t- Bleibt\n";
        using var s = new LinkSetup(book);
        var detail = KeyOf(s, "Detail 1");

        var key = s.Links.MergeDetails(s.Book, detail);

        Assert.Equal(detail, key);
        var expected =
            Head +
            "\t- Text A\n" +
            "\t\t- Detail 1\n\n\t\t  Tiefer 1\n\n\t\t  Noch tiefer\n" +
            "\t\t\t- Tiefer 2 #notiz\n" +
            "\t\t- Detail 2\n" +
            "\t\t\t- Bleibt\n";
        Assert.Equal(expected, s.ReadBook());
        AssertChangedLines(book, s.ReadBook(), removed: [6, 7], added: [6, 7, 8, 9]);

        s.Undo.Undo();

        Assert.Equal(book, s.ReadBook());
    }

    [Fact]
    public void MergeDetails_BlankDetails_AddNothing()
    {
        var book =
            Head +
            "\t-\n" +
            "\t\t-\n" +
            "\t\t\t- Erstes Detail\n" +
            "\t\t- Zweites Detail\n" +
            "\t\t-\n";
        using var s = new LinkSetup(book);
        var textBlock = s.Book.Root.Children.Single().TextBlocks.Single().Key;

        s.Links.MergeDetails(s.Book, textBlock);

        // The empty text block takes the first text; the empty details add no blank lines.
        Assert.Equal(Head + "\t- Erstes Detail\n\n\t  Zweites Detail\n", s.ReadBook());
        AssertChangedLines(book, s.ReadBook(), removed: [4, 5, 6, 7, 8], added: [4, 5, 6]);
    }

    [Fact]
    public void MergeDetails_LinkedDetail_SourcesUnion_UsedInRewritten_UndoByteExact()
    {
        using var s = new LinkSetup(LinkedBook, LinkedJournal);

        s.Links.MergeDetails(s.Book, s.Text("Text A").Key);

        var book =
            Head +
            "\t- Text A\n" +
            $"\t  id:: {B1}\n" +
            $"\t  source:: (({N1})), (({N2}))\n" +
            "\n" +
            "\t  Absatz\n" +
            "\t- Text B\n";
        Assert.Equal(book, s.ReadBook());
        AssertChangedLines(LinkedBook, s.ReadBook(), removed: [6, 7, 8, 9], added: [6, 7, 8]);
        var journal =
            "- Erste Quelle\r\n" +
            $"  id:: {N1}\r\n" +
            $"  used-in:: [[Buch - Test]] (({B1}))\r\n" +
            "- Zweite Quelle\r\n" +
            $"  id:: {N2}\r\n" +
            $"  used-in:: [[Buch - Test]] (({B1})), [[Anderes Buch]]\r\n";
        Assert.Equal(journal, s.ReadJournal());
        AssertChangedLines(LinkedJournal, s.ReadJournal(), removed: [2, 5], added: [2, 5]);
        Assert.Empty(s.Pending.Load());

        s.Undo.Undo();

        Assert.Equal(LinkedBook, s.ReadBook());
        Assert.Equal(LinkedJournal, s.ReadJournal());
        Assert.False(s.Undo.CanUndo);
        Assert.Empty(s.Pending.Load());
    }

    [Fact]
    public void MergeDetails_LinkedDetailUnderADetail_TextBlockGetsAnId_UsagesMoveToIt()
    {
        var book =
            Head +
            "\t- Text A\n" +
            "\t\t- Detail\n" +
            "\t\t\t- Absatz\n" +
            $"\t\t\t  id:: {B2}\n" +
            $"\t\t\t  source:: (({N2}))\n";
        var journal = $"- Zweite Quelle\n  id:: {N2}\n  used-in:: [[Buch - Test]] (({B2}))\n";
        using var s = new LinkSetup(book, journal);

        s.Links.MergeDetails(s.Book, KeyOf(s, "Detail"));

        // The detail gets the text, the text block the source (a detail never gets a source of its own) and an id.
        var id = s.Text("Text A").Block.Id!.Value;
        Assert.Equal(
            Head + $"\t- Text A\n\t  id:: {id}\n\t  source:: (({N2}))\n\t\t- Detail\n\n\t\t  Absatz\n",
            s.ReadBook());
        Assert.Equal(journal.Replace(B2, id.ToString()), s.ReadJournal());

        s.Undo.Undo();

        Assert.Equal(book, s.ReadBook());
        Assert.Equal(journal, s.ReadJournal());
    }

    [Theory]
    [InlineData("\t\t  farbe:: rot\n", "\t- Text A\n\t  farbe:: rot\n\n\t  Detail\n")]
    [InlineData("\t\t  Farbe:: blau\n", null)]
    public void MergeDetails_PropertiesCarriedOver_DifferentValueRefused(string detailProperty, string? merged)
    {
        var book = Head + "\t- Text A\n" + (merged is null ? "\t  farbe:: rot\n" : "") + "\t\t- Detail\n" + detailProperty;
        using var s = new LinkSetup(book);
        var a = s.Text("Text A").Key;

        if (merged is null)
        {
            Assert.Throws<MergeRefusedException>(() => s.Links.MergeDetails(s.Book, a));
            Assert.Equal(book, s.ReadBook());
            Assert.False(s.Book.Page.IsDirty);
            Assert.False(s.Undo.CanUndo);
        }
        else
        {
            s.Links.MergeDetails(s.Book, a);
            Assert.Equal(Head + merged, s.ReadBook());
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("\t  status:: fertig\n")]
    public void MergeDetails_StatusOfADetail_NeitherCarriedNorConflicting_HeadKeepsItsOwn(string head)
    {
        var book = Head + "\t- Text A\n" + head + "\t\t- Detail\n\t\t  status:: relevant\n\t- Text B\n";
        using var s = new LinkSetup(book);

        s.Links.MergeDetails(s.Book, s.Text("Text A").Key);

        Assert.Equal(Head + "\t- Text A\n" + head + "\n\t  Detail\n\t- Text B\n", s.ReadBook());
        var first = head.Length == 0 ? 5 : 6;
        AssertChangedLines(book, s.ReadBook(), removed: [first, first + 1], added: [first, first + 1]);

        s.Undo.Undo();

        Assert.Equal(book, s.ReadBook());
    }

    [Fact]
    public void MergeDetails_RemarkDetailByStatus_StaysAsDetail_LineUnchanged()
    {
        // A status:: bemerkung detail (as „Bemerkung anfügen“ makes it, no #notiz) stays like a #notiz detail.
        var book =
            Head +
            "\t- Text A\n" +
            "\t\t- Detail\n" +
            "\t\t- Anmerkung\n" +
            "\t\t  status:: bemerkung\n" +
            "\t\t\t- Unter Anmerkung\n" +
            "\t- Text B\n";
        using var s = new LinkSetup(book);

        s.Links.MergeDetails(s.Book, s.Text("Text A").Key);

        var expected =
            Head +
            "\t- Text A\n\n\t  Detail\n" +
            "\t\t- Anmerkung\n" +
            "\t\t  status:: bemerkung\n" +
            "\t\t\t- Unter Anmerkung\n" +
            "\t- Text B\n";
        Assert.Equal(expected, s.ReadBook());
        AssertChangedLines(book, s.ReadBook(), removed: [5], added: [5, 6]);

        s.Undo.Undo();

        Assert.Equal(book, s.ReadBook());
    }

    [Fact]
    public void Range_MergesDetails_NotWhenTheOnlyDetailIsARemarkByStatus()
    {
        var book =
            Head +
            "\t- Text A\n\t\t- Anmerkung\n\t\t  status:: bemerkung\n\t\t\t- Unter Anmerkung\n" +
            "\t- Text B\n\t\t- Anmerkung 2\n\t\t  Status:: Bemerkung\n\t\t- Detail\n";
        using var s = new LinkSetup(book);
        BookElement Element(string content) => BookElements.Find(s.Book, KeyOf(s, content))!;

        Assert.False(BookRanges.Of(s.Book, Element("Text A"), 0).MergesDetails);
        Assert.True(BookRanges.Of(s.Book, Element("Text B"), 0).MergesDetails);
        Assert.Throws<ArgumentException>(() => s.Links.MergeDetails(s.Book, s.Text("Text A").Key));
        Assert.Equal(book, s.ReadBook());
    }

    [Theory]
    [InlineData("", "\t\t  collapsed:: true\n")]
    [InlineData("\t  collapsed:: true\n", "\t\t  collapsed:: false\n")]
    public void MergeDetails_CollapsedOfADetail_NeitherCarriedNorConflicting_HeadKeepsItsOwn(string head, string detail)
    {
        var book = Head + "\t- Text A\n" + head + "\t\t- Detail\n" + detail + "\t\t\t- Tiefer\n\t\t- Gedanke #notiz\n";
        using var s = new LinkSetup(book);

        s.Links.MergeDetails(s.Book, s.Text("Text A").Key);

        Assert.Equal(Head + "\t- Text A\n" + head + "\n\t  Detail\n\n\t  Tiefer\n\t\t- Gedanke #notiz\n", s.ReadBook());

        s.Undo.Undo();

        Assert.Equal(book, s.ReadBook());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MergeDetails_IdOfADetailReferencedElsewhere_Refused_NothingChanged(bool inNotes)
    {
        var reference = $"Siehe (({B2}))";
        var book = Head + "\t- Text A\n\t\t- Absatz\n" + $"\t\t  id:: {B2}\n" + (inNotes ? "" : $"\t- {reference}\n");
        var journal = inNotes ? $"- {reference}\n" : LinkSetup.DefaultJournal;
        using var s = new LinkSetup(book, journal);

        Assert.Throws<MergeRefusedException>(() => s.Links.MergeDetails(s.Book, s.Text("Text A").Key));

        Assert.Equal(book, s.ReadBook());
        Assert.Equal(journal, s.ReadJournal());
        Assert.False(s.Book.Page.IsDirty);
        Assert.False(s.Undo.CanUndo);
    }

    [Fact]
    public void MergeDetails_NothingToMerge_Throws_NothingChanged()
    {
        var book = Head + "\t- Text A\n\t\t- Gedanke #notiz\n\t\t\t- Unter Gedanke\n\t- Text B\n";
        using var s = new LinkSetup(book);

        // Only a note detail (whose sub-details stay with it), no detail at all, a heading, an unknown key.
        Assert.Throws<ArgumentException>(() => s.Links.MergeDetails(s.Book, s.Text("Text A").Key));
        Assert.Throws<ArgumentException>(() => s.Links.MergeDetails(s.Book, s.Text("Text B").Key));
        Assert.Throws<ArgumentException>(() => s.Links.MergeDetails(s.Book, s.SectionKey("Kapitel")));
        Assert.Throws<ArgumentException>(() => s.Links.MergeDetails(s.Book, Guid.NewGuid()));

        Assert.Equal(book, s.ReadBook());
        Assert.False(s.Undo.CanUndo);
    }

    [Fact]
    public void MergeDetails_Undo_AfterFurtherBookEdit_Refused()
    {
        using var s = new LinkSetup(LinkedBook, LinkedJournal);
        s.Links.MergeDetails(s.Book, s.Text("Text A").Key);
        s.Book.Page.AllBlocks().Single(b => b.Content == "Text B").SetContent("Text B, geändert");
        s.Writer.Save(s.Book.Page);
        var book = s.ReadBook();
        var journal = s.ReadJournal();

        Assert.Throws<InvalidOperationException>(() => s.Undo.Undo());

        Assert.Equal(book, s.ReadBook());
        Assert.Contains("\t- Text B, geändert\n", book);
        Assert.Equal(journal, s.ReadJournal());
        Assert.False(s.Book.Page.IsDirty);
    }

    [Fact]
    public void Range_MergesDetails_OnlyForATextBlockOrDetailWithAPlainDetail_NotAtLevelMinusOne()
    {
        var book = Head + "\t- Text A\n\t\t- Detail\n\t- Text B\n\t\t- Gedanke #notiz\n\t\t\t- Unter Gedanke\n\t- Text C\n";
        using var s = new LinkSetup(book);
        BookElement Element(string content) => BookElements.Find(s.Book, KeyOf(s, content))!;

        Assert.True(BookRanges.Of(s.Book, Element("Text A"), 0).MergesDetails);
        Assert.False(BookRanges.Of(s.Book, Element("Text A"), -1).MergesDetails);
        Assert.False(BookRanges.Of(s.Book, Element("Text B"), 0).MergesDetails);
        Assert.False(BookRanges.Of(s.Book, Element("Text C"), 0).MergesDetails);
        Assert.False(BookRanges.Of(s.Book, Element("Detail"), 0).MergesDetails);
        // The text block as the range around its detail (level 1) is the text block with its details too.
        Assert.True(BookRanges.Of(s.Book, Element("Detail"), 1).MergesDetails);
        Assert.False(BookRanges.Of(s.Book, Element("Text A"), 1).MergesDetails);
    }

    private static Guid KeyOf(LinkSetup s, string content) => s.Book.Page.AllBlocks().Single(b => b.Content == content).Key;

    private static void AssertChangedLines(string before, string after, int[] removed, int[] added)
    {
        var (actualRemoved, actualAdded) = LineDiff.Changed(Encoding.UTF8.GetBytes(before), Encoding.UTF8.GetBytes(after));
        Assert.Equal(removed, actualRemoved);
        Assert.Equal(added, actualAdded);
    }
}
