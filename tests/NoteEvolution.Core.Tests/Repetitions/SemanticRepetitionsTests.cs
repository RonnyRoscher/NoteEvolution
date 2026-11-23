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

    private static IReadOnlyList<(Guid A, Guid B, float Score)> Candidates(
        Guid[] blocks, Dictionary<Guid, float[]> vectors, IReadOnlySet<(Guid A, Guid B)>? literal = null) =>
        SemanticRepetitions.Candidates(blocks, key => vectors.GetValueOrDefault(key), literal ?? NoLiteral);

    private static ComparedSentence S(int index) => new(0, index * 10, 9, 5);

    /// <summary>Confirm over blocks whose sentences S(0..n-1) have the given vectors (null = none).</summary>
    private static IReadOnlyList<SemanticPair> Confirm(
        IReadOnlyList<(Guid A, Guid B, float Score)> candidates, Dictionary<Guid, float[]?[]> sentences,
        Action<Guid, Guid, float, bool>? near = null) =>
        SemanticRepetitions.Confirm(
            candidates,
            key => [.. Enumerable.Range(0, sentences[key].Length).Select(S)],
            (key, s) => sentences[key][s.Start / 10],
            near);

    private static float[] Axis(int axis)
    {
        var v = new float[Dimensions];
        v[axis] = 1;
        return v;
    }

    [Fact]
    public void Confirm_TopicPairWithoutCloseSentence_IsDropped()
    {
        var ids = Ids(2);
        var pairs = Confirm(
            [(ids[0], ids[1], 0.95f)],
            new() { [ids[0]] = [First()], [ids[1]] = [Cos(0.88, 1)] });

        Assert.Empty(pairs);
    }

    [Fact]
    public void Confirm_PairWithOneCloseSentence_Counts_ScoreIsTheBestSentence()
    {
        var ids = Ids(2);
        var pairs = Confirm(
            [(ids[0], ids[1], 0.86f)],
            new() { [ids[0]] = [First(), Axis(5)], [ids[1]] = [Axis(6), Cos(0.93, 1)] });

        var pair = Assert.Single(pairs);
        Assert.Equal(0.93f, pair.Score, 4);
        var sentence = Assert.Single(pair.Sentences);
        Assert.Equal((S(0), S(1)), (sentence.A, sentence.B));
    }

    [Fact]
    public void Confirm_AtMostThreeSentencePairs_BestFirst()
    {
        var ids = Ids(2);
        var a = new float[]?[] { Axis(0), Axis(1), Axis(2), Axis(3) };
        var b = new float[]?[] { Cos2(0, 0.93), Cos2(1, 0.97), Cos2(2, 0.95), Cos2(3, 0.94) };

        var pair = Assert.Single(Confirm([(ids[0], ids[1], 0.9f)], new() { [ids[0]] = a, [ids[1]] = b }));

        Assert.Equal(3, pair.Sentences.Count);
        Assert.Equal([0.97f, 0.95f, 0.94f], pair.Sentences.Select(p => (float)Math.Round(p.Score, 2)));
        Assert.Equal(0.97f, pair.Score, 4);
    }

    /// <summary>A vector with the given cosine to the axis, the rest on axis 7.</summary>
    private static float[] Cos2(int axis, double cosine)
    {
        var v = new float[Dimensions];
        v[axis] = (float)cosine;
        v[7] = (float)Math.Sqrt(1 - cosine * cosine);
        return v;
    }

    [Fact]
    public void Confirm_ASentenceIsUsedOnce_GreedyByScore()
    {
        // B0 (0.93) and B1 (0.99) are both close to A0; A1 is orthogonal to both and plays no part. The better B1 comes
        // second, so only taking the pairs by score (not in order) pairs A0 with B1 and leaves B0 out.
        var ids = Ids(2);
        var a = new float[]?[] { Axis(0), Axis(1) };
        var b = new float[]?[] { Cos2(0, 0.93), Cos2(0, 0.99) };

        var pair = Assert.Single(Confirm([(ids[0], ids[1], 0.9f)], new() { [ids[0]] = a, [ids[1]] = b }));

        var sentence = Assert.Single(pair.Sentences);
        Assert.Equal((S(0), S(1)), (sentence.A, sentence.B));
        Assert.Equal(0.99f, sentence.Score, 4);
    }

    [Fact]
    public void Confirm_PerBlock_KeepsTheThreeWithTheBestSentence()
    {
        // Y has four partners (0.93, 0.97, 0.95, 0.94); the weakest one has three better partners of its own.
        var ids = Ids(5);
        var y = ids[0];
        var weakest = ids[1];
        var scores = new[] { 0.93, 0.97, 0.95, 0.94 };
        var sentences = new Dictionary<Guid, float[]?[]> { [y] = [Axis(0)] };
        var candidates = new List<(Guid, Guid, float)>();
        for (var i = 0; i < 4; i++)
        {
            sentences[ids[i + 1]] = [Cos2(0, scores[i])];
            candidates.Add((y, ids[i + 1], 0.9f));
        }

        sentences[weakest] = [Cos2(0, 0.93), Axis(1), Axis(1), Axis(1)];
        foreach (var partner in Ids(3).Select((id, k) => (id, k)))
        {
            sentences[partner.id] = [Cos2(1, 0.99 - partner.k * 0.001)];
            candidates.Add((weakest, partner.id, 0.9f));
        }

        var pairs = Confirm(candidates, sentences);

        Assert.DoesNotContain(pairs, p => p.A == y && p.B == weakest);
        Assert.Equal(6, pairs.Count);
    }

    [Fact]
    public void Confirm_MissingSentenceVector_LeavesThatSentenceOut()
    {
        var ids = Ids(2);
        var pairs = Confirm(
            [(ids[0], ids[1], 0.9f)],
            new() { [ids[0]] = [First(), Axis(5)], [ids[1]] = [null, Axis(6)] });

        Assert.Empty(pairs);

        // The sentence without a vector does not stop the next one from pairing.
        var pair = Assert.Single(Confirm(
            [(ids[0], ids[1], 0.9f)],
            new() { [ids[0]] = [First()], [ids[1]] = [null, Cos(0.95, 1)] }));
        Assert.Equal((S(0), S(1)), (pair.Sentences[0].A, pair.Sentences[0].B));
    }

    [Fact]
    public void Confirm_AsksForEachBlocksSentenceVectorsOncePerCall()
    {
        var ids = Ids(3);
        var asked = new List<Guid>();

        SemanticRepetitions.Confirm(
            [(ids[0], ids[1], 0.9f), (ids[0], ids[2], 0.9f), (ids[1], ids[2], 0.9f)],
            _ => [S(0), S(1)],
            (key, _) =>
            {
                asked.Add(key);
                return First();
            });

        Assert.Equal(6, asked.Count);
        Assert.All(ids, id => Assert.Equal(2, asked.Count(k => k == id)));
    }

    [Theory]
    [InlineData(0.849, false)]
    [InlineData(0.85, true)]
    public void Candidates_From085(double cosine, bool candidate)
    {
        var ids = Ids(2);

        var candidates = Candidates(ids, new() { [ids[0]] = First(), [ids[1]] = Cos(cosine, 1) });

        Assert.Equal(candidate, candidates.Count == 1);
        if (candidate)
        {
            Assert.Equal((ids[0], ids[1]), (candidates[0].A, candidates[0].B));
            Assert.Equal((float)cosine, candidates[0].Score, 4);
        }
    }

    [Fact]
    public void Candidates_ReversedInput_KeepsOrderOfBlocks()
    {
        var ids = Ids(2);

        var candidate = Assert.Single(Candidates([ids[1], ids[0]], new() { [ids[0]] = First(), [ids[1]] = Cos(0.95, 1) }));

        Assert.Equal((ids[1], ids[0]), (candidate.A, candidate.B));
    }

    [Fact]
    public void Candidates_MissingVector_OrOtherLength_LeavesBlockOut()
    {
        var ids = Ids(4);
        var vectors = new Dictionary<Guid, float[]>
        {
            [ids[0]] = First(),
            [ids[1]] = Cos(0.99, 1),
            [ids[3]] = [1f, 0f], // other length
        };

        var candidate = Assert.Single(Candidates(ids, vectors)); // ids[2] has no vector

        Assert.Equal((ids[0], ids[1]), (candidate.A, candidate.B));
    }

    [Fact]
    public void Candidates_LiteralPairs_AreExcluded()
    {
        var ids = Ids(2);
        var vectors = new Dictionary<Guid, float[]> { [ids[0]] = First(), [ids[1]] = Cos(0.99, 1) };

        Assert.Empty(Candidates(ids, vectors, new HashSet<(Guid A, Guid B)> { (ids[1], ids[0]) }));
        Assert.Single(Candidates(ids, vectors));
    }

    [Fact]
    public void Candidates_KeepsTopFiveOfEitherBlock_From085()
    {
        // X has six counterparts from 0.85 on (mutually below it); the sixth has five closer partners of its own, so its pair
        // with X is in neither block's top five. A counterpart below 0.85 is no candidate at all.
        var ids = Ids(13);
        var x = ids[0];
        var weakest = ids[6];
        var vectors = new Dictionary<Guid, float[]>
        {
            [x] = First(),
            [ids[1]] = Cos(0.92, 1),
            [ids[2]] = Cos(0.91, 2),
            [ids[3]] = Cos(0.90, 3),
            [ids[4]] = Cos(0.89, 4),
            [ids[5]] = Cos(0.88, 5),
            [weakest] = Cos(0.87, 6),
            [ids[7]] = Cos(0.84, 7),
        };
        for (var k = 0; k < 5; k++)
        {
            var v = new float[Dimensions];
            for (var i = 0; i < Dimensions; i++)
            {
                v[i] = (float)(0.97 * vectors[weakest][i]);
            }

            v[k + 1] += (float)Math.Sqrt(1 - 0.97 * 0.97) * (k % 2 == 0 ? 1 : -1) * 0.5f;
            vectors[ids[8 + k]] = v;
        }

        var candidates = Candidates(ids, vectors);

        Assert.DoesNotContain(candidates, c => c.A == x && c.B == weakest);
        Assert.DoesNotContain(candidates, c => c.B == ids[7] || c.A == ids[7]);
        Assert.Equal(5, candidates.Count(c => c.A == x));
        Assert.All(candidates, c => Assert.True(c.Score >= 0.85f));
    }

    [Fact]
    public void Near_ReportsTheBestSentenceOfEveryCandidate()
    {
        var ids = Ids(3);
        var seen = new List<(Guid A, Guid B, float Score, bool Listed)>();

        Confirm(
            [(ids[0], ids[1], 0.9f), (ids[0], ids[2], 0.9f)],
            new() { [ids[0]] = [First()], [ids[1]] = [Cos(0.95, 1)], [ids[2]] = [Cos(0.80, 2)] },
            (a, b, score, listed) => seen.Add((a, b, score, listed)));

        Assert.Equal(2, seen.Count);
        Assert.Equal((ids[1], 0.95f, true), (seen[0].B, (float)Math.Round(seen[0].Score, 2), seen[0].Listed));
        Assert.Equal((ids[2], 0f, false), (seen[1].B, seen[1].Score, seen[1].Listed));
    }
}
