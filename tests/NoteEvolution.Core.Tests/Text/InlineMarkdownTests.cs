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

    public static TheoryData<InlineRun[]> RunLists => new()
    {
        { [new("a", false, true), new("b", true, false)] },
        { [new("a", true, false), new("b", false, true)] },
        { [new("a", false, true), new("b", true, true)] },
        { [new("a", true, true), new("b", false, true)] },
        { [new("a", true, false), new("b", true, true), new("c", false, true)] },
        { [new("x ", true, false), new("y", false, false)] },
        { [new(" x", false, true), new("y", false, false)] },
        { [new("a", false, false), new(" b ", true, true), new("c", false, false)] },
        { [new("  ", true, false), new("c", true, false)] },
        { [new("a", true, false), new("", true, true), new("b", true, false)] },
        { [new("ab", false, true), new("cd", true, false)] },
        { [new("a", false, false), new("b", false, true), new("c", true, false), new("d", false, false)] },
    };

    [Theory]
    [MemberData(nameof(RunLists))]
    public void Inline_ParseOfFormat_EqualsNormalizedRuns(InlineRun[] runs)
    {
        Assert.Equal(InlineMarkdown.Normalize(runs), InlineMarkdown.Parse(InlineMarkdown.Format(runs)));
    }

    [Fact]
    public void Inline_ParseOfFormat_EqualsNormalizedRuns_ForAllShortRunLists()
    {
        string[] texts = ["a", "b c", " d ", "e"];
        var alphabet = (from text in texts from bold in new[] { false, true } from italic in new[] { false, true }
                        select new InlineRun(text, bold, italic)).ToArray();
        IEnumerable<InlineRun[]> lists = [[]];
        for (var length = 1; length <= 4; length++)
        {
            var size = length;
            lists = lists.Concat(Enumerable.Range(0, (int)Math.Pow(alphabet.Length, size))
                .Select(n => Enumerable.Range(0, size).Select(p => alphabet[n / (int)Math.Pow(alphabet.Length, p) % alphabet.Length]).ToArray()));
        }

        var failures = lists
            .Where(runs => !InlineMarkdown.Normalize(runs).SequenceEqual(InlineMarkdown.Parse(InlineMarkdown.Format(runs))))
            .Select(runs => Describe(runs) + "  =>  " + InlineMarkdown.Format(runs) + "  =>  " + Describe(InlineMarkdown.Parse(InlineMarkdown.Format(runs))))
            .Take(12)
            .ToList();

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static string Describe(IEnumerable<InlineRun> runs) =>
        string.Join(" | ", runs.Select(r => $"{(r.Bold ? "B" : "")}{(r.Italic ? "I" : "")}'{r.Text}'"));

    [Fact]
    public void Inline_Normalize_MovesEdgeWhitespaceOutAndMerges()
    {
        var result = InlineMarkdown.Normalize([new(" a", true, false), new("", false, true), new("b ", true, false), new("c", false, false)]);

        Assert.Equal([new InlineRun(" ", false, false), new InlineRun("ab", true, false), new InlineRun(" c", false, false)], result);
    }

    [Fact]
    public void Inline_UnderscoreInsideWordIsNotItalic()
    {
        Assert.Equal([new InlineRun("snake_case_name", false, false)], InlineMarkdown.Parse("snake_case_name"));
    }
}
