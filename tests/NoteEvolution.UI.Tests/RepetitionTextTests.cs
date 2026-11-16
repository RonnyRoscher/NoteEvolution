using System.Text;
using Bunit;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Repetitions;
using NoteEvolution.UI.Components;

namespace NoteEvolution.UI.Tests;

/// <summary>One block of the side-by-side view: its paragraphs with bold and italic, the shared words marked.</summary>
public class RepetitionTextTests : UiTestContext
{
    private static ComparedBlock Compared(string blockText)
    {
        var page = LogseqParser.Parse("Buch - Test.md", Encoding.UTF8.GetBytes("type:: book\n\n- # Eins\n" + blockText));
        return ComparisonText.Of(Book.Load(page)).Single();
    }

    private IRenderedComponent<RepetitionText> Render(ComparedBlock block, params (int Start, int Words)[] ranges) =>
        Render<RepetitionText>(p => p.Add(c => c.Block, block).Add(c => c.Ranges, ranges));

    [Fact]
    public void Highlights_TheSharedWords_AcrossBoldRun()
    {
        // Words: Der(0) schnelle(1) braune(2) Fuchs(3) springt(4) ueber(5) den(6) faulen(7) Hund(8) heute(9).
        var block = Compared("\t- Der **schnelle braune** Fuchs springt ueber den faulen Hund heute\n");

        var cut = Render(block, (1, 3));

        var mark = Assert.Single(cut.FindAll("mark.ne-rep-shared"));
        Assert.Equal("schnelle braune Fuchs", mark.TextContent);
        Assert.Equal("schnelle braune", mark.QuerySelector("strong")!.TextContent);
        Assert.Equal("Der schnelle braune Fuchs springt ueber den faulen Hund heute", cut.Find(".ne-rep-text").TextContent);
    }

    [Fact]
    public void SeveralRanges_GetOneMarkEach()
    {
        var block = Compared("\t- eins zwei drei vier fuenf sechs sieben acht neun zehn\n");

        var cut = Render(block, (0, 2), (5, 3));

        Assert.Equal(["eins zwei", "sechs sieben acht"], cut.FindAll("mark.ne-rep-shared").Select(m => m.TextContent));
    }

    [Fact]
    public void SubPoints_AreIndented()
    {
        var block = Compared(
            "\t- eins zwei drei vier fuenf sechs sieben acht\n" +
            "\t\t- Unterpunkt a\n" +
            "\t\t\t- noch tiefer\n");

        var cut = Render(block);

        var paragraphs = cut.FindAll(".ne-rep-para");
        Assert.Equal(3, paragraphs.Count);
        Assert.Null(paragraphs[0].GetAttribute("style"));
        Assert.Equal("margin-left: 24px", paragraphs[1].GetAttribute("style"));
        Assert.Equal("margin-left: 48px", paragraphs[2].GetAttribute("style"));
        Assert.Equal("Unterpunkt a", paragraphs[1].TextContent);
    }

    [Fact]
    public void NoRanges_NoMark()
    {
        var block = Compared("\t- eins zwei drei vier fuenf sechs sieben acht neun zehn\n");

        var cut = Render(block);

        Assert.Empty(cut.FindAll("mark"));
        Assert.Equal("eins zwei drei vier fuenf sechs sieben acht neun zehn", cut.Find(".ne-rep-text").TextContent);
    }

    [Fact]
    public void ARange_AcrossParagraphs_IsMarkedInEach()
    {
        var block = Compared(
            "\t- eins zwei drei vier fuenf sechs sieben acht\n" +
            "\t\t- neun zehn elf\n");

        // Words 6..9: sieben acht | neun zehn.
        var cut = Render(block, (6, 4));

        Assert.Equal(["sieben acht", "neun zehn"], cut.FindAll("mark.ne-rep-shared").Select(m => m.TextContent));
    }
}
