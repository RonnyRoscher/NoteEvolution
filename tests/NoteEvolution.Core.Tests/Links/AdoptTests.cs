using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Links;
using NoteEvolution.Core.Model;
using NoteEvolution.TestSupport;

namespace NoteEvolution.Core.Tests.Links;

public class AdoptTests
{
    [Fact]
    public void Adopt_AfterBlock_CopiesSubtreeWithoutProperties_LinksBothSides()
    {
        using var s = new LinkSetup();
        var noteKey = s.Note("Kern").Key;

        var r = s.Links.Adopt(s.Book, noteKey, new InsertPosition.After(s.Text("Erster Textblock").Key));

        var noteId = s.Note("Kern").Id;
        Assert.NotNull(noteId);
        Assert.Contains($"\t\t- Kern\n\t\t  id:: {r.TextBlockId}\n\t\t  source:: (({noteId}))\n\t\t\t- Detail", s.ReadBook());
        Assert.Contains($"used-in:: [[Buch - Test]] (({r.TextBlockId}))", s.ReadJournal());
        Assert.DoesNotContain("collapsed", BookBlockText(s, r));
        Assert.Equal(
            "title:: Buch: Test\ntype:: book\n\n- Vorspann\n- # Kapitel\n\t- ## Abschnitt\n" +
            "\t\t- Erster Textblock\n" +
            $"\t\t- Kern\n\t\t  id:: {r.TextBlockId}\n\t\t  source:: (({noteId}))\n\t\t\t- Detail\n" +
            "\t\t- Zweiter Textblock\n\t\t- ### Unterabschnitt\n\t\t\t- Text im Unterabschnitt\n",
            s.ReadBook());
        Assert.Equal(
            $"- Kern\n  collapsed:: true\n  id:: {noteId}\n  used-in:: [[Buch - Test]] (({r.TextBlockId}))\n" +
            "\t- Detail\n- Zweite Notiz\n",
            s.ReadJournal());
        Assert.False(r.NoteUpdatePending);
        Assert.Equal([noteId!.Value], Book.Load(s.Book.Page).FindTextBlock(r.TextBlockKey)!.Sources);
        Assert.Equal("Übernehmen", s.Undo.NextDescription);
        Assert.Empty(s.Pending.Load());
    }

    [Fact]
    public void Adopt_SectionEnd_InsertsBeforeFollowingSubheading()
    {
        using var s = new LinkSetup();

        var r = s.Links.Adopt(s.Book, s.Note("Zweite Notiz").Key, new InsertPosition.SectionEnd(s.SectionKey("Abschnitt")));

        var noteId = s.Note("Zweite Notiz").Id;
        Assert.Contains(
            $"\t\t- Zweiter Textblock\n\t\t- Zweite Notiz\n\t\t  id:: {r.TextBlockId}\n\t\t  source:: (({noteId}))\n\t\t- ### Unterabschnitt\n",
            s.ReadBook());
    }

    [Fact]
    public void Adopt_SectionStart_InsertsBeforeFirstTextBlock()
    {
        using var s = new LinkSetup();

        var r = s.Links.Adopt(s.Book, s.Note("Zweite Notiz").Key, new InsertPosition.SectionStart(s.SectionKey("Abschnitt")));

        Assert.Contains(
            $"\t- ## Abschnitt\n\t\t- Zweite Notiz\n\t\t  id:: {r.TextBlockId}\n",
            s.ReadBook());
        Assert.Contains($"  source:: (({s.Note("Zweite Notiz").Id}))\n\t\t- Erster Textblock\n", s.ReadBook());
    }

    [Fact]
    public void Adopt_PrologueEnd_InsertsBeforeFirstHeading()
    {
        using var s = new LinkSetup();

        var r = s.Links.Adopt(s.Book, s.Note("Zweite Notiz").Key, new InsertPosition.SectionEnd(Guid.Empty));

        Assert.Contains(
            $"- Vorspann\n- Zweite Notiz\n  id:: {r.TextBlockId}\n  source:: (({s.Note("Zweite Notiz").Id}))\n- # Kapitel\n",
            s.ReadBook());
    }

