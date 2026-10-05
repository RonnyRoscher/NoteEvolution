using NoteEvolution.Core.Links;

namespace NoteEvolution.Core.Tests.Text;

public class ValueFormatTests
{
    private const string IdA = "6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70";
    private const string IdB = "7750a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f71";

    [Fact]
    public void UsedIn_ParsesEntriesWithAndWithoutBlockRef()
    {
        var e = UsedInValue.Parse($"[[Buch - LoveMagic]] (({IdA})), [[Buch - X]]");

        Assert.Equal(2, e.Count);
        Assert.Equal(new UsedInEntry("Buch - LoveMagic", Guid.Parse(IdA)), e[0]);
        Assert.Equal(new UsedInEntry("Buch - X", null), e[1]);
    }

    [Fact]
    public void UsedIn_FormatRoundTrips()
    {
        var entries = new[]
        {
            new UsedInEntry("Buch - LoveMagic", Guid.Parse(IdA)),
            new UsedInEntry("Buch: X", null),
        };

        var text = UsedInValue.Format(entries);

        Assert.Equal($"[[Buch - LoveMagic]] (({IdA})), [[Buch: X]]", text);
        Assert.Equal(entries, UsedInValue.Parse(text));
    }

    [Fact]
    public void UsedIn_ToleratesWhitespaceAndIgnoresGarbage()
    {
        var e = UsedInValue.Parse($"  [[A]]   (( {IdA} ))  ,kaputt,  ((nicht-eine-guid)), [[B]]((nope)),(({IdB})),[[C]]  ");

        Assert.Equal(
            [new UsedInEntry("A", Guid.Parse(IdA)), new UsedInEntry("B", null), new UsedInEntry("C", null)],
            e);
    }

    [Fact]
    public void UsedIn_BlockRefWithoutPageLinkIsIgnored_AndEmptyValueGivesNothing()
    {
        Assert.Empty(UsedInValue.Parse($"(({IdA}))"));
        Assert.Empty(UsedInValue.Parse(""));
        Assert.Equal("", UsedInValue.Format([]));
    }

    [Fact]
    public void UsedIn_OnlyGuidDFormatIsAccepted()
    {
        var e = UsedInValue.Parse($"[[A]] (({IdA.Replace("-", "")}))");

        Assert.Equal([new UsedInEntry("A", null)], e);
    }

    [Fact]
    public void UsedIn_FormatWritesLowercaseIds()
    {
        var id = Guid.Parse(IdA.ToUpperInvariant());

        Assert.Equal($"[[A]] (({IdA}))", UsedInValue.Format([new UsedInEntry("A", id)]));
    }

    [Fact]
    public void Source_ParsesCommaSeparatedRefs_IgnoresGarbage()
    {
        var ids = SourceValue.Parse($"(({IdA})), kaputt, (({IdB}))");

        Assert.Equal([Guid.Parse(IdA), Guid.Parse(IdB)], ids);
    }

    [Fact]
    public void Source_ToleratesWhitespace_RejectsNonDFormat_AndFormatRoundTrips()
    {
        Assert.Equal([Guid.Parse(IdA)], SourceValue.Parse($"  ((  {IdA}  ))  , (({IdB.Replace("-", "")}))"));
        Assert.Empty(SourceValue.Parse(""));

        var text = SourceValue.Format([Guid.Parse(IdA), Guid.Parse(IdB)]);

        Assert.Equal($"(({IdA})), (({IdB}))", text);
        Assert.Equal([Guid.Parse(IdA), Guid.Parse(IdB)], SourceValue.Parse(text));
    }
}
