using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.TestSupport;

namespace NoteEvolution.Core.Tests.Books;

public class OutlineEditorTests
{
    private const string Outline =
        "type:: book\n" +
        "\n" +
        "- Vorspann\n" +
        "- # Eins\n" +
        "\t- Text eins\n" +
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

    private static Book Load(string text) =>
        Book.Load(LogseqParser.Parse("Buch - Test.md", Encoding.UTF8.GetBytes(text)));

    private static string Serialize(Book book) => Encoding.UTF8.GetString(PageSerializer.Serialize(book.Page));

    private static Guid KeyOf(Book book, string title) => AllNodes(book.Root).Single(n => n.Title == title).Key;

    private static IEnumerable<OutlineNode> AllNodes(OutlineNode node) =>
        node.Children.SelectMany(c => AllNodes(c).Prepend(c));

    private static (int Removed, int Added) Diff(string before, string after)
    {
        var (removed, added) = LineDiff.Changed(Encoding.UTF8.GetBytes(before), Encoding.UTF8.GetBytes(after));
        return (removed.Count, added.Count);
    }

    [Fact]
    public void AddHeading_UsesParentLevelPlusOne()
    {
        var book = Load(Samples.SpecBook);

        var key = OutlineEditor.AddHeading(book, KeyOf(book, "Vertrauen"), "Mut");

        var after = Serialize(book);
        Assert.Equal(Samples.SpecBook + "\t\t- ### Mut\n", after);
        var reloaded = Book.Load(book.Page);
        var node = reloaded.FindNode(key);
        Assert.NotNull(node);
        Assert.Equal(3, node.Level);
        Assert.Equal("Mut", node.Title);
        Assert.Equal(KeyOf(reloaded, "Vertrauen"), node.Parent!.Key);
    }

    [Fact]
    public void AddHeading_AtRoot_IsLevelOneAtEnd()
    {
        var book = Load(Outline);

        var key = OutlineEditor.AddHeading(book, Guid.Empty, "Drei");

        Assert.Equal(Outline + "- # Drei\n", Serialize(book));
        Assert.Equal(1, Book.Load(book.Page).FindNode(key)!.Level);
    }

    [Fact]
    public void AddHeading_AppendsAfterTextAndSubsections()
    {
        var book = Load(Outline);

        OutlineEditor.AddHeading(book, KeyOf(book, "Eins"), "Eins-C");

        Assert.Equal(Outline.Replace("- # Zwei\n", "\t- ## Eins-C\n- # Zwei\n"), Serialize(book));
    }

    [Fact]
    public void AddHeading_UnderLevelSix_Throws_PageUnchanged()
    {
        var book = Load("type:: book\n\n- # 1\n\t- ## 2\n\t\t- ### 3\n\t\t\t- #### 4\n\t\t\t\t- ##### 5\n\t\t\t\t\t- ###### 6\n");

        Assert.Throws<ArgumentException>(() => OutlineEditor.AddHeading(book, KeyOf(book, "6"), "7"));

        Assert.False(book.Page.IsDirty);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("zwei\nZeilen")]
    [InlineData("zwei\rZeilen")]
    public void InvalidTitle_Throws_PageUnchanged(string title)
    {
        var book = Load(Outline);

        Assert.Throws<ArgumentException>(() => OutlineEditor.AddHeading(book, Guid.Empty, title));
        Assert.Throws<ArgumentException>(() => OutlineEditor.Rename(book, KeyOf(book, "Eins"), title));

        Assert.False(book.Page.IsDirty);
        Assert.Equal(Outline, Serialize(book));
    }

    [Fact]
    public void UnknownOrNonHeadingKey_Throws()
    {
        var book = Load(Outline);
        var textBlock = book.Page.AllBlocks().Single(b => b.Content == "Text eins");

        Assert.Throws<ArgumentException>(() => OutlineEditor.AddHeading(book, Guid.NewGuid(), "x"));
        Assert.Throws<ArgumentException>(() => OutlineEditor.AddHeading(book, textBlock.Key, "x"));
        Assert.Throws<ArgumentException>(() => OutlineEditor.Rename(book, Guid.Empty, "x"));
        Assert.Throws<ArgumentException>(() => OutlineEditor.Rename(book, textBlock.Key, "x"));
        Assert.Throws<ArgumentException>(() => OutlineEditor.MoveSection(book, Guid.Empty, Guid.Empty, 0));
        Assert.Throws<ArgumentException>(() => OutlineEditor.MoveSection(book, KeyOf(book, "Eins"), textBlock.Key, 0));
        Assert.False(book.Page.IsDirty);
    }