    [Fact]
    public void Adopt_ChildBulletOnly_LinksChild()
    {
        using var s = new LinkSetup();

        var r = s.Links.Adopt(s.Book, s.Note("Detail").Key, new InsertPosition.After(s.Text("Erster Textblock").Key));

        var childId = s.Note("Detail").Id;
        Assert.Null(s.Note("Kern").Id);
        Assert.Contains(
            $"\t\t- Erster Textblock\n\t\t- Detail\n\t\t  id:: {r.TextBlockId}\n\t\t  source:: (({childId}))\n\t\t- Zweiter Textblock\n",
            s.ReadBook());
        Assert.Equal(
            $"- Kern\n  collapsed:: true\n\t- Detail\n\t  id:: {childId}\n\t  used-in:: [[Buch - Test]] (({r.TextBlockId}))\n- Zweite Notiz\n",
            s.ReadJournal());
    }

    [Fact]
    public void Adopt_Twice_UsedInHasTwoEntries()
    {
        using var s = new LinkSetup();

        var first = s.Links.Adopt(s.Book, s.Note("Kern").Key, new InsertPosition.After(s.Text("Erster Textblock").Key));
        var second = s.Links.Adopt(s.Book, s.Note("Kern").Key, new InsertPosition.SectionEnd(s.SectionKey("Abschnitt")));

        var noteId = s.Note("Kern").Id;
        Assert.NotEqual(first.TextBlockId, second.TextBlockId);
        Assert.Contains(
            $"  used-in:: [[Buch - Test]] (({first.TextBlockId})), [[Buch - Test]] (({second.TextBlockId}))\n",
            s.ReadJournal());
        Assert.Single(s.ReadJournal().Split('\n'), l => l.Contains("id:: ", StringComparison.Ordinal) && !l.Contains("used-in", StringComparison.Ordinal));
        Assert.Equal(2, s.ReadBook().Split($"source:: (({noteId}))").Length - 1);
    }

    [Fact]
    public void Adopt_NoteOnReadOnlyPage_ThrowsBeforeAnyChange()
    {
        using var s = new LinkSetup(journal: "- Kern\n- ```\n  offen\n");
        Assert.True(s.Journal.IsReadOnly);

        Assert.Throws<InvalidOperationException>(() =>
            s.Links.Adopt(s.Book, s.Note("Kern").Key, new InsertPosition.SectionEnd(Guid.Empty)));

        Assert.Equal(LinkSetup.DefaultBook, s.ReadBook());
        Assert.Equal("- Kern\n- ```\n  offen\n", s.ReadJournal());
        Assert.Null(s.Note("Kern").Id);
        Assert.Equal(7, s.Book.Page.AllBlocks().Count());
        Assert.False(s.Book.Page.IsDirty);
        Assert.False(s.Undo.CanUndo);
    }

    [Fact]
    public void Adopt_BookWriteFails_ReloadsBook_RevertsNote_Rethrows()
    {
        using var s = new LinkSetup();
        s.Writer.FailNext(s.BookPath);
        var staleBook = s.Book;

        Assert.Throws<IOException>(() =>
            s.Links.Adopt(staleBook, s.Note("Kern").Key, new InsertPosition.After(s.Text("Erster Textblock").Key)));

        Assert.Equal(LinkSetup.DefaultBook, s.ReadBook());
        Assert.Equal(LinkSetup.DefaultJournal, s.ReadJournal());
        Assert.DoesNotContain(s.Book.Page.AllBlocks(), b => b.Content == "Kern");
        Assert.DoesNotContain(staleBook.Page.AllBlocks(), b => b.Content == "Kern");
        Assert.Null(s.Note("Kern").Id);
        Assert.False(s.Journal.IsDirty);
        Assert.False(s.Undo.CanUndo);
        Assert.Empty(s.Pending.Load());
    }

    [Fact]
    public void Undo_Adopt_RemovesBookBlockAndUsage()
    {
        using var s = new LinkSetup();
        s.Links.Adopt(s.Book, s.Note("Kern").Key, new InsertPosition.After(s.Text("Erster Textblock").Key));
        var noteId = s.Note("Kern").Id;

        s.Undo.Undo();

        Assert.Equal(LinkSetup.DefaultBook, s.ReadBook());
        Assert.Equal($"- Kern\n  collapsed:: true\n  id:: {noteId}\n\t- Detail\n- Zweite Notiz\n", s.ReadJournal());
        Assert.False(s.Undo.CanUndo);
        Assert.Empty(s.Pending.Load());
    }

    private const string DetailBook =
        "title:: Buch: Test\n" +
        "type:: book\n" +
        "\n" +
        "- # Kapitel\n" +
        "\t- Hallo Welt\n" +
        "\t\t- **Erstes** Detail\n" +
        "\t\t\t- Tieferes Detail\n" +
        "\t\t- Zweites Detail\n" +
        "\t- Nächster Textblock\n";

