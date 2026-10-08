using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;
using NoteEvolution.TestSupport;

namespace NoteEvolution.Core.Tests.Books;

public class ManuscriptEditorTests
{
    private const string Text =
        "type:: book\n" +
        "\n" +
        "- Vorspann\n" +
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
        "- # Zwei\n" +
        "\t- Text zwei\n" +
        "\t- ## Zwei-A\n";

    private const string LevelSix =
        "type:: book\n\n- # 1\n\t- ## 2\n\t\t- ### 3\n\t\t\t- #### 4\n\t\t\t\t- ##### 5\n\t\t\t\t\t- ###### 6\n";

    private static Book Load(string text) =>
        Book.Load(LogseqParser.Parse("Buch - Test.md", Encoding.UTF8.GetBytes(text)));

    private static string Serialize(Book book) => Encoding.UTF8.GetString(PageSerializer.Serialize(book.Page));

    private static Block BlockOf(Book book, string content) => book.Page.AllBlocks().Single(b => b.Content == content);

    private static Guid KeyOf(Book book, string content) => BlockOf(book, content).Key;

    private static BookElement ElementOf(Book book, string content) => BookElements.Find(book, KeyOf(book, content))!;

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
    public void InsertAfter_Heading_NewEmptyHeadingSameLevelAfterWholeSection()
    {
        var book = Load(Text);

        var key = ManuscriptEditor.InsertAfter(book, ElementOf(book, "## Eins-A"));

        AssertResult(Text, book, Text.Replace("\t- Text nach A\n", "\t- ## \n\t- Text nach A\n"), [], ["\t- ## "]);
        var node = Book.Load(book.Page).FindNode(key)!;
        Assert.Equal(2, node.Level);
        Assert.Equal("", node.Title);
        Assert.Equal(KeyOf(book, "# Eins"), node.Parent!.Key);
    }

    [Fact]
    public void InsertAfter_TextBlock_EmptyBlockDirectlyAfter()
    {
        var book = Load(Text);

        var key = ManuscriptEditor.InsertAfter(book, ElementOf(book, "Text eins"));

        AssertResult(Text, book, Text.Replace("\t- ## Eins-A\n", "\t-\n\t- ## Eins-A\n"), [], ["\t-"]);
        var reloaded = Book.Load(book.Page);
        Assert.Equal(new BookElement(ElementKind.TextBlock, key), BookElements.Find(reloaded, key));
        Assert.Equal("", reloaded.FindTextBlock(key)!.Text);
    }

    [Fact]
    public void InsertAfter_Detail_EmptyDetailSameDepthAfterItsDeeperDetails()
    {
        var book = Load(Text);

        var key = ManuscriptEditor.InsertAfter(book, ElementOf(book, "Detail 1"));

        AssertResult(Text, book, Text.Replace("\t\t- Detail 2\n", "\t\t-\n\t\t- Detail 2\n"), [], ["\t\t-"]);
        var paragraph = Book.Load(book.Page).FindTextBlock(KeyOf(book, "Text eins"))!.Paragraphs.Single(p => p.Block.Key == key);
        Assert.Equal(1, paragraph.Depth);
        Assert.Equal("", paragraph.Text);
    }

    [Fact]
    public void InsertChild_Heading_FirstSubsectionAfterOwnTextBlocks()
    {
        var book = Load(Text);

        var key = ManuscriptEditor.InsertChild(book, ElementOf(book, "# Eins"));

        AssertResult(Text, book, Text.Replace("\t- ## Eins-A\n", "\t- ## \n\t- ## Eins-A\n"), [], ["\t- ## "]);
        var eins = Book.Load(book.Page).FindNode(KeyOf(book, "# Eins"))!;
        Assert.Equal(key, eins.Children.First().Key);
        Assert.Equal(2, eins.Children.First().Level);

        // Without sub-sections, the new heading follows the own text blocks.
        var leaf = Load(Text);
        ManuscriptEditor.InsertChild(leaf, ElementOf(leaf, "## Eins-B"));
        AssertResult(Text, leaf, Text.Replace("\t\t- Text B\n", "\t\t- Text B\n\t\t- ### \n"), [], ["\t\t- ### "]);
    }

