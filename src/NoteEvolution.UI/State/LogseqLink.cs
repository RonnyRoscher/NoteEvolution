namespace NoteEvolution.UI.State;

/// <summary>Links that open a block or a page of the vault in Logseq (<c>logseq://graph/…</c>).</summary>
public static class LogseqLink
{
    /// <summary>
    /// The link to the block with <paramref name="blockId"/> in the graph of <paramref name="vaultRoot"/> (Logseq names
    /// a graph after its folder), or to the page <paramref name="pageName"/> when the block has no id.
    /// </summary>
    public static string For(string vaultRoot, Guid? blockId, string pageName)
    {
        var graph = Uri.EscapeDataString(Path.GetFileName(Path.TrimEndingDirectorySeparator(vaultRoot)));
        var target = blockId is { } id
            ? $"block-id={Uri.EscapeDataString(id.ToString("D"))}"
            : $"page={Uri.EscapeDataString(pageName)}";
        return $"logseq://graph/{graph}?{target}";
    }
}
