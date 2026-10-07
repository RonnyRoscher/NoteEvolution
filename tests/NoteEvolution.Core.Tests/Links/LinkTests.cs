using NoteEvolution.Core.Books;

namespace NoteEvolution.Core.Tests.Links;

public class LinkTests
{
    [Fact]
    public void Link_AddsSourceAndUsedIn_BothSides_ByteExactOtherwise()
    {
        using var s = new LinkSetup();
        var text = s.Text("Erster Textblock");

        s.Links.Link(s.Book, text.Key, s.Note("Kern").Key);

        var noteId = s.Note("Kern").Id;
        var textId = s.Book.Page.AllBlocks().Single(b => b.Content == "Erster Textblock").Id;
        Assert.NotNull(noteId);
        Assert.NotNull(textId);
        Assert.Equal(
            "title:: Buch: Test\ntype:: book\n\n- Vorspann\n- # Kapitel\n\t- ## Abschnitt\n" +
            $"\t\t- Erster Textblock\n\t\t  id:: {textId}\n\t\t  source:: (({noteId}))\n" +
            "\t\t- Zweiter Textblock\n\t\t- ### Unterabschnitt\n\t\t\t- Text im Unterabschnitt\n",
            s.ReadBook());
        Assert.Equal(
            $"- Kern\n  collapsed:: true\n  id:: {noteId}\n  used-in:: [[Buch - Test]] (({textId}))\n" +
            "\t- Detail\n- Zweite Notiz\n",
            s.ReadJournal());
        Assert.Equal([noteId!.Value], Book.Load(s.Book.Page).FindTextBlock(text.Key)!.Sources);
        Assert.False(s.Undo.CanUndo);
        Assert.Empty(s.Pending.Load());
    }

    [Fact]
    public void Link_ExistingSource_AppendsNoteId()
    {
        const string book = LinkSetup.DefaultBook;
        using var s = new LinkSetup(book.Replace(
            "- Erster Textblock\n", "- Erster Textblock\n\t\t  id:: 6f1c2a3b-0000-4000-8000-000000000001\n\t\t  source:: ((6f1c2a3b-0000-4000-8000-000000000002))\n"));

        s.Links.Link(s.Book, s.Text("Erster Textblock").Key, s.Note("Kern").Key);

        var noteId = s.Note("Kern").Id;
        Assert.Contains(
            "\t\t  id:: 6f1c2a3b-0000-4000-8000-000000000001\n" +
            $"\t\t  source:: ((6f1c2a3b-0000-4000-8000-000000000002)), (({noteId}))\n",
            s.ReadBook());
        Assert.Contains("used-in:: [[Buch - Test]] ((6f1c2a3b-0000-4000-8000-000000000001))", s.ReadJournal());
    }

    [Fact]
    public void Link_ReplacesIdlessEntryForSameBook()
    {
        using var s = new LinkSetup(journal: "- Kern\n  used-in:: [[Anderes]], [[buch - test]], [[Drittes]]\n- Zweite Notiz\n");

        s.Links.Link(s.Book, s.Text("Erster Textblock").Key, s.Note("Kern").Key);

        var noteId = s.Note("Kern").Id;
        var textId = s.Book.Page.AllBlocks().Single(b => b.Content == "Erster Textblock").Id;
        Assert.Equal(
            $"- Kern\n  used-in:: [[Anderes]], [[Buch - Test]] (({textId})), [[Drittes]]\n  id:: {noteId}\n- Zweite Notiz\n",
            s.ReadJournal());
    }

    [Fact]
    public void Link_KeepsEntriesOfOtherBlocksOfSameBook()
    {
        using var s = new LinkSetup(
            journal: "- Kern\n  used-in:: [[Buch - Test]] ((6f1c2a3b-0000-4000-8000-000000000009))\n- Zweite Notiz\n");

        s.Links.Link(s.Book, s.Text("Erster Textblock").Key, s.Note("Kern").Key);

        var textId = s.Book.Page.AllBlocks().Single(b => b.Content == "Erster Textblock").Id;
        Assert.Contains(
            $"used-in:: [[Buch - Test]] ((6f1c2a3b-0000-4000-8000-000000000009)), [[Buch - Test]] (({textId}))\n", s.ReadJournal());
    }

