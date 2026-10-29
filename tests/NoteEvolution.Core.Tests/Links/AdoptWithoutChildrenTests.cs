using NoteEvolution.Core.Books;
using NoteEvolution.Core.Links;

namespace NoteEvolution.Core.Tests.Links;

public class AdoptWithoutChildrenTests
{
    private const string Journal =
        "- Kern #notiz\n" +
        "\t- Unter A\n" +
        "\t\t- Tiefer\n" +
        "\t- Unter B\n" +
        "- Zweite Notiz\n";

    private const string Book =
        "title:: Buch: Test\ntype:: book\n\n" +
        "- # Kapitel\n" +
        "\t- Hallo Welt\n" +
        "\t\t- Erstes Detail\n" +
        "\t- Nächster Textblock\n";

    [Fact]
    public void Adopt_WithoutChildren_BookHasOnlyBlockText_UsedInOnBlock_Undo()
    {
        using var s = new LinkSetup(Book, Journal);
        var textKey = s.Text("Hallo Welt").Key;

        var r = s.Links.Adopt(s.Book, s.Note("Kern #notiz").Key, new InsertPosition.After(textKey), includeChildren: false);

        var noteId = s.Note("Kern #notiz").Id;
        Assert.Equal(
            "title:: Buch: Test\ntype:: book\n\n- # Kapitel\n\t- Hallo Welt\n\t\t- Erstes Detail\n" +
            $"\t- Kern\n\t  id:: {r.TextBlockId}\n\t  source:: (({noteId}))\n\t  status:: relevant\n" +
            "\t- Nächster Textblock\n",
            s.ReadBook());
        Assert.Equal(
            $"- Kern #notiz\n  id:: {noteId}\n  used-in:: [[Buch - Test]] (({r.TextBlockId}))\n" +
            "\t- Unter A\n\t\t- Tiefer\n\t- Unter B\n- Zweite Notiz\n",
            s.ReadJournal());
        Assert.Equal(1, CountOf(s.ReadJournal(), "used-in::"));

        s.Undo.Undo();

        Assert.Equal(Book, s.ReadBook());
        Assert.DoesNotContain("used-in::", s.ReadJournal());
    }

    [Fact]
    public void AdoptInto_AfterDetail_WithoutChildren_BookHasOnlyBlockText_UsedInOnBlock_Undo()
    {
        using var s = new LinkSetup(Book, Journal);
        var detailKey = s.Text("Hallo Welt").Paragraphs.Single(p => p.Text == "Erstes Detail").Block.Key;

        var r = s.Links.AdoptInto(s.Book, s.Note("Kern #notiz").Key, new IntoPosition.AfterDetail(detailKey), includeChildren: false);

        var book = s.ReadBook();
        Assert.Contains("\t\t- Erstes Detail\n\t\t- Kern\n\t\t  status:: relevant\n\t- Nächster Textblock\n", book);
        Assert.DoesNotContain("Unter A", book);
        Assert.DoesNotContain("Tiefer", book);
        Assert.Contains($"used-in:: [[Buch - Test]] (({r.TextBlockId}))", s.ReadJournal());
        Assert.Equal(1, CountOf(s.ReadJournal(), "used-in::"));

        s.Undo.Undo();

        Assert.Equal(Book, s.ReadBook());
        Assert.DoesNotContain("used-in::", s.ReadJournal());
    }

    [Fact]
    public void AdoptInto_AtCursor_WithoutChildren_OnlyTextGoesIn()
    {
        using var s = new LinkSetup(Book, Journal);
        var textKey = s.Text("Hallo Welt").Key;

        s.Links.AdoptInto(s.Book, s.Note("Kern #notiz").Key, new IntoPosition.AtCursor(textKey, 6), includeChildren: false);

        var book = s.ReadBook();
        Assert.Contains("Hallo KernWelt", book);
        Assert.DoesNotContain("Unter A", book);
        Assert.DoesNotContain("Unter B", book);
        Assert.Equal(1, CountOf(s.ReadJournal(), "used-in::"));
        Assert.Contains("- Kern #notiz\n  id::", s.ReadJournal());

        s.Undo.Undo();

        Assert.Equal(Book, s.ReadBook());
        Assert.DoesNotContain("used-in::", s.ReadJournal());
    }

    [Fact]
    public void AdoptAsHeading_WithoutChildren_HeadingWithoutTextBlocks_UsedInOnBlock_Undo()
    {
        using var s = new LinkSetup(Book, Journal);

        var r = s.Links.AdoptAsHeading(s.Book, s.Note("Kern #notiz").Key, Guid.Empty, asSubheading: false, "(ohne Titel)", includeChildren: false);

        var heading = s.Book.FindNode(r.HeadingKey)!;
        Assert.Equal("Kern", heading.Title);
        Assert.Empty(heading.TextBlocks);
        Assert.Empty(heading.Items.OfType<OutlineNode>());
        var book = s.ReadBook();
        Assert.DoesNotContain("Unter A", book);
        Assert.DoesNotContain("Unter B", book);
        Assert.Contains("used-in:: [[Buch - Test]]\n", s.ReadJournal());
        Assert.Equal(1, CountOf(s.ReadJournal(), "used-in::"));

        s.Undo.Undo();

        Assert.Equal(Book, s.ReadBook());
        Assert.DoesNotContain("used-in::", s.ReadJournal());
    }

    [Fact]
    public void AdoptAsHeading_WithoutChildren_NoteWithoutSubPoints_IsAccepted()
    {
        using var s = new LinkSetup(Book, Journal);

        var r = s.Links.AdoptAsHeading(s.Book, s.Note("Zweite Notiz").Key, Guid.Empty, asSubheading: false, "(ohne Titel)", includeChildren: false);

        Assert.Equal("Zweite Notiz", s.Book.FindNode(r.HeadingKey)!.Title);
    }

    private static int CountOf(string text, string needle) =>
        text.Split(needle).Length - 1;
}
