using System.Text;
using System.Text.RegularExpressions;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Text;

namespace NoteEvolution.Core.Tests.Books;

/// <summary>A heading's level decides its place in the outline (Einordnen spec 2) and the command that places all warned ones (spec 3).</summary>
public partial class PlaceByLevelTests
{
    private const string Head = "type:: book\n\n";

    private const string Glossary =
        "- # Glossar\n" +
        "  anhang:: glossar\n" +
        "\t- Achtsamkeit\n" +
        "\t- # Fremd\n";

    private const string LevelSix =
        Head + "- # 1\n\t- ## 2\n\t\t- ### 3\n\t\t\t- #### 4\n\t\t\t\t- ##### 5\n\t\t\t\t\t- ###### 6\n";

    /// <summary>A regular outline with text blocks, details and sub-sections at several depths.</summary>
    private const string Outline =
        Head +
        "- Vorspann\n" +
        "- # Eins\n" +
        "\t- Text eins\n" +
        "\t\t- Punkt eins\n" +
        "\t- ## Eins-A\n" +
        "\t\t- Text A\n" +
        "\t\t- ### Eins-A-i\n" +
        "\t\t\t- Text i\n" +
        "\t\t\t- #### Eins-A-i-x\n" +
        "\t\t- Text nach i\n" +
        "\t\t- ### Eins-A-ii\n" +
        "\t- Text nach A\n" +
        "\t- ## Eins-B\n" +
        "\t\t- Text B\n" +
        "- # Zwei\n" +
        "\t- Text zwei\n" +
        "\t- ## Zwei-A\n";

    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 30, 0, TimeSpan.Zero);

    [GeneratedRegex("^#{1,6} ")]
    private static partial Regex Marks();

    private static Book Load(string text) =>
        Book.Load(LogseqParser.Parse("Buch - Test.md", Encoding.UTF8.GetBytes(text)));

    private static string Serialize(Book book) => Encoding.UTF8.GetString(PageSerializer.Serialize(book.Page));

    private static Guid KeyOf(Book book, string content) => book.Page.AllBlocks().Single(b => b.Content == content).Key;

    /// <summary>
    /// The titles and texts of every block in reading order (file order), as the reader sees them: without heading
    /// marks and line escapes, so a heading that changed its level or a sub-point that became a heading reads the same.
    /// </summary>
    private static List<string> ReadingOrder(Book book) =>
        [.. book.Page.AllBlocks().Select(b => Marks().Replace(BlockTextEscape.Unescape(b.Content), ""))];

    /// <summary>Runs <paramref name="change"/> on a fresh book and asserts that the reading order stays as it was.</summary>
    private static Book Changed(string text, Action<Book> change)
    {
        var book = Load(text);
        var before = ReadingOrder(book);
        change(book);
        var after = Book.Load(book.Page);
        Assert.Equal(before, ReadingOrder(after));
        return after;
    }

    [Fact]
    public void SameLevel_UnderSameLevelParent_MovesBesideIt_TakesFollowing()
    {
        const string text = Head + "- ## A\n\t- Text a\n\t- ## B\n\t\t- Text b\n\t- Text danach\n";
        Assert.Single(Load(text).Warnings);

        var book = Changed(text, b => ManuscriptEditor.SetHeadingLevel(b, KeyOf(b, "## B"), 2));

        Assert.Equal(Head + "- ## A\n\t- Text a\n- ## B\n\t- Text b\n\t- Text danach\n", Serialize(book));
        Assert.Equal(["A", "B"], book.Root.Children.Select(n => n.Title));
        Assert.Empty(book.Warnings);
    }

    [Fact]
    public void Outdent_TwoSteps_KeepsReadingOrder()
    {
        // Review focus 1: an H3 deep under H1 › H2 becomes H1; what followed it in H2 and in H1 is its own then.
        const string text =
            Head +
            "- # Eins\n" +
            "\t- Text eins\n" +
            "\t- ## Zwei\n" +
            "\t\t- Text zwei\n" +
            "\t\t- ### Drei\n" +
            "\t\t\t- Text drei\n" +
            "\t\t\t- #### Vier\n" +
            "\t\t- Text nach Drei\n" +
            "\t\t- ### Drei-B\n" +
            "\t- Text nach Zwei\n" +
            "\t- ## Zwei-B\n" +
            "- # Ende\n";

        var book = Changed(text, b => ManuscriptEditor.SetHeadingLevel(b, KeyOf(b, "### Drei"), 1));

        Assert.Equal(
            Head +
            "- # Eins\n" +
            "\t- Text eins\n" +
            "\t- ## Zwei\n" +
            "\t\t- Text zwei\n" +
            "- # Drei\n" +
            "\t- Text drei\n" +
            "\t- ## Vier\n" +
            "\t- Text nach Drei\n" +
            "\t- ### Drei-B\n" +
            "\t- Text nach Zwei\n" +
            "\t- ## Zwei-B\n" +
            "- # Ende\n",
            Serialize(book));
        Assert.Equal(["Eins", "Drei", "Ende"], book.Root.Children.Select(n => n.Title));
        Assert.Empty(book.Warnings);
    }

    [Fact]
    public void Indent_PreviousSiblingLowerLevel_BecomesItsLastChild()
    {
        const string text =
            Head +
            "- # Eins\n" +
            "\t- Text eins\n" +
            "\t- ## A\n" +
            "\t\t- Text a\n" +
            "\t\t- ### A-i\n" +
            "\t- ## B\n" +
            "\t\t- Text b\n";

        // One step: B goes into A, after A-i (which is not above H3).
        var book = Changed(text, b => ManuscriptEditor.SetHeadingLevel(b, KeyOf(b, "## B"), 3));
        Assert.Equal(
            Head + "- # Eins\n\t- Text eins\n\t- ## A\n\t\t- Text a\n\t\t- ### A-i\n\t\t- ### B\n\t\t\t- Text b\n",
            Serialize(book));
        Assert.Empty(book.Warnings);

        // Two steps: into A, then into A-i.
        book = Changed(text, b => ManuscriptEditor.SetHeadingLevel(b, KeyOf(b, "## B"), 4));
        Assert.Equal(
            Head + "- # Eins\n\t- Text eins\n\t- ## A\n\t\t- Text a\n\t\t- ### A-i\n\t\t\t- #### B\n\t\t\t\t- Text b\n",
            Serialize(book));
        Assert.Empty(book.Warnings);

        // A text block directly before it: the heading stays where it is.
        const string afterText = Head + "- # Eins\n\t- ## A\n\t- Text\n\t- ## B\n";
        book = Changed(afterText, b => ManuscriptEditor.SetHeadingLevel(b, KeyOf(b, "## B"), 3));
        Assert.Equal(afterText.Replace("## B", "### B"), Serialize(book));
    }

    [Fact]
    public void Outdent_CommandTakesFollowing()
    {
        // Shift+Tab: one level higher, and what followed in the section comes along.
        const string text =
            Head + "- # Eins\n\t- Text eins\n\t- ## A\n\t\t- Text a\n\t\t- ### A-i\n\t- Text nach A\n\t- ## B\n- # Zwei\n";
        var original = Load(text);
        Assert.True(ManuscriptEditor.CanOutdent(original, KeyOf(original, "## A")));

        var book = Changed(text, b => ManuscriptEditor.Outdent(b, KeyOf(b, "## A")));

        Assert.Equal(
            Head + "- # Eins\n\t- Text eins\n- # A\n\t- Text a\n\t- ## A-i\n\t- Text nach A\n\t- ## B\n- # Zwei\n",
            Serialize(book));
        Assert.Equal(["Eins", "A", "Zwei"], book.Root.Children.Select(n => n.Title));
        Assert.Empty(book.Warnings);

        // A level-1 heading cannot go higher.
        Assert.False(ManuscriptEditor.CanOutdent(book, KeyOf(book, "# A")));
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.Outdent(book, KeyOf(book, "# A")));

        // A level-2 heading at the top (no parent heading) becomes H1 in its place.
        const string top = Head + "- ## Oben\n\t- Text\n";
        book = Changed(top, b => ManuscriptEditor.Outdent(b, KeyOf(b, "## Oben")));
        Assert.Equal(Head + "- # Oben\n\t- Text\n", Serialize(book));
    }

    [Fact]
    public void LevelBlocked_NoOutOfParent_ButH1H6Bounds()
    {
        var six = Load(LevelSix);
        var five = new BookElement(ElementKind.Heading, KeyOf(six, "##### 5"));

        // Above the parent heading is outdenting now: every level up to H1 is possible.
        Assert.All([0, 1, 2, 3, 4, 5], level => Assert.Null(ManuscriptEditor.LevelBlocked(six, five, level)));
        Assert.Equal("LevelBlockedBelowH6", ManuscriptEditor.LevelBlocked(six, five, 6));
        Assert.Equal("LevelBlockedNoSuchLevel", ManuscriptEditor.LevelBlocked(six, five, 7));

        // A sub-heading of an irregular outline would rise above H1.
        var irregular = Load(Head + "- # Eins\n\t- ## A\n\t\t- # B\n");
        var a = new BookElement(ElementKind.Heading, KeyOf(irregular, "## A"));
        Assert.Equal("LevelBlockedAboveH1", ManuscriptEditor.LevelBlocked(irregular, a, 1));
        Assert.False(ManuscriptEditor.CanOutdent(irregular, a.Key));
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.Outdent(irregular, a.Key));
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.SetHeadingLevel(irregular, a.Key, 1));
        Assert.Equal(Head + "- # Eins\n\t- ## A\n\t\t- # B\n", Serialize(irregular));

        // H5 to H1 in one step: out of four parent sections, the H6 below it becomes H2.
        var book = Changed(LevelSix, b => ManuscriptEditor.SetHeadingLevel(b, KeyOf(b, "##### 5"), 1));
        Assert.Equal(Head + "- # 1\n\t- ## 2\n\t\t- ### 3\n\t\t\t- #### 4\n- # 5\n\t- ## 6\n", Serialize(book));
        Assert.Empty(book.Warnings);
    }

    [Fact]
    public void Appendix_Untouched()
    {
        // A heading after the appendix is not indented into it; one before it is not outdented behind it.
        const string text = Head + "- # Eins\n\t- ## A\n\t- Text nach A\n" + Glossary + "- ## Drei\n";
        var book = Changed(text, b => ManuscriptEditor.SetHeadingLevel(b, KeyOf(b, "## Drei"), 2));
        Assert.Equal(text, Serialize(book));

        book = Changed(text, b => ManuscriptEditor.SetHeadingLevel(b, KeyOf(b, "## A"), 1));
        Assert.Equal(Head + "- # Eins\n- # A\n\t- Text nach A\n" + Glossary + "- ## Drei\n", Serialize(book));

        // The appendix's own heading is refused, and its warned sub-heading is not placed.
        var glossary = Load(text);
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.SetHeadingLevel(glossary, KeyOf(glossary, "# Glossar"), 1));
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.SetHeadingLevel(glossary, KeyOf(glossary, "# Fremd"), 1));
        Assert.Single(glossary.Warnings);
        Assert.Empty(ManuscriptEditor.MisplacedHeadings(glossary));
        Assert.Equal(0, ManuscriptEditor.NormalizeOutline(glossary, Now));
        Assert.Equal(text, Serialize(glossary));
    }

    [Fact]
    public void Normalize_FixesAllWarnings_OneCallReturnsCount()
    {
        const string text =
            Head +
            "- # Eins\n" +
            "\t- Text eins\n" +
            "\t- # Zwei\n" +
            "\t\t- Text zwei\n" +
            "\t\t- ## Zwei-A\n" +
            "\t\t\t- Text A\n" +
            "\t\t\t\t- # Hoch\n" +
            "\t- Text nach\n" +
            "\t\t- Unterpunkt\n" +
            "\t\t\t- ## Drinnen\n" +
            "\t\t\t\t- Punkt x\n" +
            "\t\t\t\t\t- Punkt y\n" +
            Glossary;
        var book = Load(text);
        var snapshot = PageStructureSnapshot.Capture(book.Page);
        Assert.Equal(4, book.Warnings.Count);
        Assert.Equal(
            [KeyOf(book, "# Zwei"), KeyOf(book, "# Hoch"), KeyOf(book, "## Drinnen")],
            ManuscriptEditor.MisplacedHeadings(book));

        var before = ReadingOrder(book);
        Assert.Equal(3, ManuscriptEditor.NormalizeOutline(book, Now));

        var after = Book.Load(book.Page);
        Assert.Equal(before, ReadingOrder(after));
        Assert.Equal([KeyOf(after, "# Fremd")], after.Warnings.Select(w => w.BlockKey));
        Assert.Empty(ManuscriptEditor.MisplacedHeadings(after));
        Assert.EndsWith(Glossary, Serialize(after));

        // "Zwei" beside "Eins" with what followed it; "Hoch" (H1 is above its H2 section) an H3 in "Zwei-A";
        // review focus 2: "Drinnen" under a sub-point is an H2 after its text block, its sub-points text blocks under it.
        var zwei = after.Root.Children.Single(n => n.Title == "Zwei");
        Assert.Equal(1, zwei.Level);
        Assert.Equal(["Eins", "Zwei", "Glossar"], after.Root.Children.Select(n => n.Title));
        var hoch = after.Root.Children.SelectMany(Descendants).Single(n => n.Title == "Hoch");
        Assert.Equal((3, "Zwei-A"), (hoch.Level, hoch.Parent!.Title));
        var drinnen = after.Root.Children.SelectMany(Descendants).Single(n => n.Title == "Drinnen");
        Assert.Equal((2, "Zwei"), (drinnen.Level, drinnen.Parent!.Title));
        var punkt = drinnen.TextBlocks.Single();
        Assert.Equal("Punkt x", punkt.Text);
        Assert.Equal(["Punkt y"], punkt.Paragraphs.Select(p => p.Text));
        Assert.Equal(
            ["Text zwei", "Zwei-A", "Text nach", "Drinnen"],
            zwei.Items.Select(item => item is TextBlock t ? t.Text : ((OutlineNode)item).Title));

        // One step undoes the whole command.
        snapshot.RestoreInto(book.Page);
        Assert.Equal(text, Serialize(book));
    }

    public static TheoryData<string, int> EveryHeadingAndLevel()
    {
        var data = new TheoryData<string, int>();
        var book = Load(Outline);
        foreach (var node in book.Root.Children.SelectMany(Descendants))
        {
            foreach (var level in Enumerable.Range(1, OutlineEditor.MaxLevel))
            {
                if (ManuscriptEditor.LevelBlocked(book, new BookElement(ElementKind.Heading, node.Key), level) is null)
                {
                    data.Add(node.Block!.Content, level);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryHeadingAndLevel))]
    public void ReadingOrder_Unchanged(string heading, int level)
    {
        // Every level every heading may take keeps the reading order, and the outline stays without warnings.
        var key = Guid.Empty;
        var book = Changed(Outline, b =>
        {
            key = KeyOf(b, heading);
            ManuscriptEditor.SetHeadingLevel(b, key, level);
        });

        Assert.Equal(level, book.FindNode(key)!.Level);
        Assert.Empty(book.Warnings);
    }

    private static IEnumerable<OutlineNode> Descendants(OutlineNode node) => node.Children.SelectMany(Descendants).Prepend(node);
}
