using NoteEvolution.Core.Books;
using NoteEvolution.Core.Storage;

namespace NoteEvolution.Core.Links;

/// <param name="TextBlockKey"><see cref="Model.Block.Key"/> of the new book block.</param>
/// <param name="TextBlockId">The new book block's <c>id::</c>.</param>
/// <param name="NoteUpdatePending">The note could not be written; its update waits in <c>pending.json</c>.</param>
public sealed record AdoptResult(Guid TextBlockKey, Guid TextBlockId, bool NoteUpdatePending);

/// <summary>
/// The operations that keep both sides of a link consistent: <c>source::</c> on the book block and
/// <c>used-in::</c> on the note. Each writes the book first, then the notes. The methods that take a
/// <see cref="Book"/> change <c>book.Page</c>; callers rebuild their view afterwards with
/// <see cref="Book.Load"/>. If the book cannot be written, the exception is passed on and the vault holds the
/// book page as it is on disk again (take it from <see cref="Vaults.IVault.FindBook"/>). A note that cannot be
/// written (or is read-only) gets its update recorded in <c>pending.json</c> instead.
/// </summary>
public interface ILinkService
{
    /// <summary>
    /// Copies the note block with its subtree (without property lines, and without a <c>#notiz</c> tag in the note
    /// block's own text) into the book at <paramref name="position"/>, gives the copy an <c>id::</c>,
    /// <c>source:: ((note id))</c> and <c>status:: relevant</c>, then adds the usage to the note's <c>used-in::</c>.
    /// Can be undone („Übernehmen“).
    /// </summary>
    /// <exception cref="ArgumentException">Unknown note block or position.</exception>
    /// <exception cref="ReadOnlyPageException">The book page is read-only.</exception>
    /// <exception cref="InvalidOperationException">The note's page is read-only; nothing was changed.</exception>
    AdoptResult Adopt(Book book, Guid noteBlockKey, InsertPosition position);

    /// <summary>
    /// Copies the note block's text and sub-bullets (without property lines, and without a <c>#notiz</c> tag in the
    /// note block's own text) into an existing text block or detail (<paramref name="position"/>): each new top-level
    /// detail (the copy, or at the cursor each of the note's sub-bullets) gets <c>status:: relevant</c>, the element at
    /// the cursor keeps its status. It adds the note's id to the enclosing text block's <c>source::</c> unless it is
    /// there (the text block gets an <c>id::</c> if needed; a detail never gets a source of its own), then adds the
    /// usage to the note's <c>used-in::</c>. The result names the enclosing text block. Can be undone („Übernehmen“)
    /// as long as the text block with its details is still as this left it.
    /// </summary>
    /// <exception cref="ArgumentException">Unknown note block or position, or the text would not stay one block.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The offset of <see cref="IntoPosition.AtCursor"/> is outside the element's text.</exception>
    /// <exception cref="ReadOnlyPageException">The book page is read-only.</exception>
    /// <exception cref="InvalidOperationException">The note's page is read-only; nothing was changed.</exception>
    AdoptResult AdoptInto(Book book, Guid noteBlockKey, IntoPosition position);

    /// <summary>
    /// Fully links an existing text block and a note block (a confirmed placement): gives both an <c>id::</c>, adds
    /// the note's id to the text block's <c>source::</c>, then sets <c>used-in:: [[book]] ((text block id))</c> on the
    /// note, replacing an entry for the same book that has no block reference. Writes only property lines; what is
    /// there already is not written again (a fully linked pair writes nothing). Records no undo action.
    /// </summary>
    /// <exception cref="ArgumentException">Unknown text block (a linked paragraph does not count) or note block.</exception>
    /// <exception cref="ReadOnlyPageException">The book page is read-only.</exception>
    /// <exception cref="InvalidOperationException">The note's page is read-only; nothing was changed.</exception>
    void Link(Book book, Guid textBlockKey, Guid noteBlockKey);

    /// <summary>Removes <paramref name="noteBlockId"/> from the text block's <c>source::</c> and the usage from the note.</summary>
    void RemoveSource(Book book, Guid textBlockKey, Guid noteBlockId);

    /// <summary>Deletes the text block with its paragraphs and removes its usages from the notes. Can be undone („Löschen“).</summary>
    void DeleteTextBlock(Book book, Guid textBlockKey);

    /// <summary>
    /// Deletes the detail with its deeper details and removes the usages of the linked blocks among them (a detail
    /// with its own <c>id::</c> and <c>source::</c>) from the notes. Can be undone („Löschen“).
    /// </summary>
    /// <exception cref="ArgumentException">No detail has this key.</exception>
    /// <exception cref="ReadOnlyPageException">The book page is read-only.</exception>
    void DeleteDetail(Book book, Guid detailKey);

    /// <summary>
    /// Deletes the heading with everything below it (sub-sections, text blocks, paragraphs) and removes the usages of
    /// all linked text blocks in it from the notes. One undo action („Löschen“) puts the section and the notes back.
    /// </summary>
    /// <exception cref="ArgumentException">Unknown heading, or the root.</exception>
    /// <exception cref="ReadOnlyPageException">The book page is read-only.</exception>
    void DeleteSection(Book book, Guid headingKey);

