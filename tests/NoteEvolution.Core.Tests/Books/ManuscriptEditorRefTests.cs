using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Tests.Books;

/// <summary>Inserting a cross reference <c>((id))</c> into a text block or detail (spec 2.2).</summary>
public class ManuscriptEditorRefTests
{
    private const string Stamp = "2026-10-19T14:32";

    private static readonly Guid A = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");

    private const string Text =
        "type:: book\n" +
        "\n" +
        "- # Eins\n" +
        "  id:: aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa\n" +
        "\t- Text **fett** eins\n" +
        "\t  id:: bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb\n" +
        "\t\t- Detail \\* eins #notiz\n" +
        "\t- Zweiter Text\n" +
        "- # Zwei\n" +
        "\t- Dritter\n" +
        "\t- \\# Kein Titel\n";

    // A fixed local time with the local offset, so the tests hold in any time zone.
    private static readonly DateTimeOffset Now = LocalNow();

    private static DateTimeOffset LocalNow()
    {
        var local = new DateTime(2026, 10, 19, 14, 32, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static string R(Guid id) => BlockRefs.Format(id);

    private static Book Load(string text = Text) =>
        Book.Load(LogseqParser.Parse("Buch - Test.md", Encoding.UTF8.GetBytes(text)));

    private static string Serialize(Book book) => Encoding.UTF8.GetString(PageSerializer.Serialize(book.Page));

    private static Guid KeyOf(Book book, string content) => book.Page.AllBlocks().Single(b => b.Content == content).Key;

    [Fact]
    public void InsertReference_AtOffset_ExistingId_StampsReferringBlockOnly()
    {
        var book = Load();

        ManuscriptEditor.InsertReference(book, KeyOf(book, "Zweiter Text"), 8, KeyOf(book, "# Eins"), Now);

        Assert.Equal(
            Text.Replace("\t- Zweiter Text\n", $"\t- Zweiter {R(A)}Text\n\t  updated:: {Stamp}\n"),
            Serialize(book));
    }

    [Fact]
    public void InsertReference_InsideBold()
    {
        var book = Load();
        var key = KeyOf(book, "Text **fett** eins");

        ManuscriptEditor.InsertReference(book, key, 9, KeyOf(book, "# Eins"), Now);

        Assert.Equal($"Text **fe{R(A)}tt** eins", book.Page.AllBlocks().Single(b => b.Key == key).Content);
        Assert.Equal(
            Text.Replace("\t- Text **fett** eins\n", $"\t- Text **fe{R(A)}tt** eins\n")
                .Replace($"\t  id:: {B:D}\n", $"\t  id:: {B:D}\n\t  updated:: {Stamp}\n"),
            Serialize(book));
    }

    [Fact]
    public void InsertReference_TargetWithoutId_GetsIdButNoDate()
    {
        var book = Load();

        ManuscriptEditor.InsertReference(book, KeyOf(book, "Zweiter Text"), 0, KeyOf(book, "Dritter"), Now);

        var target = book.Page.AllBlocks().Single(b => b.Content == "Dritter");
        var id = target.Id!.Value;
        Assert.Equal(
            Text.Replace("\t- Zweiter Text\n", $"\t- {R(id)}Zweiter Text\n\t  updated:: {Stamp}\n")
                .Replace("\t- Dritter\n", $"\t- Dritter\n\t  id:: {id:D}\n"),
            Serialize(book));
    }

    [Fact]
    public void InsertReference_InDetail_StampsItsTextBlock()
    {
        var book = Load();

        // The content of the detail is "Detail \* eins #notiz"; offset 9 is after the escaped star.
        ManuscriptEditor.InsertReference(book, KeyOf(book, "Detail \\* eins #notiz"), 9, KeyOf(book, "# Zwei"), Now);

        var id = book.Page.AllBlocks().Single(b => b.Content == "# Zwei").Id!.Value;
        Assert.Equal(
            Text.Replace($"\t  id:: {B:D}\n", $"\t  id:: {B:D}\n\t  updated:: {Stamp}\n")
                .Replace("\t\t- Detail \\* eins #notiz\n", $"\t\t- Detail \\*{R(id)} eins #notiz\n")
                .Replace("- # Zwei\n", $"- # Zwei\n  id:: {id:D}\n"),
            Serialize(book));
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(99, 12)]
    public void InsertReference_OffsetClamped(int offset, int at)
    {
        var book = Load();

        ManuscriptEditor.InsertReference(book, KeyOf(book, "Zweiter Text"), offset, KeyOf(book, "# Eins"), Now);

        Assert.Contains($"\t- {"Zweiter Text".Insert(at, R(A))}\n", Serialize(book));
    }

    [Fact]
    public void InsertReference_InvalidTargets_Throw_NothingChanged()
    {
        var book = Load();
        var other = Load();
        var element = KeyOf(book, "Text **fett** eins");

        // A detail, a block of another book, the element itself, an unknown key; and a heading as the referring element.
        Assert.Throws<ArgumentException>(() =>
            ManuscriptEditor.InsertReference(book, KeyOf(book, "Zweiter Text"), 0, KeyOf(book, "Detail \\* eins #notiz"), Now));
        Assert.Throws<ArgumentException>(() =>
            ManuscriptEditor.InsertReference(book, element, 0, KeyOf(other, "Dritter"), Now));
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.InsertReference(book, element, 0, element, Now));
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.InsertReference(book, element, 0, Guid.NewGuid(), Now));
        Assert.Throws<ArgumentException>(() =>
            ManuscriptEditor.InsertReference(book, KeyOf(book, "# Zwei"), 0, KeyOf(book, "Dritter"), Now));

        Assert.Equal(Text, Serialize(book));
        Assert.DoesNotContain(book.Page.AllBlocks(), b => b.IsDirty);
    }

