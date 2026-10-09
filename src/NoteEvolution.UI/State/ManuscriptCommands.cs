using Microsoft.Extensions.Logging;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Links;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Storage;
using NoteEvolution.UI.Editor;

namespace NoteEvolution.UI.State;

/// <summary>
/// The structure commands of the manuscript (spec 3) on the element at the editor cursor
/// (<see cref="AppState.CurrentElement"/>), and Merge and Wrap on the marked range (<see cref="AppState.CurrentRange"/>,
/// package B, spec 4, 5), shared by the editor's shortcuts and the buttons of the marking. Every command is one entry
/// of the session's undo stack.
/// </summary>
public static class ManuscriptCommands
{
    /// <summary>
    /// Whether <paramref name="command"/> is possible now: the book is writable, no conflict is open for it (R26), and
    /// the command fits the cursor element (e.g. no indent without a previous heading of the same level, no outdent
    /// on level 1, indent, outdent and remove only on headings). Delete only takes the element the cursor itself is in
    /// (<see cref="TargetOf"/>). An irregular section (a heading in it that is not deeper) is no exception: the editor
    /// marks what the book tree holds (package B), which is what the commands act on. Merge needs a group of at least
    /// two text blocks in the marked range (<see cref="RangeInfo.MergeGroups"/>), Wrap a range head that
    /// <see cref="ManuscriptEditor.CanWrap"/> takes (not the whole book, no detail).
    /// </summary>
    public static bool CanRun(AppState state, SectionCommand command) =>
        state is { Session: { } session, CurrentBook: { Page.IsReadOnly: false } book }
        && !session.HasOpenConflict(book.Page.FilePath)
        && Applies(state, book, command);

    /// <summary>
    /// Runs <paramref name="command"/> on the cursor element. The editor saves its text first; text it could not save
    /// refuses the command before anything changes. A read-only book or an open conflict refuses it too; a command
    /// that does not fit the element (<see cref="CanRun"/>) does nothing. The structure commands save the page through
    /// <see cref="VaultSession.TrySave"/> and push an undo action (<paramref name="undoDescription"/>) that is refused
    /// once the page changed after the command; removing a heading gets a targeted undo instead
    /// (<see cref="RemovedHeading"/>), which later edits elsewhere do not block. Deleting a text block, a heading or a
    /// detail with linked blocks goes through the link service, which also removes the usages from the notes; it is
    /// refused while a conflict is open for one of those notes' pages (R26). Merge goes through the link service as well
    /// (with its own undo action), refused the same way for the note pages of the merged blocks' sources; it reveals the
    /// first remaining block. Wrap gets a targeted undo (<see cref="WrapUndo"/>) and reveals the new heading. Whatever
    /// happens, the book view is taken from the vault again, the new or moved element is revealed (the cursor goes
    /// there, the range back to level 0) and <see cref="AppState.Notify"/> is raised.
    /// </summary>
    /// <returns>The message to show, or <c>null</c> if the command ran (or did nothing) without remark.</returns>
    public static async Task<AdoptMessage?> RunAsync(AppState state, SectionCommand command, string undoDescription, ILogger logger)
    {
        Guid? reveal = null;
        try
        {
            (var message, reveal) = await TryRunAsync(state, command, undoDescription, logger);
            return message;
        }
        finally
        {
            // On success the book changed; after a failure the vault holds the page as it is on disk again.
            state.RefreshBook();
            if (reveal is { } key)
            {
                state.RevealElement(key);
            }

            state.Notify();
        }
    }