    [Fact]
    public void AdoptInto_AtCursor_TextBlockMidParagraph_RestMovesBehind_SourceOnBlock()
    {
        using var s = new LinkSetup(DetailBook);
        var textKey = s.Text("Hallo Welt").Key;

        var r = s.Links.AdoptInto(s.Book, s.Note("Zweite Notiz").Key, new IntoPosition.AtCursor(textKey, 6));

        var noteId = s.Note("Zweite Notiz").Id;
        var book =
            "title:: Buch: Test\ntype:: book\n\n- # Kapitel\n" +
            $"\t- Hallo Zweite NotizWelt\n\t  id:: {r.TextBlockId}\n\t  source:: (({noteId}))\n" +
            "\t\t- **Erstes** Detail\n\t\t\t- Tieferes Detail\n\t\t- Zweites Detail\n\t- Nächster Textblock\n";
        Assert.Equal(book, s.ReadBook());
        AssertChangedLines(DetailBook, s.ReadBook(), removed: [4], added: [4, 5, 6]);
        var journal = $"- Kern\n  collapsed:: true\n\t- Detail\n- Zweite Notiz\n  id:: {noteId}\n  used-in:: [[Buch - Test]] (({r.TextBlockId}))\n";
        Assert.Equal(journal, s.ReadJournal());
        AssertChangedLines(LinkSetup.DefaultJournal, s.ReadJournal(), removed: [], added: [4, 5]);
        Assert.Equal(textKey, r.TextBlockKey);
        Assert.Equal([noteId!.Value], Book.Load(s.Book.Page).FindTextBlock(textKey)!.Sources);
        Assert.False(r.NoteUpdatePending);
        Assert.Equal("Übernehmen", s.Undo.NextDescription);
        Assert.Empty(s.Pending.Load());
    }

    [Fact]
    public void AdoptInto_AtCursor_Detail_SubBulletsBecomeFirstChildren()
    {
        using var s = new LinkSetup(DetailBook);

        var r = s.Links.AdoptInto(s.Book, s.Note("Kern").Key, new IntoPosition.AtCursor(BookBlock(s, "**Erstes** Detail").Key, 3));

        var noteId = s.Note("Kern").Id;
        Assert.Equal(
            "title:: Buch: Test\ntype:: book\n\n- # Kapitel\n" +
            $"\t- Hallo Welt\n\t  id:: {r.TextBlockId}\n\t  source:: (({noteId}))\n" +
            "\t\t- **Ers**Kern**tes** Detail\n\t\t\t- Detail\n\t\t\t- Tieferes Detail\n\t\t- Zweites Detail\n\t- Nächster Textblock\n",
            s.ReadBook());
        AssertChangedLines(DetailBook, s.ReadBook(), removed: [5], added: [5, 6, 7, 8]);
        Assert.Equal(s.Text("Hallo Welt").Key, r.TextBlockKey);
        Assert.Equal(
            $"- Kern\n  collapsed:: true\n  id:: {noteId}\n  used-in:: [[Buch - Test]] (({r.TextBlockId}))\n\t- Detail\n- Zweite Notiz\n",
            s.ReadJournal());
        AssertChangedLines(LinkSetup.DefaultJournal, s.ReadJournal(), removed: [], added: [2, 3]);
    }

    [Fact]
    public void AdoptInto_AfterDetail_AfterDeeperDetails_SameDepth()
    {
        using var s = new LinkSetup(DetailBook);

        var r = s.Links.AdoptInto(s.Book, s.Note("Kern").Key, new IntoPosition.AfterDetail(BookBlock(s, "**Erstes** Detail").Key));

        var noteId = s.Note("Kern").Id;
        Assert.Equal(
            "title:: Buch: Test\ntype:: book\n\n- # Kapitel\n" +
            $"\t- Hallo Welt\n\t  id:: {r.TextBlockId}\n\t  source:: (({noteId}))\n" +
            "\t\t- **Erstes** Detail\n\t\t\t- Tieferes Detail\n\t\t- Kern\n\t\t\t- Detail\n\t\t- Zweites Detail\n\t- Nächster Textblock\n",
            s.ReadBook());
        AssertChangedLines(DetailBook, s.ReadBook(), removed: [], added: [5, 6, 9, 10]);
        Assert.Equal(s.Text("Hallo Welt").Key, r.TextBlockKey);
        Assert.Equal(
            $"- Kern\n  collapsed:: true\n  id:: {noteId}\n  used-in:: [[Buch - Test]] (({r.TextBlockId}))\n\t- Detail\n- Zweite Notiz\n",
            s.ReadJournal());
        AssertChangedLines(LinkSetup.DefaultJournal, s.ReadJournal(), removed: [], added: [2, 3]);
    }

