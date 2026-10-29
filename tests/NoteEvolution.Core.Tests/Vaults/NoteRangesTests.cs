using NoteEvolution.Core.Vaults;
using NoteEvolution.TestSupport;

namespace NoteEvolution.Core.Tests.Vaults;

public class NoteRangesTests
{
    private const string Page =
        "- Haupt\n" +
        "\t- A\n" +
        "\t\t- A1\n" +
        "\t\t\t- A1x\n" +
        "\t\t- A2\n" +
        "\t- B\n";

    private static (NoteBlock Note, Dictionary<string, Guid> Keys) Setup(TestVault tv)
    {
        var vault = tv.Open();
        var repo = new NoteRepository(vault);
        var all = repo.All().ToList();
        var keys = all.ToDictionary(n => n.Block.Content, n => n.Key);
        return (all.Single(n => n.Block.Content == "Haupt"), keys);
    }

    [Fact]
    public void Level0_BlockAndSubtree()
    {
        using var tv = TestVault.Create(("journals/2026_03_01.md", Page));
        var (note, k) = Setup(tv);

        var range = NoteRanges.Of(note, k["A"], 0);

        Assert.Equal(k["A"], range.HeadKey);
        Assert.False(range.OwnOnly);
        Assert.Equal([k["A"], k["A1"], k["A1x"], k["A2"]], range.Keys);
    }

    [Fact]
    public void LevelMinus1_BlockOnly()
    {
        using var tv = TestVault.Create(("journals/2026_03_01.md", Page));
        var (note, k) = Setup(tv);

        var range = NoteRanges.Of(note, k["A"], -1);

        Assert.Equal(k["A"], range.HeadKey);
        Assert.True(range.OwnOnly);
        Assert.Equal([k["A"]], range.Keys);
    }

    [Fact]
    public void LevelUp_ParentWithSubtree_CappedAtMain()
    {
        using var tv = TestVault.Create(("journals/2026_03_01.md", Page));
        var (note, k) = Setup(tv);

        Assert.Equal(k["A1"], NoteRanges.Of(note, k["A1x"], 1).HeadKey);
        Assert.Equal([k["A1"], k["A1x"]], NoteRanges.Of(note, k["A1x"], 1).Keys);
        Assert.Equal(k["A"], NoteRanges.Of(note, k["A1x"], 2).HeadKey);
        var top = NoteRanges.Of(note, k["A1x"], 3);
        Assert.Equal(k["Haupt"], top.HeadKey);
        Assert.Equal([k["Haupt"], k["A"], k["A1"], k["A1x"], k["A2"], k["B"]], top.Keys);
        Assert.Equal(top.Keys, NoteRanges.Of(note, k["A1x"], 99).Keys);
        Assert.Equal([k["A1x"]], NoteRanges.Of(note, k["A1x"], -5).Keys);
    }

    [Fact]
    public void Bounds()
    {
        using var tv = TestVault.Create(("journals/2026_03_01.md", Page));
        var (note, k) = Setup(tv);

        Assert.Equal((-1, 0), NoteRanges.LevelBounds(note, k["Haupt"]));
        Assert.Equal((-1, 1), NoteRanges.LevelBounds(note, k["B"]));
        Assert.Equal((-1, 3), NoteRanges.LevelBounds(note, k["A1x"]));
        Assert.Throws<ArgumentException>(() => NoteRanges.LevelBounds(note, Guid.NewGuid()));
    }
}
