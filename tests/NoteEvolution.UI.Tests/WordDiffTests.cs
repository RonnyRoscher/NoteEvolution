using NoteEvolution.UI.History;

namespace NoteEvolution.UI.Tests;

/// <summary>The word by word comparison of two stands of a text block (spec 6).</summary>
public class WordDiffTests
{
    [Fact]
    public void Compare_ChangedWord_RemovedAndAdded()
    {
        var parts = WordDiff.Compare("Das ist ein Test", "Das ist kein Test");

        Assert.Equal(
            [new DiffPart(DiffKind.Same, "Das ist "), new DiffPart(DiffKind.Removed, "ein "), new DiffPart(DiffKind.Added, "kein "), new DiffPart(DiffKind.Same, "Test")],
            parts);
    }

    [Fact]
    public void Compare_Equal_OnlySame()
    {
        var parts = WordDiff.Compare("Eins zwei\n  drei", "Eins zwei\n  drei");

        Assert.Equal([new DiffPart(DiffKind.Same, "Eins zwei\n  drei")], parts);
    }

    [Fact]
    public void Compare_Empty_Sides()
    {
        Assert.Empty(WordDiff.Compare("", ""));
        Assert.Equal([new DiffPart(DiffKind.Removed, "alt")], WordDiff.Compare("alt", ""));
        Assert.Equal([new DiffPart(DiffKind.Added, "neu")], WordDiff.Compare("", "neu"));
    }

    [Fact]
    public void Compare_HugeText_FallsBackToWhole()
    {
        var before = string.Join(' ', Enumerable.Repeat("alt", 4001));
        var after = string.Join(' ', Enumerable.Repeat("alt", 4001)) + " neu";

        Assert.Equal([new DiffPart(DiffKind.Removed, before), new DiffPart(DiffKind.Added, after)], WordDiff.Compare(before, after));
    }

    [Fact]
    public void Compare_AtTheLimit_IsCompared()
    {
        var before = string.Join(' ', Enumerable.Repeat("alt", 4000));

        Assert.Equal([new DiffPart(DiffKind.Same, before)], WordDiff.Compare(before, before));
    }
}
