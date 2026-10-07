using NoteEvolution.Core.Books;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Vaults;

namespace NoteEvolution.UI.State;

/// <summary>What the header's book selection offers: the books, and the pages that can be turned into one.</summary>
public static class BookChoices
{
    /// <summary>The vault's books, sorted by title.</summary>
    public static IReadOnlyList<Book> Books(IVault vault) =>
        [.. vault.Books.OrderBy(b => b.Title, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>
    /// Pages the draft assistant can turn into a book, sorted by name: not a book yet, readable, and not a journal day
    /// (a file directly in a folder named <c>journals</c>).
    /// </summary>
    public static IReadOnlyList<Page> ConvertiblePages(IVault vault) =>
        [.. vault.Pages
            .Where(p => !Book.IsBook(p) && !p.IsReadOnly && !IsJournal(p))
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>The book whose page is <paramref name="relativePath"/> in the vault, or null if that page is gone or no book.</summary>
    public static Book? Remembered(IVault vault, string? relativePath)
    {
        if (relativePath is null || vault.FindPageByPath(Path.Combine(vault.Root, relativePath)) is not { } page)
        {
            return null;
        }

        return vault.Books.FirstOrDefault(b => ReferenceEquals(b.Page, page));
    }

    private static bool IsJournal(Page page) =>
        string.Equals(Path.GetFileName(Path.GetDirectoryName(page.FilePath)), "journals", StringComparison.OrdinalIgnoreCase);
}
