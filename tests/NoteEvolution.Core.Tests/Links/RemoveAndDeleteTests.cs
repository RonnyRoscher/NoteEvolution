using NoteEvolution.Core.Links;
using NoteEvolution.Core.Storage;

namespace NoteEvolution.Core.Tests.Links;

public class RemoveAndDeleteTests
{
    private const string B1 = "6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70";
    private const string N1 = "7f3a91c2-4b1d-4e8a-9c3f-1a2b3c4d5e6f";
    private const string N2 = "81bb02aa-5c2e-4f9b-8d4a-2b3c4d5e6f71";

    private const string LinkedBook =
        "title:: Buch: Test\n" +
        "type:: book\n" +
        "\n" +
        "- # Kapitel\n" +
        "\t- Verknüpfter Text\n" +
        $"\t  id:: {B1}\n" +
        $"\t  source:: (({N1})), (({N2}))\n" +
        "\t\t- Absatz\n" +
        "\t- Anderer Text\n";

    // CRLF, a lower-case page link and a non-canonical separator: undo must still restore the bytes.
    private const string LinkedJournal =
        "- Erste Quelle\r\n" +
        $"  id:: {N1}\r\n" +
        $"  used-in:: [[buch - test]] (({B1})),[[Anderes Buch]]\r\n" +
        "- Zweite Quelle\r\n" +
        $"  id:: {N2}\r\n" +
        $"  used-in:: [[Buch - Test]] (({B1}))\r\n";

    [Fact]
    public void RemoveSource_OneOfTwoUsages_KeepsOther()
    {
        using var s = new LinkSetup();
        var first = s.Links.Adopt(s.Book, s.Note("Kern").Key, new InsertPosition.After(s.Text("Erster Textblock").Key));
        var second = s.Links.Adopt(s.Book, s.Note("Kern").Key, new InsertPosition.SectionEnd(s.SectionKey("Abschnitt")));
        var noteId = s.Note("Kern").Id!.Value;

        s.Links.RemoveSource(s.Book, first.TextBlockKey, noteId);

        Assert.Contains($"\t\t- Kern\n\t\t  id:: {first.TextBlockId}\n\t\t\t- Detail\n", s.ReadBook());
        Assert.Contains($"\t\t- Kern\n\t\t  id:: {second.TextBlockId}\n\t\t  source:: (({noteId}))\n", s.ReadBook());
        Assert.Contains($"\n  used-in:: [[Buch - Test]] (({second.TextBlockId}))\n", s.ReadJournal());
        Assert.DoesNotContain(first.TextBlockId.ToString(), s.ReadJournal());
    }

    [Fact]
    public void RemoveSource_OneOfTwoSources_KeepsOtherSourceAndOtherBooksUsage()
    {
        using var s = new LinkSetup(LinkedBook, LinkedJournal);

        s.Links.RemoveSource(s.Book, s.Text("Verknüpfter Text").Key, Guid.Parse(N1));

        Assert.Equal(LinkedBook.Replace($"(({N1})), (({N2}))", $"(({N2}))"), s.ReadBook());
        Assert.Equal(LinkedJournal.Replace($"[[buch - test]] (({B1})),[[Anderes Buch]]", "[[Anderes Buch]]"), s.ReadJournal());
    }

    [Fact]
    public void RemoveSource_NoteOnReadOnlyPage_WritesBook_RecordsPending()
    {
        var journal = $"- Erste Quelle\n  id:: {N1}\n  used-in:: [[Buch - Test]] (({B1}))\n- ```\n  offen\n";
        using var s = new LinkSetup(LinkedBook, journal);
        Assert.True(s.Journal.IsReadOnly);

        s.Links.RemoveSource(s.Book, s.Text("Verknüpfter Text").Key, Guid.Parse(N1));

        Assert.Contains($"\t  source:: (({N2}))\n", s.ReadBook());
        Assert.Equal(journal, s.ReadJournal());
        var pending = Assert.Single(s.Pending.Load());
        Assert.Equal(Guid.Parse(N1), pending.NoteBlockId);
        Assert.Equal(Guid.Parse(B1), pending.BookBlockId);
        Assert.Equal("Buch - Test", pending.BookLinkName);
        Assert.True(pending.Remove);
        Assert.Equal(0, s.Links.RetryPending());
    }

