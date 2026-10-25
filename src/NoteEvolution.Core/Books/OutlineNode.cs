using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Books;

/// <summary>A heading block (or the invisible root) with the sub-sections and text blocks below it.</summary>
public sealed class OutlineNode : BookItem
{
    private readonly List<BookItem> _items = [];

    internal OutlineNode(Block? block, string title, int level, OutlineNode? parent, AppendixKind appendixKind = AppendixKind.None)
    {
        Block = block;
        Title = title;
        Level = level;
        Parent = parent;
        AppendixKind = appendixKind;
    }

    /// <summary><see cref="Model.Block.Key"/> of the heading; <see cref="Guid.Empty"/> for the root.</summary>
    public Guid Key => Block?.Key ?? Guid.Empty;

    /// <summary>The heading block; <c>null</c> for the root.</summary>
    public Block? Block { get; }

    public string Title { get; }

    /// <summary>Number of <c>#</c> of the heading; 0 for the root.</summary>
    public int Level { get; }

    public OutlineNode? Parent { get; }

    /// <summary>
    /// The appendix section this heading is: a top-level heading with a valid <c>anhang::</c> (see
    /// <see cref="AppendixKinds.Parse"/>); <see cref="AppendixKind.None"/> for every other heading and the root.
    /// </summary>
    public AppendixKind AppendixKind { get; }

    /// <summary>Sub-sections and text blocks in file order.</summary>
    public IReadOnlyList<BookItem> Items => _items;

    public IEnumerable<OutlineNode> Children => _items.OfType<OutlineNode>();

    public IEnumerable<TextBlock> TextBlocks => _items.OfType<TextBlock>();

    /// <summary>
    /// Words in the text blocks and non-note paragraphs of the whole subtree; for the root without the appendix
    /// sections (an appendix section counts its own).
    /// </summary>
    public int WordCount => _items.Sum(item => item switch
    {
        OutlineNode { AppendixKind: AppendixKind.None } node => node.WordCount,
        TextBlock text => text.WordCount,
        _ => 0,
    });

    /// <summary>Number of distinct sources in the whole subtree; for the root without the appendix sections.</summary>
    public int SourceCount => AllTextBlocks().SelectMany(t => t.Sources).Distinct().Count();

    internal void Add(BookItem item) => _items.Add(item);

    /// <summary>
    /// The text blocks of the whole subtree in file order; for the root without the appendix sections (an appendix
    /// section yields its own).
    /// </summary>
    internal IEnumerable<TextBlock> AllTextBlocks() => _items.SelectMany(item => item switch
    {
        OutlineNode { AppendixKind: not AppendixKind.None } => [],
        OutlineNode node => node.AllTextBlocks(),
        TextBlock text => [text],
        _ => [],
    });
}
