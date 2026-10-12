using NoteEvolution.Core.Model;
using NoteEvolution.Core.Text;

namespace NoteEvolution.Core.Books;

/// <summary>The status of a text block or detail: how far it is on the way into the book.</summary>
public enum BlockStatus
{
    /// <summary>No <c>status::</c> line (the default).</summary>
    Draft,

    /// <summary><c>status:: relevant</c>; not exported.</summary>
    Relevant,

    /// <summary><c>status:: bemerkung</c> or the <c>#notiz</c> tag; not exported.</summary>
    Remark,

    /// <summary><c>status:: formulierung</c>.</summary>
    Wording,

    /// <summary><c>status:: fertig</c>.</summary>
    Done,
}

/// <summary>Reads and names the <see cref="BlockStatus"/> values as the <c>status::</c> property stores them.</summary>
public static class BlockStatuses
{
    /// <summary>The property that holds the status.</summary>
    public const string Key = "status";

    /// <summary>
    /// The block's status: the <c>status::</c> value (case-insensitive: relevant, bemerkung, formulierung, fertig)
    /// wins; else the <c>#notiz</c> tag makes it a remark; else it is a draft. An unknown or empty value is a draft.
    /// </summary>
    public static BlockStatus Of(Block block) =>
        block.GetProperty(Key)?.Trim().ToLowerInvariant() switch
        {
            "relevant" => BlockStatus.Relevant,
            "bemerkung" => BlockStatus.Remark,
            "formulierung" => BlockStatus.Wording,
            "fertig" => BlockStatus.Done,
            _ when NoteTag.Has(block.Content) => BlockStatus.Remark,
            _ => BlockStatus.Draft,
        };

    /// <summary>The <c>status::</c> value written for a non-draft status; <c>null</c> for <see cref="BlockStatus.Draft"/>.</summary>
    public static string? FileValue(BlockStatus status) =>
        status switch
        {
            BlockStatus.Relevant => "relevant",
            BlockStatus.Remark => "bemerkung",
            BlockStatus.Wording => "formulierung",
            BlockStatus.Done => "fertig",
            _ => null,
        };

    /// <summary>Relevant and remark blocks are not exported.</summary>
    public static bool IsExcluded(BlockStatus status) => status is BlockStatus.Relevant or BlockStatus.Remark;
}
