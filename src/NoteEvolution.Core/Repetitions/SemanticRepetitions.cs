namespace NoteEvolution.Core.Repetitions;

/// <summary>Two sentences, one in each block of a pair, whose meaning is close: the cosine of their vectors.</summary>
public sealed record SentencePair(ComparedSentence A, ComparedSentence B, float Score);

/// <summary>
/// Two blocks that say the same thing: <paramref name="Score"/> is the value of the best of its sentence pairs.
/// </summary>
public sealed record SemanticPair(Guid A, Guid B, float Score, IReadOnlyList<SentencePair> Sentences);

/// <summary>
/// Finds blocks that say the same thing in different words in two stages: candidate block pairs by block vectors,
/// confirmed only when a sentence pair is close.
/// </summary>
public static class SemanticRepetitions
{
    /// <summary>The block cosine from which a pair becomes a candidate.</summary>
    public const float CandidateThreshold = 0.85f;

    /// <summary>How many candidates a block gets at most (its most similar ones).</summary>
    public const int CandidatesPerBlock = 5;

    /// <summary>The sentence cosine from which two sentences count as a repetition.</summary>
    public const float SentenceThreshold = 0.92f;

    /// <summary>How many sentence pairs a block pair keeps at most.</summary>
    public const int SentencePairs = 3;

    /// <summary>How many counterparts a block lists at most (its most similar ones).</summary>
    public const int PerBlock = 3;

    /// <summary>
    /// Block pairs (A before B in the order of <paramref name="blocks"/>) whose vectors reach <see cref="CandidateThreshold"/>
    /// and that are among the <see cref="CandidatesPerBlock"/> most similar of at least one of the two; pairs in
    /// <paramref name="literal"/> (either order) are left out, as are blocks without a vector or with one of another length.
    /// </summary>
    public static IReadOnlyList<(Guid A, Guid B, float Score)> Candidates(
        IReadOnlyList<Guid> blocks, Func<Guid, float[]?> vectorOf, IReadOnlySet<(Guid A, Guid B)> literal)
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

        var all = new List<(int A, int B, float Score)>();
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
                if (score < CandidateThreshold)
                {
                    continue;
                }

