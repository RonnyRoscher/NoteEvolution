using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Storage;

/// <summary>Pairs the blocks of an earlier state of a page with the blocks of a newer version of the same file.</summary>
internal static class BlockMatcher
{
    /// <summary>Compares blocks by instance.</summary>
    public static readonly IEqualityComparer<Block> ByReference = ReferenceEqualityComparer.Instance;

    /// <summary>Groups root blocks in place of a parent.</summary>
    private static readonly object RootGroup = new();

    /// <summary>A block of the earlier state: its parent, lines and tree path then.</summary>
    /// <param name="Block">The block instance.</param>
    /// <param name="Parent">Its parent then; <c>null</c> for a root block.</param>
    /// <param name="Lines">Its lines then.</param>
    /// <param name="Path">Its tree path then (child indices from the roots).</param>
    /// <param name="Strict">
    /// The block was changed or removed locally since then: it is never paired by its text found elsewhere (see
    /// <see cref="Match"/>), so that an external copy with the old text is not taken for it.
    /// </param>
    internal sealed record Source(Block Block, Block? Parent, IReadOnlyList<RawLine> Lines, IReadOnlyList<int> Path, bool Strict);

    /// <summary>
    /// Each target block is paired with at most one source, in this order:
    /// <list type="number">
    /// <item>same <c>id::</c>;</item>
    /// <item>same tree path and same text;</item>
    /// <item>non-strict sources: same text anywhere (the first free one in file order, so a block shifted by an
    /// insertion above it is still found);</item>
    /// <item>with <paramref name="byPosition"/>, every source still unpaired, in file order: by its place among its
    /// siblings. The source's parent must be paired with the target's parent. Its nearest paired siblings before and
    /// after (the gap's bounds; none = start/end) must be paired with the nearest paired target siblings around the
    /// gap, and both gaps must hold the same number of unpaired blocks; then the source gets the target at the same
    /// index within the gap. A gap of more than one block also needs at least one real bound. Otherwise the source
    /// stays unpaired (for the caller: its counterpart is missing), because pairing would be a guess.</item>
    /// </list>
    /// Blocks with two different ids are never paired.
    /// </summary>
    /// <returns>Source block → target block.</returns>
    public static Dictionary<Block, Block> Match(IReadOnlyList<Source> sources, Page target, bool byPosition)
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
        foreach (var source in Unpaired().Where(s => !s.Strict))
        {
            if (byText.TryGetValue(sourceTexts[source.Block], out var queue))
            {
                while (queue.TryDequeue(out var candidate) && !TryPair(source, candidate))
                {
                }
            }
        }

        if (byPosition)
        {
            var siblings = sources
                .GroupBy(s => (object?)s.Parent ?? RootGroup, ReferenceEqualityComparer.Instance)
                .ToDictionary(g => g.Key!, g => g.ToList(), ReferenceEqualityComparer.Instance);
            foreach (var source in Unpaired())
            {
                TryPair(source, ByPosition(source, siblings[(object?)source.Parent ?? RootGroup], target, pairs, taken));
            }
        }

        return pairs;

        IEnumerable<Source> Unpaired() => sources.Where(s => !pairs.ContainsKey(s.Block)).ToList();
    }

    /// <summary>Step 4 of <see cref="Match"/>: the target at the source's place, if that place is unambiguous.</summary>
    private static Block? ByPosition(
        Source source, List<Source> siblings, Page target, Dictionary<Block, Block> pairs, HashSet<Block> taken)
    {
        Block? targetParent = null;
        if (source.Parent is not null && !pairs.TryGetValue(source.Parent, out targetParent))
        {
            return null;
        }

        var index = siblings.IndexOf(source);
        var first = index;
        while (first > 0 && !pairs.ContainsKey(siblings[first - 1].Block))
        {
            first--;
        }

        var last = index;
        while (last < siblings.Count - 1 && !pairs.ContainsKey(siblings[last + 1].Block))
        {
            last++;
        }

        var before = first > 0 ? pairs[siblings[first - 1].Block] : null;
        var after = last < siblings.Count - 1 ? pairs[siblings[last + 1].Block] : null;
        var candidates = targetParent?.Children ?? target.Roots;
        var start = before is null ? 0 : IndexIn(candidates, before) + 1;
        var end = after is null ? candidates.Count : IndexIn(candidates, after);
        if (start == 0 && before is not null || end < 0 || end < start)
        {
            return null; // A bound was paired with a block under another parent, or the bounds are swapped.
        }

        var gap = candidates.Skip(start).Take(end - start).Where(b => !taken.Contains(b)).ToList();
        var count = last - first + 1;
        if (gap.Count != count || count > 1 && before is null && after is null)
        {
            return null;
        }

        return gap[index - first];
    }

    /// <summary>Index of <paramref name="block"/> in <paramref name="list"/> by instance; -1 if it is not there.</summary>
    private static int IndexIn(IReadOnlyList<Block> list, Block block)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], block))
            {
                return i;
            }
        }

        return -1;
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
