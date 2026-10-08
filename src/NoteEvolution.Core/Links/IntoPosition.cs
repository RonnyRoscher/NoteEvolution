namespace NoteEvolution.Core.Links;

/// <summary>
/// Where <see cref="ILinkService.AdoptInto"/> puts a note into an existing element of the book: a text block (its own
/// text) or a detail (a descendant block of a text block, <see cref="Books.Paragraph"/>).
/// </summary>
public abstract record IntoPosition
{
    private IntoPosition()
    {
    }

    /// <summary>
    /// Into the text of the element with <see cref="Model.Block.Key"/> <paramref name="ElementKey"/>, at
    /// <paramref name="Offset"/>; the note's sub-bullets become the element's first children.
    /// </summary>
    /// <param name="ElementKey">The text block or the detail.</param>
    /// <param name="Offset">
    /// Counts characters of the element's text as the editor shows it: unescaped
    /// (<see cref="Text.BlockTextEscape.Unescape"/>), without Markdown markers, <c>"\n"</c> counting as 1; for a detail
    /// also without its <c>#notiz</c> tag (<see cref="Books.BookSnapshot.ParagraphText"/>), which stays where it is.
    /// </param>
    public sealed record AtCursor(Guid ElementKey, int Offset) : IntoPosition;

    /// <summary>As the next sibling of the detail with <see cref="Model.Block.Key"/> <paramref name="DetailKey"/>, after its deeper details.</summary>
    public sealed record AfterDetail(Guid DetailKey) : IntoPosition;

    /// <summary>As the first child of the text block or detail with <see cref="Model.Block.Key"/> <paramref name="ElementKey"/>.</summary>
    public sealed record FirstChild(Guid ElementKey) : IntoPosition;
}
