using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Tests.Books;

/// <summary>The level buttons (cross references spec 4.2): text block or sub-point to heading, heading to another level.</summary>
public class LevelConversionTests
{
    private const string Old = "2026-01-02T03:04";
    private const string Stamp = "2026-10-19T14:32";
    private const string Id = "11111111-1111-1111-1111-111111111111";
    private const string Note = "22222222-2222-2222-2222-222222222222";

    private const string Text =
        "type:: book\n" +
        "\n" +
        "- Vorspann\n" +
        "- Vorspann zwei\n" +
        "- # Eins\n" +
        "\t- Text eins\n" +
        "\t- Titel A\n" +
        $"\t  id:: {Id}\n" +
        $"\t  source:: (({Note}))\n" +
        "\t  status:: relevant\n" +
        $"\t  created:: {Old}\n" +
        $"\t  updated:: {Old}\n" +
        "\t  Weiterer Text\n" +
        "\t\t- Punkt 1\n" +
        "\t\t\t- Punkt 1a\n" +
        "\t\t- Punkt 2\n" +
        "\t- Text danach\n" +
        "\t- ## Eins-A\n" +
        "\t\t- Text A\n" +
        "\t\t- ### Eins-A-i\n" +
        "\t\t\t- tief\n" +
        "- # Zwei\n";

    private const string LevelSix =
        "type:: book\n\n- # 1\n\t- ## 2\n\t\t- ### 3\n\t\t\t- #### 4\n\t\t\t\t- ##### 5\n\t\t\t\t\t- ###### 6\n";

    // A fixed local time with the local offset, so the tests hold in any time zone.
    private static readonly DateTimeOffset Now = LocalNow();

