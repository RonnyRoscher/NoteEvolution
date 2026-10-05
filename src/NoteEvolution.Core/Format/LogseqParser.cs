using System.Text;
using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Format;

/// <summary>Parses Logseq Markdown into a <see cref="Page"/> that keeps every original line.</summary>
public static class LogseqParser
{
    private static readonly UTF8Encoding LenientUtf8 = new(false, throwOnInvalidBytes: false);

    /// <summary>
    /// Never throws. If the file cannot be parsed safely, the page is read-only, carries
    /// <c>ParseError = "Zeile {n}: {grund}"</c> and is filled as far as possible.
    /// </summary>
    public static Page Parse(string filePath, byte[] bytes)
    {
        TextDocument document;
        string? error = null;
        try
        {
            document = TextDocument.Parse(bytes);
        }
        catch (ParseException ex)
        {
            error = $"Zeile {ex.LineNumber}: Ungültige UTF-8-Kodierung.";
            // Decode with replacement characters so the page can still be shown.
            document = TextDocument.Parse(Encoding.UTF8.GetBytes(LenientUtf8.GetString(bytes)));
        }

        var lines = document.Lines;
        var index = 0;
        while (index < lines.Count && !LogseqSyntax.IsBullet(lines[index].Text))
        {
            index++;
        }

        var page = new Page(filePath, document.HasBom, document.DominantEnding, lines.Take(index));
        var stack = new Stack<(Block Block, int Width)>();

        while (index < lines.Count)
        {
            var start = index;
            LogseqSyntax.TryParseBullet(lines[start].Text, out var indent, out _, out _);
            var section = LogseqSyntax.FindPropertySection(lines, start);
            var fenceRun = 0;
            var fenceLine = 0;
            if (section.FenceUnclosed)
            {
                error ??= $"Zeile {start + 1}: Codeblock wird nicht geschlossen.";
            }

            // Skips the bullet line, a code fence it opens, and the property lines.
            index = start + section.InsertIndex;

            for (; index < lines.Count; index++)
            {
                var line = lines[index].Text;
                if (fenceRun > 0)
                {
                    if (LogseqSyntax.IsFenceClosing(line, fenceRun))
                    {
                        fenceRun = 0;
                    }

                    continue;
                }

                if (LogseqSyntax.IsBullet(line))
                {
                    break;
                }

                fenceRun = LogseqSyntax.FenceOpening(line);
                if (fenceRun > 0)
                {
                    fenceLine = index + 1;
                }
            }

            if (fenceRun > 0)
            {
                error ??= $"Zeile {fenceLine}: Codeblock wird nicht geschlossen.";
            }

            var block = new Block(lines.Skip(start).Take(index - start), start + 1);
            var width = LogseqSyntax.IndentWidth(indent);
            while (stack.Count > 0 && stack.Peek().Width >= width)
            {
                stack.Pop();
            }

            if (stack.Count == 0)
            {
                page.AddRoot(block);
            }
            else
            {
                stack.Peek().Block.AddChild(block);
            }

            stack.Push((block, width));
        }

        page.ParseError = error;
        page.MarkSaved();
        return page;
    }
}
