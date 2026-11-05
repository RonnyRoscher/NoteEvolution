using NoteEvolution.Core.Books;
using NoteEvolution.Core.Links;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Tests.Links;

namespace NoteEvolution.Core.Tests.Books;

/// <summary>Restoring the text of a text block and putting a deleted text block back (the change history).</summary>
public class ManuscriptEditorRestoreTests
{
    private const string B1 = "6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70";
    private const string B2 = "6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f71";
    private const string N1 = "7f3a91c2-4b1d-4e8a-9c3f-1a2b3c4d5e6f";
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    private const string BookText =
        "title:: Buch: Test\n" +
        "type:: book\n" +
        "\n" +
        "- # Kapitel\n" +
        "\t- ## Abschnitt\n" +
        "\t\t- Erster Text\n" +
        $"\t\t  id:: {B1}\n" +
        "\t\t  status:: fertig\n" +
        $"\t\t  source:: (({N1}))\n" +
        "\t\t\t- Punkt A\n" +
        "\t\t\t- Punkt B\n" +
        "\t\t- Zweiter Text\n" +
        $"\t\t  id:: {B2}\n" +
        "\t\t- ### Unter\n" +
        "\t\t\t- Text unter\n" +
        "\t- ## Anderer\n" +
        "\t\t- Text anderer\n" +
        "- # Anhang\n" +
        "  anhang:: glossar\n";

    private const string Journal = $"- Quelle\n  id:: {N1}\n  used-in:: [[Buch - Test]] (({B1}))\n";

    private static Guid KeyOf(Book book, string content) => book.Page.AllBlocks().Single(b => b.Content == content).Key;

    private static List<string> Contents(Book book, string text) =>
        [.. book.FindTextBlock(KeyOf(book, text))!.Paragraphs.Select(p => p.Text)];

    [Fact]
    public void RestoreText_ReplacesTextAndParagraphs_KeepsSourcesAndStatus()
    {
        using var s = new LinkSetup(BookText, Journal);
        var key = KeyOf(s.Book, "Erster Text");

        var result = ManuscriptEditor.RestoreText(
            s.Book, key, "Alter Text", [("Alt eins", 1, BlockStatus.Draft), ("Alt zwei", 2, BlockStatus.Draft)], s.Vault, Now);

        Assert.True(result.Changed);
        var book = Book.Load(s.Book.Page);
        var text = book.FindTextBlock(key)!;
        Assert.Equal("Alter Text", text.Text);
        Assert.Equal(BlockStatus.Done, text.Status);
        Assert.Equal([Guid.Parse(N1)], text.Sources);
        Assert.Equal(Guid.Parse(B1), text.Block.Id);
        Assert.Equal(["Alt eins", "Alt zwei"], text.Paragraphs.Select(p => p.Text));
        Assert.Equal([1, 2], text.Paragraphs.Select(p => p.Depth));
        Assert.Empty(result.Effects);
    }

    [Fact]
    public void RestoreText_UnchangedParagraph_KeepsKey()
    {
        using var s = new LinkSetup(BookText, Journal);
        var key = KeyOf(s.Book, "Erster Text");
        var pointA = KeyOf(s.Book, "Punkt A");
        var pointB = KeyOf(s.Book, "Punkt B");

        ManuscriptEditor.RestoreText(
            s.Book, key, "Erster Text", [("Neu", 1, BlockStatus.Draft), ("Punkt A", 1, BlockStatus.Draft)], s.Vault, Now);

        var paragraphs = Book.Load(s.Book.Page).FindTextBlock(key)!.Paragraphs;
        Assert.Equal(["Neu", "Punkt A"], paragraphs.Select(p => p.Text));
        // The unchanged point keeps its key; the new one takes the next unused old key of its depth.
        Assert.Equal([pointB, pointA], paragraphs.Select(p => p.Block.Key));
    }

    [Fact]
    public void RestoreText_NewParagraph_GetsTheStatusOfTheStand()
    {
        using var s = new LinkSetup(BookText, Journal);
        var key = KeyOf(s.Book, "Erster Text");

        ManuscriptEditor.RestoreText(
            s.Book, key, "Erster Text",
            [("Punkt A", 1, BlockStatus.Draft), ("Punkt B", 1, BlockStatus.Draft), ("Neu", 1, BlockStatus.Wording)], s.Vault, Now);

        var paragraphs = Book.Load(s.Book.Page).FindTextBlock(key)!.Paragraphs;
        Assert.Equal([BlockStatus.Draft, BlockStatus.Draft, BlockStatus.Wording], paragraphs.Select(p => p.Status));
    }

    [Fact]
    public void RestoreText_SameStand_NotChanged()
    {
        using var s = new LinkSetup(BookText, Journal);

        var result = ManuscriptEditor.RestoreText(
            s.Book, KeyOf(s.Book, "Erster Text"), "Erster Text",
            [("Punkt A", 1, BlockStatus.Draft), ("Punkt B", 1, BlockStatus.Draft)], s.Vault, Now);

        Assert.False(result.Changed);
    }

    [Fact]
    public void RestoreTextBlock_AfterPredecessor()
    {
        using var s = new LinkSetup(BookText, Journal);
        var id = Guid.NewGuid();

        var key = ManuscriptEditor.RestoreTextBlock(
            s.Book, id, Guid.Parse(B1), "Kapitel › Abschnitt", "Zurück", [new RestoredParagraph("Punkt", 1, BlockStatus.Relevant)],
            BlockStatus.Wording, [Guid.Parse(N1)], Now);

        var book = Book.Load(s.Book.Page);
        var section = book.Root.Children.Single(n => n.Title == "Kapitel").Children.Single(n => n.Title == "Abschnitt");
        Assert.Equal(["Erster Text", "Zurück", "Zweiter Text"], section.TextBlocks.Select(t => t.Text));
        var restored = book.FindTextBlock(key)!;
        Assert.Equal(id, restored.Block.Id);
        Assert.Equal(BlockStatus.Wording, restored.Status);
        Assert.Equal([Guid.Parse(N1)], restored.Sources);
        Assert.Equal("Punkt", Assert.Single(restored.Paragraphs).Text);
        Assert.Equal(BlockStatus.Relevant, restored.Paragraphs[0].Status);
        Assert.NotNull(restored.Created);
    }