    private static async Task<(AdoptMessage? Message, Guid? Reveal)> TryRunAsync(
        AppState state, SectionCommand command, string undoDescription, ILogger logger)
    {
        try
        {
            if (state.FlushEditor is { } flush)
            {
                await flush();
            }
        }
        catch (Exception ex)
        {
            // The editor's text may not be saved, so the page is not changed behind it.
            logger.LogError(ex, "Saving the editor before a structure command failed");
            return (new AdoptMessage("SectionCommandUnsaved", true), null);
        }

        if (state.HasUnsavedEditorText?.Invoke() == true)
        {
            return (new AdoptMessage("SectionCommandUnsaved", true), null);
        }

        // The flush may have saved the page or changed the state, so everything is read after it, the book from the vault.
        if (state.Session is not { } session || state.CurrentBook is not { } current
            || session.Vault.FindBook(current.LinkName) is not { } book)
        {
            return (null, null);
        }

        if (book.Page.IsReadOnly)
        {
            return (new AdoptMessage("EditorReadOnly", true), null);
        }

        if (session.HasOpenConflict(book.Page.FilePath))
        {
            return (new AdoptMessage("EditorConflict", true), null);
        }

        if (!Applies(state, book, command))
        {
            return (null, null);
        }

        if (command == SectionCommand.Merge)
        {
            return Merge(session, book, RangeOf(state, book)!.MergeGroups, logger);
        }

        // Wrap acts on the head of the range, every other command on its target element.
        var element = command == SectionCommand.Wrap ? RangeOf(state, book)!.Head! : TargetOf(state, book, command)!;
        if (command != SectionCommand.Delete)
        {
            return RunStructure(session, book, element, command, undoDescription, logger);
        }

        // The notes whose usages a delete removes are written directly by the link service (R26).
        var linked = SourceValue.LinkedBlocksIn(BlockOf(book, element)).ToList();
        var notePages = NotePagesOf(session, linked.SelectMany(block => block.Sources));
        if (notePages.Any(session.HasOpenConflict))
        {
            return (new AdoptMessage("EditorConflict", true), null);
        }

        return element.Kind != ElementKind.Detail || linked.Count > 0
            ? (Delete(session, book, element, notePages, logger), null)
            : RunStructure(session, book, element, command, undoDescription, logger);
    }

    /// <summary>
    /// Whether <paramref name="command"/> applies in <paramref name="book"/> now: Merge and Wrap to the marked range
    /// (<see cref="RangeOf"/>), every other command to its target element (<see cref="TargetOf"/>, <see cref="Fits"/>).
    /// </summary>
    private static bool Applies(AppState state, Book book, SectionCommand command) => command switch
    {
        SectionCommand.Merge => RangeOf(state, book) is { MergeGroups.Count: > 0 },
        SectionCommand.Wrap => RangeOf(state, book) is { Head: { } head } && ManuscriptEditor.CanWrap(book, head),
        _ => TargetOf(state, book, command) is { } element && Fits(book, element, command),
    };

    /// <summary>
    /// The marked range in <paramref name="book"/>, as <see cref="AppState.CurrentRange"/> has it: the range of
    /// <see cref="AppState.RangeLevel"/> around <see cref="AppState.CurrentElement"/>; <c>null</c> if the book does not
    /// have that element.
    /// </summary>
    private static RangeInfo? RangeOf(AppState state, Book book) =>
        state.CurrentElement is { } element && BookElements.Find(book, element.Key) == element
            ? BookRanges.Of(book, element, state.RangeLevel)
            : null;

    /// <summary>The distinct files of the pages holding the notes with the block ids <paramref name="noteIds"/>.</summary>
    private static List<string> NotePagesOf(VaultSession session, IEnumerable<Guid> noteIds) =>
        [.. noteIds.Distinct().Select(id => session.Vault.FindBlockById(id)?.Page.FilePath).OfType<string>().Distinct()];

    /// <summary>
    /// The element <paramref name="command"/> acts on: <see cref="AppState.CurrentElement"/>, but for Delete only the
    /// element with the cursor's own key in <paramref name="book"/>, never the fallback (a text block or a whole section
    /// the user may not see as the cursor's place); <c>null</c> if there is none.
    /// </summary>
    private static BookElement? TargetOf(AppState state, Book book, SectionCommand command) =>
        command != SectionCommand.Delete ? state.CurrentElement
        : state.Cursor is { } cursor ? BookElements.Find(book, cursor.Key)
        : null;

