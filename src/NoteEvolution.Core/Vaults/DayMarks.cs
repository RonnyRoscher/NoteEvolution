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

    public static bool IsNotRelevant(Page page, string bookLinkName) =>
        page.GetPageProperty(NotRelevantKey) is { } value
        && ParseBooks(value).Contains(bookLinkName, StringComparer.OrdinalIgnoreCase);

    public static bool IsLater(Page page) =>
        string.Equals(page.GetPageProperty(LaterKey)?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

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

        var books = ParseBooks(page.GetPageProperty(NotRelevantKey) ?? "").ToList();
        if (notRelevant)
        {
            books.Add(bookLinkName);
        }
        else
        {
            books.RemoveAll(b => string.Equals(b, bookLinkName, StringComparison.OrdinalIgnoreCase));
        }

        if (books.Count == 0)
        {
            page.RemovePageProperty(NotRelevantKey);
        }
        else
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
            page.RemovePageProperty(LaterKey);
        }

        return true;
    }
}

/// <summary>Which marked journal days are hidden; "nicht relevant" counts for <paramref name="Book"/> only (none without a book).</summary>
public sealed record DayMarkFilter(bool HideNotRelevant, bool HideLater, string? Book)
{
    public bool Hides(Page page) =>
        (HideLater && DayMarks.IsLater(page))
        || (HideNotRelevant && Book is not null && DayMarks.IsNotRelevant(page, Book));
}