    [Fact]
    public void AdoptInto_FirstChild_TextBlock_FirstDetail()
    {
        using var s = new LinkSetup(DetailBook);

        var r = s.Links.AdoptInto(s.Book, s.Note("Zweite Notiz").Key, new IntoPosition.FirstChild(s.Text("Hallo Welt").Key));

        var noteId = s.Note("Zweite Notiz").Id;
        Assert.Equal(
            "title:: Buch: Test\ntype:: book\n\n- # Kapitel\n" +
            $"\t- Hallo Welt\n\t  id:: {r.TextBlockId}\n\t  source:: (({noteId}))\n" +
            "\t\t- Zweite Notiz\n\t\t- **Erstes** Detail\n\t\t\t- Tieferes Detail\n\t\t- Zweites Detail\n\t- Nächster Textblock\n",
            s.ReadBook());
        AssertChangedLines(DetailBook, s.ReadBook(), removed: [], added: [5, 6, 7]);
        Assert.Equal(s.Text("Hallo Welt").Key, r.TextBlockKey);
        Assert.Equal(
            $"- Kern\n  collapsed:: true\n\t- Detail\n- Zweite Notiz\n  id:: {noteId}\n  used-in:: [[Buch - Test]] (({r.TextBlockId}))\n",
            s.ReadJournal());
        AssertChangedLines(LinkSetup.DefaultJournal, s.ReadJournal(), removed: [], added: [4, 5]);
    }

    [Fact]
    public void AdoptInto_FirstChild_Detail_OneDeeper()
    {
        using var s = new LinkSetup(DetailBook);

        var r = s.Links.AdoptInto(s.Book, s.Note("Zweite Notiz").Key, new IntoPosition.FirstChild(BookBlock(s, "**Erstes** Detail").Key));

        var noteId = s.Note("Zweite Notiz").Id;
        Assert.Equal(
            "title:: Buch: Test\ntype:: book\n\n- # Kapitel\n" +
            $"\t- Hallo Welt\n\t  id:: {r.TextBlockId}\n\t  source:: (({noteId}))\n" +
            "\t\t- **Erstes** Detail\n\t\t\t- Zweite Notiz\n\t\t\t- Tieferes Detail\n\t\t- Zweites Detail\n\t- Nächster Textblock\n",
            s.ReadBook());
        AssertChangedLines(DetailBook, s.ReadBook(), removed: [], added: [5, 6, 8]);
        Assert.Equal(s.Text("Hallo Welt").Key, r.TextBlockKey);
        Assert.Equal(
            $"- Kern\n  collapsed:: true\n\t- Detail\n- Zweite Notiz\n  id:: {noteId}\n  used-in:: [[Buch - Test]] (({r.TextBlockId}))\n",
            s.ReadJournal());
        AssertChangedLines(LinkSetup.DefaultJournal, s.ReadJournal(), removed: [], added: [4, 5]);
    }

    [Fact]
    public void AdoptInto_NoteAlreadySource_NotDuplicated()
    {
        var textId = Guid.Parse("0192b1c4-0000-7000-8000-000000000001");
        var noteId = Guid.Parse("0192b1c4-0000-7000-8000-000000000002");
        var book =
            "title:: Buch: Test\ntype:: book\n\n- # Kapitel\n" +
            $"\t- Hallo Welt\n\t  id:: {textId}\n\t  source:: (({noteId}))\n" +
            "\t\t- Zweites Detail\n";
        var journal = $"- Kern\n  id:: {noteId}\n  used-in:: [[Buch - Test]] (({textId}))\n\t- Detail\n- Zweite Notiz\n";
        using var s = new LinkSetup(book, journal);

        var r = s.Links.AdoptInto(s.Book, s.Note("Kern").Key, new IntoPosition.FirstChild(s.Text("Hallo Welt").Key));

        Assert.Equal(textId, r.TextBlockId);
        Assert.Equal(
            "title:: Buch: Test\ntype:: book\n\n- # Kapitel\n" +
            $"\t- Hallo Welt\n\t  id:: {textId}\n\t  source:: (({noteId}))\n" +
            "\t\t- Kern\n\t\t\t- Detail\n\t\t- Zweites Detail\n",
            s.ReadBook());
        AssertChangedLines(book, s.ReadBook(), removed: [], added: [7, 8]);
        Assert.Equal(journal, s.ReadJournal());
        Assert.Equal([noteId], Book.Load(s.Book.Page).FindTextBlock(r.TextBlockKey)!.Sources);

        // The note was a source before: undo keeps its usage.
        s.Undo.Undo();

        Assert.Equal(book, s.ReadBook());
        Assert.Equal(journal, s.ReadJournal());
    }

