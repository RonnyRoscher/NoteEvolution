using NoteEvolution.Core.Books;
using NoteEvolution.Core.Links;
using NoteEvolution.Core.Model;
using NoteEvolution.TestSupport;

namespace NoteEvolution.Core.Tests.Links;

public class AdoptAsHeadingTests
{
    private const string Journal =
        "- Thema **fett** #notiz\n" +
        "\t- Punkt eins\n" +
        "\t  collapsed:: true\n" +
        "\t\t- Tiefer\n" +
        "\t- Punkt zwei\n" +
        "- Zweite Notiz\n";

    private const string Untitled = "(ohne Titel)";

    [Fact]
    public void AsHeading_SameLevelAfterSection_OneTextBlockPerSubPoint_LinksBothSides()
    {
        using var s = new LinkSetup(journal: Journal);

        var r = s.Links.AdoptAsHeading(s.Book, s.Note("Thema **fett** #notiz").Key, s.SectionKey("Abschnitt"), asSubheading: false, Untitled);

        var (one, two) = (BookId(s, "Punkt eins"), BookId(s, "Punkt zwei"));
        var (noteId, oneId, twoId) = (s.Note("Thema **fett** #notiz").Id, s.Note("Punkt eins").Id, s.Note("Punkt zwei").Id);
        Assert.Equal(
            "title:: Buch: Test\ntype:: book\n\n- Vorspann\n- # Kapitel\n\t- ## Abschnitt\n" +
            "\t\t- Erster Textblock\n\t\t- Zweiter Textblock\n\t\t- ### Unterabschnitt\n\t\t\t- Text im Unterabschnitt\n" +
            "\t- ## Thema **fett**\n" +
            $"\t\t- Punkt eins\n\t\t  id:: {one}\n\t\t  source:: (({oneId}))\n\t\t  status:: relevant\n\t\t\t- Tiefer\n" +
            $"\t\t- Punkt zwei\n\t\t  id:: {two}\n\t\t  source:: (({twoId}))\n\t\t  status:: relevant\n",
            s.ReadBook());
        Assert.Equal(
            $"- Thema **fett** #notiz\n  id:: {noteId}\n  used-in:: [[Buch - Test]]\n" +
            $"\t- Punkt eins\n\t  collapsed:: true\n\t  id:: {oneId}\n\t  used-in:: [[Buch - Test]] (({one}))\n\t\t- Tiefer\n" +
            $"\t- Punkt zwei\n\t  id:: {twoId}\n\t  used-in:: [[Buch - Test]] (({two}))\n" +
            "- Zweite Notiz\n",
            s.ReadJournal());
        var heading = s.Book.FindNode(r.HeadingKey)!;
        Assert.Equal(("Thema **fett**", 2, "Kapitel"), (heading.Title, heading.Level, heading.Parent!.Title));
        Assert.Equal(["Text im Unterabschnitt"], s.Book.FindNode(s.SectionKey("Unterabschnitt"))!.TextBlocks.Select(t => t.Text));
        Assert.False(r.NoteUpdatePending);
        Assert.Equal("Übernehmen", s.Undo.NextDescription);
        Assert.Empty(s.Pending.Load());
    }

    [Fact]
    public void AsSubheading_OneLevelDeeper_AfterOwnTextBlocks_BeforeSubsections()
    {
        using var s = new LinkSetup(journal: Journal);

        var r = s.Links.AdoptAsHeading(s.Book, s.Note("Thema **fett** #notiz").Key, s.SectionKey("Abschnitt"), asSubheading: true, Untitled);

        Assert.Contains(
            "\t\t- Zweiter Textblock\n\t\t- ### Thema **fett**\n\t\t\t- Punkt eins\n", s.ReadBook());
        Assert.Contains("\t\t\t\t- Tiefer\n\t\t\t- Punkt zwei\n", s.ReadBook());
        Assert.EndsWith("\t\t- ### Unterabschnitt\n\t\t\t- Text im Unterabschnitt\n", s.ReadBook());
        var heading = s.Book.FindNode(r.HeadingKey)!;
        Assert.Equal((3, "Abschnitt"), (heading.Level, heading.Parent!.Title));
        Assert.Equal(["Erster Textblock", "Zweiter Textblock"], s.Book.FindNode(s.SectionKey("Abschnitt"))!.TextBlocks.Select(t => t.Text));
        Assert.Equal(["Punkt eins", "Punkt zwei"], heading.TextBlocks.Select(t => t.Text));
        Assert.Equal(["Tiefer"], heading.TextBlocks.First().Paragraphs.Select(p => p.Text));
    }

