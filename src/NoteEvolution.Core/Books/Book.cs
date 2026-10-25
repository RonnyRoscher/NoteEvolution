using System.Text.RegularExpressions;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Text;

namespace NoteEvolution.Core.Books;

/// <summary>
/// A read-only view over a parsed book page (<c>type:: book</c>): headings form the outline, other blocks are
/// text blocks. Loading never changes the page; after edits the view is rebuilt with <see cref="Load"/>.
/// </summary>
public sealed partial class Book
{
    private readonly Dictionary<Guid, OutlineNode> _nodes = [];
    private readonly Dictionary<Guid, TextBlock> _textBlocks = [];
    private readonly Dictionary<Guid, TextBlock> _textBlocksById = [];
    private readonly List<OutlineWarning> _warnings = [];

    /// <summary>Keys of every block in an appendix section's subtree, the heading included.</summary>
    private readonly HashSet<Guid> _appendixKeys = [];

    [GeneratedRegex(@"^(?<hashes>#{1,6}) (?<title>.*)$")]
    private static partial Regex HeadingRegex();

    private Book(Page page)
    {
        Page = page;
        Title = NonBlank(page.GetPageProperty("title")) ?? page.Name;
        Dedication = NonBlank(page.GetPageProperty("dedication"));
        Root = new OutlineNode(null, Title, 0, null);
        _nodes[Guid.Empty] = Root;
        AddItems(Root, page.Roots);
    }

    public Page Page { get; }

    /// <summary>The name pages use to link to the book: the file name without <c>.md</c>.</summary>
    public string LinkName => Page.Name;

    /// <summary>The <c>title::</c> property, or the page name.</summary>
    public string Title { get; }

    public string? Dedication { get; }

    /// <summary>Invisible level-0 node; its items are the prologue and the top-level headings.</summary>
    public OutlineNode Root { get; }

    public IReadOnlyList<OutlineWarning> Warnings => _warnings;

    /// <summary>The top-level headings of the book's text: the root's children without the appendix sections.</summary>
    public IEnumerable<OutlineNode> ContentChildren => Root.Children.Where(node => node.AppendixKind == AppendixKind.None);

    /// <summary>Words of the book's text, without the appendix (the same as <c>Root.WordCount</c>).</summary>
    public int ContentWordCount => Root.WordCount;

    /// <summary>
    /// The appendix sections after the first of their kind, in file order: they count as appendix (not as text), but
    /// only the first one of a kind is used.
    /// </summary>
    public IReadOnlyList<OutlineNode> DuplicateAppendixSections =>
        [.. Root.Children.Where(node => node.AppendixKind != AppendixKind.None && AppendixSection(node.AppendixKind) != node)];

    public static bool IsBook(Page page) =>
        string.Equals(page.GetPageProperty("type")?.Trim(), "book", StringComparison.OrdinalIgnoreCase);

    public static Book Load(Page page) => new(page);

    public OutlineNode? FindNode(Guid key) => _nodes.GetValueOrDefault(key);

    /// <summary>The first appendix section of <paramref name="kind"/>; <c>null</c> if the book has none.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is <see cref="AppendixKind.None"/>.</exception>
    public OutlineNode? AppendixSection(AppendixKind kind)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(kind, AppendixKind.None);
        return Root.Children.FirstOrDefault(node => node.AppendixKind == kind);
    }

    /// <summary>Whether the block <paramref name="key"/> is an appendix section's heading or lies below one.</summary>
    internal bool IsInAppendix(Guid key) => _appendixKeys.Contains(key);

    /// <summary>
    /// <paramref name="rootIndex"/> (an index among the page's root blocks for a new or moved top-level heading),
    /// but at most the index of the first appendix section: the book's text stays before the appendix.
    /// </summary>
    internal int BeforeAppendix(int rootIndex)
    {
        var roots = Page.Roots;
        for (var i = 0; i < Math.Min(rootIndex, roots.Count); i++)
        {
            if (IsInAppendix(roots[i].Key))
            {
                return i;
            }
        }

        return rootIndex;
    }

    public TextBlock? FindTextBlock(Guid key) => _textBlocks.GetValueOrDefault(key);

    /// <summary>Finds a text block by its <c>id::</c> property.</summary>
    public TextBlock? FindTextBlockById(Guid id) => _textBlocksById.GetValueOrDefault(id);

    private void AddItems(OutlineNode section, IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            if (TryParseHeading(block, out var level, out var title))
            {
                if (section.Level > 0 && level <= section.Level)
                {
                    _warnings.Add(new OutlineWarning(
                        block.Key, $"Überschrift ‚{title}‘ ist nicht tiefer als ‚{section.Title}‘"));
                }

                var kind = section == Root ? AppendixKinds.Parse(block.GetProperty(AppendixKinds.Key)) : AppendixKind.None;
                var node = new OutlineNode(block, title, level, section, kind);
                _nodes[node.Key] = node;
                if (kind != AppendixKind.None)
                {
                    AddAppendixKeys(block);
                }

                section.Add(node);
                AddItems(node, block.Children);
            }
            else
            {
                var paragraphs = new List<Paragraph>();
                AddParagraphs(paragraphs, block.Children, 1);
                var textBlock = new TextBlock(block, section, paragraphs);
                _textBlocks[textBlock.Key] = textBlock;
                if (block.Id is { } id)
                {
                    _textBlocksById.TryAdd(id, textBlock);
                }

                section.Add(textBlock);
            }
        }
    }

    private void AddAppendixKeys(Block block)
    {
        _appendixKeys.Add(block.Key);
        foreach (var child in block.Children)
        {
            AddAppendixKeys(child);
        }
    }

    private void AddParagraphs(List<Paragraph> paragraphs, IEnumerable<Block> blocks, int depth)
    {
        foreach (var block in blocks)
        {
            if (TryParseHeading(block, out _, out var title))
            {
                _warnings.Add(new OutlineWarning(block.Key, $"Überschrift ‚{title}‘ innerhalb eines Textblocks"));
            }

            paragraphs.Add(new Paragraph(
                block, NoteTag.Strip(block.Content), depth, NoteTag.Has(block.Content), BlockStatuses.Of(block)));
            AddParagraphs(paragraphs, block.Children, depth + 1);
        }
    }

    private static bool TryParseHeading(Block block, out int level, out string title)
    {
        var firstLine = block.Content.Split('\n', 2)[0];
        var match = HeadingRegex().Match(firstLine);
        level = match.Success ? match.Groups["hashes"].Length : 0;
        title = match.Success ? match.Groups["title"].Value.Trim() : "";
        return match.Success;
    }

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