    [Fact]
    public void Rename_KeepsHashesAndProperties()
    {
        var book = Load(Outline);

        OutlineEditor.Rename(book, KeyOf(book, "Eins-A"), "  Eins-A neu ");

        var after = Serialize(book);
        Assert.Equal(Outline.Replace("\t- ## Eins-A\n\t  collapsed:: true\n", "\t- ## Eins-A neu\n\t  collapsed:: true\n"), after);
        Assert.Equal((1, 1), Diff(Outline, after));
        var node = Book.Load(book.Page).FindNode(KeyOf(book, "Eins-A"));
        Assert.Equal("Eins-A neu", node!.Title);
        Assert.Equal(2, node.Level);
        Assert.Equal("true", node.Block!.GetProperty("collapsed"));
    }

    [Fact]
    public void Rename_SameTitle_NotDirty()
    {
        var book = Load(Outline);

        OutlineEditor.Rename(book, KeyOf(book, "Eins-A"), "Eins-A");

        Assert.False(book.Page.IsDirty);
    }

    [Fact]
    public void MoveSection_RecomputesLevelsInSubtree()
    {
        var book = Load(Outline);

        // "Eins-A" (level 2, with level-3 child) becomes the first child of "Zwei-A" (level 2) -> level 3 / 4.
        OutlineEditor.MoveSection(book, KeyOf(book, "Eins-A"), KeyOf(book, "Zwei-A"), 0);

        Assert.Equal(
            "type:: book\n\n" +
            "- Vorspann\n" +
            "- # Eins\n" +
            "\t- Text eins\n" +
            "\t- Text nach A\n" +
            "\t- ## Eins-B\n" +
            "\t\t- Text B\n" +
            "- # Zwei\n" +
            "\t- Text zwei\n" +
            "\t- ## Zwei-A\n" +
            "\t\t- ### Eins-A\n" +
            "\t\t  collapsed:: true\n" +
            "\t\t\t- Text A\n" +
            "\t\t\t- #### Eins-A-i\n" +
            "\t\t\t\t- tief\n",
            Serialize(book));
        var reloaded = Book.Load(book.Page);
        Assert.Equal(3, reloaded.FindNode(KeyOf(book, "Eins-A"))!.Level);
        Assert.Equal(4, reloaded.FindNode(KeyOf(book, "Eins-A-i"))!.Level);
        Assert.Empty(reloaded.Warnings);
    }

    [Fact]
    public void MoveSection_ToRoot_LiftsLevels()
    {
        var book = Load(Outline);

        OutlineEditor.MoveSection(book, KeyOf(book, "Eins-A"), Guid.Empty, 1);

        var reloaded = Book.Load(book.Page);
        Assert.Equal(["Eins", "Eins-A", "Zwei"], reloaded.Root.Children.Select(c => c.Title));
        Assert.Equal(1, reloaded.FindNode(KeyOf(book, "Eins-A"))!.Level);
        Assert.Equal(2, reloaded.FindNode(KeyOf(book, "Eins-A-i"))!.Level);
        Assert.Contains("- # Eins-A\n  collapsed:: true\n\t- Text A\n\t- ## Eins-A-i\n\t\t- tief\n- # Zwei\n", Serialize(book));
    }

    [Fact]
    public void MoveSection_OtherLinesUnchanged()
    {
        var book = Load(Outline);

        // Move the leaf "Eins-B" (no level change) in front of "Eins-A": only the moved block's lines differ.
        OutlineEditor.MoveSection(book, KeyOf(book, "Eins-B"), KeyOf(book, "Eins"), 0);

        var after = Serialize(book);
        Assert.Equal(
            Outline.Replace("\t- ## Eins-B\n\t\t- Text B\n", "").Replace("\t- ## Eins-A\n", "\t- ## Eins-B\n\t\t- Text B\n\t- ## Eins-A\n"),
            after);
        Assert.Equal((0, 2), Diff(Outline.Replace("\t- ## Eins-B\n\t\t- Text B\n", ""), after));

        // Moving into a deeper parent re-indents the subtree and rewrites only the heading lines of the subtree.
        var book2 = Load(Outline);
        OutlineEditor.MoveSection(book2, KeyOf(book2, "Eins-A"), KeyOf(book2, "Zwei-A"), 0);
        var (removed, added) = LineDiff.Changed(Encoding.UTF8.GetBytes(Outline), Encoding.UTF8.GetBytes(Serialize(book2)));
        Assert.Equal(5, removed.Count);
        Assert.Equal(5, added.Count);
    }

