using NoteEvolution.Core.Links;
using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Vaults;

/// <summary>
/// The marks of a journal day, kept as page properties of its file: <c>not-relevant-for:: [[Book]], [[Other book]]</c>
/// (per book) and <c>later:: true</c> (for every book). An unset mark has no property line.
/// </summary>
public static class DayMarks
{
    public const string NotRelevantKey = "not-relevant-for";

    public const string LaterKey = "later";

    /// <summary>The book link names of a <c>not-relevant-for::</c> value; lenient like <see cref="UsedInValue.Parse"/>.</summary>
    public static IReadOnlyList<string> ParseBooks(string value) => [.. UsedInValue.Parse(value).Select(e => e.PageName)];

    /// <summary>The canonical <c>not-relevant-for::</c> value: <c>[[A]], [[B]]</c>.</summary>
    public static string FormatBooks(IEnumerable<string> books) => UsedInValue.Format(books.Select(b => new UsedInEntry(b, null)));

    /// <summary>The books of all <c>not-relevant-for::</c> lines (a hand-edited file may hold several).</summary>
    public static IReadOnlyList<string> NotRelevantBooks(Page page) =>
        [.. Values(page, NotRelevantKey).SelectMany(ParseBooks).Distinct(StringComparer.OrdinalIgnoreCase)];

    public static bool IsNotRelevant(Page page, string bookLinkName) =>
        NotRelevantBooks(page).Contains(bookLinkName, StringComparer.OrdinalIgnoreCase);

    public static bool IsLater(Page page) =>
        Values(page, LaterKey).Any(v => string.Equals(v.Trim(), "true", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Adds or removes <paramref name="bookLinkName"/> in <c>not-relevant-for::</c>; the other books' entries stay, and
    /// the property goes when it becomes empty.
    /// </summary>
    /// <returns><c>true</c> if the page changed.</returns>
    public static bool SetNotRelevant(Page page, string bookLinkName, bool notRelevant)
    {
        if (IsNotRelevant(page, bookLinkName) == notRelevant)
        {
            return false;
        }

        var books = NotRelevantBooks(page).ToList();
        if (notRelevant)
        {
            books.Add(bookLinkName);
        }
        else
        {
            books.RemoveAll(b => string.Equals(b, bookLinkName, StringComparison.OrdinalIgnoreCase));
        }

        // Duplicate lines are merged into one; a single line is replaced in place, so the rest of the file stays as it was.
        if (books.Count == 0 || Values(page, NotRelevantKey).Count() > 1)
        {
            RemoveAll(page, NotRelevantKey);
        }

        if (books.Count > 0)
        {
            page.SetPageProperty(NotRelevantKey, FormatBooks(books));
        }

        return true;
    }

    /// <summary>Sets <c>later:: true</c> or removes the property.</summary>
    /// <returns><c>true</c> if the page changed.</returns>
    public static bool SetLater(Page page, bool later)
    {
        if (IsLater(page) == later)
        {
            return false;
        }

        if (later)
        {
            page.SetPageProperty(LaterKey, "true");
        }
        else
        {
            RemoveAll(page, LaterKey);
        }

        return true;
    }

    private static IEnumerable<string> Values(Page page, string key) =>
        page.PageProperties.Where(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Select(p => p.Value);

    /// <summary>Removes every line of <paramref name="key"/>, also hand-edited duplicates.</summary>
    private static void RemoveAll(Page page, string key)
    {
        while (page.RemovePageProperty(key))
        {
        }
    }
}

/// <summary>Which marked journal days are hidden; "nicht relevant" counts for <paramref name="Book"/> only (none without a book).</summary>
public sealed record DayMarkFilter(bool HideNotRelevant, bool HideLater, string? Book)
{
    public bool Hides(Page page) =>
        (HideLater && DayMarks.IsLater(page))
        || (HideNotRelevant && Book is not null && DayMarks.IsNotRelevant(page, Book));
}
