using NoteEvolution.Core.Format;

namespace NoteEvolution.Core.Model;

/// <summary>A parsed Logseq Markdown file: the lines before the first bullet plus a tree of blocks.</summary>
public sealed class Page
{
    private readonly List<RawLine> _prefixLines;
    private readonly List<Block> _roots = [];
    private IReadOnlyList<BlockProperty>? _pageProperties;

    /// <summary>Changes not recorded on a remaining block: removed blocks, changed prefix lines.</summary>
    private bool _structureChanged;

    private SavedState _saved = new([], []);

    /// <summary>File bytes accepted as the saved state's bytes (see <see cref="AcceptDiskAsBase"/>); reset by <see cref="MarkSaved"/>.</summary>
    private byte[]? _acceptedDiskBytes;

    internal Page(string filePath, bool hasBom, string newLine, IEnumerable<RawLine> prefixLines)
    {
        FilePath = filePath;
        Name = NameFromPath(filePath);
        HasBom = hasBom;
        NewLine = newLine;
        _prefixLines = [.. prefixLines];
    }

    public string FilePath { get; }

    /// <summary>The file name without <c>.md</c>.</summary>
    public string Name { get; }

    public bool HasBom { get; }

    /// <summary>The file's dominant line ending, used for new lines.</summary>
    public string NewLine { get; }

    /// <summary>All lines before the first bullet (page properties, other text, blank lines).</summary>
    public IReadOnlyList<RawLine> PrefixLines => _prefixLines;

    /// <summary>The <c>key:: value</c> lines among <see cref="PrefixLines"/>.</summary>
    public IReadOnlyList<BlockProperty> PageProperties => _pageProperties ??=
        [.. _prefixLines.Select(l => LogseqSyntax.TryParseProperty(l.Text)).OfType<BlockProperty>()];

    public IReadOnlyList<Block> Roots => _roots;

    /// <summary>The file could not be parsed safely; it is shown but never written.</summary>
    public bool IsReadOnly => ParseError is not null;

    /// <summary><c>"Zeile {n}: {grund}"</c> if parsing failed, otherwise <c>null</c>.</summary>
    public string? ParseError { get; internal set; }

    /// <summary>The page was changed since it was loaded or last saved.</summary>
    public bool IsDirty => _structureChanged || AllBlocks().Any(b => b.IsDirty);

    /// <summary>Value of the first page property named <paramref name="key"/> (case-insensitive), or <c>null</c>.</summary>
    public string? GetPageProperty(string key) => Block.FindProperty(PageProperties, key);

    /// <summary>All blocks, depth-first in file order.</summary>
    public IEnumerable<Block> AllBlocks() => DepthFirst(_roots);

    /// <summary>
    /// Inserts a detached block (from <see cref="Block.CreateDetached"/>, <see cref="Block.CloneDetached"/> or
    /// <see cref="RemoveBlock"/>) with its subtree at <paramref name="index"/> among the children of
    /// <paramref name="parent"/> (<c>null</c> = root level). Its lines are rendered in the target's style
    /// (<see cref="IndentStyle"/>) and get <see cref="NewLine"/>; all other lines stay as they are.
    /// </summary>
    public void InsertBlock(Block? parent, int index, Block detached)
    {
        if (detached.Page is not null || detached.Parent is not null)
        {
            throw new ArgumentException("Only a detached block can be inserted.", nameof(detached));
        }

        if (parent is not null)
        {
            EnsureOwned(parent, nameof(parent));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, ChildrenOf(parent).Count);

        Attach(parent, index, detached);
        RepairEndings();
    }

    /// <summary>Removes the block with its subtree; it is detached afterwards and can be inserted again.</summary>
    public void RemoveBlock(Block block)
    {
        EnsureOwned(block, nameof(block));
        Detach(block);
        block.SetPage(null);
        _structureChanged = true;
    }

    /// <summary>
    /// Moves the block with its subtree to <paramref name="index"/> among the children of
    /// <paramref name="newParent"/> (<c>null</c> = root level), counted without the block itself.
    /// The subtree is re-indented: in every line the block's old <see cref="Block.Indent"/> prefix is
    /// replaced by the new one (<see cref="IndentStyle.ChildIndent"/>).
    /// </summary>
    public void MoveBlock(Block block, Block? newParent, int index)
    {
        EnsureOwned(block, nameof(block));
        if (newParent is not null)
        {
            EnsureOwned(newParent, nameof(newParent));
        }

        for (var ancestor = newParent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor == block)
            {
                throw new ArgumentException("A block cannot be moved into its own subtree.", nameof(newParent));
            }
        }