    [Fact]
    public void Root_AsHeading_Level1AtEndOfBook()
    {
        using var s = new LinkSetup(journal: Journal);

        var r = s.Links.AdoptAsHeading(s.Book, s.Note("Punkt eins").Key, Guid.Empty, asSubheading: false, Untitled);

        Assert.EndsWith("\t\t\t- Text im Unterabschnitt\n- # Punkt eins\n\t- Tiefer\n", WithoutProperties(s.ReadBook()));
        Assert.Equal(1, s.Book.FindNode(r.HeadingKey)!.Level);
    }

    [Fact]
    public void Root_AsSubheading_Level1AfterPrologue()
    {
        using var s = new LinkSetup(journal: Journal);

        s.Links.AdoptAsHeading(s.Book, s.Note("Punkt eins").Key, Guid.Empty, asSubheading: true, Untitled);

        Assert.StartsWith(
            "title:: Buch: Test\ntype:: book\n\n- Vorspann\n- # Punkt eins\n\t- Tiefer\n- # Kapitel\n", WithoutProperties(s.ReadBook()));
    }

    [Fact]
    public void Level6Section_AsSubheading_Refused_AsHeadingStaysLevel6()
    {
        const string book = "title:: Buch: Test\ntype:: book\n\n- # A\n\t- ## B\n\t\t- ### C\n\t\t\t- #### D\n\t\t\t\t- ##### E\n\t\t\t\t\t- ###### F\n";
        using var s = new LinkSetup(book, Journal);

        Assert.Throws<ArgumentException>(() =>
            s.Links.AdoptAsHeading(s.Book, s.Note("Punkt eins").Key, s.SectionKey("F"), asSubheading: true, Untitled));
        Assert.Equal(book, s.ReadBook());
        Assert.Equal(Journal, s.ReadJournal());
        Assert.Null(s.Note("Punkt eins").Id);
        Assert.False(s.Undo.CanUndo);

        var r = s.Links.AdoptAsHeading(s.Book, s.Note("Punkt eins").Key, s.SectionKey("F"), asSubheading: false, Untitled);
        Assert.Equal((6, "E"), (s.Book.FindNode(r.HeadingKey)!.Level, s.Book.FindNode(r.HeadingKey)!.Parent!.Title));
    }

    [Fact]
    public void NoteWithoutSubPoints_Refused()
    {
        using var s = new LinkSetup(journal: Journal);

        Assert.Throws<ArgumentException>(() =>
            s.Links.AdoptAsHeading(s.Book, s.Note("Zweite Notiz").Key, Guid.Empty, asSubheading: false, Untitled));
        Assert.Equal(LinkSetup.DefaultBook, s.ReadBook());
    }

    [Fact]
    public void FurtherLines_BecomeFirstTextBlock_LinkedToTheNoteBlock()
    {
        const string journal = "- Titelzeile  \n  weiterer Text\n  noch mehr\n\t- Punkt\n";
        using var s = new LinkSetup(journal: journal);

        var r = s.Links.AdoptAsHeading(s.Book, s.Note("Titelzeile  \nweiterer Text\nnoch mehr").Key, Guid.Empty, asSubheading: false, Untitled);

        var heading = s.Book.FindNode(r.HeadingKey)!;
        Assert.Equal("Titelzeile", heading.Title);
        Assert.Equal(["weiterer Text\nnoch mehr", "Punkt"], heading.TextBlocks.Select(t => t.Text));
        var (extra, point) = (heading.TextBlocks.First(), heading.TextBlocks.Last());
        var noteId = s.Note("Titelzeile  \nweiterer Text\nnoch mehr").Id!.Value;
        Assert.Equal([noteId], extra.Sources);
        Assert.Equal(BlockStatus.Relevant, extra.Status);
        Assert.Equal(
            $"- Titelzeile  \n  id:: {noteId}\n  used-in:: [[Buch - Test]] (({extra.Block.Id}))\n  weiterer Text\n  noch mehr\n" +
            $"\t- Punkt\n\t  id:: {s.Note("Punkt").Id}\n\t  used-in:: [[Buch - Test]] (({point.Block.Id}))\n",
            s.ReadJournal());
    }

    [Fact]
    public void BlankFirstLine_TitleIsThePlaceholder()
    {
        const string journal = "- \n  weiterer Text\n\t- Punkt\n";
        using var s = new LinkSetup(journal: journal);
        var note = s.Journal.Roots[0];

        var r = s.Links.AdoptAsHeading(s.Book, note.Key, Guid.Empty, asSubheading: false, Untitled);

        Assert.Equal(Untitled, s.Book.FindNode(r.HeadingKey)!.Title);
        Assert.Equal(["weiterer Text", "Punkt"], s.Book.FindNode(r.HeadingKey)!.TextBlocks.Select(t => t.Text));
    }

