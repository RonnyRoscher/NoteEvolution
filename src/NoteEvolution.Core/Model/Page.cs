using NoteEvolution.Core.Format;

namespace NoteEvolution.Core.Model;

/// <summary>A parsed Logseq Markdown file: the lines before the first bullet plus a tree of blocks.</summary>
public sealed class Page
{
    private readonly List<RawLine> _prefixLines;
    private readonly List<Block> _roots = [];
    private IReadOnlyList<BlockProperty>? _pageProperties;

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

    public bool IsDirty => AllBlocks().Any(b => b.IsDirty);

    /// <summary>Value of the first page property named <paramref name="key"/> (case-insensitive), or <c>null</c>.</summary>
    public string? GetPageProperty(string key) => Block.FindProperty(PageProperties, key);

    /// <summary>All blocks, depth-first in file order.</summary>
    public IEnumerable<Block> AllBlocks()
    {
        var stack = new Stack<Block>(_roots.AsEnumerable().Reverse());
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

    internal void AddRoot(Block block)
    {
        block.Parent = null;
        _roots.Add(block);
    }

    private static string NameFromPath(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        return fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? fileName[..^3] : fileName;
    }
}
