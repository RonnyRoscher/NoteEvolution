using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;

namespace NoteEvolution.Core.Tests.Books;

/// <summary>A detail (sub-point) moved with its deeper details to another place in the book (spec 5.3).</summary>
public class MoveDetailTests
{
    private const string Id = "6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70";
    private const string Note = "7f3a91c2-4b1d-4e8a-9c3f-1a2b3c4d5e6f";
    private const string DetailId = "6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f71";
    private const string Note2 = "7f3a91c2-4b1d-4e8a-9c3f-1a2b3c4d5e60";

    private static readonly DateTimeOffset Now = new(2026, 11, 15, 9, 30, 0, TimeSpan.Zero);

    private const string Start = "type:: book\n\n- Vorspann\n- # Eins\n";

    private const string EinsHead =
        "\t- Text eins\n" +
        $"\t  id:: {Id}\n" +
        $"\t  source:: (({Note}))\n" +
        "\t  updated:: 2026-03-01 10:00\n";

    private const string D1Tree = "\t\t- D1\n\t\t\t- D1a\n\t\t\t\t- D1a1\n";

    private const string D2Tree =
        "\t\t- D2\n" +
        $"\t\t  id:: {DetailId}\n" +
        $"\t\t  source:: (({Note2}))\n" +
        "\t\t\t- D2a\n";

    private const string D3 = "\t\t- D3\n";
    private const string Zwei = "\t- Text zwei\n";
    private const string Z1 = "\t\t- Z1\n";
    private const string Glossar = "- # Glossar\n  anhang:: glossar\n\t- Begriff\n\t\t- Erklaerung\n";
    private const string Tail = "- # Zwei\n\t- Text drei\n\t\t- T1\n\t- ## Zwei-A\n" + Glossar;

    private const string Text = Start + EinsHead + D1Tree + D2Tree + D3 + Zwei + Z1 + Tail;

    private static Book Load(string text = Text) =>
        Book.Load(LogseqParser.Parse("Buch - Test.md", Encoding.UTF8.GetBytes(text)));

    private static string Serialize(Book book) => Encoding.UTF8.GetString(PageSerializer.Serialize(book.Page));

    private static Guid KeyOf(Book book, string content) => book.Page.AllBlocks().Single(b => b.Content == content).Key;

    private static NoteEvolution.Core.Model.Block BlockOf(Book book, string content) => book.Page.AllBlocks().Single(b => b.Content == content);

    private static TextBlock TextBlockOf(Book book, string text) => Assert.Single(book.Root.AllTextBlocks(), t => t.Text == text);

    private static bool Move(Book book, string detail, Func<Book, DetailTarget> target) =>
        ManuscriptEditor.MoveDetail(book, KeyOf(book, detail), target(book), Now);

    [Fact]
    public void MoveDetail_OntoATextBlock_BecomesItsFirstSubPoint()
    {
        var book = Load();

        Assert.True(Move(book, "D3", b => new DetailTarget.FirstOf(KeyOf(b, "Text zwei"))));
        Assert.Equal(Start + EinsHead + D1Tree + D2Tree + Zwei + D3 + Z1 + Tail, Serialize(book));

        // Onto its own text block: a detail that is not the first sub-point moves to the front.
        var own = Load();
        Assert.True(Move(own, "D2", b => new DetailTarget.FirstOf(KeyOf(b, "Text eins"))));
        Assert.Equal(Start + EinsHead + D2Tree + D1Tree + D3 + Zwei + Z1 + Tail, Serialize(own));

        // A deeper one becomes a sub-point of level 1, with its own deeper one below it.
        var deeper = Load();
        Assert.True(Move(deeper, "D1a", b => new DetailTarget.FirstOf(KeyOf(b, "Text eins"))));
        Assert.Equal(
            Start + EinsHead + "\t\t- D1a\n\t\t\t- D1a1\n\t\t- D1\n" + D2Tree + D3 + Zwei + Z1 + Tail, Serialize(deeper));
    }

