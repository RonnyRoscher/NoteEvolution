using System.Globalization;
using Microsoft.Extensions.Localization;
using NoteEvolution.AI.Understanding;
using NoteEvolution.Core.Books;
using NoteEvolution.UI.Resources;

namespace NoteEvolution.UI.State;

/// <summary>
/// One entry of the "Zuletzt geändert" list: a text block, or the blocks of one section changed on one day
/// (<paramref name="Count"/> &gt; 1), when it changed, where it is and what it is about.
/// <paramref name="Summary"/> is the first sentence of the section's summary, else the (first) block's <paramref name="Excerpt"/>.
/// <paramref name="TextBlockKey"/> is the (first) block; <paramref name="HeadingKey"/> the section's heading for a group
/// of a headed section, else <c>null</c>.
/// </summary>
public sealed record RecentChange(
    Guid TextBlockKey, DateTime Updated, string SectionPath, string Summary, string Excerpt, int Count = 1, Guid? HeadingKey = null)
{
    /// <summary>What a click reveals: the heading of a group, else the block.</summary>
    public Guid RevealKey => HeadingKey ?? TextBlockKey;
}

/// <summary>The most recently changed text blocks of a book (spec "Blockdaten" 4.1).</summary>
public static class RecentChanges
{
    private const int ExcerptLength = 80;

    private const int SummaryLength = 160;

    private const string PathSeparator = " › ";

    /// <summary>
    /// The <paramref name="limit"/> newest entries, newest first: the text blocks with an <c>updated::</c> date (blocks
    /// without one are left out), those of one section changed on the same local calendar day as one entry.
    /// <paramref name="sectionSummary"/> gives a section's summary or <c>null</c>; it is asked only for the sections of
    /// the listed entries.
    /// </summary>
    public static IReadOnlyList<RecentChange> Of(Book book, Func<OutlineNode, string?> sectionSummary, int limit = 20)
    {
        var groups = AllTextBlocks(book.Root)
            .Where(block => block.Updated is not null)
            .GroupBy(block => (block.Section, Day: block.Updated!.Value.Date))
            .Select(group => (Blocks: group.ToList(), Latest: group.Max(block => block.Updated!.Value)))
            .OrderByDescending(group => group.Latest)
            .Take(limit);
        return [.. groups.Select(group =>
        {
            var first = group.Blocks[0];
            var excerpt = Excerpt.FirstLine(first.Text, ExcerptLength);
            var summary = sectionSummary(first.Section);
            return new RecentChange(
                first.Key,
                group.Latest,
                PathOf(book, first.Section),
                string.IsNullOrWhiteSpace(summary) ? excerpt : SummaryText.FirstSentence(summary, SummaryLength),
                excerpt,
                group.Blocks.Count,
                group.Blocks.Count > 1 && first.Section.Parent is not null ? first.Section.Key : null);
        })];
    }

    /// <summary>"heute 14:32", "gestern 09:10" or "17.10.2026", judged by the calendar day (<paramref name="now"/> is local time).</summary>
    public static string When(DateTime updated, DateTime now, IStringLocalizer<Strings> l)
    {
        var time = updated.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (updated.Date == now.Date)
        {
            return l["RecentToday", time];
        }

        return updated.Date == now.Date.AddDays(-1)
            ? l["RecentYesterday", time]
            : updated.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
    }

    private static IEnumerable<TextBlock> AllTextBlocks(OutlineNode node) =>
        node.TextBlocks.Concat(node.Children.SelectMany(AllTextBlocks));

    /// <summary>The titles from the top level down; the book's title for the prologue before the first heading.</summary>
    private static string PathOf(Book book, OutlineNode section)
    {
        var titles = new List<string>();
        for (var n = section; n is { Parent: not null }; n = n.Parent)
        {
            titles.Insert(0, n.Title);
        }

        return titles.Count == 0 ? book.Title : string.Join(PathSeparator, titles);
    }
}
