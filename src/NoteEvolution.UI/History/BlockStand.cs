using NoteEvolution.Core.Books;

namespace NoteEvolution.UI.History;

/// <summary>Why a version of a text block was stored.</summary>
public enum HistoryReason { First, Pause, BeforeDelete, OnClose, OnOpen, BeforeRestore }

/// <summary>One sub-point of a block as it was: its text, depth and status.</summary>
public sealed record StandParagraph(string Text, int Depth, BlockStatus Status);

/// <summary>
/// What a text block looked like at one moment: its text, its sub-points, its status and the blocks it cites.
/// </summary>
public sealed record BlockStand(string Text, IReadOnlyList<StandParagraph> Paragraphs, BlockStatus Status, IReadOnlyList<Guid> Sources)
{
    /// <summary>The stand of the block as it is now.</summary>
    public static BlockStand Of(TextBlock block) =>
        new(block.Text, [.. block.Paragraphs.Select(p => new StandParagraph(p.Text, p.Depth, p.Status))], block.Status, block.Sources);

    /// <summary>The number of characters in the text and in the sub-points.</summary>
    public int Length => Text.Length + Paragraphs.Sum(p => p.Text.Length);

    /// <summary>Whether text, depths and sub-point texts are equal; status and sources do not count.</summary>
    public bool SameText(BlockStand other) =>
        Text == other.Text
        && Paragraphs.Count == other.Paragraphs.Count
        && Paragraphs.Zip(other.Paragraphs).All(pair => pair.First.Text == pair.Second.Text && pair.First.Depth == pair.Second.Depth);
}
