using NoteEvolution.Core.Links;
using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Vaults;

/// <summary>A block seen as a note: a view with its page, journal date, context and usage state. Properties read the block live.</summary>
public sealed class NoteBlock
{
    internal NoteBlock(Page page, Block block, DateOnly? date)
    {
        Page = page;
        Block = block;
        Date = date;
    }

    public Page Page { get; }

    public Block Block { get; }

    public Guid Key => Block.Key;

    /// <summary>The journal day (from the file name), or <c>null</c> for blocks outside journals.</summary>
    public DateOnly? Date { get; }

    /// <summary>The first content line of each ancestor, outermost first.</summary>
    public IReadOnlyList<string> ContextPath
    {
        get
        {
            var path = new List<string>();
            for (var parent = Block.Parent; parent is not null; parent = parent.Parent)
            {
                path.Add(FirstLine(parent.Content));
            }
            path.Reverse();
            return path;
        }
    }

    /// <summary>The block's own <c>used-in::</c> entries.</summary>
    public IReadOnlyList<UsedInEntry> Usages =>
        Block.GetProperty("used-in") is { } value ? UsedInValue.Parse(value) : [];

    /// <summary>The block is used (see <see cref="IsUsedBlock"/>).</summary>
    public bool IsUsed => IsUsedBlock(Block);

    /// <summary>"Verwendete ausblenden" leaves the block out (see <see cref="IsHiddenWhenUsedHidden"/>).</summary>
    public bool HiddenWhenUsedHidden => IsHiddenWhenUsedHidden(Block);

    /// <summary>
    /// Whether "Verwendete ausblenden" leaves <paramref name="block"/> out (with everything below it): it is used, unless
    /// it was adopted only without its sub-points (<see cref="UsedInValue.WithoutChildrenKey"/>) and one of them is still
    /// shown; then it stays, as the head of its unused sub-points.
    /// </summary>
    public static bool IsHiddenWhenUsedHidden(Block block) =>
        IsUsedBlock(block)
        && !(block.GetProperty(UsedInValue.WithoutChildrenKey) is not null
             && !string.IsNullOrWhiteSpace(block.GetProperty(UsedInValue.Key))
             && block.Children.Any(child => !IsHiddenWhenUsedHidden(child)));

    /// <summary>
    /// The sub-points of <paramref name="block"/> that are shown: with <paramref name="hideUsed"/> ("Verwendete
    /// ausblenden") without those <see cref="IsHiddenWhenUsedHidden"/> leaves out, else all of them.
    /// </summary>
    public static IEnumerable<Block> ShownChildren(Block block, bool hideUsed) =>
        hideUsed ? block.Children.Where(child => !IsHiddenWhenUsedHidden(child)) : block.Children;

    /// <summary>
    /// Whether <paramref name="block"/> is used: it has a <c>used-in::</c> property itself, or an ancestor has one that
    /// passes its usage on to its sub-points, which an ancestor adopted only without its sub-points
    /// (<see cref="UsedInValue.WithoutChildrenKey"/>) does not.
    /// </summary>
    public static bool IsUsedBlock(Block block)
    {
        if (!string.IsNullOrWhiteSpace(block.GetProperty(UsedInValue.Key)))
        {
            return true;
        }

        for (var ancestor = block.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (!string.IsNullOrWhiteSpace(ancestor.GetProperty(UsedInValue.Key))
                && ancestor.GetProperty(UsedInValue.WithoutChildrenKey) is null)
            {
                return true;
            }
        }

        return false;
    }

    private static string FirstLine(string content)
    {
        var end = content.IndexOf('\n');
        return (end < 0 ? content : content[..end]).TrimEnd('\r');
    }
}