    [Fact]
    public void AdoptInto_Undo_RestoresBlockAndNote()
    {
        using var s = new LinkSetup(DetailBook);
        s.Links.AdoptInto(s.Book, s.Note("Kern").Key, new IntoPosition.AtCursor(BookBlock(s, "**Erstes** Detail").Key, 3));
        var noteId = s.Note("Kern").Id;

        s.Undo.Undo();

        Assert.Equal(DetailBook, s.ReadBook());
        Assert.Equal($"- Kern\n  collapsed:: true\n  id:: {noteId}\n\t- Detail\n- Zweite Notiz\n", s.ReadJournal());
        AssertChangedLines(LinkSetup.DefaultJournal, s.ReadJournal(), removed: [], added: [2]);
        Assert.False(s.Book.Page.IsDirty);
        Assert.False(s.Undo.CanUndo);
        Assert.Empty(s.Pending.Load());
    }

    [Fact]
    public void AdoptInto_Undo_AfterFurtherEdit_Refused()
    {
        using var s = new LinkSetup(DetailBook);
        var r = s.Links.AdoptInto(s.Book, s.Note("Zweite Notiz").Key, new IntoPosition.FirstChild(s.Text("Hallo Welt").Key));
        BookBlock(s, "Zweites Detail").SetContent("Zweites Detail, geändert");
        s.Writer.Save(s.Book.Page);
        var book = s.ReadBook();
        var journal = s.ReadJournal();

        Assert.Throws<InvalidOperationException>(() => s.Undo.Undo());

        Assert.Equal(book, s.ReadBook());
        Assert.Contains($"\t- Hallo Welt\n\t  id:: {r.TextBlockId}\n", book);
        Assert.Contains("\t\t- Zweite Notiz\n", book);
        Assert.Equal(journal, s.ReadJournal());
        Assert.Contains($"used-in:: [[Buch - Test]] (({r.TextBlockId}))", journal);
        Assert.False(s.Book.Page.IsDirty);
        Assert.False(s.Undo.CanUndo);
    }

    [Fact]
    public void AdoptInto_SubBulletOfNote_LinksThatSubBullet()
    {
        using var s = new LinkSetup(DetailBook);

        var r = s.Links.AdoptInto(s.Book, s.Note("Detail").Key, new IntoPosition.AtCursor(s.Text("Hallo Welt").Key, 10));

        var childId = s.Note("Detail").Id;
        Assert.NotNull(childId);
        Assert.Null(s.Note("Kern").Id);
        Assert.Equal(
            "title:: Buch: Test\ntype:: book\n\n- # Kapitel\n" +
            $"\t- Hallo WeltDetail\n\t  id:: {r.TextBlockId}\n\t  source:: (({childId}))\n" +
            "\t\t- **Erstes** Detail\n\t\t\t- Tieferes Detail\n\t\t- Zweites Detail\n\t- Nächster Textblock\n",
            s.ReadBook());
        AssertChangedLines(DetailBook, s.ReadBook(), removed: [4], added: [4, 5, 6]);
        Assert.Equal(
            $"- Kern\n  collapsed:: true\n\t- Detail\n\t  id:: {childId}\n\t  used-in:: [[Buch - Test]] (({r.TextBlockId}))\n- Zweite Notiz\n",
            s.ReadJournal());
        AssertChangedLines(LinkSetup.DefaultJournal, s.ReadJournal(), removed: [], added: [3, 4]);
    }

    private static Block BookBlock(LinkSetup s, string content) => s.Book.Page.AllBlocks().Single(b => b.Content == content);

    private static void AssertChangedLines(string before, string after, int[] removed, int[] added)
    {
        var (actualRemoved, actualAdded) = LineDiff.Changed(Encoding.UTF8.GetBytes(before), Encoding.UTF8.GetBytes(after));
        Assert.Equal(removed, actualRemoved);
        Assert.Equal(added, actualAdded);
    }

    private static string BookBlockText(LinkSetup s, AdoptResult r)
    {
        var block = s.Book.Page.AllBlocks().Single(b => b.Id == r.TextBlockId);
        return string.Concat(block.Lines.Concat(block.Children.SelectMany(c => c.Lines)).Select(l => l.Text + l.Ending));
    }
}