    [Fact]
    public void RestoreTextBlock_PredecessorGone_EndOfSection()
    {
        using var s = new LinkSetup(BookText, Journal);

        var key = ManuscriptEditor.RestoreTextBlock(
            s.Book, Guid.NewGuid(), Guid.NewGuid(), "Kapitel › Abschnitt", "Zurück", [], BlockStatus.Draft, [], Now);

        var book = Book.Load(s.Book.Page);
        var section = book.Root.Children.First().Children.First();
        // The end of the section is behind its sub-sections.
        Assert.Equal(key, section.Block!.Children[^1].Key);
        Assert.Equal(["Erster Text", "Zweiter Text", "Zurück"], section.TextBlocks.Select(t => t.Text));
    }

    [Fact]
    public void RestoreTextBlock_AfterTheTextOfThePredecessor_InTheSameSection()
    {
        using var s = new LinkSetup(BookText, Journal);

        ManuscriptEditor.RestoreTextBlock(
            s.Book, Guid.NewGuid(), Guid.NewGuid(), "Kapitel › Abschnitt", "Zurück", [], BlockStatus.Draft, [], Now, afterText: "Erster");

        var section = Book.Load(s.Book.Page).Root.Children.First().Children.First();
        Assert.Equal(["Erster Text", "Zurück", "Zweiter Text"], section.TextBlocks.Select(t => t.Text));
    }

    [Theory]
    [InlineData("Text anderer")] // in another section
    [InlineData("Text unter")] // in a sub-section
    [InlineData("Gibt es nicht")]
    [InlineData("")]
    public void RestoreTextBlock_PredecessorsTextNotInTheSection_EndOfSection(string afterText)
    {
        using var s = new LinkSetup(BookText, Journal);

        var key = ManuscriptEditor.RestoreTextBlock(
            s.Book, Guid.NewGuid(), null, "Kapitel › Abschnitt", "Zurück", [], BlockStatus.Draft, [], Now, afterText);

        Assert.Equal(key, Book.Load(s.Book.Page).Root.Children.First().Children.First().Block!.Children[^1].Key);
    }

    [Fact]
    public void RestoreTextBlock_IdOfThePredecessor_GoesBeforeItsText()
    {
        using var s = new LinkSetup(BookText, Journal);

        ManuscriptEditor.RestoreTextBlock(
            s.Book, Guid.NewGuid(), Guid.Parse(B2), "Kapitel › Abschnitt", "Zurück", [], BlockStatus.Draft, [], Now, afterText: "Erster");

        var section = Book.Load(s.Book.Page).Root.Children.First().Children.First();
        Assert.Equal(["Erster Text", "Zweiter Text", "Zurück"], section.TextBlocks.Select(t => t.Text));
    }

    [Fact]
    public void RestoreTextBlock_SubPoints_GetIdAndSources()
    {
        using var s = new LinkSetup(BookText, Journal);
        var point = Guid.NewGuid();

        var key = ManuscriptEditor.RestoreTextBlock(
            s.Book, Guid.NewGuid(), null, "Kapitel › Abschnitt", "Zurück",
            [new RestoredParagraph("Verknüpft", 1, BlockStatus.Relevant, point, [Guid.Parse(N1)]), new RestoredParagraph("Schlicht", 2, BlockStatus.Draft)],
            BlockStatus.Draft, [], Now);

        var paragraphs = Book.Load(s.Book.Page).FindTextBlock(key)!.Paragraphs;
        Assert.Equal(point, paragraphs[0].Block.Id);
        Assert.Equal(SourceValue.Format([Guid.Parse(N1)]), paragraphs[0].Block.GetProperty("source"));
        Assert.Equal(BlockStatus.Relevant, paragraphs[0].Status);
        Assert.Null(paragraphs[1].Block.Id);
        Assert.Null(paragraphs[1].Block.GetProperty("source"));
    }

    [Fact]
    public void RestoreTextBlock_SectionGone_EndOfBook_BeforeAppendix()
    {
        using var s = new LinkSetup(BookText, Journal);

        var key = ManuscriptEditor.RestoreTextBlock(
            s.Book, Guid.NewGuid(), null, "Kapitel › Gibt es nicht", "Zurück", [], BlockStatus.Draft, [], Now);

        var roots = s.Book.Page.Roots;
        Assert.Equal(3, roots.Count);
        Assert.Equal(key, roots[1].Key);
        Assert.Equal("# Anhang", roots[2].Content);
        Assert.Equal(["Zurück"], Book.Load(s.Book.Page).Root.TextBlocks.Select(t => t.Text));
    }

    [Fact]
    public void RestoreTextBlock_IdInBookAlready_Refused()
    {
        using var s = new LinkSetup(BookText, Journal);
        var before = s.Book.Page.AllBlocks().Count();

        Assert.Throws<ArgumentException>(() => ManuscriptEditor.RestoreTextBlock(
            s.Book, Guid.Parse(B1), null, "", "Zurück", [], BlockStatus.Draft, [], Now));

        Assert.Equal(before, s.Book.Page.AllBlocks().Count());
    }
}
