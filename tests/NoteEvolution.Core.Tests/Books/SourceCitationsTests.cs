using NoteEvolution.Core.Books;

namespace NoteEvolution.Core.Tests.Books;

public class SourceCitationsTests
{
    private static readonly Guid A = Guid.Parse("0193a1c2-1b7e-7c1d-9a0f-2b3c4d5e6f71");
    private static readonly Guid B = Guid.Parse("0193a1c2-1b7e-7c1d-9a0f-2b3c4d5e6f72");

    [Fact]
    public void Parse_WithAndWithoutLocator_Format_RoundTrip()
    {
        var text = $"Vorn {SourceCitations.Format(A, "S. 12")} mitte {SourceCitations.Format(B, null)} hinten {SourceCitations.Format(A, "  ")}";

        var found = SourceCitations.Find(text);

        Assert.Equal(3, found.Count);
        Assert.Equal((A, "S. 12"), (found[0].Id, found[0].Locator));
        Assert.Equal((B, (string?)null), (found[1].Id, found[1].Locator));
        Assert.Null(found[2].Locator);
        Assert.Equal($"{{{{quelle (({A:D})) S. 12}}}}", text.Substring(found[0].Start, found[0].Length));
        Assert.Equal($"{{{{quelle (({B:D}))}}}}", SourceCitations.Format(B, null));
        Assert.Equal(SourceCitations.Format(A, "Min. 14:30"), SourceCitations.Format(A, " Min. 14:30 "));
        Assert.Equal("Vorn mitte hinten", SourceCitations.Remove(text, new HashSet<Guid> { A, B }).Replace("  ", " ").Trim());
        Assert.Equal(found[1].Length, SourceCitations.LengthAt(text, found[1].Start));
        Assert.Equal(0, SourceCitations.LengthAt(text, 0));
    }

    [Fact]
    public void Remove_OnlyTheGivenIds_AndOneSpace()
    {
        var text = $"a {SourceCitations.Format(A, "S. 1")} b {SourceCitations.Format(B, null)}";

        Assert.Equal($"a b {SourceCitations.Format(B, null)}", SourceCitations.Remove(text, new HashSet<Guid> { A }));
    }

    [Fact]
    public void BlockRefs_IgnoreRefsInsideTheMacro()
    {
        var text = $"Mensch {BlockRefs.Format(A)} und {SourceCitations.Format(B, "S. 3")} dann {BlockRefs.Format(B)}";

        var refs = BlockRefs.Find(text);

        Assert.Equal([A, B], refs.Select(r => r.Id));
        Assert.Equal(BlockRefs.Format(B), text.Substring(refs[1].Start, refs[1].Length));
        Assert.Equal(
            $"Mensch und {SourceCitations.Format(B, "S. 3")} dann",
            BlockRefs.Remove(text, new HashSet<Guid> { A, B }));
    }

    [Fact]
    public void Malformed_IsNotACitation()
    {
        Assert.Empty(SourceCitations.Find("{{quelle ((nicht-eine-id))}} {{quelle}} {{quelleX}}"));
        Assert.Empty(SourceCitations.Find($"{{{{quelle (({A:D}))x}}}}"));
    }
}
