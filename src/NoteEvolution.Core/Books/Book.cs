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

    public static bool IsBook(Page page) =>
        string.Equals(page.GetPageProperty("type")?.Trim(), "book", StringComparison.OrdinalIgnoreCase);

    public static Book Load(Page page) => new(page);

    public OutlineNode? FindNode(Guid key) => _nodes.GetValueOrDefault(key);

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

                var node = new OutlineNode(block, title, level, section);
                _nodes[node.Key] = node;
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

    private void AddParagraphs(List<Paragraph> paragraphs, IEnumerable<Block> blocks, int depth)
    {
        foreach (var block in blocks)
        {
            if (TryParseHeading(block, out _, out var title))
            {
                _warnings.Add(new OutlineWarning(block.Key, $"Überschrift ‚{title}‘ innerhalb eines Textblocks"));
            }

            paragraphs.Add(new Paragraph(block, NoteTag.Strip(block.Content), depth, NoteTag.Has(block.Content)));
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