    /// <summary>Whether <paramref name="command"/> fits <paramref name="element"/>, which must be in <paramref name="book"/>.</summary>
    private static bool Fits(Book book, BookElement element, SectionCommand command) =>
        BookElements.Find(book, element.Key) == element
        && command switch
        {
            SectionCommand.InsertAfter or SectionCommand.Delete => true,
            SectionCommand.InsertChild => ManuscriptEditor.CanInsertChild(book, element),
            SectionCommand.Indent => element.Kind == ElementKind.Heading && ManuscriptEditor.CanIndent(book, element.Key),
            SectionCommand.Outdent => element.Kind == ElementKind.Heading && ManuscriptEditor.CanOutdent(book, element.Key),
            SectionCommand.RemoveHeading => element.Kind == ElementKind.Heading,
            _ => false,
        };

    /// <summary>The block of <paramref name="element"/>, which must be in <paramref name="book"/>.</summary>
    private static Block BlockOf(Book book, BookElement element) => element.Kind switch
    {
        ElementKind.Heading => book.FindNode(element.Key)!.Block!,
        ElementKind.TextBlock => book.FindTextBlock(element.Key)!.Block,
        _ => BookElements.TextBlockOf(book, element)!.Paragraphs.First(p => p.Block.Key == element.Key).Block,
    };

    /// <summary>
    /// Runs a <see cref="ManuscriptEditor"/> command on the page, saves it and pushes its undo action (for removing a
    /// heading the targeted <see cref="RemoveHeadingUndo"/>, for Wrap on the range head <paramref name="element"/> the
    /// targeted <see cref="WrapUndo"/>, otherwise <see cref="StructureUndo"/>). If saving fails
    /// for another reason than a change by another program or a conflict (both handled by the session), the page is
    /// read from the file again.
    /// </summary>
    /// <returns>The message, and the element to reveal (none after deleting a detail).</returns>
    private static (AdoptMessage? Message, Guid? Reveal) RunStructure(
        VaultSession session, Book book, BookElement element, SectionCommand command, string undoDescription, ILogger logger)
    {
        var page = book.Page;
        var before = PageStructureSnapshot.Capture(page);
        RemovedHeading? removed = null;
        WrappedSection? wrapped = null;
        Guid? reveal = null;
        try
        {
            switch (command)
            {
                case SectionCommand.InsertAfter:
                    reveal = ManuscriptEditor.InsertAfter(book, element);
                    break;
                case SectionCommand.InsertChild:
                    reveal = ManuscriptEditor.InsertChild(book, element);
                    break;
                case SectionCommand.Indent:
                    ManuscriptEditor.Indent(book, element.Key);
                    reveal = element.Key;
                    break;
                case SectionCommand.Outdent:
                    ManuscriptEditor.Outdent(book, element.Key);
                    reveal = element.Key;
                    break;
                case SectionCommand.RemoveHeading:
                    removed = ManuscriptEditor.RemoveHeading(book, element.Key);
                    reveal = removed.Reveal;
                    break;
                case SectionCommand.Wrap:
                    wrapped = ManuscriptEditor.Wrap(book, element);
                    reveal = wrapped.NewHeadingKey;
                    break;
                default:
                    ManuscriptEditor.DeleteDetail(book, element.Key);
                    break;
            }
        }
        catch (ArgumentException ex)
        {
            // Refused before anything changed.
            logger.LogWarning(ex, "The structure command {Command} was refused", command);
            return (new AdoptMessage("SectionCommandFailed", true), null);
        }

        if (!session.TrySave(page, out var error))
        {
            return (SaveFailed(session, page, error), null);
        }

        session.Undo.Push(
            removed is not null ? new RemoveHeadingUndo(undoDescription, session, page.FilePath, removed)
            : wrapped is not null ? new WrapUndo(undoDescription, session, book.LinkName, wrapped)
            : new StructureUndo(undoDescription, session, page.FilePath, before, PageStructureSnapshot.Capture(page)));
        return (null, reveal);
    }

