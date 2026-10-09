using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;
using NoteEvolution.TestSupport;

namespace NoteEvolution.Core.Tests.Books;

public class WrapTests
{
    private const string Text =
        "type:: book\n" +
        "\n" +
        "- Vorspann\n" +
        "- Vorspann zwei\n" +
        "- # Eins\n" +
        "\t- Text eins\n" +
        "\t\t- Detail 1\n" +
        "\t\t\t- Detail 1a\n" +
        "\t\t- Detail 2\n" +
        "\t- ## Eins-A\n" +
        "\t  collapsed:: true\n" +
        "\t\t- Text A\n" +
        "\t\t- ### Eins-A-i\n" +
        "\t\t\t- tief\n" +
        "\t- Text nach A\n" +
        "\t- ## Eins-B\n" +
        "\t\t- Text B\n" +
        "\t- Text nach B\n" +
        "- # Zwei\n" +
        "\t- Text zwei\n" +
        "\t- ## Zwei-A\n";

    private static Book Load(string text) =>
        Book.Load(LogseqParser.Parse("Buch - Test.md", Encoding.UTF8.GetBytes(text)));

    private static string Serialize(Book book) => Encoding.UTF8.GetString(PageSerializer.Serialize(book.Page));

    private static Block BlockOf(Book book, string content) => book.Page.AllBlocks().Single(b => b.Content == content);

    private static Guid KeyOf(Book book, string content) => BlockOf(book, content).Key;

    private static BookElement ElementOf(Book book, string content) => BookElements.Find(book, KeyOf(book, content))!;

    private static Guid KeyOfItem(BookItem item) => item switch
    {
        OutlineNode node => node.Key,
        TextBlock textBlock => textBlock.Key,
        _ => Guid.Empty,
    };

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

    [Fact]
    public void Wrap_Heading_BecomesFirstChild_ReleveledOneDeeper()
    {
        var book = Load(Text);
        var head = ElementOf(book, "## Eins-A");

        Assert.True(ManuscriptEditor.CanWrap(book, head));
        var wrapped = ManuscriptEditor.Wrap(book, head);

        AssertResult(
            Text,
            book,
            Text.Replace(
                "\t- ## Eins-A\n\t  collapsed:: true\n\t\t- Text A\n\t\t- ### Eins-A-i\n\t\t\t- tief\n",
                "\t- ## \n\t\t- ### Eins-A\n\t\t  collapsed:: true\n\t\t\t- Text A\n\t\t\t- #### Eins-A-i\n\t\t\t\t- tief\n"),
            ["\t- ## Eins-A", "\t  collapsed:: true", "\t\t- Text A", "\t\t- ### Eins-A-i", "\t\t\t- tief"],
            ["\t- ## ", "\t\t- ### Eins-A", "\t\t  collapsed:: true", "\t\t\t- Text A", "\t\t\t- #### Eins-A-i", "\t\t\t\t- tief"]);
        var reloaded = Book.Load(book.Page);
        var heading = reloaded.FindNode(wrapped.NewHeadingKey)!;
        Assert.Equal((2, ""), (heading.Level, heading.Title));
        Assert.Equal([KeyOf(book, "### Eins-A")], heading.Items.Select(KeyOfItem));
        Assert.Equal(4, reloaded.FindNode(KeyOf(book, "#### Eins-A-i"))!.Level);
        Assert.Equal(
            [KeyOf(book, "Text eins"), wrapped.NewHeadingKey, KeyOf(book, "Text nach A"), KeyOf(book, "## Eins-B"), KeyOf(book, "Text nach B")],
            reloaded.FindNode(KeyOf(book, "# Eins"))!.Items.Select(KeyOfItem));
        Assert.Empty(reloaded.Warnings);
    }