    /// <summary>
    /// Merges each group of text blocks (keys of sibling text blocks, the first one stays) into its first block: the
    /// first block's content becomes the contents of all blocks joined by a blank line (<c>"\n\n"</c>; blank contents
    /// are left out, so an empty first block takes the next text), the details of the later blocks are moved, in order,
    /// behind its details, and the later blocks are removed. The first block gets the union of all <c>source::</c>
    /// values (first-seen order); without an <c>id::</c> of its own it takes over the <c>id::</c> of the first later
    /// block that has one, else it gets a new one if a later block was linked. The later blocks' other properties are
    /// carried over where the first block lacks the key (not <c>created::</c> / <c>updated::</c>: the first block gets
    /// the earliest readable <c>created::</c> of the group and a new <c>updated::</c>). Then every note whose
    /// <c>used-in::</c> referenced a removed block references the first block instead, in place; an entry that would
    /// duplicate one already there is dropped.
    /// Can be undone („Zusammenfügen“) as long as the book is still exactly as this left it: the book and the notes
    /// still as this left them get their former bytes back, other notes get their former usages added again.
    /// </summary>
    /// <returns>The key of the first block of the first group.</returns>
    /// <exception cref="ArgumentException">
    /// No group, an unknown text block key or one given twice, a group with fewer than two blocks or with blocks that
    /// are not siblings, or merged content that would not stay one block; nothing was changed.
    /// </exception>
    /// <exception cref="MergeRefusedException">
    /// Blocks of a group have different values for the same property, or the <c>id::</c> of a block that would go is
    /// referenced (<c>((id))</c>) elsewhere in the vault than in the notes' <c>used-in::</c> and the book's
    /// <c>source::</c> values; nothing was changed.
    /// </exception>
    /// <exception cref="ReadOnlyPageException">The book page is read-only.</exception>
    Guid MergeTextBlocks(Book book, IReadOnlyList<IReadOnlyList<Guid>> groups);

    /// <summary>
    /// Merges the details of a text block or a detail (<paramref name="elementKey"/>, the head) into its own text: the
    /// contents of all descendant details without the <c>#notiz</c> tag (depth-first, in file order) are appended to
    /// the head's content, joined as in <see cref="MergeTextBlocks"/> (a blank line; blank contents left out), and
    /// these details are removed. The <c>#notiz</c> details stay, with their own sub-details, as the head's children
    /// in file order (one below a merged detail comes up to the head). The merged details' <c>source::</c> values go
    /// to the enclosing text block's <c>source::</c> (union), and every note whose <c>used-in::</c> referenced a merged
    /// detail references the text block instead (which gets an <c>id::</c> if needed). Other properties of the merged
    /// details are carried over where the head lacks the key, except <c>collapsed::</c> (the view state of a detail
    /// that goes; the head keeps its own) and the dates (a text block head gets the earliest readable
    /// <c>created::</c>; the text block gets a new <c>updated::</c>). Can be undone („Zusammenfügen“) as
    /// <see cref="MergeTextBlocks"/>.
    /// </summary>
    /// <returns>The key of the head.</returns>
    /// <exception cref="ArgumentException">
    /// No text block or detail has this key, it has no detail to merge, or the merged content would not stay one
    /// block; nothing was changed.
    /// </exception>
    /// <exception cref="MergeRefusedException">
    /// A merged detail has a different value for a property of the head (or of an earlier merged detail), or the
    /// <c>id::</c> of a merged detail is referenced (<c>((id))</c>) elsewhere in the vault than in the notes'
    /// <c>used-in::</c> and the book's <c>source::</c> values; nothing was changed.
    /// </exception>
    /// <exception cref="ReadOnlyPageException">The book page is read-only.</exception>
    Guid MergeDetails(Book book, Guid elementKey);

    /// <summary>
    /// Updates the notes after <see cref="BookSync.Apply"/> changed the book and the book was saved:
    /// <see cref="BlockSplit"/> adds the new block's usage to each source note; <see cref="BlockDeleted"/> removes
    /// the block's usage from each source note and records an undo action („Löschen“) that puts the block back.
    /// </summary>
    void ApplySyncEffects(Book book, IReadOnlyList<SyncEffect> effects);

    /// <summary>Adds the entry to the note's <c>used-in::</c> unless it is there; does nothing for an unknown note.</summary>
    void AddUsage(Guid noteBlockId, UsedInEntry entry);

    /// <summary>
    /// Removes the entry with this page (ignoring case) and block id (<c>null</c> = entry without block reference)
    /// from the note's <c>used-in::</c>; does nothing for an unknown note.
    /// </summary>
    void RemoveUsage(Guid noteBlockId, string bookLinkName, Guid? bookBlockId);

    /// <summary>
    /// Tries the note updates in <c>pending.json</c> again; returns how many were completed. Called at startup and
    /// by this service after each of its operations in which every write succeeded.
    /// </summary>
    int RetryPending();
}
