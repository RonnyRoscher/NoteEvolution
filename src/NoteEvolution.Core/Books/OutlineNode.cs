using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Books;

/// <summary>A heading block (or the invisible root) with the sub-sections and text blocks below it.</summary>
public sealed class OutlineNode : BookItem
{
    private readonly List<BookItem> _items = [];

    internal OutlineNode(Block? block, string title, int level, OutlineNode? parent)
    {
        Block = block;
        Title = title;
        Level = level;
        Parent = parent;
    }

    /// <summary><see cref="Model.Block.Key"/> of the heading; <see cref="Guid.Empty"/> for the root.</summary>
    public Guid Key => Block?.Key ?? Guid.Empty;

    /// <summary>The heading block; <c>null</c> for the root.</summary>
    public Block? Block { get; }

    public string Title { get; }

    /// <summary>Number of <c>#</c> of the heading; 0 for the root.</summary>
    public int Level { get; }

    public OutlineNode? Parent { get; }

    /// <summary>Sub-sections and text blocks in file order.</summary>
    public IReadOnlyList<BookItem> Items => _items;

    public IEnumerable<OutlineNode> Children => _items.OfType<OutlineNode>();

    public IEnumerable<TextBlock> TextBlocks => _items.OfType<TextBlock>();

    /// <summary>Words in the text blocks and non-note paragraphs of the whole subtree.</summary>
    public int WordCount => _items.Sum(item => item switch
    {
        OutlineNode node => node.WordCount,
        TextBlock text => text.WordCount,
        _ => 0,
    });

    /// <summary>Number of distinct sources in the whole subtree.</summary>
    public int SourceCount => AllTextBlocks().SelectMany(t => t.Sources).Distinct().Count();

    internal void Add(BookItem item) => _items.Add(item);

    private IEnumerable<TextBlock> AllTextBlocks() => _items.SelectMany(item => item switch
    {
        OutlineNode node => node.AllTextBlocks(),
        TextBlock text => [text],
        _ => [],
    });
}