    [Fact]
    public void Wrap_TextBlockMidSection_TakesFollowingOwnBlocksOnly()
    {
        var book = Load(Text);

        var wrapped = ManuscriptEditor.Wrap(book, ElementOf(book, "Text nach A"));

        // "Text eins" (before the head) and the sub-section "Eins-B" with "Text B" stay with "Eins".
        AssertResult(
            Text,
            book,
            Text.Replace(
                "\t- Text nach A\n\t- ## Eins-B\n\t\t- Text B\n\t- Text nach B\n",
                "\t- ## \n\t\t- Text nach A\n\t\t- Text nach B\n\t- ## Eins-B\n\t\t- Text B\n"),
            ["\t- Text nach A", "\t- Text nach B"],
            ["\t- ## ", "\t\t- Text nach A", "\t\t- Text nach B"]);
        var reloaded = Book.Load(book.Page);
        var heading = reloaded.FindNode(wrapped.NewHeadingKey)!;
        Assert.Equal((2, ""), (heading.Level, heading.Title));
        Assert.Equal([KeyOf(book, "Text nach A"), KeyOf(book, "Text nach B")], heading.Items.Select(KeyOfItem));
        Assert.Equal(
            [KeyOf(book, "Text eins"), KeyOf(book, "## Eins-A"), wrapped.NewHeadingKey, KeyOf(book, "## Eins-B")],
            reloaded.FindNode(KeyOf(book, "# Eins"))!.Items.Select(KeyOfItem));
        Assert.Empty(reloaded.Warnings);
    }

    [Fact]
    public void Wrap_PrologueTextBlock_NewLevel1Heading()
    {
        var book = Load(Text);

        var wrapped = ManuscriptEditor.Wrap(book, ElementOf(book, "Vorspann"));

        AssertResult(
            Text,
            book,
            Text.Replace("- Vorspann\n- Vorspann zwei\n", "- # \n\t- Vorspann\n\t- Vorspann zwei\n"),
            ["- Vorspann", "- Vorspann zwei"],
            ["- # ", "\t- Vorspann", "\t- Vorspann zwei"]);
        var reloaded = Book.Load(book.Page);
        var heading = reloaded.FindNode(wrapped.NewHeadingKey)!;
        Assert.Equal((1, ""), (heading.Level, heading.Title));
        Assert.Equal([KeyOf(book, "Vorspann"), KeyOf(book, "Vorspann zwei")], heading.Items.Select(KeyOfItem));
        Assert.Equal(
            [wrapped.NewHeadingKey, KeyOf(book, "# Eins"), KeyOf(book, "# Zwei")],
            reloaded.Root.Items.Select(KeyOfItem));
        Assert.Empty(reloaded.Warnings);
    }

