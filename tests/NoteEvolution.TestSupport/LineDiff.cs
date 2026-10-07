using NoteEvolution.Core.Format;

namespace NoteEvolution.TestSupport;

/// <summary>
/// Line-level diff of two file contents (longest common subsequence). Lines are compared byte-exact,
/// including their line ending, so a line whose ending changed counts as removed and added.
/// </summary>
public static class LineDiff
{
    /// <returns>0-based indices of lines only in <paramref name="before"/> and of lines only in <paramref name="after"/>.</returns>
    public static (IReadOnlyList<int> Removed, IReadOnlyList<int> Added) Changed(byte[] before, byte[] after)
    {
        var a = TextDocument.Parse(before).Lines;
        var b = TextDocument.Parse(after).Lines;

        // lcs[i, j] = length of the longest common subsequence of a[i..] and b[j..].
        var lcs = new int[a.Count + 1, b.Count + 1];
        for (var i = a.Count - 1; i >= 0; i--)
        {
            for (var j = b.Count - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var removed = new List<int>();
        var added = new List<int>();
        int x = 0, y = 0;
        while (x < a.Count && y < b.Count)
        {
            if (a[x] == b[y])
            {
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                removed.Add(x++);
            }
            else
            {
                added.Add(y++);
            }
        }

        for (; x < a.Count; x++)
        {
            removed.Add(x);
        }

        for (; y < b.Count; y++)
        {
            added.Add(y);
        }

        return (removed, added);
    }
}
