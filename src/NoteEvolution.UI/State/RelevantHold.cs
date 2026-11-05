using NoteEvolution.Core.Books;

namespace NoteEvolution.UI.State;

/// <summary>
/// A text was adopted from the "Relevant" tab: its list is not computed again while the cursor stays in the area around
/// that text, the section <paramref name="AreaKey"/> of the book <paramref name="BookName"/> with its heading and
/// sub-sections. The prologue is an area without them, as it would hold the whole book otherwise.
/// <paramref name="RevealKey"/> is the element the editor reveals after the adoption.
/// </summary>
public sealed record RelevantHold(string BookName, Guid AreaKey, Guid RevealKey = default)
{
    /// <summary>
    /// The hold for the element <paramref name="elementKey"/> the text went to: the section a text block or detail stands
    /// in; for a new heading the section it stands in as well, for a top-level heading its own. <c>null</c> if the
    /// element is not in the book's text (see <see cref="BookElements.Find"/>).
    /// </summary>
    public static RelevantHold? Around(Book book, Guid elementKey)
    {
        if (BookElements.Find(book, elementKey) is not { } element)
        {
            return null;
        }

        var section = book.FindNode(BookElements.SectionOf(book, element))!;
        var area = element.Kind == ElementKind.Heading && section.Parent is { Level: > 0 } parent ? parent : section;
        return new RelevantHold(book.LinkName, area.Key, elementKey);
    }

    /// <summary>Whether <paramref name="book"/> is the book of this hold (a new view of it counts).</summary>
    public bool IsFor(Book book) => string.Equals(book.LinkName, BookName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the section <paramref name="sectionKey"/> (a key the book does not have is its start) is in the area; an
    /// area that is gone from the book covers nothing.
    /// </summary>
    public bool Covers(Book book, Guid sectionKey)
    {
        var section = book.FindNode(sectionKey) ?? book.Root;
        if (AreaKey == book.Root.Key)
        {
            return ReferenceEquals(section, book.Root);
        }

        for (var node = section; node is not null; node = node.Parent)
        {
            if (node.Key == AreaKey)
            {
                return true;
            }
        }

        return false;
    }
}
