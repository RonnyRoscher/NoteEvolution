using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Repetitions;

namespace NoteEvolution.Core.Tests.Repetitions;

public class ComparisonTextTests
{
    private const string Id = "11111111-1111-4111-8111-111111111111";
    private const string Eight = "eins zwei drei vier fünf sechs sieben acht";

    private static Book Load(string body) =>
        Book.Load(LogseqParser.Parse("Buch - Test.md", Encoding.UTF8.GetBytes("type:: book\n\n" + body)));

    private static TextBlock Block(Book book, string content) => book.Root.AllTextBlocks().Single(b => b.Text == content);

    private static string[] Normals(ComparedBlock block) => [.. block.Words.Select(w => w.Normal)];

    [Fact]
    public void Words_IgnoreCasePunctuationAndFormatting()
    {
        // Padded with two words so that the block reaches the minimum.
        const string text = "**Hallo**, Welt! Das *ist* ein Test. und noch zwei";
        var book = Load($"- # Eins\n\t- {text}\n");

        var compared = ComparisonText.Of(Block(book, text))!;

        Assert.Equal(["hallo", "welt", "das", "ist", "ein", "test", "und", "noch", "zwei"], Normals(compared));
        Assert.Equal("Hallo, Welt! Das ist ein Test. und noch zwei", compared.Paragraphs[0].Plain);
        Assert.True(compared.Paragraphs[0].Runs[0].Bold);
        Assert.Equal("Hallo", compared.Paragraphs[0].Runs[0].Text);
        var hallo = compared.Words[0];
        Assert.Equal("Hallo", compared.Paragraphs[0].Plain.Substring(hallo.Start, hallo.Length));
    }

    [Fact]
    public void References_Citations_Images_AreNotWords()
    {
        var text = $"vor ((22222222-2222-4222-8222-222222222222)) {{{{quelle (({Id})) S. 3}}}} mitte ![Bild](assets/x.png) nach {Eight}";
        var book = Load($"- # Eins\n\t- {text}\n");

        var compared = ComparisonText.Of(Block(book, text))!;

        Assert.Equal(["vor", "mitte", "nach", .. Eight.Split(' ')], Normals(compared));
        Assert.DoesNotContain("quelle", Normals(compared));
        Assert.DoesNotContain("bild", Normals(compared));
    }

    [Fact]
    public void SubPoints_BelongToTheBlock_WithDepth()
    {
        var book = Load("- # Eins\n\t- eigener Text\n\t\t- erster Unterpunkt hier\n\t\t\t- tiefer Unterpunkt dort\n");

        var compared = ComparisonText.Of(Block(book, "eigener Text"))!;

        Assert.Equal([0, 1, 2], compared.Paragraphs.Select(p => p.Depth));
        Assert.Equal(["eigener", "text", "erster", "unterpunkt", "hier", "tiefer", "unterpunkt", "dort"], Normals(compared));
        Assert.Equal([0, 0, 1, 1, 1, 2, 2, 2], compared.Words.Select(w => w.Paragraph));
    }

    [Fact]
    public void NotCompared()
    {
        var book = Load(
            $"- # Eins\n" +
            $"\t- relevanter Block {Eight}\n\t  status:: relevant\n" +
            $"\t- bemerkung Block {Eight}\n\t  status:: bemerkung\n" +
            "\t- nur sieben Wörter stehen in diesem Block\n" +
            "\t- ![](assets/bild.png)\n\t\t- genug Wörter im Unterpunkt eines Bildblocks sind acht\n" +
            $"\t- gezählter Block {Eight}\n" +
            $"- # Anhang\n\t  anhang:: glossar\n\t- Anhang Block {Eight}\n");

        var compared = ComparisonText.Of(book);

        var single = Assert.Single(compared);
        Assert.Equal(Block(book, $"gezählter Block {Eight}").Key, single.Key);
        Assert.NotNull(ComparisonText.Of(Block(book, $"gezählter Block {Eight}")));
        Assert.Null(ComparisonText.Of(Block(book, "nur sieben Wörter stehen in diesem Block")));
    }

    [Fact]
    public void ExactlyEightWords_AreCompared()
    {
        var book = Load($"- # Eins\n\t- {Eight}\n");

        Assert.Single(ComparisonText.Of(book));
    }

    [Fact]
    public void BlocksOfTheBook_InBookOrder_IncludingPrologue()
    {
        var book = Load($"- Vorspann {Eight}\n- # Eins\n\t- Erster {Eight}\n- # Zwei\n\t- Zweiter {Eight}\n");

        var compared = ComparisonText.Of(book);

        Assert.Equal(
            [Block(book, $"Vorspann {Eight}").Key, Block(book, $"Erster {Eight}").Key, Block(book, $"Zweiter {Eight}").Key],
            compared.Select(c => c.Key));
    }

    [Fact]
    public void RemarkSubPoints_AreLeftOut()
    {
        var book = Load(
            $"- # Eins\n\t- {Eight}\n" +
            "\t\t- sichtbarer Unterpunkt\n" +
            "\t\t- bemerkter Unterpunkt\n\t\t  status:: bemerkung\n" +
            "\t\t\t- Kind der Bemerkung\n" +
            "\t\t- relevanter Unterpunkt\n\t\t  status:: relevant\n");

        var compared = ComparisonText.Of(Block(book, Eight))!;

        Assert.Equal(2, compared.Paragraphs.Count);
        Assert.Equal("sichtbarer Unterpunkt", compared.Paragraphs[1].Plain);
        Assert.Equal([.. Eight.Split(' '), "sichtbarer", "unterpunkt"], Normals(compared));
    }
}
