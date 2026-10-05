using NoteEvolution.Core.Text;

namespace NoteEvolution.Core.Tests.Text;

public class InlineMarkdownTests
{
    [Fact]
    public void Inline_BoldItalicMixed()
    {
        Assert.Equal(
            [new("a ", false, false), new("fett", true, false), new(" und ", false, false), new("kursiv", false, true)],
            InlineMarkdown.Parse("a **fett** und _kursiv_"));
    }

    [Fact]
    public void Inline_UnclosedMarker_StaysText()
    {
        Assert.Equal([new InlineRun("5 * 3", false, false)], InlineMarkdown.Parse("5 * 3"));
        Assert.Equal([new InlineRun("**offen", false, false)], InlineMarkdown.Parse("**offen"));
        Assert.Equal([new InlineRun("5 * 3 * 4", false, false)], InlineMarkdown.Parse("5 * 3 * 4"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain")]
    [InlineData("a **fett** und *kursiv* x")]
    [InlineData("**fett** *kursiv*")]
    [InlineData("*a **b** c*")]
    [InlineData("***beides***")]
    [InlineData("5 * 3")]
    public void Inline_FormatOfParse_RoundTrips(string text)
    {
        Assert.Equal(text, InlineMarkdown.Format(InlineMarkdown.Parse(text)));
    }

    [Fact]
    public void Inline_Underscore_IsWrittenAsStar()
    {
        Assert.Equal("*x*", InlineMarkdown.Format(InlineMarkdown.Parse("_x_")));
    }

    [Fact]
    public void Inline_Nested_BoldItalic()
    {
        Assert.Equal([new InlineRun("x", true, true)], InlineMarkdown.Parse("**_x_**"));
        Assert.Equal([new InlineRun("x", true, true)], InlineMarkdown.Parse("***x***"));
    }

    [Fact]
    public void Inline_OtherSyntaxPassesThroughUnchanged()
    {
        const string text = @"siehe [[Seite_a_b]] und ((6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70)) #tag \- \*kein\* snake_case_name `a*b*c`";

        Assert.Equal([new InlineRun(text, false, false)], InlineMarkdown.Parse(text));
    }

    [Fact]
    public void Inline_Format_WritesMarkersAroundText()
    {
        var runs = new InlineRun[] { new("a ", false, false), new("b", true, false), new("c", false, true) };

        Assert.Equal("a **b***c*", InlineMarkdown.Format(runs));
    }

    [Fact]
    public void Inline_AdjacentRunsWithSameFlagsAreMerged()
    {
        Assert.Equal([new InlineRun("ab", true, false)], InlineMarkdown.Parse("**a****b**"));
        Assert.Equal([new InlineRun("ab", false, true)], InlineMarkdown.Parse("*a*_b_"));
    }

    [Fact]
    public void Inline_UnderscoreInsideWordIsNotItalic()
    {
        Assert.Equal([new InlineRun("snake_case_name", false, false)], InlineMarkdown.Parse("snake_case_name"));
    }
}