    [Fact]
    public void MoveDetail_AfterADetail_SameDepth_AfterItsDeeperOnes()
    {
        var book = Load();

        Assert.True(Move(book, "D3", b => new DetailTarget.After(KeyOf(b, "D1"))));
        Assert.Equal(Start + EinsHead + D1Tree + D3 + D2Tree + Zwei + Z1 + Tail, Serialize(book));

        // After a deeper detail: at its depth, after its own deeper detail.
        var deeper = Load();
        Assert.True(Move(deeper, "Z1", b => new DetailTarget.After(KeyOf(b, "D1a"))));
        Assert.Equal(Start + EinsHead + D1Tree + "\t\t\t- Z1\n" + D2Tree + D3 + Zwei + Tail, Serialize(deeper));
    }

    [Fact]
    public void MoveDetail_DeeperDetailsKeepTheirDistance()
    {
        var book = Load();

        Move(book, "D1a", b => new DetailTarget.After(KeyOf(b, "T1")));

        var after = Book.Load(book.Page);
        var drei = TextBlockOf(after, "Text drei");
        Assert.Equal(["T1", "D1a", "D1a1"], drei.Paragraphs.Select(p => p.Text));
        Assert.Equal([1, 1, 2], drei.Paragraphs.Select(p => p.Depth));

        // Deeper as well: D2 below D1 at the depth of D1a, its D2a one deeper.
        var down = Load();
        Move(down, "D2", b => new DetailTarget.After(KeyOf(b, "D1a")));

        var eins = TextBlockOf(Book.Load(down.Page), "Text eins");
        Assert.Equal(["D1", "D1a", "D1a1", "D2", "D2a", "D3"], eins.Paragraphs.Select(p => p.Text));
        Assert.Equal([1, 2, 3, 2, 3, 1], eins.Paragraphs.Select(p => p.Depth));
    }

    [Fact]
    public void MoveDetail_ToAnotherTextBlockInAnotherSection()
    {
        var book = Load();

        Assert.True(Move(book, "D2", b => new DetailTarget.FirstOf(KeyOf(b, "Text drei"))));

        Assert.Equal(
            Start + EinsHead + D1Tree + D3 + Zwei + Z1 +
            "- # Zwei\n\t- Text drei\n" + D2Tree + "\t\t- T1\n\t- ## Zwei-A\n" + Glossar,
            Serialize(book));
    }

    [Fact]
    public void MoveDetail_SectionStart_BecomesATextBlock_LikeOutdent()
    {
        var moved = Load();
        var outdented = Load();

        Assert.True(Move(moved, "D2", b => new DetailTarget.SectionStart(KeyOf(b, "# Zwei"))));
        ManuscriptEditor.OutdentDetail(outdented, KeyOf(outdented, "D2"), Now);

        var after = Book.Load(moved.Page);
        var zwei = after.FindNode(KeyOf(after, "# Zwei"))!;
        Assert.Equal(["D2", "Text drei"], zwei.Items.OfType<TextBlock>().Select(t => t.Text));
        Assert.Equal(["D2a"], TextBlockOf(after, "D2").Paragraphs.Select(p => p.Text));
        Assert.Equal([1], TextBlockOf(after, "D2").Paragraphs.Select(p => p.Depth));
        Assert.Equal(["D1", "D1a", "D1a1", "D3"], TextBlockOf(after, "Text eins").Paragraphs.Select(p => p.Text));

        // Dates and sources as "Ausrücken" gives them: the new text block keeps its own id and source and gets both
        // dates, the old one keeps its source and gets updated::.
        Assert.Equal(BlockOf(outdented, "D2").Properties, BlockOf(moved, "D2").Properties);
        Assert.Equal(BlockOf(outdented, "Text eins").Properties, BlockOf(moved, "Text eins").Properties);
        Assert.Equal(Now.LocalDateTime, BlockDates.Created(BlockOf(moved, "D2")));
        Assert.Equal(Now.LocalDateTime, BlockDates.Updated(BlockOf(moved, "Text eins")));
        Assert.Equal($"(({Note2}))", BlockOf(moved, "D2").GetProperty("source"));
        Assert.Equal(Guid.Parse(DetailId), BlockOf(moved, "D2").Id);
        Assert.Equal($"(({Note}))", BlockOf(moved, "Text eins").GetProperty("source"));
    }