    [Fact]
    public void DeleteTextBlock_RemovesUsedIn_UndoRestoresBothFiles()
    {
        using var s = new LinkSetup(LinkedBook, LinkedJournal);

        s.Links.DeleteTextBlock(s.Book, s.Text("Verknüpfter Text").Key);

        Assert.Equal("title:: Buch: Test\ntype:: book\n\n- # Kapitel\n\t- Anderer Text\n", s.ReadBook());
        Assert.Equal(
            $"- Erste Quelle\r\n  id:: {N1}\r\n  used-in:: [[Anderes Buch]]\r\n- Zweite Quelle\r\n  id:: {N2}\r\n",
            s.ReadJournal());
        Assert.Equal("Löschen", s.Undo.NextDescription);

        s.Undo.Undo();

        Assert.Equal(LinkedBook, s.ReadBook());
        Assert.Equal(LinkedJournal, s.ReadJournal());
        Assert.NotNull(s.Book.FindTextBlockById(Guid.Parse(B1)));
        Assert.False(s.Undo.CanUndo);
        Assert.Empty(s.Pending.Load());
    }

    [Fact]
    public void DeleteDetail_LinkedDetail_RemovesUsedIn_UndoRestoresBothFiles()
    {
        // A detail carrying its own id and source (possible in existing files), with a deeper detail.
        var book =
            "title:: Buch: Test\n" +
            "type:: book\n" +
            "\n" +
            "- # Kapitel\n" +
            "\t- Text\n" +
            "\t\t- Verknüpftes Detail\n" +
            $"\t\t  id:: {B1}\n" +
            $"\t\t  source:: (({N1}))\n" +
            "\t\t\t- Tiefer\n" +
            "\t\t- Anderes Detail\n";
        var journal = $"- Erste Quelle\n  id:: {N1}\n  used-in:: [[Buch - Test]] (({B1}))\n";
        using var s = new LinkSetup(book, journal);
        var detail = s.Book.Page.AllBlocks().Single(b => b.Content == "Verknüpftes Detail").Key;

        s.Links.DeleteDetail(s.Book, detail);

        Assert.Equal(book.Replace($"\t\t- Verknüpftes Detail\n\t\t  id:: {B1}\n\t\t  source:: (({N1}))\n\t\t\t- Tiefer\n", ""), s.ReadBook());
        Assert.Equal($"- Erste Quelle\n  id:: {N1}\n", s.ReadJournal());
        Assert.Equal("Löschen", s.Undo.NextDescription);

        s.Undo.Undo();

        Assert.Equal(book, s.ReadBook());
        Assert.Equal(journal, s.ReadJournal());
        Assert.False(s.Undo.CanUndo);

        // Only a detail is taken.
        Assert.Throws<ArgumentException>(() => s.Links.DeleteDetail(s.Book, s.Text("Text").Key));
        Assert.Throws<ArgumentException>(() => s.Links.DeleteDetail(s.Book, Guid.NewGuid()));
        Assert.Equal(book, s.ReadBook());
    }

    [Fact]
    public void DeleteTextBlock_AfterAdopt_UndoRestoresFilesExceptNewId()
    {
        using var s = new LinkSetup();
        var r = s.Links.Adopt(s.Book, s.Note("Kern").Key, new InsertPosition.After(s.Text("Erster Textblock").Key));
        var noteId = s.Note("Kern").Id;
        var bookAfterAdopt = s.ReadBook();
        var journalAfterAdopt = s.ReadJournal();

        s.Links.DeleteTextBlock(s.Book, r.TextBlockKey);

        Assert.Equal(LinkSetup.DefaultBook, s.ReadBook());
        Assert.Equal($"- Kern\n  collapsed:: true\n  id:: {noteId}\n\t- Detail\n- Zweite Notiz\n", s.ReadJournal());

        s.Undo.Undo();

        Assert.Equal(bookAfterAdopt, s.ReadBook());
        Assert.Equal(journalAfterAdopt, s.ReadJournal());
    }

    [Fact]
    public void DeleteTextBlock_LastBlockWithoutFinalNewline_UndoRestoresBytes()
    {
        var book = "type:: book\n\n- # Kapitel\n\t- Erster Text\n\t- Letzter Text";
        using var s = new LinkSetup(book);

        s.Links.DeleteTextBlock(s.Book, s.Text("Letzter Text").Key);
        Assert.Equal("type:: book\n\n- # Kapitel\n\t- Erster Text\n", s.ReadBook());

        s.Undo.Undo();
        Assert.Equal(book, s.ReadBook());
    }

