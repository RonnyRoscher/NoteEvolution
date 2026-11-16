using NoteEvolution.Core.Repetitions;

namespace NoteEvolution.Core.Tests.Repetitions;

public class SemanticRepetitionsTests
{
    private const int Dimensions = 8;

    private static readonly IReadOnlySet<(Guid A, Guid B)> NoLiteral = new HashSet<(Guid, Guid)>();

    private static Guid[] Ids(int count) => [.. Enumerable.Range(0, count).Select(_ => Guid.NewGuid())];

    /// <summary>A vector with the given cosine to the first axis, the rest on the axis <paramref name="axis"/>.</summary>
    private static float[] Cos(double cosine, int axis)
    {
        var v = new float[Dimensions];
        v[0] = (float)cosine;
        v[axis] = (float)Math.Sqrt(1 - cosine * cosine);
        return v;
    }

    private static float[] First() => Cos(1, 1);

    private static IReadOnlyList<SemanticPair> Find(
        Guid[] blocks, Dictionary<Guid, float[]> vectors, IReadOnlySet<(Guid A, Guid B)>? literal = null,
        Action<Guid, Guid, float, bool>? near = null) =>
        SemanticRepetitions.Find(blocks, key => vectors.GetValueOrDefault(key), literal ?? NoLiteral, near);

    [Fact]
    public void BelowThreshold_NoPair()
    {
        var ids = Ids(2);

        Assert.Empty(Find(ids, new() { [ids[0]] = First(), [ids[1]] = Cos(0.899, 1) }));
    }

    [Fact]
    public void AtThreshold_Pair()
    {
        var ids = Ids(2);

        var pair = Assert.Single(Find(ids, new() { [ids[0]] = First(), [ids[1]] = Cos(0.90, 1) }));

        Assert.Equal((ids[0], ids[1]), (pair.A, pair.B));
        Assert.Equal(0.90f, pair.Score, 4);
    }

    [Fact]
    public void ReversedInput_KeepsOrderOfBlocks()
    {
        var ids = Ids(2);

        var pair = Assert.Single(Find([ids[1], ids[0]], new() { [ids[0]] = First(), [ids[1]] = Cos(0.95, 1) }));

        Assert.Equal((ids[1], ids[0]), (pair.A, pair.B));
    }

    [Fact]
    public void FourCounterparts_AllListed_WhenEachHasTheBlockAmongItsThree()
    {
        // X has four counterparts above the threshold, mutually below it (products of the cosines are < 0.90).
        var ids = Ids(5);
        var vectors = new Dictionary<Guid, float[]>
        {
            [ids[0]] = First(),
            [ids[1]] = Cos(0.95, 1),
            [ids[2]] = Cos(0.94, 2),
            [ids[3]] = Cos(0.93, 3),
            [ids[4]] = Cos(0.92, 4),
        };

        var pairs = Find(ids, vectors);

        Assert.Equal(4, pairs.Count(p => p.A == ids[0]));
    }

    [Fact]
    public void FourCounterparts_WeakestIsListedOnlyWhenXIsAmongItsOwnThree()
    {
        var ids = Ids(8);
        var x = ids[0];
        var weakest = ids[4];
        var vectors = new Dictionary<Guid, float[]>
        {
            [x] = First(),
            [ids[1]] = Cos(0.95, 1),
            [ids[2]] = Cos(0.94, 2),
            [ids[3]] = Cos(0.93, 3),
            [weakest] = Cos(0.90, 4),
        };

        // Three counterparts that are closer to the weakest than X is, and that stay below the threshold towards X.
        var direction = vectors[weakest];
        var closer = new[] { (ids[5], 0.96, 5), (ids[6], 0.95, 6), (ids[7], 0.94, 7) };
        foreach (var (id, d, axis) in closer)
        {
            var v = new float[Dimensions];
            for (var i = 0; i < Dimensions; i++)
            {
                v[i] = (float)(d * direction[i]);
            }

            v[axis] = (float)Math.Sqrt(1 - d * d);
            vectors[id] = v;
        }

        var pairs = Find(ids, vectors);

        Assert.Equal(3, pairs.Count(p => p.A == x));
        Assert.DoesNotContain(pairs, p => p.A == x && p.B == weakest);
        Assert.Equal(3, pairs.Count(p => p.A == weakest || p.B == weakest));
    }

    [Fact]
    public void LiteralPairs_AreLeftOut()
    {
        var ids = Ids(2);
        var vectors = new Dictionary<Guid, float[]> { [ids[0]] = First(), [ids[1]] = Cos(0.99, 1) };

        Assert.Empty(Find(ids, vectors, new HashSet<(Guid A, Guid B)> { (ids[0], ids[1]) }));
        Assert.Empty(Find(ids, vectors, new HashSet<(Guid A, Guid B)> { (ids[1], ids[0]) }));
    }

    [Fact]
    public void MissingVector_OrOtherLength_LeavesBlockOut()
    {
        var ids = Ids(4);
        var vectors = new Dictionary<Guid, float[]>
        {
            [ids[0]] = First(),
            [ids[1]] = Cos(0.99, 1),
            [ids[3]] = [1f, 0f], // other length
        };

        var pair = Assert.Single(Find(ids, vectors)); // ids[2] has no vector

        Assert.Equal((ids[0], ids[1]), (pair.A, pair.B));
    }

    [Theory]
    [InlineData(0.84, false, false)]
    [InlineData(0.85, true, false)]
    [InlineData(0.89, true, false)]
    [InlineData(0.95, true, true)]
    public void Near_ReportsFrom085_WithListedFlag(double cosine, bool reported, bool listed)
    {
        var ids = Ids(2);
        var seen = new List<(Guid A, Guid B, float Score, bool Listed)>();

        Find(ids, new() { [ids[0]] = First(), [ids[1]] = Cos(cosine, 1) }, near: (a, b, score, isListed) => seen.Add((a, b, score, isListed)));

        if (!reported)
        {
            Assert.Empty(seen);
            return;
        }

        var entry = Assert.Single(seen);
        Assert.Equal((ids[0], ids[1]), (entry.A, entry.B));
        Assert.Equal((float)cosine, entry.Score, 4);
        Assert.Equal(listed, entry.Listed);
    }
}
