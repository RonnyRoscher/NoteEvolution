using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Storage;

/// <summary>How the user resolves a <see cref="BlockConflict"/>.</summary>
public enum ConflictChoice
{
    /// <summary>The local version (for a locally removed block: it stays removed).</summary>
    Mine,

    /// <summary>The external version (for a block deleted externally: it stays deleted).</summary>
    Theirs,

    /// <summary>The local version, and the external version as an extra sibling block directly after it.</summary>
    Both,
}

/// <summary>Re-applies unsaved local changes of a page onto an external version of its file.</summary>
public static class PageMerger
{
    /// <summary>
    /// Builds a new page from <paramref name="external"/> (which itself stays unchanged) and re-applies every local
    /// change since <paramref name="local"/> was last saved (see <see cref="Page.MarkSaved"/>):
    /// <list type="bullet">
    /// <item>changed texts, where the external block is unchanged;</item>
    /// <item>removed blocks, where the external block is unchanged (its external children that are not removed
    /// as well move up to its place);</item>
    /// <item>new blocks, placed directly after the counterpart of their local predecessor, else first under the
    /// counterpart of their parent, else where the parent would be;</item>
    /// <item>moved blocks (other parent or other order among their saved siblings), placed the same way;</item>
    /// <item>changed prefix lines: if both sides changed them, the external lines plus the local added ones.</item>
    /// </list>
    /// Conflicts (<see cref="ConflictDetector.Detect"/>) are resolved by <paramref name="choices"/>, keyed by
    /// <c>BlockConflict.Local.Key</c>. For <see cref="ConflictChoice.Both"/> the extra external block gets no
    /// <c>id::</c>, so that ids stay unique. A block that exists on both sides keeps its local <see cref="Block.Key"/>.
    /// <para>
    /// The result's saved state is the external file: it is dirty exactly where it differs from it, and the caller
    /// saves it.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">A conflict has no entry in <paramref name="choices"/>.</exception>
    /// <exception cref="ReadOnlyPageException"><paramref name="external"/> is read-only (could not be parsed safely).</exception>
    public static Page Merge(Page local, Page external, IReadOnlyDictionary<Guid, ConflictChoice> choices)
    {
        if (external.IsReadOnly)
        {
            throw new ReadOnlyPageException($"Die Datei '{external.FilePath}' ist schreibgeschützt: {external.ParseError}");
        }

        var entries = ConflictDetector.Compare(local, external);
        if (entries.FirstOrDefault(e => e.IsConflict && !choices.ContainsKey(e.Local.Key)) is { } open)
        {
            throw new ArgumentException($"No choice for the conflict of block {open.Local.Key}.", nameof(choices));
        }

        var merged = LogseqParser.Parse(external.FilePath, PageSerializer.Serialize(external));
        var copies = external.AllBlocks().Zip(merged.AllBlocks())
            .ToDictionary(p => p.First, p => p.Second, BlockMatcher.ByReference);
        new Run(local, merged, entries, choices, copies).Execute();
        return merged;
    }

