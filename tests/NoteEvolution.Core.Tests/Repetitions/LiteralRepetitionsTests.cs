using System.Diagnostics;
using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Repetitions;

namespace NoteEvolution.Core.Tests.Repetitions;

public class LiteralRepetitionsTests
{
    private static Book Load(params string[] blocks) =>
        Book.Load(LogseqParser.Parse(
            "Buch - Test.md",
            Encoding.UTF8.GetBytes("type:: book\n\n- # Eins\n" + string.Concat(blocks.Select(b => $"\t- {b}\n")))));

    private static string W(string prefix, int from, int count) =>
        string.Join(' ', Enumerable.Range(from, count).Select(i => $"{prefix}{i}"));

    private static IReadOnlyList<LiteralPair> Pairs(params string[] blocks) =>
        LiteralRepetitions.Find(ComparisonText.Of(Load(blocks)));

    [Fact]
    public void SevenSharedWords_NoPair()
    {
        Assert.Empty(Pairs(
            $"{W("a", 0, 3)} {W("s", 0, 7)} {W("b", 0, 3)}",
            $"{W("c", 0, 3)} {W("s", 0, 7)} {W("d", 0, 3)}"));
    }

    [Fact]
    public void EightSharedWords_Pair()
    {
        var book = Load(
            $"{W("a", 0, 3)} {W("s", 0, 8)} {W("b", 0, 3)}",
            $"{W("c", 0, 5)} {W("s", 0, 8)} {W("d", 0, 3)}");

        var pair = Assert.Single(LiteralRepetitions.Find(ComparisonText.Of(book)));

        var run = Assert.Single(pair.Runs);
        Assert.Equal(new SharedRun(3, 5, 8), run);
        Assert.Equal(8, pair.Words);
    }

    [Fact]
    public void Run_IsMaximal()
    {
        var pair = Assert.Single(Pairs(
            $"{W("a", 0, 2)} {W("s", 0, 12)} {W("b", 0, 2)}",
            $"{W("s", 0, 12)} {W("d", 0, 2)}"));

        Assert.Equal(new SharedRun(2, 0, 12), Assert.Single(pair.Runs));
    }

    [Fact]
    public void TwoRuns_InOnePair()
    {
        var pair = Assert.Single(Pairs(
            $"{W("s", 0, 8)} {W("a", 0, 3)} {W("t", 0, 9)}",
            $"{W("t", 0, 9)} {W("c", 0, 2)} {W("s", 0, 8)}"));

        Assert.Equal(2, pair.Runs.Count);
        Assert.Contains(new SharedRun(0, 11, 8), pair.Runs);
        Assert.Contains(new SharedRun(11, 0, 9), pair.Runs);
        Assert.Equal(17, pair.Words);
    }

    [Fact]
    public void RunAcrossSubPoints()
    {
        var shared = W("s", 0, 10);
        var book = Book.Load(LogseqParser.Parse(
            "Buch - Test.md",
            Encoding.UTF8.GetBytes(
                "type:: book\n\n- # Eins\n" +
                $"\t- eigener {W("s", 0, 4)}\n\t\t- {W("s", 4, 6)} Schluss\n" +
                $"\t- anderer Text {shared} Ende\n")));

        var pair = Assert.Single(LiteralRepetitions.Find(ComparisonText.Of(book)));

        Assert.Equal(new SharedRun(1, 2, 10), Assert.Single(pair.Runs));
    }

    [Fact]
    public void PairOnce_AOrderedBeforeB()
    {
        var shared = W("s", 0, 10);
        var book = Load($"{W("a", 0, 2)} {shared}", $"{W("b", 0, 2)} {shared}", $"{W("c", 0, 2)} {shared}");
        var keys = ComparisonText.Of(book).Select(c => c.Key).ToList();

        var pairs = LiteralRepetitions.Find(ComparisonText.Of(book));

        Assert.Equal(
            [(keys[0], keys[1]), (keys[0], keys[2]), (keys[1], keys[2])],
            pairs.Select(p => (p.A, p.B)));
        Assert.All(pairs, p => Assert.Equal(new SharedRun(2, 2, 10), Assert.Single(p.Runs)));
    }

    [Fact]
    public void RepeatedPhraseInsideOneBlock_NoSelfPair()
    {
        var phrase = W("s", 0, 8);

        Assert.Empty(Pairs($"{phrase} {W("a", 0, 2)} {phrase}", $"{W("b", 0, 9)}"));
    }

    [Fact]
    public void Boilerplate_In50Blocks_ListsEachPairOnce_Fast()
    {
        const string boilerplate = "wir danken allen die zu diesem Projekt beigetragen haben und";
        var blocks = Enumerable.Range(0, 50).Select(i => $"{W("eigen", i * 20, 15)} {boilerplate}").ToArray();
        var compared = ComparisonText.Of(Load(blocks));

        var watch = Stopwatch.StartNew();
        var pairs = LiteralRepetitions.Find(compared);
        watch.Stop();

        Assert.Equal(1225, pairs.Count);
        Assert.Equal(1225, pairs.Select(p => (p.A, p.B)).Distinct().Count());
        Assert.All(pairs, p => Assert.Equal(new SharedRun(15, 15, 10), Assert.Single(p.Runs)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
    }
}