    private static DateTimeOffset LocalNow()
    {
        var local = new DateTime(2026, 10, 19, 14, 32, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static Book Load(string text) =>
        Book.Load(LogseqParser.Parse("Buch - Test.md", Encoding.UTF8.GetBytes(text)));

    private static string Serialize(Book book) => Encoding.UTF8.GetString(PageSerializer.Serialize(book.Page));

    private static Guid KeyOf(Book book, string content) => book.Page.AllBlocks().Single(b => b.Content == content).Key;

    private static BookElement ElementOf(Book book, string content) => BookElements.Find(book, KeyOf(book, content))!;

    /// <summary>The dates a new text block gets, with the indentation of its property lines.</summary>
    private static string Dates(string indent) => $"{indent}created:: {Stamp}\n{indent}updated:: {Stamp}\n";

    /// <summary>The properties of "Titel A" with <c>updated::</c> stamped, at <paramref name="indent"/>.</summary>
    private static string TitleAProperties(string indent) =>
        $"{indent}id:: {Id}\n{indent}source:: (({Note}))\n{indent}status:: relevant\n" +
        $"{indent}created:: {Old}\n{indent}updated:: {Stamp}\n";

    [Fact]
    public void ToHeading_TextBlock_FurtherTextSubPointsAndFollowingTextBlocks()
    {
        var book = Load(Text);

        var key = ManuscriptEditor.ToHeading(book, KeyOf(book, "Titel A\nWeiterer Text"), 2, Now);

        // The first line is the title; the further text keeps id::, source:: and the status (the block's own lines);
        // each sub-point is a text block with its deeper sub-points; "Text danach" follows into the new section,
        // the sub-section "## Eins-A" (not deeper than H2) stays in "# Eins".
        Assert.Equal(
            "type:: book\n\n- Vorspann\n- Vorspann zwei\n- # Eins\n" +
            "\t- Text eins\n" +
            "\t- ## Titel A\n" +
            "\t\t- Weiterer Text\n" + TitleAProperties("\t\t  ") +
            "\t\t- Punkt 1\n" + Dates("\t\t  ") +
            "\t\t\t- Punkt 1a\n" +
            "\t\t- Punkt 2\n" + Dates("\t\t  ") +
            "\t\t- Text danach\n" +
            "\t- ## Eins-A\n\t\t- Text A\n\t\t- ### Eins-A-i\n\t\t\t- tief\n" +
            "- # Zwei\n",
            Serialize(book));
        var reloaded = Book.Load(book.Page);
        var heading = reloaded.FindNode(key)!;
        Assert.Equal((2, "Titel A"), (heading.Level, heading.Title));
        Assert.Empty(reloaded.Warnings);

        // The first text block under the heading is now the target of the id:: (cross references stay valid).
        var first = reloaded.FindTextBlockById(Guid.Parse(Id))!;
        Assert.Equal(key, first.Section.Key);
        Assert.Equal("Weiterer Text", first.Block.Content);
        Assert.Equal([Guid.Parse(Note)], first.Sources);
        Assert.Equal(BlockStatus.Relevant, first.Status);
    }

    [Fact]
    public void ToHeading_SameLevelAsSection_TakesTheRestOfTheSection()
    {
        var book = Load(Text);

        ManuscriptEditor.ToHeading(book, KeyOf(book, "Text danach"), 1, Now);

        // An H1 in "# Eins" ends it: the new H1 follows it and takes the sub-section after the block.
        Assert.Equal(
            Text.Replace(
                "\t- Text danach\n\t- ## Eins-A\n\t\t- Text A\n\t\t- ### Eins-A-i\n\t\t\t- tief\n",
                "- # Text danach\n\t- ## Eins-A\n\t\t- Text A\n\t\t- ### Eins-A-i\n\t\t\t- tief\n"),
            Serialize(book));
        Assert.Empty(Book.Load(book.Page).Warnings);
    }

    [Fact]
    public void ToHeading_SubPoint_HeadingAfterItsTextBlock()
    {
        var book = Load(Text);

        var key = ManuscriptEditor.ToHeading(book, KeyOf(book, "Punkt 1"), 2, Now);

        // Only the sub-point with its sub-points goes; its text block is changed and keeps "Punkt 2".
        Assert.Equal(
            Text
                .Replace($"\t  updated:: {Old}\n", $"\t  updated:: {Stamp}\n")
                .Replace(
                    "\t\t- Punkt 1\n\t\t\t- Punkt 1a\n\t\t- Punkt 2\n\t- Text danach\n",
                    "\t\t- Punkt 2\n\t- ## Punkt 1\n\t\t- Punkt 1a\n" + Dates("\t\t  ") + "\t\t- Text danach\n"),
            Serialize(book));
        Assert.Equal(KeyOf(book, "# Eins"), Book.Load(book.Page).FindNode(key)!.Parent!.Key);
    }

    [Fact]
    public void ToHeading_SubPointWithIdAndStatus_KeepsThemOnItsFurtherText()
    {
        const string text =
            "type:: book\n\n- # Eins\n\t- Text\n\t\t- Punkt\n\t\t  id:: " + Id + "\n\t\t  status:: fertig\n\t\t  mehr\n";
        var book = Load(text);

        ManuscriptEditor.ToHeading(book, KeyOf(book, "Punkt\nmehr"), 2, Now);

        Assert.Equal(
            "type:: book\n\n- # Eins\n\t- Text\n" + $"\t  updated:: {Stamp}\n" +
            "\t- ## Punkt\n\t\t- mehr\n\t\t  id:: " + Id + "\n\t\t  status:: fertig\n" + Dates("\t\t  "),
            Serialize(book));
    }

    [Fact]
    public void ToHeading_OneLineWithId_IdGoesToTheFirstSubPoint()
    {
        const string text = "type:: book\n\n- Kapitel\n  id:: " + Id + "\n\t- Punkt\n\t\t- tiefer\n";
        var book = Load(text);

        ManuscriptEditor.ToHeading(book, KeyOf(book, "Kapitel"), 1, Now);

        Assert.Equal(
            "type:: book\n\n- # Kapitel\n\t- Punkt\n\t  id:: " + Id + "\n" + Dates("\t  ") + "\t\t- tiefer\n",
            Serialize(book));
    }

    [Fact]
    public void ToHeading_OneLineWithId_SubPointWithOwnId_EmptyTextBlockCarriesIt()
    {
        const string other = "33333333-3333-3333-3333-333333333333";
        const string text = "type:: book\n\n- Kapitel\n  id:: " + Id + "\n\t- Punkt\n\t  id:: " + other + "\n";
        var book = Load(text);

        ManuscriptEditor.ToHeading(book, KeyOf(book, "Kapitel"), 1, Now);

        Assert.Equal(
            "type:: book\n\n- # Kapitel\n\t-\n\t  id:: " + Id + "\n" + Dates("\t  ") +
            "\t- Punkt\n\t  id:: " + other + "\n" + Dates("\t  "),
            Serialize(book));
    }

    [Fact]
    public void ToHeading_OtherProperty_EmptyTextBlockKeepsItAndCreated()
    {
        const string text = $"type:: book\n\n- Kapitel\n  tags:: wichtig\n  created:: {Old}\n  updated:: {Old}\n";
        var book = Load(text);

        ManuscriptEditor.ToHeading(book, KeyOf(book, "Kapitel"), 2, Now);

        // Any property but the dates needs a text block; the original created:: stays with it.
        Assert.Equal(
            $"type:: book\n\n- ## Kapitel\n\t-\n\t  tags:: wichtig\n\t  created:: {Old}\n\t  updated:: {Stamp}\n",
            Serialize(book));
    }

    [Fact]
    public void ToHeading_OtherPropertyAndCreated_GoToTheFirstSubPoint()
    {
        const string text =
            $"type:: book\n\n- Kapitel\n  tags:: wichtig\n  created:: {Old}\n  updated:: {Old}\n\t- Punkt\n\t  collapsed:: true\n\t\t- tiefer\n";
        var book = Load(text);

        ManuscriptEditor.ToHeading(book, KeyOf(book, "Kapitel"), 1, Now);

        Assert.Equal(
            "type:: book\n\n- # Kapitel\n\t- Punkt\n\t  collapsed:: true\n\t  tags:: wichtig\n" +
            $"\t  created:: {Old}\n\t  updated:: {Stamp}\n\t\t- tiefer\n",
            Serialize(book));
    }

    [Fact]
    public void ToHeading_OnlyDates_NoTextBlockLeft()
    {
        const string text = $"type:: book\n\n- Kapitel\n  created:: {Old}\n  updated:: {Old}\n";
        var book = Load(text);

        ManuscriptEditor.ToHeading(book, KeyOf(book, "Kapitel"), 2, Now);

        // The dates belong to the text, which is the title now; a heading has none.
        Assert.Equal("type:: book\n\n- ## Kapitel\n", Serialize(book));
    }

    [Fact]
    public void ToHeading_NothingToCarry_NoTextBlockLeft()
    {
        const string text = "type:: book\n\n- Vorspann\n- Mehr\n- # Eins\n";
        var book = Load(text);

        ManuscriptEditor.ToHeading(book, KeyOf(book, "Vorspann"), 3, Now);

        // In the prologue every level is possible; the new H3 takes the prologue's following text block.
        Assert.Equal("type:: book\n\n- ### Vorspann\n\t- Mehr\n- # Eins\n", Serialize(book));
    }

    [Fact]
    public void ToHeading_NoteTagAndEscapedTitle_StatusRemarkCarried()
    {
        const string text = "type:: book\n\n- \\# Gedanke #notiz\n";
        var book = Load(text);

        ManuscriptEditor.ToHeading(book, KeyOf(book, "\\# Gedanke #notiz"), 2, Now);

        // The title is the shown text without heading marks; the remark status goes to an empty text block.
        Assert.Equal("type:: book\n\n- ## Gedanke\n\t-\n\t  status:: bemerkung\n" + Dates("\t  "), Serialize(book));
    }

    [Fact]
    public void ToHeading_Blocked_ThrowsAndChangesNothing()
    {
        var book = Load(Text);

        Assert.Throws<ArgumentException>(() => ManuscriptEditor.ToHeading(book, KeyOf(book, "Text A"), 1, Now));
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.ToHeading(book, KeyOf(book, "Text A"), 7, Now));
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.ToHeading(book, KeyOf(book, "## Eins-A"), 3, Now));

        Assert.Equal(Text, Serialize(book));
    }

