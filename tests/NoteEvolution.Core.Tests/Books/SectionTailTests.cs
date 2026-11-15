using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;

namespace NoteEvolution.Core.Tests.Books;

/// <summary>
/// The read tail of a section (blocks on the heading's indentation that read as part of its section) and how
/// <see cref="OutlineEditor.MoveSection"/> and <see cref="OutlineEditor.MoveSectionAfter"/> take it along.
/// </summary>
public class SectionTailTests
{
    /// <summary>"A" has a tail (text, a deeper heading with text, text after it); "B" and "C" have none.</summary>
    private const string WithTail =
        "type:: book\n" +
        "\n" +
        "- # Eins\n" +
        "\t- ## A\n" +
        "\t- Text a\n" +
        "\t- ### A-i\n" +
        "\t\t- Text A-i\n" +
        "\t- Text nach A-i\n" +
        "\t- ## B\n" +
        "- # Zwei\n" +
        "\t- ## C\n";

    private static Book Load(string text) =>
        Book.Load(LogseqParser.Parse("Buch - Test.md", Encoding.UTF8.GetBytes(text)));

    private static string Serialize(Book book) => Encoding.UTF8.GetString(PageSerializer.Serialize(book.Page));

    private static Guid KeyOf(Book book, string title) => AllNodes(book.Root).Single(n => n.Title == title).Key;

    private static IEnumerable<OutlineNode> AllNodes(OutlineNode node) =>
        node.Children.SelectMany(c => AllNodes(c).Prepend(c));

    private static List<string> TailOf(Book book, string title) =>
        [.. OutlineEditor.ReadTail(book, KeyOf(book, title)).Select(block => block.Content)];

    [Fact]
    public void ReadTail_TextAndDeeperHeadings_UpToTheNextHeadingOfSameOrHigherLevel()
    {
        var book = Load(
            "type:: book\n\n" +
            "- # Eins\n" +
            "\t- ## A\n" +
            "\t- Text a\n" +
            "\t- ### A-i\n" +
            "\t\t- Text A-i\n" +
            "\t- #### A-i-x\n" +
            "\t- Text nach\n" +
            "\t- ## B\n" +
            "\t- Text B\n" +
            "- # Zwei\n");

        Assert.Equal(["Text a", "### A-i", "#### A-i-x", "Text nach"], TailOf(book, "A"));
        Assert.Equal(["#### A-i-x", "Text nach"], TailOf(book, "A-i"));
        Assert.Equal(["Text B"], TailOf(book, "B"));
        Assert.Empty(TailOf(book, "Zwei"));
    }

    [Fact]
    public void ReadTail_StopsAtTheAppendix()
    {
        var book = Load(
            "type:: book\n\n" +
            "- # Eins\n" +
            "- Text eins\n" +
            "- ## Glossar\n" +
            "  anhang:: glossar\n" +
            "\t- Begriff\n");

        Assert.Equal(["Text eins"], TailOf(book, "Eins"));
    }

    [Fact]
    public void ReadTail_NothingForAProperlyNestedSection()
    {
        var book = Load(
            "type:: book\n\n" +
            "- Vorspann\n" +
            "- # Eins\n" +
            "\t- Text eins\n" +
            "\t- ## A\n" +
            "\t\t- Text A\n" +
            "\t\t- ### A-i\n" +
            "\t- ## B\n" +
            "- # Zwei\n");

        Assert.Empty(TailOf(book, "Eins"));
        Assert.Empty(TailOf(book, "A"));
        Assert.Empty(TailOf(book, "A-i"));
    }

    [Fact]
    public void MoveSection_TakesTheTailAlong_NestedUnderTheHeading()
    {
        var book = Load(WithTail);

        OutlineEditor.MoveSection(book, KeyOf(book, "A"), KeyOf(book, "Zwei"), 1);

        Assert.Equal(
            "type:: book\n\n" +
            "- # Eins\n" +
            "\t- ## B\n" +
            "- # Zwei\n" +
            "\t- ## C\n" +
            "\t- ## A\n" +
            "\t\t- Text a\n" +
            "\t\t- ### A-i\n" +
            "\t\t\t- Text A-i\n" +
            "\t\t\t- Text nach A-i\n",
            Serialize(book));
        Assert.Empty(Book.Load(book.Page).Warnings);
    }

