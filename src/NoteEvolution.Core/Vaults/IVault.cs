using NoteEvolution.Core.Books;
using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Vaults;

/// <summary>The in-memory set of parsed pages that all services work against.</summary>
public interface IVault
{
    string Root { get; }

    VaultSettings Settings { get; }

    /// <summary>All pages (books and notes) in vault order; a snapshot that does not change afterwards.</summary>
    IReadOnlyList<Page> Pages { get; }

    /// <summary>The book views of all book pages, always matching the current <see cref="Pages"/>.</summary>
    IReadOnlyList<Book> Books { get; }

    /// <summary>Compares full paths, ignoring case.</summary>
    Page? FindPageByPath(string path);

    /// <summary>Finds a book by <see cref="Book.LinkName"/>, ignoring case.</summary>
    Book? FindBook(string linkName);

    /// <summary>Finds a block by its <c>id::</c> property.</summary>
    (Page Page, Block Block)? FindBlockById(Guid id);

    (Page Page, Block Block)? FindBlockByKey(Guid key);

    /// <summary>Replaces the page with the same path, or adds it; raises <see cref="PageReplaced"/> afterwards.</summary>
    void ReplacePage(Page page);

    void RemovePage(string path);

    event Action<Page>? PageReplaced;
}
