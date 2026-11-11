using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Tests.Books;

/// <summary>Image blocks inserted into the book, captions changed, the image found at an editor offset.</summary>
public class ManuscriptEditorImageTests
{
    private const string Stamp = "2026-10-19T14:32";
    private const string Img = "![](../assets/b.png)";

    private const string Text =
        "type:: book\n" +
        "\n" +
        "- Vorspann\n" +
        "- # Eins\n" +
        "\t- Text eins\n" +
        "\t\t- Detail 1\n" +
        "\t- ## Eins-A\n" +
        "\t\t- Text A\n" +
        "\t- Text nach A\n" +
        "- # Zwei\n" +
        "- # Anhang\n" +
        "  anhang:: glossar\n" +
        "\t- Eintrag\n";

    private static readonly DateTimeOffset Now = LocalNow();

    private static DateTimeOffset LocalNow()
    {
        var local = new DateTime(2026, 10, 19, 14, 32, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static Book Load(string text = Text) =>
        Book.Load(LogseqParser.Parse("Buch - Test.md", Encoding.UTF8.GetBytes(text)));

    private static string Serialize(Book book) => Encoding.UTF8.GetString(PageSerializer.Serialize(book.Page));

    private static Guid KeyOf(Book book, string content) => book.Page.AllBlocks().Single(b => b.Content == content).Key;

    private static BookElement Element(Book book, string content) =>
        BookElements.Find(book, KeyOf(book, content))!;

    [Fact]
    public void InsertImageBlock_AtTextBlock_DirectlyAfterIt_DetailsStayWithTheFirst()
    {
        var book = Load();

        var key = ManuscriptEditor.InsertImageBlock(book, Element(book, "Text eins"), Img);

        Assert.Equal(key, KeyOf(book, Img));
        Assert.Equal(
            Text.Replace("\t\t- Detail 1\n", $"\t\t- Detail 1\n\t- {Img}\n"),
            Serialize(book));
    }

    [Fact]
    public void InsertImageBlock_AtDetail_AfterItsTextBlock()
    {
        var book = Load();

        ManuscriptEditor.InsertImageBlock(book, Element(book, "Detail 1"), Img);

        Assert.Equal(Text.Replace("\t\t- Detail 1\n", $"\t\t- Detail 1\n\t- {Img}\n"), Serialize(book));
    }

    [Fact]
    public void InsertImageBlock_AtHeading_FirstTextBlockOfTheSection()
    {
        var book = Load();

        ManuscriptEditor.InsertImageBlock(book, Element(book, "# Eins"), Img);

        Assert.Equal(Text.Replace("- # Eins\n", $"- # Eins\n\t- {Img}\n"), Serialize(book));
    }

    [Fact]
    public void InsertImageBlock_AtHeadingWithoutText_BecomesItsOnlyTextBlock()
    {
        var book = Load();

        ManuscriptEditor.InsertImageBlock(book, Element(book, "# Zwei"), Img);

        Assert.Equal(Text.Replace("- # Zwei\n", $"- # Zwei\n\t- {Img}\n"), Serialize(book));
    }

    [Fact]
    public void InsertImageBlock_AtPrologueTextBlock_AfterIt()
    {
        var book = Load();

        ManuscriptEditor.InsertImageBlock(book, Element(book, "Vorspann"), Img);

        Assert.Equal(Text.Replace("- Vorspann\n", $"- Vorspann\n- {Img}\n"), Serialize(book));
    }

    [Fact]
    public void InsertImageBlock_WithTime_HasBothDates()
    {
        var book = Load();

        ManuscriptEditor.InsertImageBlock(book, Element(book, "Text eins"), Img, Now);

        Assert.Equal(
            Text.Replace("\t\t- Detail 1\n", $"\t\t- Detail 1\n\t- {Img}\n\t  created:: {Stamp}\n\t  updated:: {Stamp}\n"),
            Serialize(book));
    }

    [Fact]
    public void InsertImageBlock_AppendixElement_ThrowsAndChangesNothing()
    {
        var book = Load();
        var entry = new BookElement(ElementKind.TextBlock, KeyOf(book, "Eintrag"));

        Assert.Throws<ArgumentException>(() => ManuscriptEditor.InsertImageBlock(book, entry, Img, Now));

        Assert.Equal(Text, Serialize(book));
        Assert.False(book.Page.IsDirty);
    }

    [Fact]
    public void SetImageCaption_SecondImage_OnlyThatOneChanges()
    {
        var text = "- a ![x](p.png) b ![y](q.png) c\n";
        var book = Load("- # H\n\t" + text);
        var key = KeyOf(book, "a ![x](p.png) b ![y](q.png) c");

        var changed = ManuscriptEditor.SetImageCaption(book, key, 1, "Neu");

        Assert.True(changed);
        Assert.Equal("- # H\n\t- a ![x](p.png) b ![Neu](q.png) c\n", Serialize(book));
    }

    [Fact]
    public void SetImageCaption_WithTime_StampsUpdated()
    {
        var book = Load("- # H\n\t- ![x](p.png)\n");

        ManuscriptEditor.SetImageCaption(book, KeyOf(book, "![x](p.png)"), 0, "Neu", Now);

        Assert.Equal($"- # H\n\t- ![Neu](p.png)\n\t  updated:: {Stamp}\n", Serialize(book));
    }

    [Fact]
    public void SetImageCaption_InDetail_StampsTheTextBlock()
    {
        var book = Load("- # H\n\t- Text\n\t\t- ![x](p.png)\n");

        ManuscriptEditor.SetImageCaption(book, KeyOf(book, "![x](p.png)"), 0, "Neu", Now);

        Assert.Equal($"- # H\n\t- Text\n\t  updated:: {Stamp}\n\t\t- ![Neu](p.png)\n", Serialize(book));
    }

    [Fact]
    public void SetImageCaption_SameCaption_ReturnsFalseAndLeavesPageClean()
    {
        var book = Load("- # H\n\t- ![x](p.png)\n");

        var changed = ManuscriptEditor.SetImageCaption(book, KeyOf(book, "![x](p.png)"), 0, "x", Now);

        Assert.False(changed);
        Assert.False(book.Page.IsDirty);
    }

    [Fact]
    public void SetImageCaption_HeadingOrNoSuchImage_Throws()
    {
        var book = Load("- # H\n\t- ![x](p.png)\n");

        Assert.Throws<ArgumentException>(() => ManuscriptEditor.SetImageCaption(book, KeyOf(book, "# H"), 0, "n"));
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.SetImageCaption(book, KeyOf(book, "![x](p.png)"), 5, "n"));
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.SetImageCaption(book, Guid.NewGuid(), 0, "n"));
    }

