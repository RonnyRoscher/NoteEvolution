namespace NoteEvolution.Core.Books;

/// <summary>What an element of the manuscript is: a heading, a text block or a detail (a descendant block of a text block).</summary>
public enum ElementKind
{
    Heading,
    TextBlock,
    Detail,
}

/// <summary>An element of the manuscript, e.g. the one at the cursor.</summary>
/// <param name="Kind">Whether the element is a heading, a text block or a detail.</param>
/// <param name="Key"><see cref="Model.Block.Key"/> of the heading, the text block or the detail block.</param>
public sealed record BookElement(ElementKind Kind, Guid Key);

/// <summary>Locates the elements of a <see cref="Book"/> (headings, text blocks, details) by their block key.</summary>
public static class BookElements
{
    /// <summary>The element whose block has <paramref name="key"/>; <c>null</c> if no heading, text block or detail has it.</summary>
    public static BookElement? Find(Book book, Guid key)
    {
        if (book.FindNode(key) is { Block: not null })
        {
            return new BookElement(ElementKind.Heading, key);
        }

        if (book.FindTextBlock(key) is not null)
        {
            return new BookElement(ElementKind.TextBlock, key);
        }

        return FindDetailOwner(book, key) is not null ? new BookElement(ElementKind.Detail, key) : null;
    }

    /// <summary>
    /// The key of the heading the element belongs to: a heading's own key, else the heading of the (detail's) text
    /// block; <see cref="Guid.Empty"/> for the prologue.
    /// </summary>
    /// <exception cref="ArgumentException">The element is not in the book.</exception>
    public static Guid SectionOf(Book book, BookElement element) =>
        element.Kind == ElementKind.Heading ? Heading(book, element).Key : TextBlock(book, element).Section.Key;

    /// <summary>The text block itself or the detail's text block; <c>null</c> for a heading.</summary>
    /// <exception cref="ArgumentException">The element is not in the book.</exception>
    public static TextBlock? TextBlockOf(Book book, BookElement element)
    {
        if (element.Kind == ElementKind.Heading)
        {
            Heading(book, element);
            return null;
        }

        return TextBlock(book, element);
    }

    /// <summary>
    /// The distinct notes (block ids of <c>source::</c>) the element is based on, in file order: for a heading those of
    /// all text blocks of its whole section, for a text block its own, for a detail those of its text block.
    /// </summary>
    /// <exception cref="ArgumentException">The element is not in the book.</exception>
    public static IReadOnlyList<Guid> SourcesOf(Book book, BookElement element)
    {
        var textBlocks = element.Kind == ElementKind.Heading
            ? Heading(book, element).AllTextBlocks()
            : [TextBlock(book, element)];
        return [.. textBlocks.SelectMany(textBlock => textBlock.Sources).Distinct()];
    }

    /// <summary>The key of the last heading in file order; <c>null</c> if the book has none.</summary>
    public static Guid? LastHeadingKey(Book book)
    {
        // The last heading in file order is the last one of the last heading's sub-sections, recursively.
        var node = book.Root;
        while (node.Children.LastOrDefault() is { } last)
        {
            node = last;
        }

        return node.Block is null ? null : node.Key;
    }

    private static OutlineNode Heading(Book book, BookElement element) =>
        book.FindNode(element.Key) is { Block: not null } node
            ? node
            : throw new ArgumentException("There is no such heading in the book.", nameof(element));

    private static TextBlock TextBlock(Book book, BookElement element)
    {
        var textBlock = element.Kind == ElementKind.TextBlock ? book.FindTextBlock(element.Key) : FindDetailOwner(book, element.Key);
        return textBlock ?? throw new ArgumentException($"There is no such {element.Kind} in the book.", nameof(element));
    }

    /// <summary>The text block with a paragraph (detail) whose block has <paramref name="key"/>, or <c>null</c>.</summary>
    private static TextBlock? FindDetailOwner(Book book, Guid key) =>
        book.Root.AllTextBlocks().FirstOrDefault(textBlock => textBlock.Paragraphs.Any(p => p.Block.Key == key));
}