                all.Add((i, j, score));
                (best[i] ??= []).Add((j, score));
                (best[j] ??= []).Add((i, score));
            }
        }

        var top = new HashSet<(int, int)>();
        for (var i = 0; i < best.Length; i++)
        {
            foreach (var (other, _) in best[i]?.OrderByDescending(c => c.Score).ThenBy(c => c.Other).Take(CandidatesPerBlock) ?? [])
            {
                top.Add((Math.Min(i, other), Math.Max(i, other)));
            }
        }

        return [.. all.Where(c => top.Contains((c.A, c.B))).Select(c => (blocks[c.A], blocks[c.B], c.Score))];
    }

    /// <summary>
    /// Keeps the candidates that have a sentence pair from <see cref="SentenceThreshold"/> on. A pair lists up to
    /// <see cref="SentencePairs"/> sentence pairs, best first, each sentence used once (greedy by score); its score is the
    /// best sentence pair's. A pair is kept if it is among the <see cref="PerBlock"/> best-scored counterparts of at least
    /// one of its two blocks, so a block can list more than <see cref="PerBlock"/> (counterparts that keep it in their own
    /// top). A sentence without a vector is left out; each block's sentence vectors are asked for once per call.
    /// <paramref name="near"/> receives every candidate with the best cosine over all its sentence pairs with vectors, also
    /// when it stays below <see cref="SentenceThreshold"/> (0 only if no sentence pair has vectors), and whether it is listed.
    /// </summary>
    public static IReadOnlyList<SemanticPair> Confirm(
        IReadOnlyList<(Guid A, Guid B, float Score)> candidates,
        Func<Guid, IReadOnlyList<ComparedSentence>> sentencesOf, Func<Guid, ComparedSentence, float[]?> vectorOf,
        Action<Guid, Guid, float, bool>? near = null)
    {
        var vectors = new Dictionary<Guid, List<(ComparedSentence Sentence, float[] Vector, double Norm)>>();
        List<(ComparedSentence Sentence, float[] Vector, double Norm)> VectorsOf(Guid block)
        {
            if (!vectors.TryGetValue(block, out var list))
            {
                vectors[block] = list = Vectors(block, sentencesOf, vectorOf);
            }

            return list;
        }

        var confirmed = new List<SemanticPair?>();
        var best = new List<float>();
        foreach (var (a, b, _) in candidates)
        {
            var (sentences, bestScore) = ConfirmSentences(VectorsOf(a), VectorsOf(b));
            confirmed.Add(sentences.Count == 0 ? null : new SemanticPair(a, b, sentences[0].Score, sentences));
            best.Add(bestScore ?? 0f);
        }

        var ranked = new Dictionary<Guid, List<(int Index, float Score)>>();
        for (var i = 0; i < confirmed.Count; i++)
        {
            if (confirmed[i] is not { } pair)
            {
                continue;
            }

            foreach (var key in new[] { pair.A, pair.B })
            {
                if (!ranked.TryGetValue(key, out var list))
                {
                    ranked[key] = list = [];
                }

                list.Add((i, pair.Score));
            }
        }

        var top = new HashSet<int>();
        foreach (var list in ranked.Values)
        {
            foreach (var (index, _) in list.OrderByDescending(c => c.Score).ThenBy(c => c.Index).Take(PerBlock))
            {
                top.Add(index);
            }
        }

        var pairs = new List<SemanticPair>();
        for (var i = 0; i < candidates.Count; i++)
        {
            var listed = confirmed[i] is not null && top.Contains(i);
            near?.Invoke(candidates[i].A, candidates[i].B, best[i], listed);
            if (listed)
            {
                pairs.Add(confirmed[i]!);
            }
        }

        return pairs;
    }

    /// <summary>
    /// The sentence pairs from <see cref="SentenceThreshold"/> on, taken greedily by score, and the best cosine over all
    /// sentence pairs with vectors (null if there is none), whether it reaches the threshold or not.
    /// </summary>
    private static (List<SentencePair> Taken, float? Best) ConfirmSentences(
        List<(ComparedSentence Sentence, float[] Vector, double Norm)> left,
        List<(ComparedSentence Sentence, float[] Vector, double Norm)> right)
    {
        var scored = new List<SentencePair>();
        float? best = null;
        foreach (var (sentenceA, vectorA, normA) in left)
        {
            foreach (var (sentenceB, vectorB, normB) in right)
            {
                if (vectorA.Length != vectorB.Length)
                {
                    continue;
                }

                var score = (float)(Dot(vectorA, vectorB) / (normA * normB));
                best = Math.Max(best ?? score, score);
                if (score >= SentenceThreshold)
                {
                    scored.Add(new SentencePair(sentenceA, sentenceB, score));
                }
            }
        }

        var taken = new List<SentencePair>();
        var usedA = new HashSet<ComparedSentence>();
        var usedB = new HashSet<ComparedSentence>();
        foreach (var pair in scored.OrderByDescending(p => p.Score))
        {
            if (usedA.Contains(pair.A) || usedB.Contains(pair.B))
            {
                continue;
            }

            usedA.Add(pair.A);
            usedB.Add(pair.B);
            taken.Add(pair);
            if (taken.Count == SentencePairs)
            {
                break;
            }
        }

        return (taken, best);
    }

    private static List<(ComparedSentence Sentence, float[] Vector, double Norm)> Vectors(
        Guid block, Func<Guid, IReadOnlyList<ComparedSentence>> sentencesOf, Func<Guid, ComparedSentence, float[]?> vectorOf)
    {
        var result = new List<(ComparedSentence, float[], double)>();
        foreach (var sentence in sentencesOf(block))
        {
            if (vectorOf(block, sentence) is { Length: > 0 } vector && Norm(vector) is var norm and > 0)
            {
                result.Add((sentence, vector, norm));
            }
        }

        return result;
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
