using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Tests.Books;

/// <summary>Appending a cross reference at the end of a text block's own text, and giving a pair ids (repetitions).</summary>
public class AppendReferenceTests
{
    private const string Stamp = "2026-10-19T14:32";

    private static readonly Guid A = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");

    private const string Text =
        "type:: book\n" +
        "\n" +
        "- # Eins\n" +
        "\t- Erster Text\n" +
        "\t  id:: aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa\n" +
        "\t\t- Detail eins\n" +
        "\t- Zweiter Text\n" +
        "\t  id:: bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb\n" +
        "\t- Dritter Text\n";

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
    public void Append_PutsReferenceAtEndOfOwnText_BeforeSubPoints()
    {
        var book = Load();

        ManuscriptEditor.AppendReference(book, KeyOf(book, "Erster Text"), KeyOf(book, "Zweiter Text"), Now);

        Assert.Equal(
            Text.Replace(
                "\t- Erster Text\n\t  id:: aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa\n",
                $"\t- Erster Text {R(B)}\n\t  id:: {A:D}\n\t  updated:: {Stamp}\n"),
            Serialize(book));
    }

    [Fact]
    public void Append_OwnTextEndingWithReference_AddsSecondWithOneSpace()
    {
        var book = Load();
        var key = KeyOf(book, "Erster Text");
        ManuscriptEditor.AppendReference(book, key, KeyOf(book, "Zweiter Text"), Now);

        ManuscriptEditor.AppendReference(book, key, KeyOf(book, "Dritter Text"), Now);

        var third = book.Page.AllBlocks().Single(b => b.Content == "Dritter Text").Id!.Value;
        Assert.Equal($"Erster Text {R(B)} {R(third)}", book.Page.AllBlocks().Single(b => b.Key == key).Content);
    }

    [Fact]
    public void Append_EmptyOwnText_NoLeadingSpace()
    {
        var book = Load(Text.Replace("\t- Erster Text\n", "\t-\n"));
        var key = book.Page.AllBlocks().Single(b => b.Id == A).Key;

        ManuscriptEditor.AppendReference(book, key, KeyOf(book, "Zweiter Text"), Now);

        Assert.Equal(R(B), book.Page.AllBlocks().Single(b => b.Key == key).Content);
    }

    [Fact]
    public void Append_TargetWithoutId_GetsId_NoUpdatedOnTarget()
    {
        var book = Load();

        ManuscriptEditor.AppendReference(book, KeyOf(book, "Erster Text"), KeyOf(book, "Dritter Text"), Now);

        var id = book.Page.AllBlocks().Single(b => b.Content == "Dritter Text").Id!.Value;
        var serialized = Serialize(book);
        Assert.Contains($"\t- Dritter Text\n\t  id:: {id:D}\n", serialized);
        Assert.Contains($"Erster Text {R(id)}", serialized);
        Assert.Single(serialized.Split("updated::").Skip(1));
    }

    [Fact]
    public void Append_StampsUpdated_OnReferrer()
    {
        var book = Load();

        ManuscriptEditor.AppendReference(book, KeyOf(book, "Zweiter Text"), KeyOf(book, "Erster Text"), Now);

        Assert.Contains($"\t- Zweiter Text {R(A)}\n\t  id:: {B:D}\n\t  updated:: {Stamp}\n", Serialize(book));
    }

    [Fact]
    public void Append_SameBlock_Throws()
    {
        var book = Load();
        var key = KeyOf(book, "Erster Text");

        Assert.Throws<ArgumentException>(() => ManuscriptEditor.AppendReference(book, key, key, Now));
        Assert.Equal(Text, Serialize(book));
    }

    [Fact]
    public void Append_UnknownBlock_Throws()
    {
        var book = Load();

        Assert.Throws<ArgumentException>(() =>
            ManuscriptEditor.AppendReference(book, Guid.NewGuid(), KeyOf(book, "Zweiter Text"), Now));
        Assert.Throws<ArgumentException>(() =>
            ManuscriptEditor.AppendReference(book, KeyOf(book, "Erster Text"), Guid.NewGuid(), Now));
        Assert.Equal(Text, Serialize(book));
    }

    [Fact]
    public void RefersTo_True_AlsoFromSubPoint()
    {
        var book = Load(Text.Replace("Detail eins", $"Detail eins {R(B)}"));

        Assert.True(ManuscriptEditor.RefersTo(book, KeyOf(book, "Erster Text"), KeyOf(book, "Zweiter Text")));

        var own = Load(Text.Replace("\t- Erster Text\n", $"\t- Erster Text {R(B)}\n"));
        Assert.True(ManuscriptEditor.RefersTo(own, own.Page.AllBlocks().Single(b => b.Id == A).Key, KeyOf(own, "Zweiter Text")));
    }

    [Fact]
    public void RefersTo_False_WithoutId()
    {
        var book = Load();

        Assert.False(ManuscriptEditor.RefersTo(book, KeyOf(book, "Erster Text"), KeyOf(book, "Dritter Text")));
        Assert.False(ManuscriptEditor.RefersTo(book, KeyOf(book, "Erster Text"), KeyOf(book, "Zweiter Text")));
        Assert.Equal(Text, Serialize(book));
    }

    [Fact]
    public void EnsureIds_AddsMissingIds_KeepsExisting_NoUpdated()
    {
        var book = Load();

        var (a, b) = ManuscriptEditor.EnsureIds(book, KeyOf(book, "Dritter Text"), KeyOf(book, "Zweiter Text"));

        var third = book.Page.AllBlocks().Single(x => x.Content == "Dritter Text").Id!.Value;
        Assert.Equal(third, a);
        Assert.Equal(B, b);
        Assert.Equal(Text.Replace("\t- Dritter Text\n", $"\t- Dritter Text\n\t  id:: {third:D}\n"), Serialize(book));
    }
}