    [Fact]
    public void ImageIndexAt_FindsTheImageAtTheShownPosition()
    {
        var book = Load("- # H\n\t- ab ![x](p.png) cd ![y](q.png)\n");
        var key = KeyOf(book, "ab ![x](p.png) cd ![y](q.png)");

        Assert.Equal(0, ManuscriptEditor.ImageIndexAt(book, key, 3));
        Assert.Equal(1, ManuscriptEditor.ImageIndexAt(book, key, 8));
        Assert.Null(ManuscriptEditor.ImageIndexAt(book, key, 0));
        Assert.Null(ManuscriptEditor.ImageIndexAt(book, key, 4));
        Assert.Null(ManuscriptEditor.ImageIndexAt(book, key, 9));
        Assert.Null(ManuscriptEditor.ImageIndexAt(book, key, -1));
    }

    [Fact]
    public void ImageIndexAt_ImageWithUnderscoresAndAfterBold_CountsAsOnePosition()
    {
        var book = Load("- # H\n\t- **fett** ![a_b_c](../assets/x_y_z.png){:w 1} Ende\n");
        var key = KeyOf(book, "**fett** ![a_b_c](../assets/x_y_z.png){:w 1} Ende");

        Assert.Equal(0, ManuscriptEditor.ImageIndexAt(book, key, 5));
        Assert.Null(ManuscriptEditor.ImageIndexAt(book, key, 6));
    }

    [Fact]
    public void ContentOffset_ImageIsOnePosition()
    {
        var book = Load("- # H\n\t- ab ![x](p.png) cd\n");
        var key = KeyOf(book, "ab ![x](p.png) cd");

        Assert.Equal(3, ManuscriptEditor.ContentOffset(book, key, 3));
        Assert.Equal(3 + "![x](p.png)".Length, ManuscriptEditor.ContentOffset(book, key, 4));
        Assert.Equal(3 + "![x](p.png)".Length + 1, ManuscriptEditor.ContentOffset(book, key, 5));
    }
}
