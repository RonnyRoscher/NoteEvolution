using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Tests.Books;

public class BookRangesTests
{
    // A3 is an own text block of "# A" that stands after its sub-section "## A-i" in the file.
    private const string Text =
        "type:: book\n" +
        "\n" +
        "- Vorspann\n" +
        "- # A\n" +
        "\t- A1\n" +
        "\t\t- d1\n" +
        "\t\t\t- d1a\n" +
        "\t- A2\n" +
        "\t- ## A-i\n" +
        "\t\t- Ai1\n" +
        "\t- A3\n" +
        "- # B\n" +
        "\t- B1\n";

    private static Book Load(string text) =>
        Book.Load(LogseqParser.Parse("Buch - Test.md", Encoding.UTF8.GetBytes(text)));

    private static Block BlockOf(Book book, string content) => book.Page.AllBlocks().Single(b => b.Content == content);

    private static Guid KeyOf(Book book, string content) => BlockOf(book, content).Key;

    private static BookElement ElementOf(Book book, string content) => BookElements.Find(book, KeyOf(book, content))!;

    private static RangeInfo RangeOf(Book book, string content, int level) => BookRanges.Of(book, ElementOf(book, content), level);

    private static string ContentOf(Book book, Guid key) => book.Page.AllBlocks().Single(b => b.Key == key).Content;

    /// <summary>The marked nodes by content; an own-text-only node is suffixed with " (own)".</summary>
    private static string[] Nodes(Book book, RangeInfo range) =>
        [.. range.Nodes.Select(n => ContentOf(book, n.Key) + (n.OwnTextOnly ? " (own)" : ""))];

    private static string[] TextBlocks(Book book, RangeInfo range) => [.. range.TextBlockKeys.Select(key => ContentOf(book, key))];

    private static string[][] MergeGroups(Book book, RangeInfo range) =>
        [.. range.MergeGroups.Select(group => group.Select(key => ContentOf(book, key)).ToArray())];

    [Fact]
    public void Range_TextBlockLevel0_WholeBlock()
    {
        var book = Load(Text);

        var range = RangeOf(book, "A2", 0);

        Assert.Equal(ElementOf(book, "A2"), range.Head);
        Assert.Equal(0, range.Level);
        Assert.Equal(["A2"], Nodes(book, range));
        Assert.Equal(["A2"], TextBlocks(book, range));
        Assert.Equal(KeyOf(book, "# A"), range.SectionKey);
        Assert.Empty(range.MergeGroups);

        // A text block with details is marked whole, details included.
        Assert.Equal(["A1"], Nodes(book, RangeOf(book, "A1", 0)));
    }

    [Fact]
    public void Range_TextBlockLevelMinus1_OwnTextOnly()
    {
        var book = Load(Text);

        var range = RangeOf(book, "A2", -1);

        Assert.Equal(ElementOf(book, "A2"), range.Head);
        Assert.Equal(-1, range.Level);
        Assert.Equal([new MarkedNode(KeyOf(book, "A2"), true)], range.Nodes);
        Assert.Equal([KeyOf(book, "A2")], range.TextBlockKeys);
        Assert.Equal(KeyOf(book, "# A"), range.SectionKey);
        Assert.Empty(range.MergeGroups);

        // In the prologue the section is the root.
        var prologue = RangeOf(book, "Vorspann", -1);
        Assert.Equal(["Vorspann (own)"], Nodes(book, prologue));
        Assert.Equal(Guid.Empty, prologue.SectionKey);
    }

    [Fact]
    public void Range_DetailLevel0_And_Minus1()
    {
        var book = Load(Text);

        var whole = RangeOf(book, "d1", 0);
        Assert.Equal(ElementOf(book, "d1"), whole.Head);
        Assert.Equal([new MarkedNode(KeyOf(book, "d1"), false)], whole.Nodes);
        Assert.Equal(["A1"], TextBlocks(book, whole));
        Assert.Equal(KeyOf(book, "# A"), whole.SectionKey);
        Assert.Empty(whole.MergeGroups);

        var own = RangeOf(book, "d1", -1);
        Assert.Equal(ElementOf(book, "d1"), own.Head);
        Assert.Equal([new MarkedNode(KeyOf(book, "d1"), true)], own.Nodes);
        Assert.Equal(["A1"], TextBlocks(book, own));
        Assert.Equal(KeyOf(book, "# A"), own.SectionKey);
    }

