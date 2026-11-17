namespace NoteEvolution.Core.Repetitions;

/// <summary>Two blocks whose meaning is close: the cosine similarity of their vectors.</summary>
public sealed record SemanticPair(Guid A, Guid B, float Score);

/// <summary>Finds blocks that say the same thing in different words, by the similarity of their embedding vectors.</summary>
public static class SemanticRepetitions
{
    /// <summary>The cosine from which two blocks count as a repetition.</summary>
    public const float Threshold = 0.90f;

    /// <summary>The cosine from which a pair is reported to the log, listed or not.</summary>
    public const float LogThreshold = 0.85f;

    /// <summary>How many counterparts a block lists at most (its most similar ones).</summary>
    public const int PerBlock = 3;

    /// <summary>
    /// Pairs of blocks (in the order of <paramref name="blocks"/>) whose vectors reach <see cref="Threshold"/> and that
    /// are among the <see cref="PerBlock"/> most similar of at least one of the two; pairs in <paramref name="literal"/>
    /// (either order) are left out. A block without a vector, or with one of another length than the first found, is left
    /// out. <paramref name="near"/> receives every other pair from <see cref="LogThreshold"/> on, with whether it is listed.
    /// </summary>
    public static IReadOnlyList<SemanticPair> Find(
        IReadOnlyList<Guid> blocks, Func<Guid, float[]?> vectorOf, IReadOnlySet<(Guid A, Guid B)> literal,
        Action<Guid, Guid, float, bool>? near = null)
    {
        var vectors = new float[blocks.Count][];
        var norms = new double[blocks.Count];
        var length = -1;
        for (var i = 0; i < blocks.Count; i++)
        {
            if (vectorOf(blocks[i]) is not { Length: > 0 } vector || (length >= 0 && vector.Length != length))
            {
                continue;
            }

            var norm = Norm(vector);
            if (norm == 0)
            {
                continue;
            }

            length = vector.Length;
            vectors[i] = vector;
            norms[i] = norm;
        }

        // Every pair from the log threshold on, with the rank candidates of both blocks.
        var candidates = new List<(int A, int B, float Score)>();
        var best = new List<(int Other, float Score)>[blocks.Count];
        for (var i = 0; i < blocks.Count; i++)
        {
            if (vectors[i] is null)
            {
                continue;
            }

            for (var j = i + 1; j < blocks.Count; j++)
            {
                if (vectors[j] is null || literal.Contains((blocks[i], blocks[j])) || literal.Contains((blocks[j], blocks[i])))
                {
                    continue;
                }

                var score = (float)(Dot(vectors[i], vectors[j]) / (norms[i] * norms[j]));
                if (score < LogThreshold)
                {
                    continue;
                }

                candidates.Add((i, j, score));
                (best[i] ??= []).Add((j, score));
                (best[j] ??= []).Add((i, score));
            }
        }

        // The block's own top PerBlock: highest score first, earlier block first on a tie.
        var top = new HashSet<(int, int)>();
        for (var i = 0; i < best.Length; i++)
        {
            foreach (var (other, _) in best[i]?.OrderByDescending(c => c.Score).ThenBy(c => c.Other).Take(PerBlock) ?? [])
            {
                top.Add((Math.Min(i, other), Math.Max(i, other)));
            }
        }

        var pairs = new List<SemanticPair>();
        foreach (var (a, b, score) in candidates)
        {
            var listed = score >= Threshold && top.Contains((a, b));
            near?.Invoke(blocks[a], blocks[b], score, listed);
            if (listed)
            {
                pairs.Add(new SemanticPair(blocks[a], blocks[b], score));
            }
        }

        return pairs;
    }

    private static double Dot(float[] a, float[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += (double)a[i] * b[i];
        }

        return sum;
    }

    private static double Norm(float[] vector) => Math.Sqrt(Dot(vector, vector));
}