    [Fact]
    public void NewTextBlocksHaveBothDates()
    {
        var now = new DateTimeOffset(2026, 10, 19, 14, 32, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 19, 14, 32, 0)));
        using var s = new LinkSetup(journal: Journal, clock: new FakeClock(now));

        var r = s.Links.AdoptAsHeading(s.Book, s.Note("Thema **fett** #notiz").Key, Guid.Empty, asSubheading: false, Untitled);

        var textBlocks = s.Book.FindNode(r.HeadingKey)!.TextBlocks.ToList();
        Assert.Equal(2, textBlocks.Count);
        Assert.All(textBlocks, t => Assert.Equal((now.DateTime, now.DateTime), (t.Created, t.Updated)));
        Assert.Equal(2, s.ReadBook().Split("created:: 2026-10-19T14:32").Length - 1);
    }

    [Fact]
    public void BookWriteFails_ReloadsBook_RevertsNotes_Rethrows()
    {
        using var s = new LinkSetup(journal: Journal);
        s.Writer.FailNext(s.BookPath);

        Assert.Throws<IOException>(() =>
            s.Links.AdoptAsHeading(s.Book, s.Note("Thema **fett** #notiz").Key, Guid.Empty, asSubheading: false, Untitled));

        Assert.Equal(LinkSetup.DefaultBook, s.ReadBook());
        Assert.DoesNotContain(s.Book.Page.AllBlocks(), b => b.Content.Contains("Thema", StringComparison.Ordinal));
        Assert.Equal(Journal, s.ReadJournal());
        Assert.All(s.Journal.AllBlocks(), b => Assert.Null(b.Id));
        Assert.False(s.Journal.IsDirty);
        Assert.False(s.Undo.CanUndo);
        Assert.Empty(s.Pending.Load());
    }

    [Fact]
    public void NoteWriteFails_BookKept_UsagesWaitInPending()
    {
        using var s = new LinkSetup(journal: Journal);
        s.Writer.FailNext(s.JournalPath, times: 2);

        var r = s.Links.AdoptAsHeading(s.Book, s.Note("Thema **fett** #notiz").Key, Guid.Empty, asSubheading: false, Untitled);

        Assert.True(r.NoteUpdatePending);
        Assert.Contains("- # Thema **fett**\n", s.ReadBook());
        Assert.Equal(Journal, s.ReadJournal());
        Assert.Equal(
            [BookId(s, "Punkt eins"), BookId(s, "Punkt zwei")],
            s.Pending.Load().Select(p => p.BookBlockId).Order());
    }

    [Fact]
    public void Undo_RemovesHeadingAndAllUsages()
    {
        using var s = new LinkSetup(journal: Journal);
        s.Links.AdoptAsHeading(s.Book, s.Note("Thema **fett** #notiz").Key, s.SectionKey("Abschnitt"), asSubheading: true, Untitled);
        var (noteId, oneId, twoId) = (s.Note("Thema **fett** #notiz").Id, s.Note("Punkt eins").Id, s.Note("Punkt zwei").Id);

        s.Undo.Undo();

        Assert.Equal(LinkSetup.DefaultBook, s.ReadBook());
        Assert.Equal(
            $"- Thema **fett** #notiz\n  id:: {noteId}\n" +
            $"\t- Punkt eins\n\t  collapsed:: true\n\t  id:: {oneId}\n\t\t- Tiefer\n" +
            $"\t- Punkt zwei\n\t  id:: {twoId}\n- Zweite Notiz\n",
            s.ReadJournal());
        Assert.False(s.Undo.CanUndo);
        Assert.Empty(s.Pending.Load());
    }

    [Fact]
    public void Undo_KeepsAnEntryWithoutBlockReferenceThatWasThereBefore()
    {
        const string journal = "- Thema\n  used-in:: [[Buch - Test]]\n\t- Punkt\n";
        using var s = new LinkSetup(journal: journal);
        s.Links.AdoptAsHeading(s.Book, s.Note("Thema").Key, Guid.Empty, asSubheading: false, Untitled);

        s.Undo.Undo();

        Assert.Contains("  used-in:: [[Buch - Test]]\n", s.ReadJournal());
    }

    private static Guid BookId(LinkSetup s, string content) => s.Book.Page.AllBlocks().Single(b => b.Content == content).Id!.Value;

    /// <summary>The book text without the property lines of the adopted blocks.</summary>
    private static string WithoutProperties(string text) =>
        string.Concat(text.Split('\n').Where(line => !line.Contains(":: ", StringComparison.Ordinal) || line.StartsWith("t", StringComparison.Ordinal))
            .Select(line => line + "\n"))[..^1];
}