    [Fact]
    public void Link_AlreadyLinked_NoDuplicate_WritesNothing()
    {
        using var s = new LinkSetup();
        var text = s.Text("Erster Textblock");
        var note = s.Note("Kern");
        s.Links.Link(s.Book, text.Key, note.Key);
        var bookAfter = s.ReadBook();
        var journalAfter = s.ReadJournal();
        s.Writer.FailNext(s.BookPath);
        s.Writer.FailNext(s.JournalPath);

        s.Links.Link(s.Book, text.Key, note.Key);

        Assert.Equal(bookAfter, s.ReadBook());
        Assert.Equal(journalAfter, s.ReadJournal());
        Assert.Empty(s.Pending.Load());
        Assert.False(s.Book.Page.IsDirty);
        Assert.False(s.Journal.IsDirty);
    }

    [Fact]
    public void Link_ReadOnlyNote_ThrowsBeforeChange()
    {
        const string broken = "- Kaputt\n  ```\n  nie geschlossen\n";
        using var s = new LinkSetup(journal: broken);
        var note = s.Journal.AllBlocks().First();

        Assert.Throws<InvalidOperationException>(() =>
            s.Links.Link(s.Book, s.Text("Erster Textblock").Key, note.Key));

        Assert.Equal(LinkSetup.DefaultBook, s.ReadBook());
        Assert.Equal(broken, s.ReadJournal());
        Assert.Null(s.Text("Erster Textblock").Block.Id);
        Assert.False(s.Book.Page.IsDirty);
        Assert.Empty(s.Pending.Load());
    }

    [Fact]
    public void Link_UnknownKeys_ThrowBeforeChange()
    {
        using var s = new LinkSetup();

        Assert.Throws<ArgumentException>(() => s.Links.Link(s.Book, Guid.NewGuid(), s.Note("Kern").Key));
        Assert.Throws<ArgumentException>(() => s.Links.Link(s.Book, s.Text("Erster Textblock").Key, Guid.NewGuid()));
        // A book block is no note.
        Assert.Throws<ArgumentException>(() =>
            s.Links.Link(s.Book, s.Text("Erster Textblock").Key, s.Text("Zweiter Textblock").Key));

        Assert.Equal(LinkSetup.DefaultBook, s.ReadBook());
        Assert.Equal(LinkSetup.DefaultJournal, s.ReadJournal());
        Assert.Null(s.Text("Erster Textblock").Block.Id);
        Assert.Null(s.Note("Kern").Id);
        Assert.False(s.Book.Page.IsDirty);
        Assert.False(s.Journal.IsDirty);
    }

    [Fact]
    public void Link_BookWriteFails_ReloadsBook_RevertsNote_Rethrows()
    {
        using var s = new LinkSetup();
        var staleBook = s.Book;
        s.Writer.FailNext(s.BookPath);

        Assert.Throws<IOException>(() =>
            s.Links.Link(staleBook, s.Text("Erster Textblock").Key, s.Note("Kern").Key));

        Assert.Equal(LinkSetup.DefaultBook, s.ReadBook());
        Assert.Equal(LinkSetup.DefaultJournal, s.ReadJournal());
        Assert.Null(s.Note("Kern").Id);
        Assert.False(s.Journal.IsDirty);
        Assert.False(s.Book.Page.IsDirty);
        Assert.Null(s.Text("Erster Textblock").Block.Id);
        Assert.Empty(s.Pending.Load());
    }

    [Fact]
    public void Link_NoteWriteFails_Pending()
    {
        using var s = new LinkSetup();
        s.Writer.FailNext(s.JournalPath);

        s.Links.Link(s.Book, s.Text("Erster Textblock").Key, s.Note("Kern").Key);

        var noteId = s.Note("Kern").Id;
        var textId = s.Book.Page.AllBlocks().Single(b => b.Content == "Erster Textblock").Id;
        Assert.Contains($"source:: (({noteId}))", s.ReadBook());
        Assert.Equal(LinkSetup.DefaultJournal, s.ReadJournal());
        var update = Assert.Single(s.Pending.Load());
        Assert.Equal(noteId, update.NoteBlockId);
        Assert.Equal(textId, update.BookBlockId);
        Assert.False(update.Remove);

        Assert.Equal(1, s.Links.RetryPending());
        Assert.Contains($"used-in:: [[Buch - Test]] (({textId}))", s.ReadJournal());
        Assert.Empty(s.Pending.Load());
    }
}
