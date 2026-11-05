using System.Text.RegularExpressions;

namespace NoteEvolution.UI.History;

/// <summary>How a part of a comparison relates to the two texts.</summary>
public enum DiffKind { Same, Removed, Added }

/// <summary>A run of text that is in both texts, only in the first (removed) or only in the second (added).</summary>
public sealed record DiffPart(DiffKind Kind, string Text);

/// <summary>Compares two texts word by word (spec 6).</summary>
public static partial class WordDiff
{
    /// <summary>With more words than this on one side the texts are not compared in detail.</summary>
    private const int MaxWords = 4000;

    [GeneratedRegex(@"\S+\s*|\s+")]
    private static partial Regex WordPattern();

    /// <summary>
    /// The longest common sequence of words, each with the white space after it, as parts in reading order; a replaced
    /// run shows the removed part before the added one. Neighbouring parts of one kind are joined. With more than
    /// 4000 words on a side the whole of <paramref name="before"/> counts as removed and the whole of
    /// <paramref name="after"/> as added.
    /// </summary>
    public static IReadOnlyList<DiffPart> Compare(string before, string after)
    {
        var a = Split(before);
        var b = Split(after);
        var parts = new List<DiffPart>();
        if (a.Count > MaxWords || b.Count > MaxWords)
        {
            Add(parts, DiffKind.Removed, before);
            Add(parts, DiffKind.Added, after);
            return parts;
        }

        // lengths[i, j]: the length of the common sequence of a[i..] and b[j..].
        var lengths = new ushort[a.Count + 1, b.Count + 1];
        for (var i = a.Count - 1; i >= 0; i--)
        {
            for (var j = b.Count - 1; j >= 0; j--)
            {
                lengths[i, j] = a[i] == b[j] ? (ushort)(lengths[i + 1, j + 1] + 1) : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        int x = 0, y = 0;
        while (x < a.Count && y < b.Count)
        {
            if (a[x] == b[y])
            {
                Add(parts, DiffKind.Same, a[x++]);
                y++;
            }
            else if (lengths[x + 1, y] >= lengths[x, y + 1])
            {
                Add(parts, DiffKind.Removed, a[x++]);
            }
            else
            {
                Add(parts, DiffKind.Added, b[y++]);
            }
        }

        for (; x < a.Count; x++)
        {
            Add(parts, DiffKind.Removed, a[x]);
        }

        for (; y < b.Count; y++)
        {
            Add(parts, DiffKind.Added, b[y]);
        }

        return parts;
    }

    private static List<string> Split(string text) => [.. WordPattern().Matches(text).Select(match => match.Value)];

    /// <summary>Appends the text, joined to the last part if that is of the same kind; no text adds nothing.</summary>
    private static void Add(List<DiffPart> parts, DiffKind kind, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (parts.Count > 0 && parts[^1].Kind == kind)
        {
            parts[^1] = parts[^1] with { Text = parts[^1].Text + text };
        }
        else
        {
            parts.Add(new DiffPart(kind, text));
        }
    }
}
