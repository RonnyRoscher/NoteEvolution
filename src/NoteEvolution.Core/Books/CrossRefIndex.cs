namespace NoteEvolution.Core.Books;

/// <summary>A block reference target: a heading or a text block, identified by its <c>id::</c> property.</summary>
public sealed record CrossRefTarget(Guid Id, OutlineNode? Heading, TextBlock? TextBlock);

/// <summary>A referring element: the text block, or the key of the paragraph inside it that holds the reference.</summary>
public sealed record CrossRefSource(Guid ElementKey, TextBlock TextBlock, Guid TargetId);

/// <summary>Resolves the block references of a book snapshot to their targets, in book order.</summary>
public sealed class CrossRefIndex
{
    private readonly Dictionary<Guid, CrossRefTarget> _targets = [];
    private readonly List<CrossRefSource> _sources = [];
    private readonly Dictionary<Guid, List<CrossRefSource>> _byTarget = [];
    private readonly List<CrossRefSource> _missing = [];

    private CrossRefIndex()
    {
    }

    public IReadOnlyList<CrossRefSource> Missing => _missing;

    public static CrossRefIndex Of(Book book)
    {
        var index = new CrossRefIndex();
        index.Visit(book.Root);
        foreach (var source in index._sources)
        {
            if (index._targets.ContainsKey(source.TargetId))
            {
                if (!index._byTarget.TryGetValue(source.TargetId, out var list))
                {
                    index._byTarget[source.TargetId] = list = [];
                }

                list.Add(source);
            }
            else
            {
                index._missing.Add(source);
            }
        }

        return index;
    }

    public CrossRefTarget? Target(Guid id) => _targets.GetValueOrDefault(id);

    /// <summary>The sources referring to <paramref name="targetId"/> in book order.</summary>
    public IReadOnlyList<CrossRefSource> To(Guid targetId) =>
        _byTarget.TryGetValue(targetId, out var list) ? list : [];

    private void Visit(OutlineNode node)
    {
        if (node.Block?.Id is { } headingId)
        {
            _targets.TryAdd(headingId, new CrossRefTarget(headingId, node, null));
        }

        foreach (var item in node.Items)
        {
            if (item is OutlineNode child)
            {
                Visit(child);
            }
            else if (item is TextBlock tb)
            {
                if (tb.Block.Id is { } id)
                {
                    _targets.TryAdd(id, new CrossRefTarget(id, null, tb));
                }

                Collect(tb.Key, tb.Text, tb);
                foreach (var p in tb.Paragraphs)
                {
                    Collect(p.Block.Key, p.Text, tb);
                }
            }
        }
    }

    private void Collect(Guid elementKey, string text, TextBlock tb)
    {
        foreach (var r in BlockRefs.Find(text))
        {
            _sources.Add(new CrossRefSource(elementKey, tb, r.Id));
        }
    }
}
