using System.Text.Json.Serialization;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Links;

namespace NoteEvolution.UI.History;

/// <summary>Why a version of a text block was stored.</summary>
public enum HistoryReason { First, Pause, BeforeDelete, OnClose, OnOpen, BeforeRestore }

/// <summary>
/// One sub-point of a block as it was: its text, depth and status, and its <c>id::</c> and the blocks it cites as far as it
/// has them (a stand stored before these were kept has none).
/// </summary>
public sealed record StandParagraph(string Text, int Depth, BlockStatus Status, Guid? Id = null, IReadOnlyList<Guid>? Sources = null);

/// <summary>
/// What a text block looked like at one moment: its text, its sub-points, its status and the blocks it cites.
/// </summary>
public sealed record BlockStand(string Text, IReadOnlyList<StandParagraph> Paragraphs, BlockStatus Status, IReadOnlyList<Guid> Sources)
{
    /// <summary>The stand of the block as it is now.</summary>
    public static BlockStand Of(TextBlock block) =>
        new(block.Text, [.. block.Paragraphs.Select(Of)], block.Status, block.Sources);

    private static StandParagraph Of(Paragraph paragraph) => new(
        paragraph.Text, paragraph.Depth, paragraph.Status, paragraph.Block.Id,
        SourceValue.Parse(paragraph.Block.GetProperty("source") ?? "") is { Count: > 0 } sources ? sources : null);

    /// <summary>The number of characters in the text and in the sub-points.</summary>
    [JsonIgnore]
    public int Length => Text.Length + Paragraphs.Sum(p => p.Text.Length);

    /// <summary>Whether text, depths and sub-point texts are equal; status, ids and sources do not count.</summary>
    public bool SameText(BlockStand other) =>
        Text == other.Text
        && Paragraphs.Count == other.Paragraphs.Count
        && Paragraphs.Zip(other.Paragraphs).All(pair => pair.First.Text == pair.Second.Text && pair.First.Depth == pair.Second.Depth);
}