    [Fact]
    public void MoveSection_TailHeadingsAreNestedByLevel()
    {
        var book = Load(
            "type:: book\n\n" +
            "- # Eins\n" +
            "\t- ## A\n" +
            "\t- ### A1\n" +
            "\t- #### A1x\n" +
            "\t- ### A2\n" +
            "- # Zwei\n");

        OutlineEditor.MoveSection(book, KeyOf(book, "A"), KeyOf(book, "Zwei"), 0);

        Assert.Equal(
            "type:: book\n\n" +
            "- # Eins\n" +
            "- # Zwei\n" +
            "\t- ## A\n" +
            "\t\t- ### A1\n" +
            "\t\t\t- #### A1x\n" +
            "\t\t- ### A2\n",
            Serialize(book));

        // To the root the levels follow the new depth.
        var toRoot = Load(
            "type:: book\n\n" +
            "- # Eins\n" +
            "\t- ## A\n" +
            "\t- ### A1\n" +
            "\t- #### A1x\n" +
            "\t- ### A2\n" +
            "- # Zwei\n");

        OutlineEditor.MoveSection(toRoot, KeyOf(toRoot, "A"), Guid.Empty, 2);

        Assert.Equal(
            "type:: book\n\n" +
            "- # Eins\n" +
            "- # Zwei\n" +
            "- # A\n" +
            "\t- ## A1\n" +
            "\t\t- ### A1x\n" +
            "\t- ## A2\n",
            Serialize(toRoot));
    }

    [Fact]
    public void MoveSection_SamePlace_ChangesNothing_AlsoWithATail()
    {
        // Headings of "Eins" without "A": [A-i, B]. Before A-i (A's own tail) and before B are both A's place.
        foreach (var index in new[] { 0, 1 })
        {
            var book = Load(WithTail);

            OutlineEditor.MoveSection(book, KeyOf(book, "A"), KeyOf(book, "Eins"), index);

            Assert.False(book.Page.IsDirty);
            Assert.Equal(WithTail, Serialize(book));
        }

        var withoutTail = Load(WithTail);
        OutlineEditor.MoveSection(withoutTail, KeyOf(withoutTail, "B"), KeyOf(withoutTail, "Eins"), 2);
        Assert.False(withoutTail.Page.IsDirty);
        Assert.Equal(WithTail, Serialize(withoutTail));
    }

    [Fact]
    public void MoveSection_IntoItsReadTail_Refused_FileUnchanged()
    {
        var book = Load(WithTail);

        Assert.Throws<ArgumentException>(() => OutlineEditor.MoveSection(book, KeyOf(book, "A"), KeyOf(book, "A-i"), 0));

        Assert.False(book.Page.IsDirty);
        Assert.Equal(WithTail, Serialize(book));

        // Between two headings of its own tail.
        const string twoTailHeadings =
            "type:: book\n\n" +
            "- # Eins\n" +
            "\t- ## A\n" +
            "\t- ### A1\n" +
            "\t- ### A2\n" +
            "\t- ## B\n";
        var between = Load(twoTailHeadings);

        Assert.Throws<ArgumentException>(() => OutlineEditor.MoveSection(between, KeyOf(between, "A"), KeyOf(between, "Eins"), 1));

        Assert.False(between.Page.IsDirty);
        Assert.Equal(twoTailHeadings, Serialize(between));
    }

    [Fact]
    public void MoveSection_TailWouldExceedLevel6_Refused_FileUnchanged()
    {
        const string text =
            "type:: book\n\n" +
            "- # A\n" +
            "\t- ## B\n" +
            "\t- ### C\n" +
            "\t- #### D\n" +
            "\t- ##### E\n" +
            "- # F\n" +
            "\t- ## G\n" +
            "\t\t- ### H\n";
        var book = Load(text);

        // B alone would become level 4 under H; with its tail C, D, E nested below it E would reach level 7.
        Assert.Throws<ArgumentException>(() => OutlineEditor.MoveSection(book, KeyOf(book, "B"), KeyOf(book, "H"), 0));

        Assert.False(book.Page.IsDirty);
        Assert.Equal(text, Serialize(book));
    }

    [Fact]
    public void MoveSection_KeepsIdsSourcesAndDatesOfTheTail()
    {
        var book = Load(
            "type:: book\n\n" +
            "- # Eins\n" +
            "\t- ## A\n" +
            "\t- Text a\n" +
            "\t  id:: 6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70\n" +
            "\t  source:: ((6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f71))\n" +
            "\t  created:: 2026-11-01 09:30\n" +
            "\t  updated:: 2026-11-02 10:00\n" +
            "\t- ### A-i\n" +
            "\t  id:: 6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f72\n" +
            "- # Zwei\n");

        OutlineEditor.MoveSection(book, KeyOf(book, "A"), KeyOf(book, "Zwei"), 0);

        Assert.Equal(
            "type:: book\n\n" +
            "- # Eins\n" +
            "- # Zwei\n" +
            "\t- ## A\n" +
            "\t\t- Text a\n" +
            "\t\t  id:: 6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70\n" +
            "\t\t  source:: ((6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f71))\n" +
            "\t\t  created:: 2026-11-01 09:30\n" +
            "\t\t  updated:: 2026-11-02 10:00\n" +
            "\t\t- ### A-i\n" +
            "\t\t  id:: 6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f72\n",
            Serialize(book));
    }

