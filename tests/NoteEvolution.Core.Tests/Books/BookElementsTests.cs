using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Tests.Books;

public class BookElementsTests
{
    private const string N1 = "11111111-1111-4111-8111-111111111111";
    private const string N2 = "22222222-2222-4222-8222-222222222222";
    private const string N3 = "33333333-3333-4333-8333-333333333333";

    private const string Text =
        "type:: book\n" +
        "\n" +
        "- Vorspann\n" +
        $"  source:: (({N3}))\n" +
        "\t- Vorspann-Detail\n" +
        "- # Eins\n" +
        "\t- Text eins\n" +
        $"\t  source:: (({N1})), (({N2}))\n" +
        "\t\t- Detail\n" +
        "\t\t\t- Tiefer\n" +
        "\t- ## Eins-A\n" +
        "\t\t- Text A\n" +
        $"\t\t  source:: (({N2}))\n" +
        "\t\t- ### Eins-A-i\n" +
        "\t\t\t- Text tief\n" +
        $"\t\t\t  source:: (({N3})), (({N1}))\n" +
        "- # Zwei\n" +
        "\t- ## Zwei-A\n";

    private static Book Load(string text) =>
        Book.Load(LogseqParser.Parse("Buch - Test.md", Encoding.UTF8.GetBytes(text)));

    private static Block BlockOf(Book book, string content) => book.Page.AllBlocks().Single(b => b.Content == content);

    [Fact]
    public void Find_EachKind()
    {
        var book = Load(Text);

        Assert.Equal(new BookElement(ElementKind.Heading, BlockOf(book, "## Eins-A").Key), BookElements.Find(book, BlockOf(book, "## Eins-A").Key));
        Assert.Equal(new BookElement(ElementKind.TextBlock, BlockOf(book, "Text eins").Key), BookElements.Find(book, BlockOf(book, "Text eins").Key));
        Assert.Equal(new BookElement(ElementKind.TextBlock, BlockOf(book, "Vorspann").Key), BookElements.Find(book, BlockOf(book, "Vorspann").Key));
        Assert.Equal(new BookElement(ElementKind.Detail, BlockOf(book, "Detail").Key), BookElements.Find(book, BlockOf(book, "Detail").Key));
        Assert.Equal(new BookElement(ElementKind.Detail, BlockOf(book, "Tiefer").Key), BookElements.Find(book, BlockOf(book, "Tiefer").Key));
        Assert.Null(BookElements.Find(book, Guid.Empty));
        Assert.Null(BookElements.Find(book, Guid.NewGuid()));

        var detail = BookElements.Find(book, BlockOf(book, "Tiefer").Key)!;
        Assert.Equal(BlockOf(book, "Text eins").Key, BookElements.TextBlockOf(book, detail)!.Key);
        var textBlock = BookElements.Find(book, BlockOf(book, "Text A").Key)!;
        Assert.Equal(BlockOf(book, "Text A").Key, BookElements.TextBlockOf(book, textBlock)!.Key);
        Assert.Null(BookElements.TextBlockOf(book, BookElements.Find(book, BlockOf(book, "# Eins").Key)!));
    }

    [Fact]
    public void SectionOf_PrologueIsEmpty()
    {
        var book = Load(Text);
        Guid SectionOf(string content) => BookElements.SectionOf(book, BookElements.Find(book, BlockOf(book, content).Key)!);

        Assert.Equal(Guid.Empty, SectionOf("Vorspann"));
        Assert.Equal(Guid.Empty, SectionOf("Vorspann-Detail"));
        Assert.Equal(BlockOf(book, "# Eins").Key, SectionOf("# Eins"));
        Assert.Equal(BlockOf(book, "# Eins").Key, SectionOf("Text eins"));
        Assert.Equal(BlockOf(book, "# Eins").Key, SectionOf("Tiefer"));
        Assert.Equal(BlockOf(book, "## Eins-A").Key, SectionOf("Text A"));
        Assert.Equal(BlockOf(book, "### Eins-A-i").Key, SectionOf("Text tief"));
        Assert.Throws<ArgumentException>(() => BookElements.SectionOf(book, new BookElement(ElementKind.TextBlock, Guid.NewGuid())));
    }

    [Fact]
    public void SourcesOf_HeadingCountsWholeSectionDistinct()
    {
        var book = Load(Text);
        IReadOnlyList<Guid> SourcesOf(string content) =>
            BookElements.SourcesOf(book, BookElements.Find(book, BlockOf(book, content).Key)!);

        Assert.Equal([Guid.Parse(N1), Guid.Parse(N2), Guid.Parse(N3)], SourcesOf("# Eins"));
        Assert.Equal([Guid.Parse(N2), Guid.Parse(N3), Guid.Parse(N1)], SourcesOf("## Eins-A"));
        Assert.Equal([Guid.Parse(N3), Guid.Parse(N1)], SourcesOf("### Eins-A-i"));
        Assert.Empty(SourcesOf("# Zwei"));
        Assert.Equal([Guid.Parse(N1), Guid.Parse(N2)], SourcesOf("Text eins"));
        Assert.Equal([Guid.Parse(N3)], SourcesOf("Vorspann"));
    }

    [Fact]
    public void SourcesOf_DetailUsesItsTextBlock()
    {
        var book = Load(Text);

        var detail = BookElements.Find(book, BlockOf(book, "Tiefer").Key)!;

        Assert.Equal([Guid.Parse(N1), Guid.Parse(N2)], BookElements.SourcesOf(book, detail));
        Assert.Equal([Guid.Parse(N3)], BookElements.SourcesOf(book, BookElements.Find(book, BlockOf(book, "Vorspann-Detail").Key)!));
    }

    [Fact]
    public void LastHeadingKey()
    {
        var book = Load(Text);
        Assert.Equal(BlockOf(book, "## Zwei-A").Key, BookElements.LastHeadingKey(book));

        // The last heading in document order, even when a later top-level text block follows a deeper one.
        var nested = Load("type:: book\n\n- # Eins\n\t- ## Eins-A\n\t\t- ### Eins-A-i\n\t- Text\n");
        Assert.Equal(BlockOf(nested, "### Eins-A-i").Key, BookElements.LastHeadingKey(nested));

        Assert.Null(BookElements.LastHeadingKey(Load("type:: book\n\n- Nur Text\n")));
    }
}