    [Fact]
    public void MoveDetail_SectionEnd()
    {
        const string head = "title:: Buch: Test\ntype:: book\n\n";
        const string deep = "# Tief #notiz";
        const string text = head + "- Vorspann\n- # A\n\t- Text\n\t\t- Punkt\n\t\t\t- " + deep + "\n\t\t\t\t- Unten\n- # B\n\t- Text b\n\t- ## B1\n";
        var book = Load(text);

        // A deeper detail: a text block after the section's last one, its deeper detail its detail; a first line that
        // would read as a heading escaped, its #notiz kept.
        Assert.True(Move(book, deep, b => new DetailTarget.SectionEnd(KeyOf(b, "# B"))));

        var escaped = HeadingText.AsTextBlockContent(deep, shownUnescaped: true);
        Assert.NotEqual(deep, escaped);
        var after = Book.Load(book.Page);
        var b = after.FindNode(KeyOf(after, "# B"))!;
        var made = Assert.IsType<TextBlock>(b.Items[1]);
        Assert.Equal(escaped, made.Block.Content);
        Assert.Contains("\n\t- Text b\n\t- " + escaped + "\n", Serialize(book));
        Assert.Equal(["Unten"], made.Paragraphs.Select(p => p.Text));
        Assert.Equal([1], made.Paragraphs.Select(p => p.Depth));
        Assert.Equal(BlockStatus.Remark, BlockStatuses.Of(made.Block));
        Assert.Equal(Now.LocalDateTime, BlockDates.Created(made.Block));
        Assert.Equal(Now.LocalDateTime, BlockDates.Updated(made.Block));
        var old = TextBlockOf(after, "Text");
        Assert.Equal(["Punkt"], old.Paragraphs.Select(p => p.Text));
        Assert.Equal(Now.LocalDateTime, BlockDates.Updated(old.Block));

        // The prologue's end.
        var prologue = Load(text);
        Assert.True(Move(prologue, "Unten", p => new DetailTarget.SectionEnd(Guid.Empty)));
        Assert.StartsWith(head + "- Vorspann\n- Unten\n", Serialize(prologue));
    }

    [Fact]
    public void MoveDetail_LastDetailOfABlock_LeavesTheBlockClean()
    {
        var book = Load();

        Assert.True(Move(book, "Z1", b => new DetailTarget.After(KeyOf(b, "D3"))));

        Assert.Equal(Start + EinsHead + D1Tree + D2Tree + D3 + Z1 + Zwei + Tail, Serialize(book));
        Assert.Empty(TextBlockOf(Book.Load(book.Page), "Text zwei").Paragraphs);
    }

    [Fact]
    public void MoveDetail_SamePlace_ReturnsFalse_FileUnchanged()
    {
        var book = Load();

        Assert.False(Move(book, "D1", b => new DetailTarget.FirstOf(KeyOf(b, "Text eins"))));
        Assert.False(Move(book, "D2", b => new DetailTarget.After(KeyOf(b, "D1"))));
        Assert.False(Move(book, "D3", b => new DetailTarget.After(KeyOf(b, "D2"))));

        Assert.Equal(Text, Serialize(book));
        Assert.False(book.Page.IsDirty);
    }

    [Fact]
    public void MoveDetail_OntoItselfOrItsOwnDeeperDetail_Refused_FileUnchanged()
    {
        var book = Load();

        Assert.Throws<ArgumentException>(() => Move(book, "D1", b => new DetailTarget.After(KeyOf(b, "D1"))));
        Assert.Throws<ArgumentException>(() => Move(book, "D1", b => new DetailTarget.After(KeyOf(b, "D1a"))));
        Assert.Throws<ArgumentException>(() => Move(book, "D1", b => new DetailTarget.After(KeyOf(b, "D1a1"))));

        Assert.Equal(Text, Serialize(book));
        Assert.False(book.Page.IsDirty);
    }

