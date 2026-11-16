using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Text;

namespace NoteEvolution.Core.Repetitions;

/// <summary>One paragraph of a block as shown side by side: the own text (Depth 0) or a sub-point.</summary>
public sealed record ComparedParagraph(int Depth, IReadOnlyList<InlineRun> Runs, string Plain);

/// <summary>A word of the block: paragraph index, offset and length in that paragraph's Plain, and its normalized form.</summary>
public readonly record struct ComparedWord(int Paragraph, int Start, int Length, string Normal);

/// <summary>A text block as it is compared: its visible paragraphs and the words of all of them in order.</summary>
public sealed record ComparedBlock(Guid Key, IReadOnlyList<ComparedParagraph> Paragraphs, IReadOnlyList<ComparedWord> Words);

/// <summary>
/// The text of the book's blocks as it is compared for repetitions: visible text without emphasis markers, references,
/// source citations and images, split into case-insensitive words.
/// </summary>
public static class ComparisonText
{
    public const int MinWords = 8;

    /// <summary>The compared blocks of the book in book order; blocks that are not compared are left out.</summary>
    public static IReadOnlyList<ComparedBlock> Of(Book book) =>
        [.. book.Root.AllTextBlocks().Select(Of).OfType<ComparedBlock>()];

    /// <summary>Null when the block is not compared (status, image block, fewer than <see cref="MinWords"/> words).</summary>
    public static ComparedBlock? Of(TextBlock block)
    {
        if (BlockStatuses.IsExcluded(block.Status))
        {
            return null;
        }

        var own = Visible(block.Text);
        if (IsImageBlock(own))
        {
            return null;
        }

        var paragraphs = new List<ComparedParagraph> { Paragraph(0, own.Text) };
        paragraphs.AddRange(block.ExportedParagraphs.Select(p => Paragraph(p.Depth, Visible(p.Text).Text)));

        var words = new List<ComparedWord>();
        for (var index = 0; index < paragraphs.Count; index++)
        {
            AddWords(words, index, paragraphs[index].Plain);
        }

        return words.Count < MinWords ? null : new ComparedBlock(block.Key, paragraphs, words);
    }

    private static ComparedParagraph Paragraph(int depth, string visible)
    {
        var runs = InlineMarkdown.Parse(visible);
        return new ComparedParagraph(depth, runs, string.Concat(runs.Select(run => run.Text)));
    }

    /// <summary>The text unescaped, without references, source citations and images; reports whether an image was removed.</summary>
    private static (string Text, bool HadImage) Visible(string content)
    {
        var text = BlockTextEscape.Unescape(content);
        var sb = new StringBuilder(text.Length);
        var hadImage = false;
        for (var i = 0; i < text.Length;)
        {
            var image = BlockImages.LengthAt(text, i);
            var skip = image > 0 ? image
                : SourceCitations.LengthAt(text, i) is > 0 and var citation ? citation
                : BlockRefs.LengthAt(text, i);
            if (skip > 0)
            {
                hadImage |= image > 0;
                i += skip;
                continue;
            }

            sb.Append(text[i]);
            i++;
        }

        return (sb.ToString(), hadImage);
    }

    /// <summary>A block whose own text is only images (nothing else is left once they are removed).</summary>
    private static bool IsImageBlock((string Text, bool HadImage) own) =>
        own.HadImage && string.IsNullOrWhiteSpace(own.Text);

    private static void AddWords(List<ComparedWord> words, int paragraph, string plain)
    {
        for (var i = 0; i < plain.Length;)
        {
            if (!char.IsLetterOrDigit(plain[i]))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < plain.Length && char.IsLetterOrDigit(plain[i]))
            {
                i++;
            }

            words.Add(new ComparedWord(paragraph, start, i - start, plain[start..i].ToLowerInvariant()));
        }
    }
}
