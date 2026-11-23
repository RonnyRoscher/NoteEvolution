using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Text;

namespace NoteEvolution.Core.Repetitions;

/// <summary>
/// One paragraph of a block as shown side by side: the own text (Depth 0) or a sub-point. <c>Key</c> is the key of the
/// text block (paragraph 0) or of the sub-point's block. <c>Shown[i]</c> is the offset of <c>Plain[i]</c> in the text as
/// the editor shows it (the units of <c>GlossaryHit.Start</c>: a block reference, source citation or image is one unit,
/// emphasis markers take none); <c>Shown.Count == Plain.Length + 1</c>, the last entry is the end.
/// </summary>
public sealed record ComparedParagraph(Guid Key, int Depth, IReadOnlyList<InlineRun> Runs, string Plain, IReadOnlyList<int> Shown);

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

        var paragraphs = new List<ComparedParagraph> { Paragraph(block.Key, 0, own) };
        paragraphs.AddRange(block.ExportedParagraphs.Select(p => Paragraph(p.Block.Key, p.Depth, Visible(p.Text))));

        var words = new List<ComparedWord>();
        for (var index = 0; index < paragraphs.Count; index++)
        {
            AddWords(words, index, paragraphs[index].Plain);
        }

        return words.Count < MinWords ? null : new ComparedBlock(block.Key, paragraphs, words);
    }

    /// <summary>
    /// The span of a word range of a paragraph in the editor: the key of the paragraph's block, and start and length in
    /// the units of <c>GlossaryHit.Start</c>, from <c>Shown[start]</c> and <c>Shown[start + length]</c>.
    /// </summary>
    public static (Guid Key, int Start, int Length) ShownSpan(ComparedBlock block, int paragraph, int start, int length)
    {
        var compared = block.Paragraphs[paragraph];
        var from = compared.Shown[start];
        return (compared.Key, from, compared.Shown[start + length] - from);
    }

    private static ComparedParagraph Paragraph(Guid key, int depth, VisibleText visible)
    {
        var runs = InlineMarkdown.Parse(visible.Text);
        var plain = string.Concat(runs.Select(run => run.Text));
        return new ComparedParagraph(key, depth, runs, plain, ShownOffsets(plain, visible));
    }

    /// <summary>
    /// The editor offset of every char of <paramref name="plain"/> and its end. <paramref name="plain"/> is the visible
    /// text without emphasis markers, so its chars are matched in order against the visible text; the editor shows a
    /// <c>\*</c> or <c>\_</c> as one unit and each removed reference, citation or image as one unit before the char.
    /// </summary>
    private static int[] ShownOffsets(string plain, VisibleText visible)
    {
        var shown = new int[plain.Length + 1];
        var units = 0;
        var at = 0;
        var secondOfEscape = false;
        for (var i = 0; i < plain.Length; i++)
        {
            while (at < visible.Text.Length && visible.Text[at] != plain[i])
            {
                at++;
            }

            var removed = at < visible.RemovedBefore.Count ? visible.RemovedBefore[at] : visible.Removed;
            at++;
            if (secondOfEscape)
            {
                // The star or underscore of "\*" or "\_" is in the unit of its backslash.
                shown[i] = shown[i - 1];
                secondOfEscape = false;
                continue;
            }

            shown[i] = units + removed;
            units++;
            secondOfEscape = plain[i] == '\\' && i + 1 < plain.Length && plain[i + 1] is '*' or '_';
        }

        shown[plain.Length] = units + visible.Removed;
        return shown;
    }

    /// <summary>The unescaped text without references, citations and images; <c>RemovedBefore[i]</c> counts those removed before char i, <c>Removed</c> all.</summary>
    private sealed record VisibleText(string Text, bool HadImage, IReadOnlyList<int> RemovedBefore, int Removed);

    /// <summary>The text unescaped, without references, source citations and images; reports whether an image was removed.</summary>
    private static VisibleText Visible(string content)
    {
        var text = BlockTextEscape.Unescape(content);
        var sb = new StringBuilder(text.Length);
        var removedBefore = new List<int>(text.Length);
        var removed = 0;
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
                removed++;
                i += skip;
                continue;
            }

            sb.Append(text[i]);
            removedBefore.Add(removed);
            i++;
        }

        return new VisibleText(sb.ToString(), hadImage, removedBefore, removed);
    }

    /// <summary>A block whose own text is only images (nothing else is left once they are removed).</summary>
    private static bool IsImageBlock(VisibleText own) =>
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