    [Fact]
    public void Range_DetailLevel1_ParentDetail_Level2_TextBlock()
    {
        var book = Load(Text);

        var parentDetail = RangeOf(book, "d1a", 1);
        Assert.Equal(ElementOf(book, "d1"), parentDetail.Head);
        Assert.Equal(1, parentDetail.Level);
        Assert.Equal(["d1"], Nodes(book, parentDetail));
        Assert.Equal(["A1"], TextBlocks(book, parentDetail));
        Assert.Equal(KeyOf(book, "# A"), parentDetail.SectionKey);

        var textBlock = RangeOf(book, "d1a", 2);
        Assert.Equal(ElementOf(book, "A1"), textBlock.Head);
        Assert.Equal(["A1"], Nodes(book, textBlock));
        Assert.Equal(["A1"], TextBlocks(book, textBlock));
        Assert.Equal(KeyOf(book, "# A"), textBlock.SectionKey);

        var heading = RangeOf(book, "d1a", 3);
        Assert.Equal(ElementOf(book, "# A"), heading.Head);
        Assert.Equal(["# A", "A1", "A2", "## A-i", "Ai1", "A3"], Nodes(book, heading));

        Assert.Equal(4, BookRanges.MaxLevel(book, ElementOf(book, "d1a")));
        Assert.Null(RangeOf(book, "d1a", 4).Head);
    }

    [Fact]
    public void Range_HeadingLevel0_WholeSection_Minus1_OwnBlocksOnly()
    {
        var book = Load(Text);

        var whole = RangeOf(book, "# A", 0);
        Assert.Equal(ElementOf(book, "# A"), whole.Head);
        Assert.Equal(["# A", "A1", "A2", "## A-i", "Ai1", "A3"], Nodes(book, whole));
        Assert.Equal(["A1", "A2", "Ai1", "A3"], TextBlocks(book, whole));
        Assert.Equal(KeyOf(book, "# A"), whole.SectionKey);

        var own = RangeOf(book, "# A", -1);
        Assert.Equal(ElementOf(book, "# A"), own.Head);
        Assert.Equal(-1, own.Level);
        Assert.Equal(["# A", "A1", "A2", "A3"], Nodes(book, own));
        Assert.Equal(["A1", "A2", "A3"], TextBlocks(book, own));
        Assert.Equal(KeyOf(book, "# A"), own.SectionKey);
        Assert.Equal([["A1", "A2", "A3"]], MergeGroups(book, own));

        // A heading without own text blocks marks only itself at level -1.
        var nested = Load("type:: book\n\n- # E\n\t- ## F\n\t\t- T\n");
        var empty = RangeOf(nested, "# E", -1);
        Assert.Equal(["# E"], Nodes(nested, empty));
        Assert.Empty(empty.TextBlockKeys);
    }

    [Fact]
    public void Range_TextBlockLevel1_IsItsHeading()
    {
        var book = Load(Text);

        var section = RangeOf(book, "Ai1", 1);
        Assert.Equal(ElementOf(book, "## A-i"), section.Head);
        Assert.Equal(1, section.Level);
        Assert.Equal(["## A-i", "Ai1"], Nodes(book, section));
        Assert.Equal(["Ai1"], TextBlocks(book, section));
        Assert.Equal(KeyOf(book, "## A-i"), section.SectionKey);

        var parent = RangeOf(book, "Ai1", 2);
        Assert.Equal(ElementOf(book, "# A"), parent.Head);
        Assert.Equal(["# A", "A1", "A2", "## A-i", "Ai1", "A3"], Nodes(book, parent));
        Assert.Equal(KeyOf(book, "# A"), parent.SectionKey);
    }

    [Fact]
    public void Range_MaxLevel_WholeBook_HeadNull_SectionEmpty()
    {
        var book = Load(Text);
        int MaxLevel(string content) => BookRanges.MaxLevel(book, ElementOf(book, content));

        Assert.Equal(1, MaxLevel("Vorspann"));
        Assert.Equal(1, MaxLevel("# A"));
        Assert.Equal(2, MaxLevel("## A-i"));
        Assert.Equal(2, MaxLevel("A2"));
        Assert.Equal(3, MaxLevel("Ai1"));
        Assert.Equal(3, MaxLevel("d1"));

        var range = RangeOf(book, "A2", 2);
        Assert.Null(range.Head);
        Assert.Equal(2, range.Level);
        Assert.Equal(["Vorspann", "# A", "A1", "A2", "## A-i", "Ai1", "A3", "# B", "B1"], Nodes(book, range));
        Assert.Equal(["Vorspann", "A1", "A2", "Ai1", "A3", "B1"], TextBlocks(book, range));
        Assert.Equal(Guid.Empty, range.SectionKey);
        Assert.Equal([["A1", "A2", "A3"]], MergeGroups(book, range));

        var fromPrologue = RangeOf(book, "Vorspann", 1);
        Assert.Null(fromPrologue.Head);
        Assert.Equal(Guid.Empty, fromPrologue.SectionKey);

        Assert.Throws<ArgumentException>(() => BookRanges.MaxLevel(book, new BookElement(ElementKind.Detail, Guid.NewGuid())));
        Assert.Throws<ArgumentException>(() => BookRanges.Of(book, new BookElement(ElementKind.Heading, Guid.NewGuid()), 0));
    }