    /// <summary>"A" has a sub-section and a tail heading "A-ii"; "C" is in another chapter.</summary>
    private const string Anchors =
        "type:: book\n\n" +
        "- Vorspann\n" +
        "- # Eins\n" +
        "\t- ## A\n" +
        "\t\t- Text A\n" +
        "\t\t- ### A-i\n" +
        "\t- ### A-ii\n" +
        "\t- ## B\n" +
        "- # Zwei\n" +
        "\t- ## C\n" +
        "\t\t- Text C\n";

    [Fact]
    public void MoveSectionAfter_SameLevelDirectlyAfterTheAnchorSection()
    {
        var book = Load(Anchors);

        OutlineEditor.MoveSectionAfter(book, KeyOf(book, "C"), KeyOf(book, "A"));

        Assert.Equal(
            "type:: book\n\n" +
            "- Vorspann\n" +
            "- # Eins\n" +
            "\t- ## A\n" +
            "\t\t- Text A\n" +
            "\t\t- ### A-i\n" +
            "\t- ### A-ii\n" +
            "\t- ## C\n" +
            "\t\t- Text C\n" +
            "\t- ## B\n" +
            "- # Zwei\n",
            Serialize(book));
    }

    [Fact]
    public void MoveSectionAfter_Root_FirstTopLevelSection()
    {
        var book = Load(Anchors);

        OutlineEditor.MoveSectionAfter(book, KeyOf(book, "C"), Guid.Empty);

        Assert.Equal(
            "type:: book\n\n" +
            "- Vorspann\n" +
            "- # C\n" +
            "\t- Text C\n" +
            "- # Eins\n" +
            "\t- ## A\n" +
            "\t\t- Text A\n" +
            "\t\t- ### A-i\n" +
            "\t- ### A-ii\n" +
            "\t- ## B\n" +
            "- # Zwei\n",
            Serialize(book));
    }

    [Fact]
    public void MoveSectionAfter_LevelsShift()
    {
        var book = Load(Anchors);

        OutlineEditor.MoveSectionAfter(book, KeyOf(book, "Zwei"), KeyOf(book, "B"));

        Assert.Equal(
            "type:: book\n\n" +
            "- Vorspann\n" +
            "- # Eins\n" +
            "\t- ## A\n" +
            "\t\t- Text A\n" +
            "\t\t- ### A-i\n" +
            "\t- ### A-ii\n" +
            "\t- ## B\n" +
            "\t- ## Zwei\n" +
            "\t\t- ### C\n" +
            "\t\t\t- Text C\n",
            Serialize(book));

        var up = Load(Anchors);
        OutlineEditor.MoveSectionAfter(up, KeyOf(up, "A"), KeyOf(up, "Eins"));

        Assert.Equal(
            "type:: book\n\n" +
            "- Vorspann\n" +
            "- # Eins\n" +
            "\t- ## B\n" +
            "- # A\n" +
            "\t- Text A\n" +
            "\t- ## A-i\n" +
            "\t- ## A-ii\n" +
            "- # Zwei\n" +
            "\t- ## C\n" +
            "\t\t- Text C\n",
            Serialize(up));
    }

    [Fact]
    public void MoveSectionAfter_AnchorInsideTheMovedSection_Refused()
    {
        var book = Load(Anchors);

        Assert.Throws<ArgumentException>(() => OutlineEditor.MoveSectionAfter(book, KeyOf(book, "A"), KeyOf(book, "A")));
        Assert.Throws<ArgumentException>(() => OutlineEditor.MoveSectionAfter(book, KeyOf(book, "A"), KeyOf(book, "A-i")));
        Assert.Throws<ArgumentException>(() => OutlineEditor.MoveSectionAfter(book, KeyOf(book, "A"), KeyOf(book, "A-ii")));
        Assert.Throws<ArgumentException>(() => OutlineEditor.MoveSectionAfter(book, KeyOf(book, "Eins"), KeyOf(book, "B")));

        Assert.False(book.Page.IsDirty);
        Assert.Equal(Anchors, Serialize(book));
    }

    [Fact]
    public void MoveSectionAfter_AnchorIsThePreviousSibling_ChangesNothing()
    {
        // B follows A's whole section (with its tail heading A-ii); Zwei follows Eins.
        var book = Load(Anchors);

        OutlineEditor.MoveSectionAfter(book, KeyOf(book, "B"), KeyOf(book, "A"));
        OutlineEditor.MoveSectionAfter(book, KeyOf(book, "Zwei"), KeyOf(book, "Eins"));

        Assert.False(book.Page.IsDirty);
        Assert.Equal(Anchors, Serialize(book));
    }
}