    /// <summary>
    /// The message after <see cref="VaultSession.TrySave"/> failed. A change by another program was merged or reported
    /// as a conflict by the session, and an open conflict keeps the local page for its resolution; after any other
    /// failure the page is read from the file again, so the editor shows what is on disk. A merged external change
    /// brings the command's change into the file together with it, but without an undo entry (none is pushed after a
    /// failed save).
    /// </summary>
    private static AdoptMessage SaveFailed(VaultSession session, Page page, Exception? error)
    {
        if (session.HasOpenConflict(page.FilePath))
        {
            return new AdoptMessage("EditorConflict", true);
        }

        if (error is FileChangedExternallyException)
        {
            return new AdoptMessage("EditorExternalChange", false);
        }

        PageReload.FromDisk(session, page);
        return new AdoptMessage("SectionCommandFailed", true);
    }

    /// <summary>
    /// Deletes a text block, a heading with its section or a detail through the link service, which removes the usages
    /// from the notes (on <paramref name="notePages"/>) and pushes its own undo action („Löschen“).
    /// </summary>
    private static AdoptMessage? Delete(
        VaultSession session, Book book, BookElement element, IReadOnlyList<string> notePages, ILogger logger) =>
        RunLinked(session, book, notePages, logger, $"Deleting a {element.Kind}", () =>
        {
            switch (element.Kind)
            {
                case ElementKind.Heading:
                    session.Links.DeleteSection(book, element.Key);
                    break;
                case ElementKind.TextBlock:
                    session.Links.DeleteTextBlock(book, element.Key);
                    break;
                default:
                    session.Links.DeleteDetail(book, element.Key);
                    break;
            }
        });

    /// <summary>
    /// Merges the text blocks of each of <paramref name="groups"/> (the range's <see cref="RangeInfo.MergeGroups"/>)
    /// through the link service, which moves the notes' usages of the removed blocks to the remaining one and pushes its
    /// own undo action („Zusammenfügen“). Refused while a conflict is open for the page of a note that is a source of a
    /// merged block (R26).
    /// </summary>
    /// <returns>The message, and the first remaining block to reveal (none if the merge failed).</returns>
    private static (AdoptMessage? Message, Guid? Reveal) Merge(
        VaultSession session, Book book, IReadOnlyList<IReadOnlyList<Guid>> groups, ILogger logger)
    {
        var notePages = NotePagesOf(session, groups.SelectMany(group => group)
            .Select(book.FindTextBlock).OfType<TextBlock>().SelectMany(textBlock => textBlock.Sources));
        if (notePages.Any(session.HasOpenConflict))
        {
            return (new AdoptMessage("EditorConflict", true), null);
        }

        Guid? first = null;
        var message = RunLinked(session, book, notePages, logger, "Merging text blocks",
            () => first = session.Links.MergeTextBlocks(book, groups));
        return (message, first);
    }

    /// <summary>
    /// Changes <paramref name="book"/> and the notes on <paramref name="notePages"/> through the link service
    /// (<paramref name="change"/>, which writes the files and pushes its own undo action), then updates the search index
    /// for those notes. A change of the book by another program is handled by the session.
    /// </summary>
    /// <param name="what">What <paramref name="change"/> does, for the log.</param>
    private static AdoptMessage? RunLinked(
        VaultSession session, Book book, IReadOnlyList<string> notePages, ILogger logger, string what, Action change)
    {
        var bookPath = book.Page.FilePath;
        try
        {
            change();
        }
        catch (FileChangedExternallyException)
        {
            session.HandleExternalChange(bookPath);
            return session.HasOpenConflict(bookPath)
                ? new AdoptMessage("EditorConflict", true)
                : new AdoptMessage("EditorExternalChange", false);
        }
        catch (ReadOnlyPageException)
        {
            return new AdoptMessage("EditorReadOnly", true);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Includes the page writer refusing a result that would not parse safely (nothing was written).
            logger.LogWarning(ex, "{What} failed", what);
            return new AdoptMessage("SectionCommandFailed", true);
        }

        // The search index knows which notes are used; it is updated for the notes whose used-in changed.
        foreach (var path in notePages)
        {
            try
            {
                session.Search.UpdatePage(session.Notes, path);
            }
            catch (Exception ex)
            {
                // The change is done; a stale search index (an aid, rebuilt on opening) must not hide that.
                logger.LogWarning(ex, "Updating the search index for {Path} failed", path);
            }
        }

        return null;
    }