    [Fact]
    public void MoveSection_IndexCountsOnlyHeadings_KeepsTextBlocksWhereTheyAre()
    {
        var book = Load(Outline);

        // Children of "Eins": [Text eins, Eins-A, Text nach A, Eins-B]; headings: [Eins-A, Eins-B].
        OutlineEditor.MoveSection(book, KeyOf(book, "Zwei-A"), KeyOf(book, "Eins"), 1);

        Assert.Equal(
            Outline.Replace("\t- ## Zwei-A\n", "").Replace("\t- ## Eins-B\n", "\t- ## Zwei-A\n\t- ## Eins-B\n"),
            Serialize(book));

        var atEnd = Load(Outline);
        OutlineEditor.MoveSection(atEnd, KeyOf(atEnd, "Zwei-A"), KeyOf(atEnd, "Eins"), 2);
        Assert.Equal(
            Outline.Replace("\t- ## Zwei-A\n", "").Replace("- # Zwei\n", "\t- ## Zwei-A\n- # Zwei\n"),
            Serialize(atEnd));

        var first = Load(Outline);
        OutlineEditor.MoveSection(first, KeyOf(first, "Zwei-A"), KeyOf(first, "Eins"), 0);
        Assert.Equal(
            Outline.Replace("\t- ## Zwei-A\n", "").Replace("\t- ## Eins-A\n", "\t- ## Zwei-A\n\t- ## Eins-A\n"),
            Serialize(first));
    }

    [Fact]
    public void MoveSection_WithinSameParent_IndexCountedWithoutTheNode()
    {
        var book = Load(Outline);

        OutlineEditor.MoveSection(book, KeyOf(book, "Eins"), Guid.Empty, 1);

        Assert.Equal(["Zwei", "Eins"], Book.Load(book.Page).Root.Children.Select(c => c.Title));
    }

    [Fact]
    public void MoveSection_IntoOwnSubtree_Throws_PageUnchanged()
    {
        var book = Load(Outline);

        Assert.Throws<ArgumentException>(() => OutlineEditor.MoveSection(book, KeyOf(book, "Eins"), KeyOf(book, "Eins"), 0));
        Assert.Throws<ArgumentException>(() => OutlineEditor.MoveSection(book, KeyOf(book, "Eins"), KeyOf(book, "Eins-A-i"), 0));

        Assert.False(book.Page.IsDirty);
        Assert.Equal(Outline, Serialize(book));
    }

    [Fact]
    public void MoveSection_BeyondLevelSix_Throws_PageUnchanged()
    {
        var book = Load("type:: book\n\n- # A\n\t- ## B\n\t\t- ### C\n\t\t\t- #### D\n\t\t\t\t- ##### E\n- # F\n\t- ## G\n\t\t- ### H\n");

        // A has depth 5 (A..E); under G (level 2) it would reach level 7.
        Assert.Throws<ArgumentException>(() => OutlineEditor.MoveSection(book, KeyOf(book, "A"), KeyOf(book, "G"), 0));

        Assert.False(book.Page.IsDirty);
    }

    [Fact]
    public void MoveSection_IndexOutOfRange_Throws_PageUnchanged()
    {
        var book = Load(Outline);

        Assert.Throws<ArgumentOutOfRangeException>(() => OutlineEditor.MoveSection(book, KeyOf(book, "Zwei-A"), KeyOf(book, "Eins"), 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => OutlineEditor.MoveSection(book, KeyOf(book, "Zwei-A"), KeyOf(book, "Eins"), -1));

        Assert.False(book.Page.IsDirty);
    }

    [Fact]
    public void MoveSection_KeepsIdProperty()
    {
        var book = Load(Samples.SpecBook);
        var vertrauen = KeyOf(book, "Vertrauen");
        var textBlock = book.Page.AllBlocks().Single(b => b.Id is not null);

        OutlineEditor.MoveSection(book, vertrauen, Guid.Empty, 0);

        Assert.Equal(Guid.Parse("6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70"), textBlock.Id);
        Assert.Equal(1, Book.Load(book.Page).FindNode(vertrauen)!.Level);
    }
}