    [Fact]
    public void Wrap_Detail_CanWrapFalse_Throws()
    {
        var book = Load(Text);

        foreach (var detail in new[] { "Detail 1", "Detail 1a" })
        {
            Assert.False(ManuscriptEditor.CanWrap(book, ElementOf(book, detail)));
            Assert.Throws<ArgumentException>(() => ManuscriptEditor.Wrap(book, ElementOf(book, detail)));
        }

        // The whole book (no head), an unknown element and an element of the wrong kind are refused as well.
        var unknown = new BookElement(ElementKind.TextBlock, Guid.NewGuid());
        var wrongKind = new BookElement(ElementKind.Heading, KeyOf(book, "Text eins"));
        Assert.False(ManuscriptEditor.CanWrap(book, null));
        Assert.False(ManuscriptEditor.CanWrap(book, unknown));
        Assert.False(ManuscriptEditor.CanWrap(book, wrongKind));
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.Wrap(book, unknown));
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.Wrap(book, wrongKind));

        Assert.False(book.Page.IsDirty);
        Assert.Equal(Text, Serialize(book));
    }

    [Fact]
    public void Wrap_WouldExceedLevel6_CanWrapFalse()
    {
        const string deep =
            "type:: book\n\n- # 1\n\t- ## 2\n\t\t- ### 3\n\t\t\t- #### 4\n\t\t\t\t- ##### 5\n" +
            "\t\t\t\t\t- Text 5\n\t\t\t\t\t- ###### 6\n\t\t\t\t\t\t- Text 6\n";
        var book = Load(deep);

        // A new level-6 heading is possible; a level-7 one, or a subtree pushed beyond level 6, is not.
        Assert.True(ManuscriptEditor.CanWrap(book, ElementOf(book, "Text 5")));
        foreach (var head in new[] { "Text 6", "###### 6", "##### 5", "# 1" })
        {
            Assert.False(ManuscriptEditor.CanWrap(book, ElementOf(book, head)));
            Assert.Throws<ArgumentException>(() => ManuscriptEditor.Wrap(book, ElementOf(book, head)));
        }

        Assert.False(book.Page.IsDirty);
        Assert.Equal(deep, Serialize(book));
    }

    [Theory]
    [InlineData("## Eins-A")]
    [InlineData("# Eins")]
    [InlineData("### Eins-A-i")]
    [InlineData("## Zwei-A")]
    [InlineData("Text eins")]
    [InlineData("Text nach A")]
    [InlineData("Vorspann zwei")]
    [InlineData("Text zwei")]
    public void Wrap_Undo_ByteExact(string head)
    {
        // Also with CRLF, spaces as indentation and no final line ending, so that every line ending is restored.
        foreach (var text in new[] { Text, Text.Replace("\n", "\r\n")[..^2], Text.Replace("\t", "  ")[..^1] })
        {
            var book = Load(text);
            var original = PageSerializer.Serialize(book.Page);
            var keys = book.Page.AllBlocks().Select(b => b.Key).ToList();

            var wrapped = ManuscriptEditor.Wrap(book, ElementOf(book, head));
            Assert.NotEqual(original, PageSerializer.Serialize(book.Page));
            book.Page.MarkSaved();
            var reloaded = Book.Load(book.Page);
            Assert.True(wrapped.CanRestore(reloaded));
            wrapped.RestoreInto(reloaded);

            Assert.Equal(original, PageSerializer.Serialize(book.Page));
            Assert.Equal(keys, book.Page.AllBlocks().Select(b => b.Key));
            Assert.True(book.Page.IsDirty);

            // Once the new heading is gone, the command is not undone a second time.
            Assert.False(wrapped.CanRestore(Book.Load(book.Page)));
        }
    }

    [Fact]
    public void Wrap_Undo_AfterTitleAndTextEdits_RestoresStructure()
    {
        // A heading head: the new heading's title, the head's title, a moved text and a moved sub-heading's title, and
        // a text elsewhere were edited.
        var book = Load(Text);
        var einsA = KeyOf(book, "## Eins-A");
        var wrapped = ManuscriptEditor.Wrap(book, ElementOf(book, "## Eins-A"));
        BlockOf(book, "## ").SetContent("## Neu");
        BlockOf(book, "### Eins-A").SetContent("### Eins-A neu");
        BlockOf(book, "Text A").SetContent("Text A neu");
        BlockOf(book, "#### Eins-A-i").SetContent("#### Eins-A-i neu");
        BlockOf(book, "Text zwei").SetContent("Text zwei geändert");

        Assert.True(wrapped.CanRestore(Book.Load(book.Page)));
        wrapped.RestoreInto(Book.Load(book.Page));

        AssertResult(
            Text,
            book,
            Text.Replace("\t- ## Eins-A\n", "\t- ## Eins-A neu\n")
                .Replace("\t\t- Text A\n", "\t\t- Text A neu\n")
                .Replace("\t\t- ### Eins-A-i\n", "\t\t- ### Eins-A-i neu\n")
                .Replace("\t- Text zwei\n", "\t- Text zwei geändert\n"),
            ["\t- ## Eins-A", "\t\t- Text A", "\t\t- ### Eins-A-i", "\t- Text zwei"],
            ["\t- ## Eins-A neu", "\t\t- Text A neu", "\t\t- ### Eins-A-i neu", "\t- Text zwei geändert"]);
        var reloaded = Book.Load(book.Page);
        Assert.Equal(("Eins-A neu", 2), (reloaded.FindNode(einsA)!.Title, reloaded.FindNode(einsA)!.Level));
        Assert.Equal(3, reloaded.FindNode(KeyOf(book, "### Eins-A-i neu"))!.Level);
        Assert.Null(reloaded.FindNode(wrapped.NewHeadingKey));
        Assert.Empty(reloaded.Warnings);

        // A text block head: the moved blocks go back between the sub-sections they stood between.
        var text = Load(Text);
        wrapped = ManuscriptEditor.Wrap(text, ElementOf(text, "Text nach A"));
        BlockOf(text, "## ").SetContent("## Titel");
        BlockOf(text, "Text nach B").SetContent("Text nach B neu");

        Assert.True(wrapped.CanRestore(Book.Load(text.Page)));
        wrapped.RestoreInto(Book.Load(text.Page));

        AssertResult(Text, text, Text.Replace("\t- Text nach B\n", "\t- Text nach B neu\n"), ["\t- Text nach B"], ["\t- Text nach B neu"]);
    }

    [Fact]
    public void Wrap_Undo_MovedBlockElsewhere_Refused_NothingChanged()
    {
        // The new heading was removed (its content moved up again).
        var gone = Load(Text);
        var wrapped = ManuscriptEditor.Wrap(gone, ElementOf(gone, "Text nach A"));
        ManuscriptEditor.RemoveHeading(Book.Load(gone.Page), wrapped.NewHeadingKey);
        AssertRefused(gone, wrapped);

        // The new heading was moved into another section.
        var indented = Load(Text);
        wrapped = ManuscriptEditor.Wrap(indented, ElementOf(indented, "## Eins-B"));
        ManuscriptEditor.Indent(Book.Load(indented.Page), wrapped.NewHeadingKey);
        AssertRefused(indented, wrapped);

        // A moved text block was moved into another section.
        var moved = Load(Text);
        wrapped = ManuscriptEditor.Wrap(moved, ElementOf(moved, "Text nach A"));
        moved.Page.MoveBlock(BlockOf(moved, "Text nach B"), BlockOf(moved, "# Zwei"), 0);
        AssertRefused(moved, wrapped);

        // A detail of a moved text block was moved to another text block.
        var detail = Load(Text);
        wrapped = ManuscriptEditor.Wrap(detail, ElementOf(detail, "Text eins"));
        detail.Page.MoveBlock(BlockOf(detail, "Detail 2"), BlockOf(detail, "Text zwei"), 0);
        AssertRefused(detail, wrapped);

        // A deeper moved block was moved below another block at the same depth, so that its lines are still those the
        // command left: putting its original lines back would leave it a detail of "Detail 1a" with a wrong indentation.
        var deep = Load(Text);
        wrapped = ManuscriptEditor.Wrap(deep, ElementOf(deep, "## Eins-A"));
        var lines = BlockOf(deep, "tief").Lines.ToList();
        deep.Page.MoveBlock(BlockOf(deep, "tief"), BlockOf(deep, "Detail 1a"), 0);
        Assert.Equal(lines, BlockOf(deep, "tief").Lines);
        AssertRefused(deep, wrapped);

        // A new block was added below the new heading: removing the heading would lose it.
        var added = Load(Text);
        wrapped = ManuscriptEditor.Wrap(added, ElementOf(added, "Text nach A"));
        var later = Book.Load(added.Page);
        ManuscriptEditor.InsertAfter(later, BookElements.Find(later, KeyOf(added, "Text nach B"))!);
        AssertRefused(added, wrapped);

        static void AssertRefused(Book book, WrappedSection wrapped)
        {
            var before = Serialize(book);
            var reloaded = Book.Load(book.Page);
            Assert.False(wrapped.CanRestore(reloaded));
            Assert.Throws<InvalidOperationException>(() => wrapped.RestoreInto(reloaded));
            Assert.Equal(before, Serialize(book));
        }
    }
}