    [Fact]
    public void InsertChild_TextBlock_FirstDetailDepth1()
    {
        var book = Load(Text);

        var key = ManuscriptEditor.InsertChild(book, ElementOf(book, "Text eins"));

        AssertResult(Text, book, Text.Replace("\t\t- Detail 1\n", "\t\t-\n\t\t- Detail 1\n"), [], ["\t\t-"]);
        var first = Book.Load(book.Page).FindTextBlock(KeyOf(book, "Text eins"))!.Paragraphs[0];
        Assert.Equal((key, 1), (first.Block.Key, first.Depth));
    }

    [Fact]
    public void InsertChild_Detail_FirstDetailOneDeeper()
    {
        var book = Load(Text);

        var key = ManuscriptEditor.InsertChild(book, ElementOf(book, "Detail 1"));

        AssertResult(Text, book, Text.Replace("\t\t\t- Detail 1a\n", "\t\t\t-\n\t\t\t- Detail 1a\n"), [], ["\t\t\t-"]);
        var paragraphs = Book.Load(book.Page).FindTextBlock(KeyOf(book, "Text eins"))!.Paragraphs;
        Assert.Equal((key, 2), (paragraphs[1].Block.Key, paragraphs[1].Depth));
    }

    [Fact]
    public void InsertChild_Level6Heading_Throws_CanInsertChildFalse()
    {
        var book = Load(LevelSix);
        var six = ElementOf(book, "###### 6");

        Assert.False(ManuscriptEditor.CanInsertChild(book, six));
        Assert.True(ManuscriptEditor.CanInsertChild(book, ElementOf(book, "##### 5")));
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.InsertChild(book, six));

