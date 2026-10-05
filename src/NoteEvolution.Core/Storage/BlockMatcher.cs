using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Storage;

/// <summary>Pairs the blocks of an earlier state of a page with the blocks of a newer version of the same file.</summary>
internal static class BlockMatcher
{
    /// <summary>Compares blocks by instance.</summary>
    public static readonly IEqualityComparer<Block> ByReference = ReferenceEqualityComparer.Instance;

    /// <summary>A block of the earlier state: the lines and the tree path it had then.</summary>
    internal sealed record Source(Block Block, IReadOnlyList<RawLine> Lines, IReadOnlyList<int> Path);

    /// <summary>
    /// Each target block is paired with at most one source, in this order: same <c>id::</c>; else same tree path and
    /// same text; else same text anywhere (the first free one in file order, so a block shifted by an insertion
    /// above it is still found); with <paramref name="byPathAlone"/> finally the block at the same tree path, which
    /// then is the same block changed. Blocks with two different ids are never paired.
    /// </summary>
    /// <returns>Source block → target block.</returns>
    public static Dictionary<Block, Block> Match(IReadOnlyList<Source> sources, Page target, bool byPathAlone)
    {
        var pairs = new Dictionary<Block, Block>(ByReference);
        var taken = new HashSet<Block>(ByReference);
        var targets = WithPaths(target).ToList();
        var sourceIds = sources.ToDictionary(s => s.Block, s => IdOf(s.Lines), ByReference);
        var sourceTexts = sources.ToDictionary(s => s.Block, s => TextKey(s.Lines), ByReference);
        var targetTexts = targets.ToDictionary(t => t.Block, t => TextKey(t.Block.Lines), ByReference);
        var byPath = targets.ToDictionary(t => PathKey(t.Path), t => t.Block);

        bool TryPair(Source source, Block? candidate)
        {
            if (candidate is null || taken.Contains(candidate)
                || sourceIds[source.Block] is { } id && candidate.Id is { } other && id != other)
            {
                return false;
            }

            pairs[source.Block] = candidate;
            taken.Add(candidate);
            return true;
        }

        var byId = new Dictionary<Guid, Block>();
        foreach (var (block, _) in targets)
        {
            if (block.Id is { } id)
            {
                byId.TryAdd(id, block);
            }
        }

        foreach (var source in sources)
        {
            if (sourceIds[source.Block] is { } id)
            {
                TryPair(source, byId.GetValueOrDefault(id));
            }
        }

        foreach (var source in Unpaired())
        {
            var candidate = byPath.GetValueOrDefault(PathKey(source.Path));
            if (candidate is not null && targetTexts[candidate] == sourceTexts[source.Block])
            {
                TryPair(source, candidate);
            }
        }

        var byText = targets.Where(t => !taken.Contains(t.Block))
            .GroupBy(t => targetTexts[t.Block])
            .ToDictionary(g => g.Key, g => new Queue<Block>(g.Select(t => t.Block)));
        foreach (var source in Unpaired())
        {
            if (byText.TryGetValue(sourceTexts[source.Block], out var queue))
            {
                while (queue.TryDequeue(out var candidate) && !TryPair(source, candidate))
                {
                }
            }
        }

        if (byPathAlone)
        {
            foreach (var source in Unpaired())
            {
                TryPair(source, byPath.GetValueOrDefault(PathKey(source.Path)));
            }
        }

        return pairs;

        IEnumerable<Source> Unpaired() => sources.Where(s => !pairs.ContainsKey(s.Block)).ToList();
    }

    /// <summary>
    /// The two line lists hold the same block text: equal line texts once the block's own indentation is removed,
    /// ignoring line endings and trailing blank lines.
    /// </summary>
    public static bool SameText(IReadOnlyList<RawLine> a, IReadOnlyList<RawLine> b) => TextKey(a) == TextKey(b);

    /// <summary>All blocks of <paramref name="page"/> depth-first with their tree paths (child indices from the roots).</summary>
    public static IEnumerable<(Block Block, IReadOnlyList<int> Path)> WithPaths(Page page)
    {
        var stack = new Stack<(Block Block, IReadOnlyList<int> Path)>();
        for (var i = page.Roots.Count - 1; i >= 0; i--)
        {
            stack.Push((page.Roots[i], [i]));
        }

        while (stack.Count > 0)
        {
            var (block, path) = stack.Pop();
            yield return (block, path);
            for (var i = block.Children.Count - 1; i >= 0; i--)
            {
                stack.Push((block.Children[i], [.. path, i]));
            }
        }
    }

    private static Guid? IdOf(IReadOnlyList<RawLine> lines) => lines.Count == 0 ? null : new Block(lines, 0).Id;

    private static string PathKey(IReadOnlyList<int> path) => string.Join('/', path);

    private static string TextKey(IReadOnlyList<RawLine> lines)
    {
        if (lines.Count == 0)
        {
            return "";
        }

        LogseqSyntax.TryParseBullet(lines[0].Text, out var indent, out _, out _);
        var texts = lines.Select(l => l.Text.StartsWith(indent, StringComparison.Ordinal) ? l.Text[indent.Length..] : l.Text).ToList();
        while (texts.Count > 1 && string.IsNullOrWhiteSpace(texts[^1]))
        {
            texts.RemoveAt(texts.Count - 1);
        }

        return string.Join('\n', texts);
    }
}