    [Fact]
    public void InsertReference_AtStartOfEscapedLine_DropsTheEscapeNoLongerNeeded()
    {
        var book = Load();

        ManuscriptEditor.InsertReference(book, KeyOf(book, "\\# Kein Titel"), 0, KeyOf(book, "# Eins"), Now);

        Assert.Equal(
            Text.Replace("\t- \\# Kein Titel\n", $"\t- {R(A)}# Kein Titel\n\t  updated:: {Stamp}\n"),
            Serialize(book));
    }

    [Theory]
    // The editor shows "Text fett eins": bold markers are not shown.
    [InlineData("Text **fett** eins", 0, 0)]
    [InlineData("Text **fett** eins", 7, 9)]
    [InlineData("Text **fett** eins", 14, 18)]
    // "\*" shows as one star; the tag of a detail is not shown.
    [InlineData("Detail \\* eins #notiz", 8, 9)]
    [InlineData("Detail \\* eins #notiz", 13, 14)]
    // A line escape is not shown; at the line start the offset stays 0 (the escape goes on insert).
    [InlineData("\\# Kein Titel", 0, 0)]
    [InlineData("\\# Kein Titel", 2, 3)]
    // Out of range is clamped.
    [InlineData("Zweiter Text", 50, 12)]
    public void ContentOffset_FromEditorOffset(string content, int shown, int expected)
    {
        var book = Load();

        Assert.Equal(expected, ManuscriptEditor.ContentOffset(book, KeyOf(book, content), shown));
    }

    [Fact]
    public void ContentOffset_AfterHardBreak()
    {
        var text = Text.Replace("\t- Dritter\n", "\t- Erste *Zeile\n\t  zweite* Zeile\n");
        var book = Load(text);

        // "Erste Zeile\nzweite Zeile": offset 12 is the start of the second line.
        Assert.Equal(13, ManuscriptEditor.ContentOffset(book, KeyOf(book, "Erste *Zeile\nzweite* Zeile"), 12));
    }

    [Fact]
    public void ContentOffset_CountsAReferenceAsOnePosition()
    {
        // The editor shows "Zweiter ", the reference (one position), "Text ", a second reference.
        var text = Text.Replace("\t- Zweiter Text\n", $"\t- Zweiter {R(A)}Text {R(B)}\n");
        var book = Load(text);
        var key = KeyOf(book, $"Zweiter {R(A)}Text {R(B)}");

        Assert.Equal(8, ManuscriptEditor.ContentOffset(book, key, 8));
        Assert.Equal(48, ManuscriptEditor.ContentOffset(book, key, 9));
        Assert.Equal(50, ManuscriptEditor.ContentOffset(book, key, 11));
        Assert.Equal(53, ManuscriptEditor.ContentOffset(book, key, 14));
        Assert.Equal(93, ManuscriptEditor.ContentOffset(book, key, 15));
        // Out of range is clamped to the end.
        Assert.Equal(93, ManuscriptEditor.ContentOffset(book, key, 50));
    }

    [Fact]
    public void ContentOffset_ReferenceInsideBold()
    {
        var text = Text.Replace("Text **fett** eins", $"Text **fe{R(A)}tt** eins");
        var book = Load(text);
        var key = KeyOf(book, $"Text **fe{R(A)}tt** eins");

        // "Text fe", the reference, "tt eins": right after the reference is still inside the bold text.
        Assert.Equal(49, ManuscriptEditor.ContentOffset(book, key, 8));
        Assert.Equal(50, ManuscriptEditor.ContentOffset(book, key, 9));
    }
}
