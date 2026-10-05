namespace NoteEvolution.Core.Model;

/// <summary>How new or moved blocks are indented and bulleted, so that they match their surroundings.</summary>
public static class IndentStyle
{
    /// <summary>
    /// The indentation for a child of <paramref name="parent"/>: the first existing child's indentation; otherwise
    /// <c>parent.Indent</c> plus one unit, where the unit is <c>parent.Indent</c> without the grandparent's
    /// indentation (<c>"\t"</c> if that is empty). Root blocks get <c>""</c>.
    /// </summary>
    public static string ChildIndent(Block? parent)
    {
        if (parent is null)
        {
            return "";
        }

        if (parent.Children.Count > 0)
        {
            return parent.Children[0].Indent;
        }

        var outer = parent.Parent?.Indent ?? "";
        var unit = parent.Indent.StartsWith(outer, StringComparison.Ordinal) ? parent.Indent[outer.Length..] : "";
        return parent.Indent + (unit.Length > 0 ? unit : "\t");
    }

    /// <summary>
    /// The bullet for a block inserted at <paramref name="index"/> among the children of <paramref name="parent"/>:
    /// that of a neighbour (preceding, else following), else the parent's, else <c>'-'</c>.
    /// </summary>
    public static char BulletFor(Block? parent, int index) => BulletFor(parent?.Children ?? [], parent, index);

    /// <inheritdoc cref="BulletFor(Block?, int)"/>
    /// <param name="siblings">The blocks the new one is inserted among (for root blocks: the page's roots).</param>
    internal static char BulletFor(IReadOnlyList<Block> siblings, Block? parent, int index)
    {
        if (index > 0 && index - 1 < siblings.Count)
        {
            return siblings[index - 1].Bullet;
        }

        if (index >= 0 && index < siblings.Count)
        {
            return siblings[index].Bullet;
        }

        return parent?.Bullet ?? '-';
    }
}