    [Fact]
    public void Range_LevelClampedToBounds()
    {
        var book = Load(Text);

        var above = RangeOf(book, "A2", 99);
        Assert.Equal(2, above.Level);
        Assert.Null(above.Head);

        var below = RangeOf(book, "A2", -5);
        Assert.Equal(-1, below.Level);
        Assert.Equal(["A2 (own)"], Nodes(book, below));
    }

    [Fact]
    public void Range_HeadingTextAfterSubsections_NodesNotContiguous_GroupInFileOrder()
    {
        var book = Load(Text);

        var range = RangeOf(book, "# A", 0);

        Assert.Equal(
            [
                new MarkedNode(KeyOf(book, "# A"), false),
                new MarkedNode(KeyOf(book, "A1"), false),
                new MarkedNode(KeyOf(book, "A2"), false),
                new MarkedNode(KeyOf(book, "## A-i"), false),
                new MarkedNode(KeyOf(book, "Ai1"), false),
                new MarkedNode(KeyOf(book, "A3"), false),
            ],
            range.Nodes);
        Assert.Equal([["A1", "A2", "A3"]], MergeGroups(book, range));
    }

    [Fact]
    public void Range_MergeGroups_PerHeading_OnlyGroupsOfTwoOrMore()
    {
        var book = Load(
            "type:: book\n" +
            "\n" +
            "- P1\n" +
            "- P2\n" +
            "- # X\n" +
            "\t- X1\n" +
            "\t- ## Y\n" +
            "\t\t- Y1\n" +
            "\t\t- Y2\n" +
            "\t- X2\n" +
            "\t- ## Z\n" +
            "\t\t- Z1\n");

        // Whole book: the root's prologue blocks, then each heading's own blocks; "## Z" has only one.
        Assert.Equal([["P1", "P2"], ["X1", "X2"], ["Y1", "Y2"]], MergeGroups(book, RangeOf(book, "P1", 1)));
        Assert.Equal([["X1", "X2"], ["Y1", "Y2"]], MergeGroups(book, RangeOf(book, "# X", 0)));
        Assert.Equal([["Y1", "Y2"]], MergeGroups(book, RangeOf(book, "Y1", 1)));
        Assert.Equal([["X1", "X2"]], MergeGroups(book, RangeOf(book, "# X", -1)));
        Assert.Empty(RangeOf(book, "## Z", 0).MergeGroups);
        Assert.Empty(RangeOf(book, "X1", 0).MergeGroups);
        Assert.Empty(RangeOf(book, "X1", -1).MergeGroups);
    }

    [Fact]
    public void Range_IrregularLevels_FollowsTree()
    {
        // "### X-deep" skips a level below "# X"; "# Q" is not deeper than its parent "## P". The tree decides.
        var book = Load(
            "type:: book\n" +
            "\n" +
            "- # X\n" +
            "\t- ### X-deep\n" +
            "\t\t- T1\n" +
            "\t- ## X-mid\n" +
            "\t\t- T2\n" +
            "- ## P\n" +
            "\t- # Q\n" +
            "\t\t- T3\n");

        var deep = RangeOf(book, "T1", 1);
        Assert.Equal(ElementOf(book, "### X-deep"), deep.Head);
        Assert.Equal(["### X-deep", "T1"], Nodes(book, deep));

        var top = RangeOf(book, "T1", 2);
        Assert.Equal(ElementOf(book, "# X"), top.Head);
        Assert.Equal(["# X", "### X-deep", "T1", "## X-mid", "T2"], Nodes(book, top));
        Assert.Equal(["T1", "T2"], TextBlocks(book, top));
        Assert.Equal(3, BookRanges.MaxLevel(book, ElementOf(book, "T1")));

        var q = RangeOf(book, "T3", 1);
        Assert.Equal(ElementOf(book, "# Q"), q.Head);
        Assert.Equal(["# Q", "T3"], Nodes(book, q));

        var p = RangeOf(book, "# Q", 1);
        Assert.Equal(ElementOf(book, "## P"), p.Head);
        Assert.Equal(["## P", "# Q", "T3"], Nodes(book, p));
        Assert.Equal(KeyOf(book, "## P"), p.SectionKey);
        Assert.Equal(3, BookRanges.MaxLevel(book, ElementOf(book, "T3")));
    }
}
