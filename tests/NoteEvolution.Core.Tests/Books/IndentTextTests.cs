using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Links;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Tests.Links;

namespace NoteEvolution.Core.Tests.Books;

/// <summary>A text block becomes a sub-point of the one before and a level-1 sub-point its own text block (Einordnen spec 4).</summary>
public class IndentTextTests
{
    private const string Head = "title:: Buch: Test\ntype:: book\n\n";
    private const string Note1 = "0190a000-0000-7000-8000-000000000001";
    private const string Block1 = "0190b000-0000-7000-8000-000000000001";
    private const string Block2 = "0190b000-0000-7000-8000-000000000002";

    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 30, 0, TimeSpan.Zero);

    private static Book Load(string text) =>
        Book.Load(LogseqParser.Parse("Buch - Test.md", Encoding.UTF8.GetBytes(text)));

    private static string Serialize(Book book) => Encoding.UTF8.GetString(PageSerializer.Serialize(book.Page));

    private static Guid KeyOf(Book book, string content) => book.Page.AllBlocks().Single(b => b.Content == content).Key;

    private static BookElement Element(Book book, string content) =>
        BookElements.Find(book, KeyOf(book, content))!;

    private static string NoRefs(string content) => System.Text.RegularExpressions.Regex.Replace(content, @"\(\([0-9a-f-]{36}\)\)", "(())");

    private static List<string> Order(Book book) => [.. book.Page.AllBlocks().Select(b => b.Content)];

    [Fact]
    public void IndentTextBlock_UnderPrevious_KeepsItsDetails_AndIdAndSource()
    {
        var text = Head +
                   "- # Kapitel\n" +
                   "\t- Eins\n" +
                   "\t\t- Eins-a\n" +
                   "\t- Zwei\n" +
                   $"\t  id:: {Block1}\n" +
                   $"\t  source:: (({Note1}))\n" +
                   "\t\t- Zwei-a\n" +
                   $"\t- Drei verweist auf (({Block1}))\n";
        var journal = "- Notiz\n" + $"  id:: {Note1}\n" + $"  used-in:: [[Buch - Test]] (({Block1}))\n";
        using var s = new LinkSetup(text, journal);
        var loaded = s.Book;
        var order = Order(loaded);

        var key = ManuscriptEditor.IndentTextBlock(loaded, KeyOf(loaded, "Zwei"), Now);

        var after = Book.Load(loaded.Page);
        Assert.Equal(KeyOf(loaded, "Zwei"), key);

        // The reference to the moved block now names the receiving one; the texts are otherwise as they were.
        Assert.Equal(order.Select(NoRefs), Order(after).Select(NoRefs));
        Assert.Equal(new BookElement(ElementKind.Detail, key), BookElements.Find(after, key));
        var eins = Assert.Single(after.Root.AllTextBlocks(), t => t.Text == "Eins");
        Assert.Equal(["Eins-a", "Zwei", "Zwei-a"], eins.Paragraphs.Select(p => p.Text));
        Assert.Equal([1, 1, 2], eins.Paragraphs.Select(p => p.Depth));
        var moved = after.Page.AllBlocks().Single(b => b.Key == key);
        Assert.Equal(Guid.Parse(Block1), moved.Id);
        Assert.Equal($"(({Note1}))", moved.GetProperty("source"));

        // The note's usage still names a block of the book, and nothing is missing or broken for the link check.
        var report = new LinkChecker(s.Vault, s.Links, s.Writer).Analyze();
        Assert.Empty(report.Missing);
        Assert.Empty(report.Orphans);
        Assert.Empty(report.Broken);
    }

    [Fact]
    public void IndentTextBlock_LinkedBlock_CrossReferenceRetargetedToReceivingBlock()
    {
        var loaded = Load(Head + "- # Kapitel\n\t- Eins\n\t- Zwei\n" + $"\t  id:: {Block1}\n" + $"\t- Drei (({Block1}))\n");
        Assert.Empty(CrossRefIndex.Of(loaded).Missing);

        ManuscriptEditor.IndentTextBlock(loaded, KeyOf(loaded, "Zwei"), Now);

        var after = Book.Load(loaded.Page);
        var eins = Assert.Single(after.Root.AllTextBlocks(), t => t.Text == "Eins");
        var newId = Assert.IsType<Guid>(eins.Block.Id);
        Assert.NotEqual(Guid.Parse(Block1), newId);
        var index = CrossRefIndex.Of(after);
        Assert.Empty(index.Missing);
        Assert.Equal(eins.Key, index.Target(newId)!.TextBlock!.Key);
        Assert.Contains($"(({newId}))", loaded.Page.AllBlocks().Single(b => b.Content.StartsWith("Drei")).Content);

        // The detail keeps its own id (the notes' used-in entries name it).
        Assert.Equal(Guid.Parse(Block1), loaded.Page.AllBlocks().Single(b => b.Content == "Zwei").Id);
    }

    [Fact]
    public void IndentTextBlock_ReceivingBlockWithId_KeepsIt_UnreferencedBlockGetsNoId()
    {
        var loaded = Load(
            Head + "- # K\n\t- Eins\n" + $"\t  id:: {Block2}\n" + "\t- Zwei\n" + $"\t  id:: {Block1}\n" + $"\t- Drei (({Block1}))\n");
        ManuscriptEditor.IndentTextBlock(loaded, KeyOf(loaded, "Zwei"), Now);
        Assert.Equal(Guid.Parse(Block2), loaded.Page.AllBlocks().Single(b => b.Content == "Eins").Id);
        Assert.Contains($"(({Block2}))", loaded.Page.AllBlocks().Single(b => b.Content.StartsWith("Drei")).Content);

        var plain = Load(Head + "- # K\n\t- Eins\n\t- Zwei\n" + $"\t  id:: {Block1}\n");
        ManuscriptEditor.IndentTextBlock(plain, KeyOf(plain, "Zwei"), Now);
        Assert.Null(plain.Page.AllBlocks().Single(b => b.Content == "Eins").Id);
    }

    [Fact]
    public void IndentTextBlock_ReadOnlyPage_RefusedBeforeAnythingChanges()
    {
        var loaded = Load(Head + "- # K\n\t- Eins\n\t- Zwei\n\t  ```\n");
        Assert.True(loaded.Page.IsReadOnly);
        var key = loaded.Page.AllBlocks().Single(b => b.Content.StartsWith("Zwei")).Key;
        var before = loaded.Page.AllBlocks().Select(b => (b.Key, b.Parent?.Key, b.Content)).ToList();

        Assert.Throws<InvalidOperationException>(() => ManuscriptEditor.IndentTextBlock(loaded, key, Now));
        Assert.Equal(before, loaded.Page.AllBlocks().Select(b => (b.Key, b.Parent?.Key, b.Content)).ToList());
    }

    [Fact]
    public void IndentTextBlock_FirstInSection_Blocked()
    {
        var loaded = Load(Head + "- # A\n\t- Text a\n\t- ## B\n\t\t- Text b\n\t\t- ### C\n\t\t- Text c\n- Vorspann\n");

        Assert.Equal("IndentBlockedNoPreviousText", ManuscriptEditor.IndentBlocked(loaded, Element(loaded, "Text a")));
        Assert.Equal("IndentBlockedNoPreviousText", ManuscriptEditor.IndentBlocked(loaded, Element(loaded, "Text b")));

        // A heading between the two text blocks would change the reading order: blocked as well.
        Assert.Equal("IndentBlockedNoPreviousText", ManuscriptEditor.IndentBlocked(loaded, Element(loaded, "Text c")));
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.IndentTextBlock(loaded, KeyOf(loaded, "Text a"), Now));
        Assert.Equal("OutdentBlockedTextBlock", ManuscriptEditor.OutdentBlocked(loaded, Element(loaded, "Text a")));
    }

    [Fact]
    public void IndentBlocked_DetailWithoutDetailBefore_AndHeadings()
    {
        var loaded = Load(Head + "- # A\n\t- Text\n\t\t- Eins\n\t\t- Zwei\n\t\t\t- Tief\n\t- ## B\n- # C\n");

        Assert.Equal("IndentBlockedNoPreviousDetail", ManuscriptEditor.IndentBlocked(loaded, Element(loaded, "Eins")));
        Assert.Null(ManuscriptEditor.IndentBlocked(loaded, Element(loaded, "Zwei")));
        Assert.Equal("IndentBlockedNoPreviousDetail", ManuscriptEditor.IndentBlocked(loaded, Element(loaded, "Tief")));
        Assert.Null(ManuscriptEditor.OutdentBlocked(loaded, Element(loaded, "Tief")));
        Assert.Null(ManuscriptEditor.IndentBlocked(loaded, Element(loaded, "# C")));
        Assert.Equal("LevelBlockedAboveH1", ManuscriptEditor.OutdentBlocked(loaded, Element(loaded, "# C")));
        Assert.Equal("IndentBlockedNoPreviousHeading", ManuscriptEditor.IndentBlocked(loaded, Element(loaded, "# A")));
        Assert.Null(ManuscriptEditor.OutdentBlocked(loaded, Element(loaded, "## B")));
    }

    [Fact]
    public void OutdentDetail_BecomesTextBlockAfter_FollowingSiblingsComeAlong()
    {
        var loaded = Load(
            Head + "- # A\n\t- Text\n\t\t- Vor\n\t\t- Punkt\n\t\t\t- Tief\n\t\t- Nach 1\n\t\t\t- Nach 1 tief\n\t\t- Nach 2\n\t- Danach\n");
        var order = Order(loaded);

        var key = ManuscriptEditor.OutdentDetail(loaded, KeyOf(loaded, "Punkt"), Now);

        var after = Book.Load(loaded.Page);
        Assert.Equal(KeyOf(loaded, "Punkt"), key);
        Assert.Equal(order, Order(after));
        Assert.Equal(new BookElement(ElementKind.TextBlock, key), BookElements.Find(after, key));
        Assert.Equal(["Text", "Punkt", "Danach"], after.Root.AllTextBlocks().Select(t => t.Text));
        var text = Assert.Single(after.Root.AllTextBlocks(), t => t.Text == "Text");
        Assert.Equal(["Vor"], text.Paragraphs.Select(p => p.Text));
        var punkt = after.FindTextBlock(key)!;
        Assert.Equal(["Tief", "Nach 1", "Nach 1 tief", "Nach 2"], punkt.Paragraphs.Select(p => p.Text));
        Assert.Equal([1, 1, 2, 1], punkt.Paragraphs.Select(p => p.Depth));
    }

    [Fact]
    public void OutdentDetail_SourceStaysOnOldBlock()
    {
        var loaded = Load(
            Head + "- # A\n" +
            "\t- Text\n" +
            $"\t  id:: {Block1}\n" +
            $"\t  source:: (({Note1}))\n" +
            "\t\t- Eigen\n" +
            $"\t\t  id:: {Block2}\n" +
            $"\t\t  source:: (({Note1}))\n" +
            "\t\t- Ohne\n");
        var ohne = loaded.Page.AllBlocks().Single(b => b.Content == "Ohne");

        ManuscriptEditor.OutdentDetail(loaded, KeyOf(loaded, "Eigen"), Now);

        var after = Book.Load(loaded.Page);
        var old = Assert.Single(after.Root.AllTextBlocks(), t => t.Text == "Text");
        Assert.Equal($"(({Note1}))", old.Block.GetProperty("source"));
        Assert.Equal(Guid.Parse(Block1), old.Block.Id);
        Assert.Equal([Guid.Parse(Note1)], old.Sources);

        // A detail's own id and source go with it.
        var own = Assert.Single(after.Root.AllTextBlocks(), t => t.Text == "Eigen");
        Assert.Equal(Guid.Parse(Block2), own.Block.Id);
        Assert.Equal([Guid.Parse(Note1)], own.Sources);

        // A sub-point of the moved one has none of the old block's.
        Assert.Null(ohne.GetProperty("source"));
        Assert.Null(ohne.Id);
    }

    [Fact]
    public void OutdentDetail_NewBlockWithoutId_WhenNothingLinksIt()
    {
        var loaded = Load(Head + "- # A\n\t- Text\n" + $"\t  id:: {Block1}\n" + "\t\t- Punkt\n");

        var key = ManuscriptEditor.OutdentDetail(loaded, KeyOf(loaded, "Punkt"), Now);

        Assert.Null(loaded.Page.AllBlocks().Single(b => b.Key == key).Id);
    }

    [Fact]
    public void OutdentDetail_DeeperLevel_NotCore()
    {
        const string text = Head + "- # A\n\t- Text\n\t\t- Eins\n\t\t\t- Tief\n";
        var loaded = Load(text);

        Assert.Throws<ArgumentException>(() => ManuscriptEditor.OutdentDetail(loaded, KeyOf(loaded, "Tief"), Now));
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.OutdentDetail(loaded, KeyOf(loaded, "Text"), Now));
        Assert.Equal(text, Serialize(loaded));
    }

    [Fact]
    public void Dates_StampedAsSpecified()
    {
        const string created = "created:: 2026-01-01T08:00:00";
        var loaded = Load(
            Head + "- # A\n" +
            $"\t- Eins\n\t  {created}\n" +
            $"\t- Zwei\n\t  {created}\n\t  updated:: 2026-01-02T08:00:00\n" +
            "\t\t- Punkt\n" +
            "\t\t- Spaeter\n");

        ManuscriptEditor.IndentTextBlock(loaded, KeyOf(loaded, "Zwei"), Now);

        var eins = loaded.Page.AllBlocks().Single(b => b.Content == "Eins");
        Assert.Equal(Now.LocalDateTime, BlockDates.Updated(eins));
        Assert.Equal("2026-01-01T08:00:00", eins.GetProperty("created"));

        // The indented block keeps its dates; it is outdented again, then only updated:: changes.
        var again = Book.Load(loaded.Page);
        var later = Now.AddMinutes(5);
        ManuscriptEditor.OutdentDetail(again, KeyOf(again, "Zwei"), later);

        Assert.Equal(later.LocalDateTime, BlockDates.Updated(loaded.Page.AllBlocks().Single(b => b.Content == "Eins")));
        var zwei = loaded.Page.AllBlocks().Single(b => b.Content == "Zwei");
        Assert.Equal("2026-01-01T08:00:00", zwei.GetProperty("created"));
        Assert.Equal(later.LocalDateTime, BlockDates.Updated(zwei));

        // A detail without dates gets both.
        var plain = Load(Head + "- # A\n\t- Text\n\t\t- Punkt\n");
        var key = ManuscriptEditor.OutdentDetail(plain, KeyOf(plain, "Punkt"), Now);
        var made = plain.Page.AllBlocks().Single(b => b.Key == key);
        Assert.Equal(Now.LocalDateTime, BlockDates.Created(made));
        Assert.Equal(Now.LocalDateTime, BlockDates.Updated(made));
        Assert.Equal(Now.LocalDateTime, BlockDates.Updated(plain.Page.AllBlocks().Single(b => b.Content == "Text")));
    }

    [Fact]
    public void ReadingOrder_Unchanged()
    {
        var loaded = Load(
            Head + "- Vorspann\n- # A\n\t- Eins\n\t\t- E1\n\t- Zwei\n\t\t- Z1\n\t\t\t- Z11\n\t\t- Z2\n\t- ## B\n\t\t- Drei\n");
        var order = Order(loaded);

        ManuscriptEditor.IndentTextBlock(loaded, KeyOf(loaded, "Zwei"), Now);
        Assert.Equal(order, Order(Book.Load(loaded.Page)));

        ManuscriptEditor.OutdentDetail(Book.Load(loaded.Page), KeyOf(loaded, "E1"), Now);
        Assert.Equal(order, Order(Book.Load(loaded.Page)));

        ManuscriptEditor.OutdentDetail(Book.Load(loaded.Page), KeyOf(loaded, "Zwei"), Now);
        Assert.Equal(order, Order(Book.Load(loaded.Page)));
    }
}
