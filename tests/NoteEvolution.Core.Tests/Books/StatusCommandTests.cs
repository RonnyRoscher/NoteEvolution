using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;
using NoteEvolution.TestSupport;

namespace NoteEvolution.Core.Tests.Books;

public class StatusCommandTests
{
    private const string Text =
        "type:: book\n" +
        "\n" +
        "- # Eins\n" +
        "\t- Text eins\n" +
        "\t\t- Detail 1\n" +
        "\t\t\t- Detail 1a\n" +
        "\t\t- Detail 2\n" +
        "\t- Formuliert\n" +
        "\t  status:: formulierung\n" +
        "\t- Notiztext #notiz\n" +
        "\t\t- Notizdetail #notiz\n" +
        "\t- Mit Zeile #notiz\n" +
        "\t  status:: formulierung\n" +
        "\t- Unbekannt\n" +
        "\t  status:: später\n";

    private static Book Load(string text) =>
        Book.Load(LogseqParser.Parse("Buch - Test.md", Encoding.UTF8.GetBytes(text)));

    private static string Serialize(Book book) => Encoding.UTF8.GetString(PageSerializer.Serialize(book.Page));

    private static Block BlockOf(Book book, string content) => book.Page.AllBlocks().Single(b => b.Content == content);

    private static BookElement ElementOf(Book book, string content) => BookElements.Find(book, BlockOf(book, content).Key)!;

    /// <summary>
    /// Asserts the exact result and, with <see cref="LineDiff"/>, that exactly the lines <paramref name="removed"/>
    /// of <paramref name="before"/> and <paramref name="added"/> of the result differ; every other line is unchanged.
    /// </summary>
    private static void AssertResult(string before, Book book, string expected, string[] removed, string[] added)
    {
        var after = Serialize(book);
        Assert.Equal(expected, after);
        var (removedIndices, addedIndices) = LineDiff.Changed(Encoding.UTF8.GetBytes(before), Encoding.UTF8.GetBytes(after));
        var beforeLines = before.Split('\n');
        var afterLines = after.Split('\n');
        Assert.Equal(removed, removedIndices.Select(i => beforeLines[i]));
        Assert.Equal(added, addedIndices.Select(i => afterLines[i]));
    }

    [Theory]
    [InlineData(BlockStatus.Relevant, "Text eins", "\t", "relevant")]
    [InlineData(BlockStatus.Remark, "Text eins", "\t", "bemerkung")]
    [InlineData(BlockStatus.Wording, "Text eins", "\t", "formulierung")]
    [InlineData(BlockStatus.Done, "Text eins", "\t", "fertig")]
    [InlineData(BlockStatus.Relevant, "Detail 1", "\t\t", "relevant")]
    [InlineData(BlockStatus.Remark, "Detail 1", "\t\t", "bemerkung")]
    [InlineData(BlockStatus.Wording, "Detail 1", "\t\t", "formulierung")]
    [InlineData(BlockStatus.Done, "Detail 1", "\t\t", "fertig")]
    public void SetStatus_EachStatus_OnTextBlockAndDetail(BlockStatus status, string content, string indent, string value)
    {
        var book = Load(Text);

        ManuscriptEditor.SetStatus(book, ElementOf(book, content), status);

        var line = $"{indent}  status:: {value}";
        AssertResult(Text, book, Text.Replace($"{indent}- {content}\n", $"{indent}- {content}\n{line}\n"), [], [line]);
        Assert.Equal(status, BlockStatuses.Of(BlockOf(Book.Load(book.Page), content)));
    }

    [Fact]
    public void SetStatus_Draft_RemovesLine()
    {
        var book = Load(Text);

        ManuscriptEditor.SetStatus(book, ElementOf(book, "Formuliert"), BlockStatus.Draft);

        AssertResult(
            Text,
            book,
            Text.Replace("\t- Formuliert\n\t  status:: formulierung\n", "\t- Formuliert\n"),
            ["\t  status:: formulierung"],
            []);

        // A draft without a line stays as it is.
        var untouched = Load(Text);
        ManuscriptEditor.SetStatus(untouched, ElementOf(untouched, "Detail 2"), BlockStatus.Draft);
        Assert.Equal(Text, Serialize(untouched));
        Assert.False(untouched.Page.IsDirty);
    }

