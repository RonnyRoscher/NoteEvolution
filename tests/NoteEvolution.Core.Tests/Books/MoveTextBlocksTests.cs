using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Links;
using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Tests.Books;

/// <summary>Text blocks moved with their details to another place in the book (dragged between the split view's halves).</summary>
public class MoveTextBlocksTests
{
    private const string Id = "6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70";
    private const string Note = "7f3a91c2-4b1d-4e8a-9c3f-1a2b3c4d5e6f";

    private const string Text =
        "type:: book\n" +
        "\n" +
        "- Vorspann\n" +
        "- # Eins\n" +
        "\t- Text eins\n" +
        $"\t  id:: {Id}\n" +
        $"\t  source:: (({Note}))\n" +
        "\t  updated:: 2026-03-01 10:00\n" +
        "\t\t- Detail 1\n" +
        "\t\t\t- Detail 1a\n" +
        "\t- ## Eins-A\n" +
        "\t\t- Text A\n" +
        "\t- Text nach A\n" +
        "- # Zwei\n" +
        "\t- Text zwei\n" +
        "\t- ## Zwei-A\n";

    private const string TextEinsAt3 =
        "\t\t- Text eins\n" +
        $"\t\t  id:: {Id}\n" +
        $"\t\t  source:: (({Note}))\n" +
        "\t\t  updated:: 2026-03-01 10:00\n" +
        "\t\t\t- Detail 1\n" +
        "\t\t\t\t- Detail 1a\n";

    private static Book Load(string text = Text) =>
        Book.Load(LogseqParser.Parse("Buch - Test.md", Encoding.UTF8.GetBytes(text)));

    private static string Serialize(Book book) => Encoding.UTF8.GetString(PageSerializer.Serialize(book.Page));

    private static Guid KeyOf(Book book, string content) => book.Page.AllBlocks().Single(b => b.Content == content).Key;

    private static Guid SectionOf(Book book, string title) => KeyOf(book, title);

    [Fact]
    public void AfterTextBlockInAnotherChapter_MovesWithDetails_KeepsProperties()
    {
        var book = Load();

        var moved = ManuscriptEditor.MoveTextBlocks(book, [KeyOf(book, "Text eins")], new InsertPosition.After(KeyOf(book, "Text zwei")));

        Assert.True(moved);
        Assert.Equal(
            "type:: book\n\n- Vorspann\n- # Eins\n" +
            "\t- ## Eins-A\n\t\t- Text A\n\t- Text nach A\n" +
            "- # Zwei\n\t- Text zwei\n" +
            "\t- Text eins\n" +
            $"\t  id:: {Id}\n" +
            $"\t  source:: (({Note}))\n" +
            "\t  updated:: 2026-03-01 10:00\n" +
            "\t\t- Detail 1\n" +
            "\t\t\t- Detail 1a\n" +
            "\t- ## Zwei-A\n",
            Serialize(book));
    }

    [Fact]
    public void SectionEnd_IntoADeeperSection_Reindented()
    {
        var book = Load();

        ManuscriptEditor.MoveTextBlocks(book, [KeyOf(book, "Text eins")], new InsertPosition.SectionEnd(SectionOf(book, "## Eins-A")));

        Assert.Equal(
            "type:: book\n\n- Vorspann\n- # Eins\n" +
            "\t- ## Eins-A\n\t\t- Text A\n" + TextEinsAt3 +
            "\t- Text nach A\n- # Zwei\n\t- Text zwei\n\t- ## Zwei-A\n",
            Serialize(book));
    }

    [Fact]
    public void SeveralBlocks_KeepTheirOrder_AtSectionStart()
    {
        var book = Load();

        ManuscriptEditor.MoveTextBlocks(
            book, [KeyOf(book, "Text A"), KeyOf(book, "Text nach A")], new InsertPosition.SectionStart(SectionOf(book, "# Zwei")));

        Assert.Equal(
            "type:: book\n\n- Vorspann\n- # Eins\n" +
            "\t- Text eins\n" +
            $"\t  id:: {Id}\n" +
            $"\t  source:: (({Note}))\n" +
            "\t  updated:: 2026-03-01 10:00\n" +
            "\t\t- Detail 1\n" +
            "\t\t\t- Detail 1a\n" +
            "\t- ## Eins-A\n" +
            "- # Zwei\n\t- Text A\n\t- Text nach A\n\t- Text zwei\n\t- ## Zwei-A\n",
            Serialize(book));
    }

    [Fact]
    public void LaterInTheSameSection_CountedWithoutTheMovedBlock()
    {
        var book = Load();

        ManuscriptEditor.MoveTextBlocks(book, [KeyOf(book, "Text eins")], new InsertPosition.After(KeyOf(book, "Text nach A")));

        Assert.Equal(
            "type:: book\n\n- Vorspann\n- # Eins\n" +
            "\t- ## Eins-A\n\t\t- Text A\n\t- Text nach A\n" +
            "\t- Text eins\n" +
            $"\t  id:: {Id}\n" +
            $"\t  source:: (({Note}))\n" +
            "\t  updated:: 2026-03-01 10:00\n" +
            "\t\t- Detail 1\n" +
            "\t\t\t- Detail 1a\n" +
            "- # Zwei\n\t- Text zwei\n\t- ## Zwei-A\n",
            Serialize(book));
    }

    [Fact]
    public void IntoThePrologue()
    {
        var book = Load();

        ManuscriptEditor.MoveTextBlocks(book, [KeyOf(book, "Text zwei")], new InsertPosition.SectionEnd(Guid.Empty));

        Assert.StartsWith("type:: book\n\n- Vorspann\n- Text zwei\n- # Eins\n", Serialize(book));
    }

    [Fact]
    public void AfterItself_NothingChanges()
    {
        var book = Load();
        var key = KeyOf(book, "Text eins");

        Assert.False(ManuscriptEditor.MoveTextBlocks(book, [key], new InsertPosition.After(key)));
        Assert.Equal(Text, Serialize(book));
        Assert.False(book.Page.IsDirty);
    }

    [Fact]
    public void AlreadyThere_NothingChanges()
    {
        var book = Load();

        Assert.False(ManuscriptEditor.MoveTextBlocks(book, [KeyOf(book, "Text zwei")], new InsertPosition.SectionStart(SectionOf(book, "# Zwei"))));
        Assert.Equal(Text, Serialize(book));
    }

    [Fact]
    public void NoTextBlock_Refused_NothingChanges()
    {
        var book = Load();

        Assert.Throws<ArgumentException>(() => ManuscriptEditor.MoveTextBlocks(
            book, [KeyOf(book, "Text eins"), KeyOf(book, "## Eins-A")], new InsertPosition.SectionEnd(SectionOf(book, "# Zwei"))));
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.MoveTextBlocks(
            book, [KeyOf(book, "Detail 1")], new InsertPosition.SectionEnd(SectionOf(book, "# Zwei"))));
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.MoveTextBlocks(
            book, [], new InsertPosition.SectionEnd(SectionOf(book, "# Zwei"))));
        Assert.Equal(Text, Serialize(book));
    }
}