    [Fact]
    public void SetHeadingLevel_ShiftsSubHeadings()
    {
        var book = Load(Text);

        ManuscriptEditor.SetHeadingLevel(book, KeyOf(book, "## Eins-A"), 4);

        Assert.Equal(Text.Replace("\t- ## Eins-A\n", "\t- #### Eins-A\n").Replace("### Eins-A-i", "##### Eins-A-i"), Serialize(book));

        book = Load(Text);
        ManuscriptEditor.SetHeadingLevel(book, KeyOf(book, "# Eins"), 2);
        Assert.Equal(
            Text.Replace("- # Eins\n", "- ## Eins\n").Replace("\t- ## Eins-A\n", "\t- ### Eins-A\n").Replace("\t- ### Eins-A-i\n", "\t- #### Eins-A-i\n"),
            Serialize(book));
    }

    [Fact]
    public void SetHeadingLevel_Blocked_ThrowsAndChangesNothing()
    {
        var book = Load(LevelSix);

        Assert.Throws<ArgumentException>(() => ManuscriptEditor.SetHeadingLevel(book, KeyOf(book, "##### 5"), 6));
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.SetHeadingLevel(book, KeyOf(book, "##### 5"), 3));

        Assert.Equal(LevelSix, Serialize(book));
    }

    [Fact]
    public void SetHeadingLevel_IrregularOutline_NoSubHeadingAboveH1()
    {
        // A warned outline: the sub-heading is not deeper than its parent.
        const string text = "type:: book\n\n- ## A\n\t- # B\n";
        var book = Load(text);
        Assert.NotEmpty(book.Warnings);
        var a = ElementOf(book, "## A");

        Assert.Equal("LevelBlockedAboveH1", ManuscriptEditor.LevelBlocked(book, a, 1));
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.SetHeadingLevel(book, a.Key, 1));
        Assert.Equal(text, Serialize(book));

        Assert.Null(ManuscriptEditor.LevelBlocked(book, a, 3));
        ManuscriptEditor.SetHeadingLevel(book, a.Key, 3);
        Assert.Equal("type:: book\n\n- ### A\n\t- ## B\n", Serialize(book));
    }

    [Fact]
    public void LevelBlocked_TextBlocksAndSubPoints()
    {
        var book = Load(Text);
        var textA = ElementOf(book, "Text A");
        var detail = ElementOf(book, "Punkt 1a");

        // Text is always possible; a heading above the surrounding section would swallow sections not its own.
        Assert.Null(ManuscriptEditor.LevelBlocked(book, textA, 0));
        Assert.Equal("LevelBlockedAboveSection", ManuscriptEditor.LevelBlocked(book, textA, 1));
        Assert.All([2, 3, 4, 5, 6], level => Assert.Null(ManuscriptEditor.LevelBlocked(book, textA, level)));
        Assert.Null(ManuscriptEditor.LevelBlocked(book, detail, 1));

        // There is no H7 (nor anything below Text).
        Assert.Equal("LevelBlockedNoSuchLevel", ManuscriptEditor.LevelBlocked(book, textA, 7));
        Assert.Equal("LevelBlockedNoSuchLevel", ManuscriptEditor.LevelBlocked(book, textA, -1));

        // The prologue allows every level.
        Assert.All([1, 2, 3, 4, 5, 6], level => Assert.Null(ManuscriptEditor.LevelBlocked(book, ElementOf(book, "Vorspann"), level)));
    }

    [Fact]
    public void LevelBlocked_Headings()
    {
        var book = Load(Text);
        var einsA = ElementOf(book, "## Eins-A");

        // Not above the parent heading's level + 1 (that is outdenting); Text is always possible.
        Assert.Equal("LevelBlockedOutOfParent", ManuscriptEditor.LevelBlocked(book, einsA, 1));
        Assert.All([0, 2, 3, 4, 5], level => Assert.Null(ManuscriptEditor.LevelBlocked(book, einsA, level)));

        // Review focus 4: a sub-heading at H6 blocks every deeper level, never producing H7.
        Assert.Equal("LevelBlockedBelowH6", ManuscriptEditor.LevelBlocked(book, einsA, 6));
        var six = Load(LevelSix);
        Assert.Equal("LevelBlockedBelowH6", ManuscriptEditor.LevelBlocked(six, ElementOf(six, "##### 5"), 6));
        Assert.All([1, 2, 3, 4], level =>
            Assert.Equal("LevelBlockedOutOfParent", ManuscriptEditor.LevelBlocked(six, ElementOf(six, "##### 5"), level)));
        Assert.Null(ManuscriptEditor.LevelBlocked(six, ElementOf(six, "###### 6"), 6));
        Assert.Equal("LevelBlockedNoSuchLevel", ManuscriptEditor.LevelBlocked(six, ElementOf(six, "###### 6"), 7));
        Assert.Null(ManuscriptEditor.LevelBlocked(six, ElementOf(six, "# 1"), 1));
        Assert.Equal("LevelBlockedBelowH6", ManuscriptEditor.LevelBlocked(six, ElementOf(six, "# 1"), 2));
    }
}