    [Fact]
    public void SetStatus_RemarkOnNotiz_NoChange()
    {
        var book = Load(Text);

        ManuscriptEditor.SetStatus(book, ElementOf(book, "Notiztext #notiz"), BlockStatus.Remark);
        ManuscriptEditor.SetStatus(book, ElementOf(book, "Notizdetail #notiz"), BlockStatus.Remark);

        Assert.Equal(Text, Serialize(book));
        Assert.False(book.Page.IsDirty);
    }

    [Theory]
    [InlineData(BlockStatus.Relevant, "relevant")]
    [InlineData(BlockStatus.Wording, "formulierung")]
    [InlineData(BlockStatus.Done, "fertig")]
    [InlineData(BlockStatus.Draft, null)]
    public void SetStatus_OtherOnNotiz_RemovesTag(BlockStatus status, string? value)
    {
        var book = Load(Text);

        ManuscriptEditor.SetStatus(book, ElementOf(book, "Notizdetail #notiz"), status);

        string[] added = value is null ? ["\t\t- Notizdetail"] : ["\t\t- Notizdetail", $"\t\t  status:: {value}"];
        AssertResult(
            Text,
            book,
            Text.Replace("\t\t- Notizdetail #notiz\n", string.Concat(added.Select(line => line + "\n"))),
            ["\t\t- Notizdetail #notiz"],
            added);
        Assert.Equal(status, BlockStatuses.Of(BlockOf(Book.Load(book.Page), "Notizdetail")));
    }

    [Fact]
    public void SetStatus_DraftOnNotizWithLine_RemovesBoth()
    {
        var book = Load(Text);

        ManuscriptEditor.SetStatus(book, ElementOf(book, "Mit Zeile #notiz"), BlockStatus.Draft);

        AssertResult(
            Text,
            book,
            Text.Replace("\t- Mit Zeile #notiz\n\t  status:: formulierung\n", "\t- Mit Zeile\n"),
            ["\t- Mit Zeile #notiz", "\t  status:: formulierung"],
            ["\t- Mit Zeile"]);
    }

    [Fact]
    public void SetStatus_ReplacesUnknownValue()
    {
        var book = Load(Text);

        ManuscriptEditor.SetStatus(book, ElementOf(book, "Unbekannt"), BlockStatus.Done);

        AssertResult(
            Text,
            book,
            Text.Replace("\t  status:: später\n", "\t  status:: fertig\n"),
            ["\t  status:: später"],
            ["\t  status:: fertig"]);
    }

    [Fact]
    public void SetStatus_Heading_Throws()
    {
        var book = Load(Text);
        var heading = ElementOf(book, "# Eins");
        var unknown = new BookElement(ElementKind.TextBlock, Guid.NewGuid());

        Assert.Throws<ArgumentException>(() => ManuscriptEditor.SetStatus(book, heading, BlockStatus.Done));
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.SetStatus(book, unknown, BlockStatus.Done));

        // A remark is added to a text block or detail only.
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.AddRemark(book, heading));
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.AddRemark(book, unknown));

        Assert.Equal(Text, Serialize(book));
        Assert.False(book.Page.IsDirty);
    }

    [Fact]
    public void AddRemark_AppendsLastDetail_WithStatusLine()
    {
        var book = Load(Text);

        var key = ManuscriptEditor.AddRemark(book, ElementOf(book, "Text eins"));

        AssertRemarkAppended(book, key);
    }

    [Fact]
    public void AddRemark_FromDetail_UsesItsTextBlock()
    {
        var book = Load(Text);

        var key = ManuscriptEditor.AddRemark(book, ElementOf(book, "Detail 1a"));

        AssertRemarkAppended(book, key);
    }

    /// <summary>The remark is an empty detail of depth 1 after "Detail 2", the last detail of "Text eins".</summary>
    private static void AssertRemarkAppended(Book book, Guid key)
    {
        AssertResult(
            Text,
            book,
            Text.Replace("\t\t- Detail 2\n", "\t\t- Detail 2\n\t\t-\n\t\t  status:: bemerkung\n"),
            [],
            ["\t\t-", "\t\t  status:: bemerkung"]);
        Assert.Equal(key, Book.Load(book.Page).FindTextBlock(BlockOf(book, "Text eins").Key)!.Paragraphs[^1].Block.Key);

        // Read back from the written text, the remark is an empty detail of depth 1 with status Remark.
        var reloaded = Load(Serialize(book));
        var last = reloaded.FindTextBlock(BlockOf(reloaded, "Text eins").Key)!.Paragraphs[^1];
        Assert.Equal((1, "", BlockStatus.Remark), (last.Depth, last.Text, last.Status));
    }
}