        var siblingCount = ChildrenOf(newParent).Count - (block.Parent == newParent ? 1 : 0);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, siblingCount);

        var oldIndent = block.Indent;
        Detach(block);
        var newIndent = IndentStyle.ChildIndent(newParent);
        InsertAt(newParent, index, block);
        foreach (var moved in DepthFirst([block]))
        {
            moved.Reindent(oldIndent, newIndent);
        }

        RepairEndings();
    }

    /// <summary>
    /// Replaces the first page property line named <paramref name="key"/> (case-insensitive) in place;
    /// otherwise inserts <c>key:: value</c> after the last page property, or as the first line followed by a blank line.
    /// </summary>
    /// <exception cref="ArgumentException">Invalid key, or a value spanning several lines.</exception>
    public void SetPageProperty(string key, string value)
    {
        var text = LogseqSyntax.FormatProperty("", key, value);
        var last = -1;
        for (var index = 0; index < _prefixLines.Count; index++)
        {
            var property = LogseqSyntax.TryParseProperty(_prefixLines[index].Text);
            if (property is null)
            {
                continue;
            }

            if (string.Equals(property.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                if (LogseqSyntax.IsSame(property, key, value))
                {
                    return;
                }

                _prefixLines[index] = _prefixLines[index] with { Text = text };
                PrefixChanged();
                return;
            }

            last = index;
        }

        if (last >= 0)
        {
            _prefixLines.Insert(last + 1, new RawLine(text, NewLine));
        }
        else
        {
            _prefixLines.InsertRange(0, [new RawLine(text, NewLine), new RawLine("", NewLine)]);
        }

        PrefixChanged();
        RepairEndings();
    }

    /// <summary>Records the current state as saved: <c>BaseLines = Lines</c> and not dirty, for every block.</summary>
    public void MarkSaved()
    {
        foreach (var block in AllBlocks())
        {
            block.MarkSaved();
        }

        _structureChanged = false;
        _saved = CaptureSaved();
        _acceptedDiskBytes = null;
    }

    /// <summary>
    /// The page as it was last loaded or saved: its prefix lines and its blocks with their places then. The blocks
    /// are the instances themselves, so their <see cref="Block.BaseLines"/> are the saved lines; one that is no
    /// longer on this page (<c>Block.Page != this</c>) was removed since, one on the page but not in this list is new.
    /// Merging external changes (<see cref="Storage.PageMerger"/>) relies on it, because removals leave no trace
    /// on the remaining blocks.
    /// </summary>
    internal SavedState Saved => _saved;

    /// <summary>
    /// The file's bytes as they were when the page was last loaded or saved, or the bytes accepted with
    /// <see cref="AcceptDiskAsBase"/> since then.
    /// </summary>
    internal byte[] SavedBytes() => _acceptedDiskBytes
        ?? TextDocument.Encode(HasBom, _saved.PrefixLines.Concat(_saved.Blocks.SelectMany(b => b.Block.BaseLines)));

    /// <summary>
    /// Treats <paramref name="diskBytes"/> (the file's current content, e.g. an unreadable external version the user
    /// chose to replace) as what the page was loaded from, so that the next save may overwrite it. The block-level
    /// saved state (used for merging) is unchanged; <see cref="MarkSaved"/> ends this.
    /// </summary>
    internal void AcceptDiskAsBase(byte[] diskBytes) => _acceptedDiskBytes = diskBytes;

    /// <summary>Replaces all prefix lines (merging external changes); only the last line of the file may lack an ending.</summary>
    internal void ReplacePrefixLines(IEnumerable<RawLine> lines)
    {
        _prefixLines.Clear();
        _prefixLines.AddRange(lines);
        PrefixChanged();
        RepairEndings();
    }

    internal void AddRoot(Block block) => InsertAt(null, _roots.Count, block);

    /// <summary>
    /// Puts a block taken out with <see cref="RemoveBlock"/> back at <paramref name="index"/> (clamped to the
    /// children of <paramref name="parent"/>) with its lines unchanged, so that undo restores the file exactly.
    /// </summary>
    internal void RestoreBlock(Block? parent, int index, Block removed)
    {
        if (removed.Page is not null || removed.Parent is not null)
        {
            throw new ArgumentException("Only a detached block can be restored.", nameof(removed));
        }

        if (parent is not null)
        {
            EnsureOwned(parent, nameof(parent));
        }

        InsertAt(parent, Math.Clamp(index, 0, ChildrenOf(parent).Count), removed);
        _structureChanged = true;
        RepairEndings();
    }

    /// <summary>
    /// Rebuilds the whole block tree from <paramref name="blocks"/> (each block with its parent, depth-first in file
    /// order), so that undo of structure changes restores the file exactly: blocks not listed are dropped, the listed
    /// ones (also removed ones) get their former places and keep their lines as they are.
    /// </summary>
    internal void RestoreTree(IReadOnlyList<(Block Block, Block? Parent)> blocks)
    {
        foreach (var block in AllBlocks().ToList().Concat(blocks.Select(b => b.Block)))
        {
            block.TakeChildren();
            block.Parent = null;
            block.SetPage(null);
        }

        _roots.Clear();
        foreach (var (block, parent) in blocks)
        {
            InsertAt(parent, ChildrenOf(parent).Count, block);
        }

        _structureChanged = true;
        RepairEndings();
    }

    private SavedState CaptureSaved()
    {
        var blocks = new List<SavedBlock>();
        void Add(IReadOnlyList<Block> siblings, SavedBlock? parent)
        {
            for (var index = 0; index < siblings.Count; index++)
            {
                var saved = new SavedBlock(siblings[index], parent, [.. parent?.Path ?? [], index]);
                blocks.Add(saved);
                Add(siblings[index].Children, saved);
            }
        }

        Add(_roots, null);
        return new SavedState([.. _prefixLines], blocks);
    }

    private void PrefixChanged()
    {
        _pageProperties = null;
        _structureChanged = true;
    }

    /// <summary>Renders <paramref name="block"/> for its new place, inserts it, then its former children below it.</summary>
    private void Attach(Block? parent, int index, Block block)
    {
        var children = block.TakeChildren();
        block.Render(
            IndentStyle.ChildIndent(parent),
            IndentStyle.BulletFor(ChildrenOf(parent), parent, index),
            NewLine);
        InsertAt(parent, index, block);
        for (var i = 0; i < children.Count; i++)
        {
            Attach(block, i, children[i]);
        }
    }

    private void InsertAt(Block? parent, int index, Block block)
    {
        if (parent is null)
        {
            block.Parent = null;
            block.SetPage(this);
            _roots.Insert(index, block);
        }
        else
        {
            parent.InsertChild(index, block);
        }
    }

    private void Detach(Block block)
    {
        if (block.Parent is null)
        {
            _roots.Remove(block);
        }
        else
        {
            block.Parent.RemoveChild(block);
            block.Parent = null;
        }
    }

    private IReadOnlyList<Block> ChildrenOf(Block? parent) => parent?.Children ?? _roots;

    private void EnsureOwned(Block block, string paramName)
    {
        if (block.Page != this)
        {
            throw new ArgumentException("The block does not belong to this page.", paramName);
        }
    }

    /// <summary>Only the last line of the file may lack a line ending; any other such line gets <see cref="NewLine"/>.</summary>
    private void RepairEndings()
    {
        // (owner, index) of every line in file order; owner null = prefix line.
        var lines = Enumerable.Range(0, _prefixLines.Count).Select(i => ((Block?)null, i))
            .Concat(AllBlocks().SelectMany(b => Enumerable.Range(0, b.Lines.Count).Select(i => ((Block?)b, i))))
            .ToList();
        foreach (var (block, index) in lines.SkipLast(1))
        {
            if (block is null && _prefixLines[index].Ending.Length == 0)
            {
                _prefixLines[index] = _prefixLines[index] with { Ending = NewLine };
                _structureChanged = true;
            }
            else if (block is not null && block.Lines[index].Ending.Length == 0)
            {
                block.SetEnding(index, NewLine);
            }
        }
    }

    private static IEnumerable<Block> DepthFirst(IEnumerable<Block> roots)
    {
        var stack = new Stack<Block>(roots.Reverse());
        while (stack.Count > 0)
        {
            var block = stack.Pop();
            yield return block;
            for (var i = block.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(block.Children[i]);
            }
        }
    }

    private static string NameFromPath(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        return fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? fileName[..^3] : fileName;
    }

    /// <summary>See <see cref="Saved"/>.</summary>
    /// <param name="PrefixLines">The prefix lines then.</param>
    /// <param name="Blocks">All blocks then, depth-first in file order.</param>
    internal sealed record SavedState(IReadOnlyList<RawLine> PrefixLines, IReadOnlyList<SavedBlock> Blocks);

    /// <summary>A block of <see cref="SavedState"/> with its parent then and its tree path then (child indices from the roots).</summary>
    internal sealed record SavedBlock(Block Block, SavedBlock? Parent, IReadOnlyList<int> Path);
}