    private sealed class Run(
        Page local,
        Page merged,
        IReadOnlyList<ConflictDetector.SavedEntry> entries,
        IReadOnlyDictionary<Guid, ConflictChoice> choices,
        Dictionary<Block, Block> copies)
    {
        private static readonly object RootGroup = new();

        /// <summary>Local block → its block in <c>merged</c>.</summary>
        private readonly Dictionary<Block, Block> _counterparts = new(BlockMatcher.ByReference);

        private readonly Dictionary<Block, ConflictDetector.SavedEntry> _entries =
            entries.ToDictionary(e => e.Local, BlockMatcher.ByReference);

        /// <summary>The saved blocks grouped by their saved parent (<see cref="RootGroup"/> for roots), in saved order.</summary>
        private readonly Dictionary<object, List<Page.SavedBlock>> _savedSiblings = entries
            .GroupBy(e => e.Saved.Parent?.Block ?? RootGroup, ReferenceEqualityComparer.Instance)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Saved).ToList(), ReferenceEqualityComparer.Instance);

        public void Execute()
        {
            var toRemove = new HashSet<Block>(BlockMatcher.ByReference);
            var restore = new HashSet<Block>(BlockMatcher.ByReference);
            var extraCopies = new List<(Block Block, Block Extra)>();

            foreach (var entry in entries)
            {
                var choice = entry.IsConflict ? choices[entry.Local.Key] : (ConflictChoice?)null;
                var target = entry.External is null ? null : copies[entry.External];
                if (!entry.Present)
                {
                    if (target is not null && (!entry.ExternalChanged || choice == ConflictChoice.Mine))
                    {
                        toRemove.Add(target);
                    }

                    continue;
                }

                if (target is null)
                {
                    if (choice is ConflictChoice.Mine or ConflictChoice.Both)
                    {
                        restore.Add(entry.Local);
                    }

                    continue;
                }

                _counterparts[entry.Local] = target;
                target.AssignKey(entry.Local.Key);
                var takeLocal = entry.IsConflict ? choice != ConflictChoice.Theirs : entry.LocalChanged && !entry.ExternalChanged;
                if (choice == ConflictChoice.Both)
                {
                    var extra = Block.CreateDetached(target.Lines, Guid.NewGuid());
                    extra.RemoveProperty("id");
                    extraCopies.Add((target, extra));
                }

                if (takeLocal)
                {
                    SetLines(target, entry.Local.Lines);
                }
            }

            foreach (var block in merged.AllBlocks().Reverse().Where(toRemove.Contains).ToList())
            {
                Remove(block);
            }

            foreach (var block in local.AllBlocks())
            {
                if (_counterparts.TryGetValue(block, out var target))
                {
                    if (IsMoved(block))
                    {
                        MoveTo(target, Anchor(block));
                    }
                }
                else if (!_entries.ContainsKey(block) || restore.Contains(block))
                {
                    var copy = Block.CreateDetached(block.Lines, block.Key);
                    var (parent, index) = Anchor(block);
                    merged.InsertBlock(parent, index, copy);
                    _counterparts[block] = copy;
                }
            }

            foreach (var (block, extra) in extraCopies)
            {
                merged.InsertBlock(block.Parent, IndexOf(block) + 1, extra);
            }

            MergePrefix();
        }

        /// <summary>Gives <paramref name="target"/> the local lines, rendered for its own place.</summary>
        private void SetLines(Block target, IReadOnlyList<RawLine> lines)
        {
            var (indent, bullet) = (target.Indent, target.Bullet);
            target.RestoreLines(lines, isDirty: true);
            target.Render(indent, bullet, merged.NewLine);
        }

        /// <summary>Removes the block; its remaining children move up to its place.</summary>
        private void Remove(Block block)
        {
            var index = IndexOf(block);
            var children = block.Children.ToList();
            for (var i = 0; i < children.Count; i++)
            {
                merged.MoveBlock(children[i], block.Parent, index + 1 + i);
            }

            merged.RemoveBlock(block);
        }

        /// <summary>
        /// The block now has another parent than when saved, or another nearest preceding sibling among the saved
        /// siblings that are still under that parent.
        /// </summary>
        private bool IsMoved(Block block)
        {
            var saved = _entries[block].Saved;
            var savedParent = saved.Parent?.Block;
            if (!ReferenceEquals(block.Parent, savedParent))
            {
                return true;
            }

            Block? localPrevious = null;
            var siblings = SiblingsOf(local, block);
            for (var i = IndexIn(siblings, block) - 1; i >= 0 && localPrevious is null; i--)
            {
                if (_entries.TryGetValue(siblings[i], out var other) && ReferenceEquals(other.Saved.Parent?.Block, savedParent))
                {
                    localPrevious = siblings[i];
                }
            }

            Block? savedPrevious = null;
            var savedSiblings = _savedSiblings[savedParent ?? RootGroup];
            for (var i = savedSiblings.IndexOf(saved) - 1; i >= 0 && savedPrevious is null; i--)
            {
                var other = savedSiblings[i].Block;
                if (other.Page == local && ReferenceEquals(other.Parent, savedParent))
                {
                    savedPrevious = other;
                }
            }

            return !ReferenceEquals(localPrevious, savedPrevious);
        }

        /// <summary>
        /// Where the local block goes in <c>merged</c>: directly after the counterpart of its nearest preceding
        /// sibling that has one; else first under its parent's counterpart; else where its parent would go.
        /// </summary>
        private (Block? Parent, int Index) Anchor(Block block)
        {
            var siblings = SiblingsOf(local, block);
            for (var i = IndexIn(siblings, block) - 1; i >= 0; i--)
            {
                if (_counterparts.TryGetValue(siblings[i], out var previous))
                {
                    return (previous.Parent, IndexOf(previous) + 1);
                }
            }

            if (block.Parent is null)
            {
                return (null, 0);
            }

            return _counterparts.TryGetValue(block.Parent, out var parent) ? (parent, 0) : Anchor(block.Parent);
        }

        /// <summary>Moves <paramref name="block"/> to the place given by <see cref="Anchor"/>, if it is not there yet.</summary>
        private void MoveTo(Block block, (Block? Parent, int Index) place)
        {
            var (parent, index) = place;
            for (var ancestor = parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                if (ancestor == block)
                {
                    return;
                }
            }

            if (ReferenceEquals(block.Parent, parent))
            {
                var current = IndexOf(block);
                if (current == index || current == index - 1)
                {
                    return;
                }

                if (current < index)
                {
                    index--;
                }
            }

            merged.MoveBlock(block, parent, index);
        }

        /// <summary>
        /// Takes the local prefix lines if only they changed; if both sides changed them, keeps the external ones,
        /// drops the lines removed locally and adds the lines added locally after the last page property.
        /// </summary>
        private void MergePrefix()
        {
            var saved = local.Saved.PrefixLines;
            if (SameTexts(local.PrefixLines, saved))
            {
                return;
            }

            if (SameTexts(merged.PrefixLines, saved))
            {
                merged.ReplacePrefixLines(local.PrefixLines);
                return;
            }

            var savedTexts = saved.Select(l => l.Text).ToHashSet();
            var localTexts = local.PrefixLines.Select(l => l.Text).ToHashSet();
            var result = merged.PrefixLines.Where(l => !savedTexts.Contains(l.Text) || localTexts.Contains(l.Text)).ToList();
            var resultTexts = result.Select(l => l.Text).ToHashSet();
            var added = local.PrefixLines.Where(l => !savedTexts.Contains(l.Text) && !resultTexts.Contains(l.Text));
            var lastProperty = result.FindLastIndex(l => LogseqSyntax.TryParseProperty(l.Text) is not null);
            result.InsertRange(lastProperty + 1, added);
            merged.ReplacePrefixLines(result);
        }

        private int IndexOf(Block block) => IndexIn(SiblingsOf(merged, block), block);

        private static IReadOnlyList<Block> SiblingsOf(Page page, Block block) => block.Parent?.Children ?? page.Roots;

        private static int IndexIn(IReadOnlyList<Block> siblings, Block block)
        {
            for (var i = 0; i < siblings.Count; i++)
            {
                if (ReferenceEquals(siblings[i], block))
                {
                    return i;
                }
            }

            throw new InvalidOperationException("The block is not among its siblings.");
        }

        private static bool SameTexts(IReadOnlyList<RawLine> a, IReadOnlyList<RawLine> b) =>
            a.Select(l => l.Text).SequenceEqual(b.Select(l => l.Text));
    }
}
