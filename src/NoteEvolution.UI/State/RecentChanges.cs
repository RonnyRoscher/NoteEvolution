using System.Globalization;
using Microsoft.Extensions.Localization;
using NoteEvolution.AI.Understanding;
using NoteEvolution.Core.Books;
using NoteEvolution.UI.Resources;

namespace NoteEvolution.UI.State;

/// <summary>
/// One entry of the "Zuletzt geändert" list: a text block, when it changed, where it is and what it is about.
/// <paramref name="Summary"/> is the first sentence of its section's summary, else the block's <paramref name="Excerpt"/>.
/// </summary>
public sealed record RecentChange(Guid TextBlockKey, DateTime Updated, string SectionPath, string Summary, string Excerpt);

/// <summary>The most recently changed text blocks of a book (spec "Blockdaten" 4.1).</summary>
public static class RecentChanges
{
    private const int ExcerptLength = 80;

    private const int SummaryLength = 160;

    private const string PathSeparator = " › ";

    /// <summary>
    /// The <paramref name="limit"/> text blocks with the newest <c>updated::</c> date, newest first (blocks without one
    /// are left out). <paramref name="sectionSummary"/> gives a section's summary or <c>null</c>; it is asked only for
    /// the sections of the listed blocks.
    /// </summary>
    public static IReadOnlyList<RecentChange> Of(Book book, Func<OutlineNode, string?> sectionSummary, int limit = 20)
    {
        var newest = AllTextBlocks(book.Root)
            .Where(block => block.Updated is not null)
            .OrderByDescending(block => block.Updated)
            .Take(limit);
        return [.. newest.Select(block =>
        {
            var excerpt = Excerpt.FirstLine(block.Text, ExcerptLength);
            var summary = sectionSummary(block.Section);
            return new RecentChange(
                block.Key,
                block.Updated!.Value,
                PathOf(book, block.Section),
                string.IsNullOrWhiteSpace(summary) ? excerpt : SummaryText.FirstSentence(summary, SummaryLength),
                excerpt);
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
