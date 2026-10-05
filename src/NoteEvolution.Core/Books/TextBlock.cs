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
    }

    public Block Block { get; }

    public Guid Key => Block.Key;

    public string Text { get; }

    public IReadOnlyList<Paragraph> Paragraphs { get; }

    /// <summary>The blocks referenced by the <c>source::</c> property.</summary>
    public IReadOnlyList<Guid> Sources { get; }

    public OutlineNode Section { get; }

    /// <summary>Words in the text and in the paragraphs that are not notes.</summary>
    internal int WordCount => CountWords(Text) + Paragraphs.Where(p => !p.IsNote).Sum(p => CountWords(p.Text));

    private static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
