namespace NoteEvolution.Core.Repetitions;

/// <summary>A shared run: first word index and word count in A and in B (same count).</summary>
public readonly record struct SharedRun(int StartA, int StartB, int Words);

/// <summary>Two blocks that share at least one literal run.</summary>
public sealed record LiteralPair(Guid A, Guid B, IReadOnlyList<SharedRun> Runs)
{
    /// <summary>The shared words, the sum over the runs.</summary>
    public int Words => Runs.Sum(run => run.Words);
}

/// <summary>Finds blocks that share a stretch of at least <see cref="MinRun"/> equal words in a row.</summary>
public static class LiteralRepetitions
{
    public const int MinRun = 8;

    /// <summary>
    /// All pairs with at least one run of <see cref="MinRun"/> words, A before B in the order of <paramref name="blocks"/>,
    /// each pair once, ordered by A's then B's position. Runs are maximal and do not overlap within a pair.
    /// </summary>
    public static IReadOnlyList<LiteralPair> Find(IReadOnlyList<ComparedBlock> blocks)
    {
        var words = blocks.Select(block => block.Words.Select(word => word.Normal).ToArray()).ToArray();

        // Every n-gram of MinRun words -> where it starts.
        var grams = new Dictionary<ulong, List<(int Block, int Word)>>();
        for (var b = 0; b < words.Length; b++)
        {
            for (var i = 0; i + MinRun <= words[b].Length; i++)
            {
                var key = Hash(words[b], i);
                if (!grams.TryGetValue(key, out var starts))
                {
                    grams[key] = starts = [];
                }

                starts.Add((b, i));
            }
        }

        var found = new Dictionary<(int A, int B), List<SharedRun>>();
        foreach (var starts in grams.Values)
        {
            for (var p = 0; p < starts.Count; p++)
            {
                for (var q = p + 1; q < starts.Count; q++)
                {
                    var (a, i) = starts[p];
                    var (b, j) = starts[q];
                    if (a == b || !IsRunStart(words[a], i, words[b], j))
                    {
                        continue;
                    }

                    var length = RunLength(words[a], i, words[b], j);
                    if (length < MinRun)
                    {
                        continue; // hash collision
                    }

                    if (!found.TryGetValue((a, b), out var runs))
                    {
                        found[(a, b)] = runs = [];
                    }

                    runs.Add(new SharedRun(i, j, length));
                }
            }
        }

        return
        [
            .. found.OrderBy(entry => entry.Key.A).ThenBy(entry => entry.Key.B)
                .Select(entry => new LiteralPair(blocks[entry.Key.A].Key, blocks[entry.Key.B].Key, WithoutOverlap(entry.Value))),
        ];
    }

    private static ulong Hash(string[] words, int start)
    {
        var hash = 14695981039346656037UL;
        for (var k = 0; k < MinRun; k++)
        {
            hash = (hash ^ (uint)words[start + k].GetHashCode()) * 1099511628211UL;
        }

        return hash;
    }

    /// <summary>The run through (i, j) begins here: the words before it differ or do not exist.</summary>
    private static bool IsRunStart(string[] a, int i, string[] b, int j) =>
        i == 0 || j == 0 || a[i - 1] != b[j - 1];

    private static int RunLength(string[] a, int i, string[] b, int j)
    {
        var length = 0;
        while (i + length < a.Length && j + length < b.Length && a[i + length] == b[j + length])
        {
            length++;
        }

        return length;
    }

    /// <summary>
    /// Runs on different diagonals can overlap when a phrase repeats inside a block; the longer run wins, a run that
    /// overlaps one already kept (in A or in B) is dropped.
    /// </summary>
    private static IReadOnlyList<SharedRun> WithoutOverlap(List<SharedRun> runs)
    {
        var kept = new List<SharedRun>();
        foreach (var run in runs.OrderByDescending(r => r.Words).ThenBy(r => r.StartA).ThenBy(r => r.StartB))
        {
            if (!kept.Any(k => Overlap(k.StartA, k.Words, run.StartA, run.Words) || Overlap(k.StartB, k.Words, run.StartB, run.Words)))
            {
                kept.Add(run);
            }
        }

        return [.. kept.OrderBy(r => r.StartA).ThenBy(r => r.StartB)];
    }

    private static bool Overlap(int start1, int length1, int start2, int length2) =>
        start1 < start2 + length2 && start2 < start1 + length1;
}
