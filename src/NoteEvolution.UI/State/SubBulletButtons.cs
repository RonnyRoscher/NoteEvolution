using System.Text.RegularExpressions;
using NoteEvolution.Core.Model;

namespace NoteEvolution.UI.State;

/// <summary>
/// Which sub-bullets of a note offer their own buttons ("Übernehmen", "Wohin damit?"): those with more than one line
/// that are no item of an enumeration. Short entries and list items are adopted with their note.
/// </summary>
public static partial class SubBulletButtons
{
    /// <summary>A sub-bullet with more characters than this counts as more than one line.</summary>
    public const int LineLength = 80;

    /// <summary>
    /// Whether <paramref name="subBullet"/> gets its own buttons: it has more than one line (a line break, sub-bullets of
    /// its own, or more than <see cref="LineLength"/> characters), and it is not an item of an enumeration (it and at
    /// least one sibling start with a numbering or a list mark such as "1.", "2)", "a)", "–" or "•").
    /// </summary>
    public static bool Offer(Block subBullet) => IsLong(subBullet) && !InEnumeration(subBullet);

    private static bool IsLong(Block block) =>
        block.Children.Count > 0 || block.Content.Contains('\n') || block.Content.Trim().Length > LineLength;

    private static bool InEnumeration(Block block) =>
        ListMark().IsMatch(block.Content)
        && block.Parent is { } parent
        && parent.Children.Count(sibling => ListMark().IsMatch(sibling.Content)) >= 2;

    [GeneratedRegex(@"^\s*(?:\d{1,3}[.)]|[A-Za-z][.)]|[-–—•·*])\s")]
    private static partial Regex ListMark();
}
