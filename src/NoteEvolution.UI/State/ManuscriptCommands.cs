using Microsoft.Extensions.Logging;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Links;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Storage;
using NoteEvolution.UI.Editor;

namespace NoteEvolution.UI.State;

/// <summary>
/// The structure commands of the manuscript (spec 3) on the element at the editor cursor
/// (<see cref="AppState.CurrentElement"/>), shared by the editor's shortcuts and the buttons of the marking. Every
/// command is one entry of the session's undo stack.
/// </summary>
public static class ManuscriptCommands
{
    /// <summary>
    /// Whether <paramref name="command"/> is possible now: the book is writable, no conflict is open for it (R26), and
    /// the command fits the cursor element (e.g. no indent without a previous heading of the same level, no outdent
    /// on level 1, indent, outdent and remove only on headings).
    /// </summary>
    public static bool CanRun(AppState state, SectionCommand command) =>
        state is { Session: { } session, CurrentBook: { Page.IsReadOnly: false } book }
        && !session.HasOpenConflict(book.Page.FilePath)
        && state.CurrentElement is { } element
        && Fits(book, element, command);

    /// <summary>
    /// Runs <paramref name="command"/> on the cursor element. The editor saves its text first; text it could not save
    /// refuses the command before anything changes. A read-only book or an open conflict refuses it too; a command
    /// that does not fit the element (<see cref="CanRun"/>) does nothing. The structure commands save the page through
    /// <see cref="VaultSession.TrySave"/> and push an undo action (<paramref name="undoDescription"/>) that is refused
    /// once the page changed after the command; deleting a text block or a heading goes through the link service,
    /// which also removes the usages from the notes. Whatever happens, the book view is taken from the vault again,
    /// the new or moved element is revealed (the cursor goes there) and <see cref="AppState.Notify"/> is raised.
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

        if (state.CurrentElement is not { } element || !Fits(book, element, command))
        {
            return (null, null);
        }

        return command == SectionCommand.Delete && element.Kind != ElementKind.Detail
            ? (Delete(session, book, element, logger), null)
            : RunStructure(session, book, element, command, undoDescription, logger);
    }

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

    /// <summary>
    /// Runs a <see cref="ManuscriptEditor"/> command on the page, saves it and pushes its undo action. If saving fails
    /// for another reason than a change by another program or a conflict (both handled by the session), the page is
    /// read from the file again.
    /// </summary>
    /// <returns>The message, and the element to reveal (none after deleting a detail).</returns>
    private static (AdoptMessage? Message, Guid? Reveal) RunStructure(
        VaultSession session, Book book, BookElement element, SectionCommand command, string undoDescription, ILogger logger)
    {
        var page = book.Page;
        var before = PageStructureSnapshot.Capture(page);
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
                    reveal = ManuscriptEditor.RemoveHeading(book, element.Key);
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

        var after = PageStructureSnapshot.Capture(page);
        session.Undo.Push(new StructureUndo(undoDescription, session, page.FilePath, before, after));
        return (null, reveal);
    }

    /// <summary>
    /// The message after <see cref="VaultSession.TrySave"/> failed. A change by another program was merged or reported
    /// as a conflict by the session, and an open conflict keeps the local page for its resolution; after any other
    /// failure the page is read from the file again, so the editor shows what is on disk.
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
    /// Deletes a text block or a heading with its section through the link service, which removes the usages from the
    /// notes and pushes its own undo action („Löschen“).
    /// </summary>
    private static AdoptMessage? Delete(VaultSession session, Book book, BookElement element, ILogger logger)
    {
        var bookPath = book.Page.FilePath;
        var sources = BookElements.SourcesOf(book, element);
        try
        {
            if (element.Kind == ElementKind.Heading)
            {
                session.Links.DeleteSection(book, element.Key);
            }
            else
            {
                session.Links.DeleteTextBlock(book, element.Key);
            }
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
            logger.LogWarning(ex, "Deleting a {Kind} failed", element.Kind);
            return new AdoptMessage("SectionCommandFailed", true);
        }

        // The search index knows which notes are used; it is updated for the notes whose used-in changed.
        foreach (var path in sources.Select(id => session.Vault.FindBlockById(id)?.Page.FilePath).OfType<string>().Distinct())
        {
            try
            {
                session.Search.UpdatePage(session.Notes, path);
            }
            catch (Exception ex)
            {
                // The section is deleted; a stale search index (an aid, rebuilt on opening) must not hide that.
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
}