    [Fact]
    public void DeleteTextBlock_NoteWriteFails_RecordsPending_UndoRestoresAndClearsIt()
    {
        using var s = new LinkSetup(LinkedBook, LinkedJournal);
        s.Writer.FailNext(s.JournalPath, 2);

        s.Links.DeleteTextBlock(s.Book, s.Text("Verknüpfter Text").Key);

        Assert.Equal(LinkedJournal, s.ReadJournal());
        Assert.Equal(2, s.Pending.Load().Count);
        Assert.All(s.Pending.Load(), p => Assert.True(p.Remove));

        s.Undo.Undo();

        Assert.Equal(LinkedBook, s.ReadBook());
        Assert.Equal(LinkedJournal, s.ReadJournal());
        Assert.Empty(s.Pending.Load());
    }

    private const string B2 = "92c1b3d4-6d3f-4a0c-8e5b-3c4d5e6f7081";
    private const string B3 = "a3d2c4e5-7e40-4b1d-9f6c-4d5e6f708192";
    private const string N3 = "b4e3d5f6-8f51-4c2e-8a7d-5e6f70819203";

    // A section with two linked text blocks and a sub-section with a third one.
    private const string SectionBook =
        "title:: Buch: Test\n" +
        "type:: book\n" +
        "\n" +
        "- # Kapitel\n" +
        "\t- ## Abschnitt\n" +
        "\t\t- Text A\n" +
        $"\t\t  id:: {B1}\n" +
        $"\t\t  source:: (({N1}))\n" +
        "\t\t- Text B\n" +
        $"\t\t  id:: {B2}\n" +
        $"\t\t  source:: (({N2}))\n" +
        "\t\t\t- Absatz\n" +
        "\t\t- ### Unterabschnitt\n" +
        "\t\t\t- Text C\n" +
        $"\t\t\t  id:: {B3}\n" +
        $"\t\t\t  source:: (({N3}))\n" +
        "\t- Anderer Text\n";

    private const string SectionJournal =
        "- Erste Quelle\r\n" +
        $"  id:: {N1}\r\n" +
        $"  used-in:: [[Buch - Test]] (({B1}))\r\n" +
        "- Zweite Quelle\r\n" +
        $"  id:: {N2}\r\n" +
        $"  used-in:: [[Buch - Test]] (({B2})),[[Anderes Buch]]\r\n" +
        "- Dritte Quelle\r\n" +
        $"  id:: {N3}\r\n" +
        $"  used-in:: [[Buch - Test]] (({B3}))\r\n";

    [Fact]
    public void DeleteSection_LinkedBlocks_RemovesUsages_UndoRestores()
    {
        using var s = new LinkSetup(SectionBook, SectionJournal);

        s.Links.DeleteSection(s.Book, s.SectionKey("Abschnitt"));

        Assert.Equal("title:: Buch: Test\ntype:: book\n\n- # Kapitel\n\t- Anderer Text\n", s.ReadBook());
        Assert.Equal(
            $"- Erste Quelle\r\n  id:: {N1}\r\n- Zweite Quelle\r\n  id:: {N2}\r\n  used-in:: [[Anderes Buch]]\r\n" +
            $"- Dritte Quelle\r\n  id:: {N3}\r\n",
            s.ReadJournal());
        Assert.Equal("Löschen", s.Undo.NextDescription);

        s.Undo.Undo();

        Assert.Equal(SectionBook, s.ReadBook());
        Assert.Equal(SectionJournal, s.ReadJournal());
        Assert.NotNull(s.Book.FindNode(s.SectionKey("Abschnitt")));
        Assert.False(s.Undo.CanUndo);
        Assert.Empty(s.Pending.Load());
    }

    [Fact]
    public void DeleteSection_Unknown_Throws()
    {
        using var s = new LinkSetup(SectionBook, SectionJournal);

        Assert.Throws<ArgumentException>(() => s.Links.DeleteSection(s.Book, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => s.Links.DeleteSection(s.Book, Guid.Empty));

        Assert.Equal(SectionBook, s.ReadBook());
        Assert.Equal(SectionJournal, s.ReadJournal());
        Assert.False(s.Undo.CanUndo);
    }

    [Fact]
    public void DeleteSection_ReadOnlyBook_Throws_NothingChanged()
    {
        var book = SectionBook + "- ```\n  offen\n";
        using var s = new LinkSetup(book, SectionJournal);
        Assert.True(s.Book.Page.IsReadOnly);

        Assert.Throws<ReadOnlyPageException>(() => s.Links.DeleteSection(s.Book, s.SectionKey("Abschnitt")));

        Assert.Equal(book, s.ReadBook());
        Assert.Equal(SectionJournal, s.ReadJournal());
        Assert.False(s.Undo.CanUndo);
    }
}
