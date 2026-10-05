using NoteEvolution.Core.Books;
using NoteEvolution.Core.Links;

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

    private static string BookBlockText(LinkSetup s, AdoptResult r)
    {
        var block = s.Book.Page.AllBlocks().Single(b => b.Id == r.TextBlockId);
        return string.Concat(block.Lines.Concat(block.Children.SelectMany(c => c.Lines)).Select(l => l.Text + l.Ending));
    }
}
