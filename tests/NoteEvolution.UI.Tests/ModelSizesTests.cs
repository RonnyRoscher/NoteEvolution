using System.Globalization;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

public class ModelSizesTests
{
    [Theory]
    [InlineData(118_308_185L, 120L)]
    [InlineData(5_069_051L, 5L)]
    public void Megabytes_RoundsToTensAboveOneHundred(long bytes, long expected) =>
        Assert.Equal(expected, ModelSizes.Megabytes(bytes));

    [Theory]
    [InlineData("de-DE", 386_616_370L, "0,4")]
    [InlineData("en-US", 1_273_537_524L, "1.3")]
    public void Gigabytes_OneDecimal_InTheCurrentCulture(string culture, long bytes, string expected)
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            Assert.Equal(expected, ModelSizes.Gigabytes(bytes));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }
}
