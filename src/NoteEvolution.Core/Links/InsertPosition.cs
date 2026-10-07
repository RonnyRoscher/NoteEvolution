namespace NoteEvolution.Core.Links;

/// <summary>Where <see cref="ILinkService.Adopt"/> inserts the new text block into the book.</summary>
public abstract record InsertPosition
{
    private InsertPosition()
    {
    }

    /// <summary>Directly after the text block with <see cref="Model.Block.Key"/> <paramref name="TextBlockKey"/>.</summary>
    public sealed record After(Guid TextBlockKey) : InsertPosition;

    /// <summary>
    /// Before the first text block of the section (<see cref="Books.OutlineNode.Key"/>; <see cref="Guid.Empty"/> = prologue);
    /// at the section's start if it has none.
    /// </summary>
    public sealed record SectionStart(Guid SectionKey) : InsertPosition;

    /// <summary>
    /// After the last text block of the section and before the sub-heading that follows it
    /// (<see cref="Guid.Empty"/> = prologue); at the section's start if it has no text block.
    /// </summary>
    public sealed record SectionEnd(Guid SectionKey) : InsertPosition;
}