    [Fact]
    public void MoveDetail_IntoTheAppendix_Refused()
    {
        var book = Load();

        Assert.Throws<ArgumentException>(() => Move(book, "Erklaerung", b => new DetailTarget.FirstOf(KeyOf(b, "Text eins"))));
        Assert.Throws<ArgumentException>(() => Move(book, "D1", b => new DetailTarget.FirstOf(KeyOf(b, "Begriff"))));
        Assert.Throws<ArgumentException>(() => Move(book, "D1", b => new DetailTarget.After(KeyOf(b, "Erklaerung"))));
        Assert.Throws<ArgumentException>(() => Move(book, "D1", b => new DetailTarget.SectionStart(KeyOf(b, "# Glossar"))));
        Assert.Throws<ArgumentException>(() => Move(book, "D1", b => new DetailTarget.SectionEnd(KeyOf(b, "# Glossar"))));

        Assert.Equal(Text, Serialize(book));
        Assert.False(book.Page.IsDirty);
    }

    [Fact]
    public void MoveDetail_UnknownDetailOrTarget_Refused()
    {
        var book = Load();

        // No detail: a text block, a heading, an unknown key.
        Assert.Throws<ArgumentException>(() => Move(book, "Text zwei", b => new DetailTarget.FirstOf(KeyOf(b, "Text eins"))));
        Assert.Throws<ArgumentException>(() => Move(book, "# Zwei", b => new DetailTarget.FirstOf(KeyOf(b, "Text eins"))));
        Assert.Throws<ArgumentException>(() => ManuscriptEditor.MoveDetail(book, Guid.NewGuid(), new DetailTarget.FirstOf(KeyOf(book, "Text eins")), Now));

        // No target of its kind: a detail as a text block, a text block as a detail, an unknown section.
        Assert.Throws<ArgumentException>(() => Move(book, "D3", b => new DetailTarget.FirstOf(KeyOf(b, "Z1"))));
        Assert.Throws<ArgumentException>(() => Move(book, "D3", b => new DetailTarget.After(KeyOf(b, "Text zwei"))));
        Assert.Throws<ArgumentException>(() => Move(book, "D3", b => new DetailTarget.SectionStart(Guid.NewGuid())));
        Assert.Throws<ArgumentException>(() => Move(book, "D3", b => new DetailTarget.SectionEnd(KeyOf(b, "Text zwei"))));

        Assert.Equal(Text, Serialize(book));
        Assert.False(book.Page.IsDirty);
    }

    [Fact]
    public void MoveDetail_ReadOnlyPage_Refused_FileUnchanged()
    {
        var book = Load("type:: book\n\n- # A\n\t- Text\n\t\t- Eins\n\t\t- Zwei\n\t  ```\n");
        Assert.True(book.Page.IsReadOnly);
        var before = book.Page.AllBlocks().Select(b => (b.Key, b.Parent?.Key, b.Content)).ToList();

        var zwei = book.Page.AllBlocks().Single(b => b.Content.StartsWith("Zwei", StringComparison.Ordinal)).Key;

        Assert.Throws<InvalidOperationException>(() => Move(book, "Eins", _ => new DetailTarget.After(zwei)));
        Assert.Throws<InvalidOperationException>(() => Move(book, "Eins", b => new DetailTarget.SectionStart(KeyOf(b, "# A"))));
        Assert.Equal(before, book.Page.AllBlocks().Select(b => (b.Key, b.Parent?.Key, b.Content)).ToList());
    }

    [Fact]
    public void MoveDetail_KeepsIdAndSourcesOfDetails()
    {
        var book = Load();

        Assert.True(Move(book, "D2", b => new DetailTarget.After(KeyOf(b, "T1"))));

        // Only the place changes: lines as they were apart from the indentation, nothing stamped.
        Assert.Equal(
            Start + EinsHead + D1Tree + D3 + Zwei + Z1 +
            "- # Zwei\n\t- Text drei\n\t\t- T1\n" + D2Tree + "\t- ## Zwei-A\n" + Glossar,
            Serialize(book));
        var d2 = BlockOf(book, "D2");
        Assert.Equal(Guid.Parse(DetailId), d2.Id);
        Assert.Equal($"(({Note2}))", d2.GetProperty("source"));
        Assert.Null(BlockDates.Created(d2));
        Assert.Null(BlockDates.Updated(BlockOf(book, "Text drei")));
    }
}
