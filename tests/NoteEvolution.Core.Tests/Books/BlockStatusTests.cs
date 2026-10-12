using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Tests.Links;

namespace NoteEvolution.Core.Tests.Books;

public class BlockStatusTests
{
    private static Page Parse(string text) =>
        LogseqParser.Parse("Buch - Test.md", Encoding.UTF8.GetBytes(text));

    private static Block BlockOf(string text) => Parse("- " + text + "\n").Roots.Single();

    [Theory]
    [InlineData("relevant", BlockStatus.Relevant)]
    [InlineData("Relevant", BlockStatus.Relevant)]
    [InlineData("bemerkung", BlockStatus.Remark)]
    [InlineData("formulierung", BlockStatus.Wording)]
    [InlineData("fertig", BlockStatus.Done)]
    [InlineData(null, BlockStatus.Draft)]
    public void Status_ValuesFromFile(string? value, BlockStatus expected)
    {
        var text = value is null ? "Text" : $"Text\n  status:: {value}";
        Assert.Equal(expected, BlockStatuses.Of(BlockOf(text)));
    }

    [Fact]
    public void Status_NotizTag_IsRemark() =>
        Assert.Equal(BlockStatus.Remark, BlockStatuses.Of(BlockOf("Text #notiz")));

    [Fact]
    public void Status_NotizWithStatusLine_LineWins() =>
        Assert.Equal(BlockStatus.Done, BlockStatuses.Of(BlockOf("Text #notiz\n  status:: fertig")));

    [Theory]
    [InlineData("unbekannt")]
    [InlineData("")]
    public void Status_UnknownValue_IsDraft(string value) =>
        Assert.Equal(BlockStatus.Draft, BlockStatuses.Of(BlockOf($"Text\n  status:: {value}")));

    [Fact]
    public void Book_TextBlockAndParagraph_CarryStatus()
    {
        var book = Book.Load(Parse(
            "type:: book\n\n" +
            "- # Kapitel\n" +
            "\t- Block\n" +
            "\t  status:: relevant\n" +
            "\t\t- Detail fertig\n" +
            "\t\t  status:: fertig\n" +
            "\t\t- Detail Notiz #notiz\n" +
            "\t\t- Detail Entwurf\n"));
        var block = book.Root.Children.Single().TextBlocks.Single();
        Assert.Equal(BlockStatus.Relevant, block.Status);
        Assert.Equal(
            [BlockStatus.Done, BlockStatus.Remark, BlockStatus.Draft],
            block.Paragraphs.Select(p => p.Status));
        // IsNote stays "has the #notiz tag" for the tag round-trip.
        Assert.Equal([false, true, false], block.Paragraphs.Select(p => p.IsNote));
    }

    [Fact]
    public void Snapshot_CarriesStatus()
    {
        const string bookText =
            "type:: book\n\n" +
            "- # Kapitel\n" +
            "\t- Block\n" +
            "\t  status:: formulierung\n" +
            "\t\t- Detail\n" +
            "\t\t  status:: bemerkung\n";
        using var s = new LinkSetup(bookText);
        var snapshot = BookSnapshot.Create(s.Book, s.Vault, Guid.Empty, includeSubsections: true);
        var block = snapshot.Nodes.OfType<SnapshotTextBlock>().Single();
        Assert.Equal(BlockStatus.Wording, block.Status);
        Assert.Equal(BlockStatus.Remark, block.Paragraphs.Single().Status);
        Assert.False(block.Paragraphs.Single().IsNote);
    }

    [Fact]
    public void FileValue_And_IsExcluded()
    {
        Assert.Null(BlockStatuses.FileValue(BlockStatus.Draft));
        Assert.Equal("relevant", BlockStatuses.FileValue(BlockStatus.Relevant));
        Assert.Equal("bemerkung", BlockStatuses.FileValue(BlockStatus.Remark));
        Assert.Equal("formulierung", BlockStatuses.FileValue(BlockStatus.Wording));
        Assert.Equal("fertig", BlockStatuses.FileValue(BlockStatus.Done));
        Assert.Equal("status", BlockStatuses.Key);

        Assert.Equal(
            [false, true, true, false, false],
            new[] { BlockStatus.Draft, BlockStatus.Relevant, BlockStatus.Remark, BlockStatus.Wording, BlockStatus.Done }
                .Select(BlockStatuses.IsExcluded));
    }
}
