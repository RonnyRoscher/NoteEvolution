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

    /// <summary>The block itself or one of its ancestors has a <c>used-in::</c> property.</summary>
    public bool IsUsed
    {
        get
        {
            for (var block = Block; block is not null; block = block.Parent)
            {
                if (!string.IsNullOrWhiteSpace(block.GetProperty("used-in"))) return true;
            }
            return false;
        }
    }

    private static string FirstLine(string content)
    {
        var end = content.IndexOf('\n');
        return (end < 0 ? content : content[..end]).TrimEnd('\r');
    }
}
