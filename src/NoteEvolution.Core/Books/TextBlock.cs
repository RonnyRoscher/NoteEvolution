using NoteEvolution.Core.Links;
using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Books;

/// <summary>A non-heading block of the book with its descendants as paragraphs.</summary>
public sealed class TextBlock : BookItem
{
    internal TextBlock(Block block, OutlineNode section, IReadOnlyList<Paragraph> paragraphs)
    {
        Block = block;
        Section = section;
        Paragraphs = paragraphs;
        Text = block.Content;
        Sources = SourceValue.Parse(block.GetProperty("source") ?? "");
        Status = BlockStatuses.Of(block);
        Created = BlockDates.Created(block);
        Updated = BlockDates.Updated(block);
    }

    public Block Block { get; }

    public Guid Key => Block.Key;

    public string Text { get; }

    public IReadOnlyList<Paragraph> Paragraphs { get; }

    /// <summary>The blocks referenced by the <c>source::</c> property.</summary>
    public IReadOnlyList<Guid> Sources { get; }

    public OutlineNode Section { get; }

    /// <summary>The <c>status::</c> property, or the <c>#notiz</c> tag as a remark; a draft by default.</summary>
    public BlockStatus Status { get; }

    /// <summary>The <c>created::</c> date as local time, or <c>null</c> if missing or unreadable.</summary>
    public DateTime? Created { get; }

    /// <summary>The <c>updated::</c> date as local time, or <c>null</c> if missing or unreadable.</summary>
    public DateTime? Updated { get; }

    /// <summary>
    /// The paragraphs that are exported: those whose status is not excluded and that are not inside an excluded
    /// paragraph's subtree. <see cref="Paragraphs"/> is a flat preorder list, so everything deeper than an excluded
    /// paragraph that follows it (until a paragraph at its depth or above) belongs to its subtree. An excluded block
    /// exports nothing; check <see cref="Status"/> first.
    /// </summary>
    public IEnumerable<Paragraph> ExportedParagraphs
    {
        get
        {
            var excludedDepth = int.MaxValue;
            foreach (var paragraph in Paragraphs)
            {
                if (paragraph.Depth > excludedDepth)
                {
                    continue;
                }

                var excluded = BlockStatuses.IsExcluded(paragraph.Status);
                excludedDepth = excluded ? paragraph.Depth : int.MaxValue;
                if (!excluded)
                {
                    yield return paragraph;
                }
            }
        }
    }

    /// <summary>Words in the exported text: nothing for an excluded block, else its text and its exported paragraphs.</summary>
    internal int WordCount => BlockStatuses.IsExcluded(Status)
        ? 0
        : CountWords(Text) + ExportedParagraphs.Sum(p => CountWords(p.Text));

    private static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