        Assert.False(book.Page.IsDirty);
        Assert.Equal(LevelSix, Serialize(book));
    }

    [Fact]
    public void Indent_BecomesLastChildOfPreviousSameLevelHeading_SubtreeReleveled()
    {
        var book = Load(Text);
        var zwei = KeyOf(book, "# Zwei");

        Assert.True(ManuscriptEditor.CanIndent(book, zwei));
        ManuscriptEditor.Indent(book, zwei);

        AssertResult(
            Text,
            book,
            Text.Replace("- # Zwei\n\t- Text zwei\n\t- ## Zwei-A\n", "\t- ## Zwei\n\t\t- Text zwei\n\t\t- ### Zwei-A\n"),
            ["- # Zwei", "\t- Text zwei", "\t- ## Zwei-A"],
            ["\t- ## Zwei", "\t\t- Text zwei", "\t\t- ### Zwei-A"]);
        var reloaded = Book.Load(book.Page);
        Assert.Equal(["## Eins-A", "## Eins-B", "## Zwei"], reloaded.FindNode(KeyOf(book, "# Eins"))!.Children.Select(c => c.Block!.Content));
        Assert.Equal(3, reloaded.FindNode(KeyOf(book, "### Zwei-A"))!.Level);
        Assert.Empty(reloaded.Warnings);
    }

    [Fact]
    public void Indent_WithoutPreviousSameLevelHeading_Throws_CanIndentFalse()
    {
        var book = Load(Text);

        foreach (var first in new[] { "# Eins", "## Eins-A", "### Eins-A-i", "## Zwei-A" })
        {
            Assert.False(ManuscriptEditor.CanIndent(book, KeyOf(book, first)));
            Assert.Throws<ArgumentException>(() => ManuscriptEditor.Indent(book, KeyOf(book, first)));
        }

        Assert.False(ManuscriptEditor.CanIndent(book, KeyOf(book, "Text eins")));
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.Indent(book, KeyOf(book, "Text eins")));
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.Indent(book, Guid.NewGuid()));

        // Indenting would push the subtree beyond level 6.
        var deep = Load("type:: book\n\n- # A\n- # B\n\t- ## C\n\t\t- ### D\n\t\t\t- #### E\n\t\t\t\t- ##### F\n\t\t\t\t\t- ###### G\n");
        Assert.False(ManuscriptEditor.CanIndent(deep, KeyOf(deep, "# B")));
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.Indent(deep, KeyOf(deep, "# B")));

        Assert.False(book.Page.IsDirty);
        Assert.False(deep.Page.IsDirty);
        Assert.Equal(Text, Serialize(book));
    }

    [Fact]
    public void Outdent_PlacedDirectlyAfterFormerParentSection()
    {
        var book = Load(Text);
        var key = KeyOf(book, "### Eins-A-i");

        Assert.True(ManuscriptEditor.CanOutdent(book, key));
        ManuscriptEditor.Outdent(book, key);

        // Directly after the section of "Eins-A", before the text block "Text nach A" of "Eins".
        AssertResult(
            Text,
            book,
            Text.Replace("\t\t- ### Eins-A-i\n\t\t\t- tief\n\t- Text nach A\n", "\t- ## Eins-A-i\n\t\t- tief\n\t- Text nach A\n"),
            ["\t\t- ### Eins-A-i", "\t\t\t- tief"],
            ["\t- ## Eins-A-i", "\t\t- tief"]);
        var reloaded = Book.Load(book.Page);
        Assert.Equal(["## Eins-A", "## Eins-A-i", "## Eins-B"], reloaded.FindNode(KeyOf(book, "# Eins"))!.Children.Select(c => c.Block!.Content));
        Assert.Empty(reloaded.Warnings);

        // To the root: the subtree is releveled.
        var top = Load(Text);
        ManuscriptEditor.Outdent(top, KeyOf(top, "## Eins-A"));
        Assert.Equal(
            Text.Replace("\t- ## Eins-A\n\t  collapsed:: true\n\t\t- Text A\n\t\t- ### Eins-A-i\n\t\t\t- tief\n", "")
                .Replace("- # Zwei\n", "- # Eins-A\n  collapsed:: true\n\t- Text A\n\t- ## Eins-A-i\n\t\t- tief\n- # Zwei\n"),
            Serialize(top));
    }

    [Fact]
    public void Outdent_Level1_Throws_CanOutdentFalse()
    {
        var book = Load(Text);

        Assert.False(ManuscriptEditor.CanOutdent(book, KeyOf(book, "# Eins")));
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.Outdent(book, KeyOf(book, "# Eins")));
        Assert.False(ManuscriptEditor.CanOutdent(book, KeyOf(book, "Text eins")));
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.Outdent(book, KeyOf(book, "Text eins")));
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.Outdent(book, Guid.NewGuid()));

        Assert.False(book.Page.IsDirty);
        Assert.Equal(Text, Serialize(book));
    }

    [Fact]
    public void RemoveHeading_TextAndSubsectionsMoveUpInPlace_SubheadingsOneLevelHigher()
    {
        var book = Load(Text);

        var reveal = ManuscriptEditor.RemoveHeading(book, KeyOf(book, "## Eins-A")).Reveal;

        AssertResult(
            Text,
            book,
            Text.Replace(
                "\t- ## Eins-A\n\t  collapsed:: true\n\t\t- Text A\n\t\t- ### Eins-A-i\n\t\t\t- tief\n",
                "\t- Text A\n\t- ## Eins-A-i\n\t\t- tief\n"),
            ["\t- ## Eins-A", "\t  collapsed:: true", "\t\t- Text A", "\t\t- ### Eins-A-i", "\t\t\t- tief"],
            ["\t- Text A", "\t- ## Eins-A-i", "\t\t- tief"]);
        Assert.Equal(KeyOf(book, "Text A"), reveal);
        var reloaded = Book.Load(book.Page);
        var eins = reloaded.FindNode(KeyOf(book, "# Eins"))!;
        Assert.Equal(
            [KeyOf(book, "Text eins"), KeyOf(book, "Text A"), KeyOf(book, "## Eins-A-i"), KeyOf(book, "Text nach A"), KeyOf(book, "## Eins-B")],
            eins.Items.Select(item => item switch { OutlineNode n => n.Key, TextBlock t => t.Key, _ => Guid.Empty }));
        Assert.Equal(2, reloaded.FindNode(KeyOf(book, "## Eins-A-i"))!.Level);
        Assert.Empty(reloaded.Warnings);
    }

    [Fact]
    public void RemoveHeading_FirstHeading_ContentJoinsPrologue()
    {
        var book = Load(Text);

        var reveal = ManuscriptEditor.RemoveHeading(book, KeyOf(book, "# Eins")).Reveal;

        Assert.Equal(
            "type:: book\n" +
            "\n" +
            "- Vorspann\n" +
            "- Text eins\n" +
            "\t- Detail 1\n" +
            "\t\t- Detail 1a\n" +
            "\t- Detail 2\n" +
            "- # Eins-A\n" +
            "  collapsed:: true\n" +
            "\t- Text A\n" +
            "\t- ## Eins-A-i\n" +
            "\t\t- tief\n" +
            "- Text nach A\n" +
            "- # Eins-B\n" +
            "\t- Text B\n" +
            "- # Zwei\n" +
            "\t- Text zwei\n" +
            "\t- ## Zwei-A\n",
            Serialize(book));
        Assert.Equal(KeyOf(book, "Text eins"), reveal);
        var reloaded = Book.Load(book.Page);
        Assert.Equal(Guid.Empty, BookElements.SectionOf(reloaded, BookElements.Find(reloaded, KeyOf(book, "Text eins"))!));
        Assert.Equal(Guid.Empty, BookElements.SectionOf(reloaded, BookElements.Find(reloaded, KeyOf(book, "Text nach A"))!));
        Assert.Equal(["# Eins-A", "# Eins-B", "# Zwei"], reloaded.Root.Children.Select(c => c.Block!.Content));
        Assert.Empty(reloaded.Warnings);
    }

    [Fact]
    public void RemoveHeading_WithoutContent_RevealsPreviousElement()
    {
        var book = Load(Text);
        Assert.Equal(KeyOf(book, "Text zwei"), ManuscriptEditor.RemoveHeading(book, KeyOf(book, "## Zwei-A")).Reveal);
        Assert.Equal(Text.Replace("\t- ## Zwei-A\n", ""), Serialize(book));

        var single = Load("type:: book\n\n- # Leer\n");
        Assert.Equal(Guid.Empty, ManuscriptEditor.RemoveHeading(single, KeyOf(single, "# Leer")).Reveal);
        Assert.Equal("type:: book\n\n", Serialize(single));

        Assert.Throws<ArgumentException>(() => ManuscriptEditor.RemoveHeading(book, KeyOf(book, "Text zwei")));
    }

    [Theory]
    [InlineData("## Eins-A")]
    [InlineData("# Eins")]
    [InlineData("## Zwei-A")]
    [InlineData("### Eins-A-i")]
    public void RemoveHeading_RestoreInto_NothingElseChanged_GivesOriginalBytes(string heading)
    {
        // Also with CRLF, spaces as indentation and no final line ending, so that every line ending is restored.
        foreach (var text in new[] { Text, Text.Replace("\n", "\r\n")[..^2], Text.Replace("\t", "  ")[..^1] })
        {
            var book = Load(text);
            var original = PageSerializer.Serialize(book.Page);
            var keys = book.Page.AllBlocks().Select(b => b.Key).ToList();

            var removed = ManuscriptEditor.RemoveHeading(book, KeyOf(book, heading));
            book.Page.MarkSaved();
            Assert.True(removed.CanRestore(book.Page));
            removed.RestoreInto(book.Page);

            Assert.Equal(original, PageSerializer.Serialize(book.Page));
            Assert.Equal(keys, book.Page.AllBlocks().Select(b => b.Key));
            Assert.True(book.Page.IsDirty);

            // Once the heading is back, it is not restored a second time.
            Assert.False(removed.CanRestore(book.Page));
        }
    }

    [Fact]
    public void RemoveHeading_RestoreInto_AfterOtherEdits_PutsHeadingBackAroundItsContent()
    {
        var book = Load(Text);
        var einsA = KeyOf(book, "## Eins-A");
        var removed = ManuscriptEditor.RemoveHeading(book, einsA);

        // Later edits elsewhere in the book (text and structure) and inside the moved blocks (a text, a sub-heading's title).
        BlockOf(book, "Text zwei").SetContent("Text zwei geändert");
        var later = Book.Load(book.Page);
        ManuscriptEditor.InsertAfter(later, BookElements.Find(later, KeyOf(book, "Text B"))!);
        BlockOf(book, "Text A").SetContent("Text A neu");
        BlockOf(book, "## Eins-A-i").SetContent("## Eins-A-i neu");

        Assert.True(removed.CanRestore(book.Page));
        removed.RestoreInto(book.Page);

        Assert.Equal(
            Text.Replace("\t\t- Text A\n\t\t- ### Eins-A-i\n", "\t\t- Text A neu\n\t\t- ### Eins-A-i neu\n")
                .Replace("\t\t- Text B\n", "\t\t- Text B\n\t\t-\n")
                .Replace("\t- Text zwei\n", "\t- Text zwei geändert\n"),
            Serialize(book));
        var reloaded = Book.Load(book.Page);
        Assert.Equal(("Eins-A", 2), (reloaded.FindNode(einsA)!.Title, reloaded.FindNode(einsA)!.Level));
        Assert.Equal(einsA, reloaded.FindNode(KeyOf(book, "### Eins-A-i neu"))!.Parent!.Key);
        Assert.Equal(einsA, reloaded.FindTextBlock(KeyOf(book, "Text A neu"))!.Section.Key);
        Assert.Empty(reloaded.Warnings);
    }

    [Fact]
    public void RemoveHeading_RestoreInto_MovedChildGoneOrElsewhere_Refused_PageUnchanged()
    {
        // A moved child was deleted.
        var deleted = Load(Text);
        var removed = ManuscriptEditor.RemoveHeading(deleted, KeyOf(deleted, "## Eins-A"));
        deleted.Page.RemoveBlock(BlockOf(deleted, "Text A"));
        AssertRefused(deleted, removed);

        // A moved child was moved into another section.
        var moved = Load(Text);
        removed = ManuscriptEditor.RemoveHeading(moved, KeyOf(moved, "## Eins-A"));
        moved.Page.MoveBlock(BlockOf(moved, "Text A"), BlockOf(moved, "# Zwei"), 0);
        AssertRefused(moved, removed);

        // The heading's former parent is gone.
        var parentGone = Load(Text);
        removed = ManuscriptEditor.RemoveHeading(parentGone, KeyOf(parentGone, "### Eins-A-i"));
        parentGone.Page.RemoveBlock(BlockOf(parentGone, "## Eins-A"));
        AssertRefused(parentGone, removed);

        // A deeper moved block (below a moved child) was moved into another section at the same depth, so that its
        // lines are still those the command left: putting it back would make it a detail of "Text B".
        var deep = Load(Text);
        removed = ManuscriptEditor.RemoveHeading(deep, KeyOf(deep, "## Eins-A"));
        deep.Page.MoveBlock(BlockOf(deep, "tief"), BlockOf(deep, "## Eins-B"), 1);
        AssertRefused(deep, removed);

        // A moved sub-heading below a moved child was moved into another section.
        var deepHeading = Load(Text);
        removed = ManuscriptEditor.RemoveHeading(deepHeading, KeyOf(deepHeading, "# Eins"));
        deepHeading.Page.MoveBlock(BlockOf(deepHeading, "## Eins-A-i"), BlockOf(deepHeading, "# Eins-B"), 1);
        AssertRefused(deepHeading, removed);

        static void AssertRefused(Book book, RemovedHeading removed)
        {
            var before = Serialize(book);
            Assert.False(removed.CanRestore(book.Page));
            Assert.Throws<InvalidOperationException>(() => removed.RestoreInto(book.Page));
            Assert.Equal(before, Serialize(book));
        }
    }

    [Fact]
    public void RemoveHeading_RestoreInto_TextOfDeeperMovedBlockEdited_StillRestored()
    {
        var book = Load(Text);
        var removed = ManuscriptEditor.RemoveHeading(book, KeyOf(book, "## Eins-A"));
        BlockOf(book, "tief").SetContent("tief neu");

        Assert.True(removed.CanRestore(book.Page));
        removed.RestoreInto(book.Page);

        AssertResult(Text, book, Text.Replace("\t\t\t- tief\n", "\t\t\t- tief neu\n"), ["\t\t\t- tief"], ["\t\t\t- tief neu"]);
    }

    [Fact]
    public void DeleteDetail_RemovesDetailWithDeeperDetails()
    {
        var book = Load(Text);

        ManuscriptEditor.DeleteDetail(book, KeyOf(book, "Detail 1"));

        AssertResult(
            Text,
            book,
            Text.Replace("\t\t- Detail 1\n\t\t\t- Detail 1a\n", ""),
            ["\t\t- Detail 1", "\t\t\t- Detail 1a"],
            []);

        var untouched = Load(Text);
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.DeleteDetail(untouched, KeyOf(untouched, "Text eins")));
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.DeleteDetail(untouched, KeyOf(untouched, "# Eins")));
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.DeleteDetail(untouched, Guid.NewGuid()));
        Assert.False(untouched.Page.IsDirty);
    }

    [Fact]
    public void UnknownElement_Throws_PageUnchanged()
    {
        var book = Load(Text);
        var unknown = new BookElement(ElementKind.TextBlock, Guid.NewGuid());
        var wrongKind = new BookElement(ElementKind.Heading, KeyOf(book, "Text eins"));

        Assert.Throws<ArgumentException>(() => ManuscriptEditor.InsertAfter(book, unknown));
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.InsertChild(book, unknown));
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.InsertAfter(book, wrongKind));
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.InsertChild(book, wrongKind));
        Assert.False(ManuscriptEditor.CanInsertChild(book, unknown));

        Assert.False(book.Page.IsDirty);
    }

    [Fact]
    public void EmptyHeading_ParsesAsHeadingWithEmptyTitle_RoundTrips()
    {
        var text = "type:: book\n\n- # \n\t- Text\n\t- ## \n";
        var book = Load(text);

        var top = Assert.Single(book.Root.Children);
        Assert.Equal((1, ""), (top.Level, top.Title));
        var sub = Assert.Single(top.Children);
        Assert.Equal((2, ""), (sub.Level, sub.Title));
        Assert.Empty(book.Warnings);
        Assert.Equal(text, Serialize(book));

        // A new heading is written in exactly this form, and a title can be given to it afterwards.
        var created = Load(Text);
        var key = ManuscriptEditor.InsertAfter(created, ElementOf(created, "# Zwei"));
        Assert.EndsWith("- # \n", Serialize(created));
        OutlineEditor.Rename(Book.Load(created.Page), key, "Drei");
        Assert.Equal(Text + "- # Drei\n", Serialize(created));
    }

    public static TheoryData<string> Commands =>
    [
        "InsertAfter heading", "InsertAfter last heading", "InsertAfter text", "InsertAfter detail", "InsertAfter last detail",
        "InsertChild heading", "InsertChild leaf heading", "InsertChild text", "InsertChild detail",
        "Indent", "Outdent", "Outdent to root",
        "RemoveHeading", "RemoveHeading first", "RemoveHeading last", "DeleteDetail",
    ];

    private static void Apply(Book book, string command)
    {
        switch (command)
        {
            case "InsertAfter heading": ManuscriptEditor.InsertAfter(book, ElementOf(book, "## Eins-A")); break;
            case "InsertAfter last heading": ManuscriptEditor.InsertAfter(book, ElementOf(book, "# Zwei")); break;
            case "InsertAfter text": ManuscriptEditor.InsertAfter(book, ElementOf(book, "Text eins")); break;
            case "InsertAfter detail": ManuscriptEditor.InsertAfter(book, ElementOf(book, "Detail 1")); break;
            case "InsertAfter last detail": ManuscriptEditor.InsertAfter(book, ElementOf(book, "Detail 2")); break;
            case "InsertChild heading": ManuscriptEditor.InsertChild(book, ElementOf(book, "# Eins")); break;
            case "InsertChild leaf heading": ManuscriptEditor.InsertChild(book, ElementOf(book, "## Zwei-A")); break;
            case "InsertChild text": ManuscriptEditor.InsertChild(book, ElementOf(book, "Text eins")); break;
            case "InsertChild detail": ManuscriptEditor.InsertChild(book, ElementOf(book, "Detail 1")); break;
            case "Indent": ManuscriptEditor.Indent(book, KeyOf(book, "# Zwei")); break;
            case "Outdent": ManuscriptEditor.Outdent(book, KeyOf(book, "### Eins-A-i")); break;
            case "Outdent to root": ManuscriptEditor.Outdent(book, KeyOf(book, "## Zwei-A")); break;
            case "RemoveHeading": ManuscriptEditor.RemoveHeading(book, KeyOf(book, "## Eins-A")); break;
            case "RemoveHeading first": ManuscriptEditor.RemoveHeading(book, KeyOf(book, "# Eins")); break;
            case "RemoveHeading last": ManuscriptEditor.RemoveHeading(book, KeyOf(book, "## Zwei-A")); break;
            case "DeleteDetail": ManuscriptEditor.DeleteDetail(book, KeyOf(book, "Detail 1")); break;
            default: throw new ArgumentOutOfRangeException(nameof(command), command, null);
        }
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public void Snapshot_RestoreInto_GivesOriginalBytes_ForEveryCommand(string command)
    {
        // Also with CRLF, spaces as indentation and no final line ending, so that every line ending is restored.
        var variants = new[]
        {
            Text,
            Text.Replace("\n", "\r\n")[..^2],
            Text.Replace("\t", "  ")[..^1],
        };
        foreach (var text in variants)
        {
            var book = Load(text);
            var original = PageSerializer.Serialize(book.Page);
            var keys = book.Page.AllBlocks().Select(b => b.Key).ToList();
            var snapshot = PageStructureSnapshot.Capture(book.Page);

            Apply(book, command);
            Assert.NotEqual(original, PageSerializer.Serialize(book.Page));
            Assert.False(snapshot.Matches(book.Page));
            snapshot.RestoreInto(book.Page);

            Assert.Equal(original, PageSerializer.Serialize(book.Page));
            Assert.True(snapshot.Matches(book.Page));
            Assert.Equal(keys, book.Page.AllBlocks().Select(b => b.Key));
            Assert.Equal(text.Replace("\r\n", "\n"), Serialize(Book.Load(book.Page)).Replace("\r\n", "\n"));
        }
    }

    [Fact]
    public void Snapshot_RestoreInto_AfterSave_MarksChangedBlocksDirty()
    {
        var book = Load(Text);
        var snapshot = PageStructureSnapshot.Capture(book.Page);
        ManuscriptEditor.DeleteDetail(book, KeyOf(book, "Detail 1"));
        book.Page.MarkSaved();

        snapshot.RestoreInto(book.Page);

        Assert.True(book.Page.IsDirty);
        Assert.Equal(Text, Serialize(book));
    }

    [Fact]
    public void Snapshot_Matches_FalseAfterAnyFurtherChange()
    {
        var book = Load(Text);
        ManuscriptEditor.InsertAfter(book, ElementOf(book, "## Eins-A"));
        var after = PageStructureSnapshot.Capture(book.Page);
        Assert.True(after.Matches(book.Page));

        // A title typed into the new heading.
        var reloaded = Book.Load(book.Page);
        var empty = reloaded.Root.Children.First().Children.Single(c => c.Title == "");
        empty.Block!.SetContent("## Neu");
        Assert.False(after.Matches(book.Page));
        empty.Block.SetContent("## ");
        Assert.True(after.Matches(book.Page));

        // A further structure change.
        ManuscriptEditor.DeleteDetail(reloaded, KeyOf(book, "Detail 1"));
        Assert.False(after.Matches(book.Page));

        // Another page with the same bytes is not the captured one.
        Assert.False(after.Matches(LogseqParser.Parse("Buch - Test.md", PageSerializer.Serialize(book.Page))));
        var snapshot = PageStructureSnapshot.Capture(book.Page);
        Assert.True(snapshot.Matches(book.Page));
        Assert.False(snapshot.Matches(LogseqParser.Parse("Buch - Test.md", PageSerializer.Serialize(book.Page))));
    }
}
