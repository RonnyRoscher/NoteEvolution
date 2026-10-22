using System.Text;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Vaults;

namespace NoteEvolution.Core.Tests.Vaults;

public class DayMarksTests
{
    private const string Alpha = "Buch - Alpha";
    private const string Beta = "Buch - Beta";

    private static Page Parse(string text) => LogseqParser.Parse("journals/2026_10_01.md", Encoding.UTF8.GetBytes(text));

    private static string Text(Page page) => Encoding.UTF8.GetString(PageSerializer.Serialize(page));

    [Fact]
    public void ParseBooks_IsLenient_FormatIsCanonical()
    {
        Assert.Equal([Alpha, Beta], DayMarks.ParseBooks($" [[ {Alpha} ]] ,[[{Beta}]], kaputt, [[]]"));
        Assert.Empty(DayMarks.ParseBooks(""));
        Assert.Equal($"[[{Alpha}]], [[{Beta}]]", DayMarks.FormatBooks([Alpha, Beta]));
    }

    [Fact]
    public void NotRelevant_Check_WritesThePropertyBeforeTheBullets()
    {
        var page = Parse("- Eintrag\n");

        Assert.True(DayMarks.SetNotRelevant(page, Alpha, true));

        Assert.Equal($"not-relevant-for:: [[{Alpha}]]\n\n- Eintrag\n", Text(page));
        Assert.True(DayMarks.IsNotRelevant(page, Alpha));
        Assert.True(DayMarks.IsNotRelevant(page, Alpha.ToUpperInvariant()));
        Assert.False(DayMarks.IsNotRelevant(page, Beta));
        Assert.False(DayMarks.IsLater(page));
    }

    [Theory]
    [InlineData("- Eintrag\n")]
    [InlineData("- Eintrag")]
    [InlineData("\n- Eintrag\r\n")]
    [InlineData("title:: Tag\r\n\r\n- Eintrag\r\n")]
    [InlineData("Freier Text\n- Eintrag\n")]
    [InlineData("")]
    public void NotRelevant_CheckThenUncheck_RestoresTheBytes(string text)
    {
        var page = Parse(text);

        DayMarks.SetNotRelevant(page, Alpha, true);
        Assert.True(DayMarks.SetNotRelevant(page, Alpha, false));

        Assert.Equal(text, Text(page));
        Assert.Null(page.GetPageProperty(DayMarks.NotRelevantKey));
    }

    [Fact]
    public void NotRelevant_TwoBooks_KeepEachOthersEntries()
    {
        const string text = "- Eintrag\n";
        var page = Parse(text);

        DayMarks.SetNotRelevant(page, Alpha, true);
        var afterAlpha = Text(page);
        DayMarks.SetNotRelevant(page, Beta, true);
        Assert.Equal($"not-relevant-for:: [[{Alpha}]], [[{Beta}]]\n\n- Eintrag\n", Text(page));

        DayMarks.SetNotRelevant(page, Beta, false);
        Assert.Equal(afterAlpha, Text(page));
        Assert.True(DayMarks.IsNotRelevant(page, Alpha));
        Assert.False(DayMarks.IsNotRelevant(page, Beta));

        DayMarks.SetNotRelevant(page, Beta, true);
        DayMarks.SetNotRelevant(page, Alpha, false);
        Assert.Equal($"not-relevant-for:: [[{Beta}]]\n\n- Eintrag\n", Text(page));

        DayMarks.SetNotRelevant(page, Beta, false);
        Assert.Equal(text, Text(page));
    }

    [Fact]
    public void NotRelevant_NoChange_ReturnsFalseAndLeavesThePageClean()
    {
        var page = Parse($"not-relevant-for:: [[{Alpha}]]\n\n- Eintrag\n");

        Assert.False(DayMarks.SetNotRelevant(page, Alpha, true));
        Assert.False(DayMarks.SetNotRelevant(page, Beta, false));
        Assert.False(DayMarks.SetLater(page, false));
        Assert.False(page.IsDirty);
    }

    [Fact]
    public void Later_IsIndependentOfNotRelevant()
    {
        const string text = "title:: Tag\n\n- Eintrag\n";
        var page = Parse(text);

        Assert.True(DayMarks.SetLater(page, true));
        DayMarks.SetNotRelevant(page, Alpha, true);
        Assert.Equal($"title:: Tag\nlater:: true\nnot-relevant-for:: [[{Alpha}]]\n\n- Eintrag\n", Text(page));
        Assert.True(DayMarks.IsLater(page));

        DayMarks.SetLater(page, false);
        Assert.False(DayMarks.IsLater(page));
        Assert.True(DayMarks.IsNotRelevant(page, Alpha));

        DayMarks.SetNotRelevant(page, Alpha, false);
        Assert.Equal(text, Text(page));
    }

    [Fact]
    public void Later_CheckThenUncheck_RestoresTheBytes()
    {
        const string text = "- Eintrag\r\n\t- Unterpunkt\r\n";
        var page = Parse(text);

        DayMarks.SetLater(page, true);
        Assert.Equal("later:: true\r\n\r\n- Eintrag\r\n\t- Unterpunkt\r\n", Text(page));
        DayMarks.SetLater(page, false);

        Assert.Equal(text, Text(page));
    }

    [Fact]
    public void HandEditedDuplicates_AreRead_AndUncheckRemovesAllOfThem()
    {
        var later = Parse("later:: true\nlater:: true\n\n- Eintrag\n");
        Assert.True(DayMarks.SetLater(later, false));
        Assert.Equal("- Eintrag\n", Text(later));

        var page = Parse($"title:: Tag\nnot-relevant-for:: [[{Alpha}]]\nnot-relevant-for:: [[{Beta}]], [[{Alpha}]]\n\n- Eintrag\n");
        Assert.True(DayMarks.IsNotRelevant(page, Beta));

        DayMarks.SetNotRelevant(page, Alpha, false);
        Assert.Equal($"title:: Tag\nnot-relevant-for:: [[{Beta}]]\n\n- Eintrag\n", Text(page));
        DayMarks.SetNotRelevant(page, Beta, false);
        Assert.Equal("title:: Tag\n\n- Eintrag\n", Text(page));
    }

    [Fact]
    public void Filter_HidesMarkedDays_NotRelevantOnlyForItsBook()
    {
        var page = Parse($"later:: true\nnot-relevant-for:: [[{Alpha}]]\n\n- Eintrag\n");
        var plain = Parse("- Eintrag\n");

        Assert.False(new DayMarkFilter(false, false, Alpha).Hides(page));
        Assert.True(new DayMarkFilter(true, false, Alpha).Hides(page));
        Assert.False(new DayMarkFilter(true, false, Beta).Hides(page));
        Assert.False(new DayMarkFilter(true, false, null).Hides(page));
        Assert.True(new DayMarkFilter(false, true, null).Hides(page));
        Assert.False(new DayMarkFilter(true, true, Alpha).Hides(plain));
    }
}
