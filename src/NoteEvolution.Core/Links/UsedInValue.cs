using System.Text.RegularExpressions;

namespace NoteEvolution.Core.Links;

/// <summary>One <c>used-in::</c> entry: the linked book page and, optionally, the specific book block.</summary>
public sealed record UsedInEntry(string PageName, Guid? BookBlockId);

/// <summary>
/// The value of a <c>used-in::</c> property: <c>[[Page]] ((block-id)), [[Other page]]</c>.
/// Parsing is lenient (extra whitespace, unparseable fragments and entries without a page link are ignored);
/// formatting is canonical.
/// </summary>
public static partial class UsedInValue
{
    /// <summary>The property that holds the usages.</summary>
    public const string Key = "used-in";

    /// <summary>
    /// <c>used-without-children:: true</c> next to <c>used-in::</c>: every usage took the block without its sub-points,
    /// so they stay unused (see <c>NoteBlock.IsUsedBlock</c>). It goes with the block's last usage.
    /// </summary>
    public const string WithoutChildrenKey = "used-without-children";

    [GeneratedRegex(@"\[\[(?<name>.+?)\]\](?:[ \t]*\(\([ \t]*(?<id>[^()]*?)[ \t]*\)\))?")]
    private static partial Regex EntryRegex();

    public static IReadOnlyList<UsedInEntry> Parse(string value)
    {
        var entries = new List<UsedInEntry>();
        foreach (Match match in EntryRegex().Matches(value))
        {
            var name = match.Groups["name"].Value.Trim();
            if (name.Length == 0) continue;
            Guid? blockId = match.Groups["id"].Success
                            && Guid.TryParseExact(match.Groups["id"].Value, "D", out var id)
                ? id
                : null;
            entries.Add(new UsedInEntry(name, blockId));
        }
        return entries;
    }

    public static string Format(IEnumerable<UsedInEntry> entries) =>
        string.Join(", ", entries.Select(e =>
            e.BookBlockId is { } id ? $"[[{e.PageName}]] (({id:D}))" : $"[[{e.PageName}]]"));
}
