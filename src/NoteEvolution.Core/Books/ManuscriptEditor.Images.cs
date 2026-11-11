using NoteEvolution.Core.Model;
using NoteEvolution.Core.Text;

namespace NoteEvolution.Core.Books;

public static partial class ManuscriptEditor
{
    /// <summary>
    /// A new text block with the text <paramref name="markdown"/> (an image) after the element <paramref name="current"/>:
    /// after a text block directly after it; at a detail after the detail's text block; at a heading as the first text
    /// block of its section. With <paramref name="now"/> it gets both dates.
    /// </summary>
    /// <returns>The <see cref="Block.Key"/> of the new block.</returns>
    /// <exception cref="ArgumentException">The element is not in the book (or in the appendix).</exception>
    public static Guid InsertImageBlock(Book book, BookElement current, string markdown, DateTimeOffset? now = null)
    {
        var block = BlockOf(book, current);
        Block? parent;
        int index;
        if (current.Kind == ElementKind.Heading)
        {
            parent = block;
            index = 0;
        }
        else
        {
            var owner = BookElements.TextBlockOf(book, current)!.Block;
            parent = owner.Parent;
            index = IndexOf(book.Page, owner) + 1;
        }

        return Insert(book, parent, index, markdown, ElementKind.TextBlock, current, now);
    }

    /// <summary>
    /// Gives the <paramref name="imageIndex"/>-th image (0-based) in the text of the text block or detail
    /// <paramref name="elementKey"/> the caption <paramref name="caption"/> (<see cref="BlockImages.WithCaption"/> on the
    /// block's content); with <paramref name="now"/>, <c>updated::</c> is stamped on the referring text block as for a
    /// text change.
    /// </summary>
    /// <returns><c>false</c> (nothing changed) when the caption is already that.</returns>
    /// <exception cref="ArgumentException">The element is a heading or not in the book, or the text has no such image.</exception>
    public static bool SetImageCaption(Book book, Guid elementKey, int imageIndex, string caption, DateTimeOffset? now = null)
    {
        var element = ReferringElement(book, elementKey);
        var block = BlockOf(book, element);
        var content = block.Content;
        if (imageIndex < 0 || imageIndex >= BlockImages.Find(content).Count)
        {
            throw new ArgumentException("The element's text has no such image.", nameof(imageIndex));
        }

        var changed = BlockImages.WithCaption(content, imageIndex, caption);
        if (changed == content)
        {
            return false;
        }

        var owner = BookElements.TextBlockOf(book, element)!.Block;
        Changing([block, owner], () =>
        {
            block.SetContent(changed);
            if (now is { } time)
            {
                BlockDates.StampChanged(owner, time);
            }
        });
        return true;
    }

    /// <summary>
    /// The 0-based index of the image at <paramref name="shownOffset"/> in the text the editor shows of the text block or
    /// detail <paramref name="elementKey"/> (each image, block reference and citation is one position there, see
    /// <see cref="ContentOffset"/>), or <c>null</c> if there is no image at that position.
    /// </summary>
    /// <exception cref="ArgumentException">The element is a heading or not in the book.</exception>
    public static int? ImageIndexAt(Book book, Guid elementKey, int shownOffset)
    {
        var element = ReferringElement(book, elementKey);
        Func<string, string> editorText = element.Kind == ElementKind.TextBlock ? BlockTextEscape.Unescape : BookSnapshot.ParagraphText;
        var shown = InlineMarkdown.ShownChars(editorText(BlockOf(book, element).Content));
        if (shownOffset < 0 || shownOffset >= shown.Count || !IsImage(shown[shownOffset].Char))
        {
            return null;
        }

        return shown.Take(shownOffset).Count(c => IsImage(c.Char));
    }

    private static bool IsImage(string shownChar) => BlockImages.LengthAt(shownChar, 0) == shownChar.Length;
}
