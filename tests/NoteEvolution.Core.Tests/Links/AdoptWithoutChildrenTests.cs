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
            $"- Kern #notiz\n  id:: {noteId}\n  used-in:: [[Buch - Test]] (({r.TextBlockId}))\n  used-without-children:: true\n" +
            "\t- Unter A\n\t\t- Tiefer\n\t- Unter B\n- Zweite Notiz\n",
            s.ReadJournal());
        Assert.Equal(1, CountOf(s.ReadJournal(), "used-in::"));

        s.Undo.Undo();

        Assert.Equal(Book, s.ReadBook());
        Assert.DoesNotContain("used-in::", s.ReadJournal());
        Assert.DoesNotContain("used-without-children::", s.ReadJournal());
    }

    [Fact]
    public void WithoutChildren_SubPointsStayUnused_WithChildrenTheyAreUsed()
    {
        using var s = new LinkSetup(Book, Journal);
        var textKey = s.Text("Hallo Welt").Key;
        bool Used(string content) => NoteEvolution.Core.Vaults.NoteBlock.IsUsedBlock(s.Note(content));

        // A sub-point adopted alone: its own sub-point stays unused, the main point too.
        s.Links.Adopt(s.Book, s.Note("Unter A").Key, new InsertPosition.After(textKey), includeChildren: false);

        Assert.True(Used("Unter A"));
        Assert.False(Used("Tiefer"));
        Assert.False(Used("Kern #notiz"));

        // The main point alone: its sub-points stay unused (only "Unter A" has a usage of its own).
        s.Links.Adopt(s.Book, s.Note("Kern #notiz").Key, new InsertPosition.After(textKey), includeChildren: false);

        Assert.True(Used("Kern #notiz"));
        Assert.False(Used("Unter B"));
        Assert.False(Used("Tiefer"));

        // Adopted with its sub-points as well, the mark goes: now they are used through it.
        s.Links.Adopt(s.Book, s.Note("Kern #notiz").Key, new InsertPosition.After(textKey));

        Assert.Null(s.Note("Kern #notiz").GetProperty("used-without-children"));
        Assert.True(Used("Unter B"));
        Assert.True(Used("Tiefer"));
        Assert.Equal(1, CountOf(s.ReadJournal(), "used-without-children::"));

        // Adopting it alone again does not bring the mark back: an earlier usage took its sub-points.
        s.Links.Adopt(s.Book, s.Note("Kern #notiz").Key, new InsertPosition.After(textKey), includeChildren: false);

        Assert.True(Used("Unter B"));
        Assert.Equal(1, CountOf(s.ReadJournal(), "used-without-children::"));
    }

    [Fact]
    public void WithoutChildren_RemovingTheLastSource_RemovesTheMark()
    {
        using var s = new LinkSetup(Book, Journal);
        var r = s.Links.Adopt(s.Book, s.Note("Kern #notiz").Key, new InsertPosition.After(s.Text("Hallo Welt").Key), includeChildren: false);
        Assert.Contains("used-without-children:: true", s.ReadJournal());

        s.Links.RemoveSource(s.Book, r.TextBlockKey, s.Note("Kern #notiz").Id!.Value);

        Assert.DoesNotContain("used-in::", s.ReadJournal());
        Assert.DoesNotContain("used-without-children::", s.ReadJournal());
    }

    [Fact]
    public void WithChildren_NoMark()
    {
        using var s = new LinkSetup(Book, Journal);

        s.Links.Adopt(s.Book, s.Note("Kern #notiz").Key, new InsertPosition.After(s.Text("Hallo Welt").Key));

        Assert.DoesNotContain("used-without-children::", s.ReadJournal());
    }

    [Fact]
    public void AsHeading_WithoutChildren_Marks_UndoRemovesIt()
    {
        using var s = new LinkSetup(Book, Journal);
        var section = s.Book.Root.Children.Single().Key;

        s.Links.AdoptAsHeading(s.Book, s.Note("Kern #notiz").Key, section, asSubheading: true, "(ohne Titel)", includeChildren: false);

        Assert.Contains("  used-in:: [[Buch - Test]]\n  used-without-children:: true\n", s.ReadJournal());

        s.Undo.Undo();

        Assert.DoesNotContain("used-without-children::", s.ReadJournal());
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