    /// <summary>
    /// Undoes a structure command: puts the page of <paramref name="path"/> back to its state <paramref name="before"/>
    /// the command and saves it, but only while the page is exactly what the command left (<paramref name="after"/>);
    /// a later change (e.g. a title typed into a new heading) is never overwritten.
    /// </summary>
    private sealed class StructureUndo(
        string description, VaultSession session, string path, PageStructureSnapshot before, PageStructureSnapshot after) : IUndoAction
    {
        public string Description => description;

        /// <exception cref="InvalidOperationException">The page changed after the command, or saving it failed.</exception>
        public void Undo()
        {
            var page = session.Vault.FindPageByPath(path);
            if (page is null || !after.Matches(page))
            {
                throw new InvalidOperationException($"'{path}' changed after the structure command; it is not undone.");
            }

            before.RestoreInto(page);
            SaveUndone(session, page, path);
        }
    }

    /// <summary>
    /// Undoes removing a heading (<see cref="RemovedHeading.RestoreInto"/>) and saves the page: the heading comes back
    /// around the blocks it held, also after later edits elsewhere or in those blocks' text. Refused (no change) when
    /// the heading's former parent is gone, a moved block is gone or was moved elsewhere, or the heading is back.
    /// </summary>
    private sealed class RemoveHeadingUndo(string description, VaultSession session, string path, RemovedHeading removed) : IUndoAction
    {
        public string Description => description;

        /// <exception cref="InvalidOperationException">The heading cannot be put back, or saving the page failed.</exception>
        public void Undo()
        {
            var page = session.Vault.FindPageByPath(path);
            if (page is null || !removed.CanRestore(page))
            {
                throw new InvalidOperationException($"The heading removed from '{path}' cannot be put back; it is not undone.");
            }

            removed.RestoreInto(page);
            SaveUndone(session, page, path);
        }
    }

    /// <summary>
    /// Undoes putting a range under a new heading (<see cref="WrappedSection.RestoreInto"/>) in the book
    /// <paramref name="linkName"/> and saves its page: the new heading goes and the moved blocks come back, also after
    /// later edits of the heading's title or of those blocks' text. Refused (no change) when the new heading is gone or
    /// holds other blocks, or a moved block was moved elsewhere.
    /// </summary>
    private sealed class WrapUndo(string description, VaultSession session, string linkName, WrappedSection wrapped) : IUndoAction
    {
        public string Description => description;

        /// <exception cref="InvalidOperationException">The structure cannot be put back, or saving the page failed.</exception>
        public void Undo()
        {
            var book = session.Vault.FindBook(linkName);
            if (book is null || !wrapped.CanRestore(book))
            {
                throw new InvalidOperationException($"The range put under a new heading in '{linkName}' cannot be put back; it is not undone.");
            }

            wrapped.RestoreInto(book);
            SaveUndone(session, book.Page, book.Page.FilePath);
        }
    }

    /// <summary>Saves the page after an undo; if that fails, the vault takes the file's version again where it should.</summary>
    /// <exception cref="InvalidOperationException">Saving failed.</exception>
    private static void SaveUndone(VaultSession session, Page page, string path)
    {
        if (!session.TrySave(page, out var error))
        {
            if (error is not FileChangedExternallyException && !session.HasOpenConflict(path))
            {
                // The restored page was not written: the vault takes the file's version again.
                PageReload.FromDisk(session, page);
            }

            throw new InvalidOperationException($"Saving '{path}' after undoing the structure command failed.", error);
        }
    }
}
