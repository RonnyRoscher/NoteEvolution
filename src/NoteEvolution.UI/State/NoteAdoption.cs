using Microsoft.Extensions.Logging;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Links;
using NoteEvolution.Core.Storage;

namespace NoteEvolution.UI.State;

/// <summary>A message about an adoption: the key of its text in <c>Strings.resx</c> and whether it reports an error.</summary>
public sealed record AdoptMessage(string Key, bool IsError);

/// <summary>Adopting a note into the current book (spec 5.2), shared by the note cards and the editor's drop target.</summary>
public static class NoteAdoption
{
    /// <summary>
    /// Adopts the note block <paramref name="noteBlockKey"/> (a note or one of its sub-bullets) into the current book at
    /// <paramref name="position"/>, which is computed after the editor's text was saved. Always waits for the editor to
    /// save first (<see cref="AppState.FlushEditor"/>). Refuses while a conflict is open for the book or the note page
    /// (R26; <see cref="ILinkService"/> writes through the page writer directly and would overwrite it). A change of the
    /// book file by another program is handed to the session (merge or conflict) and the user adopts again. Whatever
    /// happens, the book view is taken from the vault again and <see cref="AppState.Notify"/> is raised.
    /// </summary>
    /// <returns>The message to show, or <c>null</c> if the note was adopted without remark.</returns>
    public static async Task<AdoptMessage?> AdoptAsync(
        AppState state, Guid noteBlockKey, Func<Book, InsertPosition> position, ILogger logger)
    {
        try
        {
            return await TryAdoptAsync(state, noteBlockKey, position, logger);
        }
        finally
        {
            // On success the book changed; after a failure the vault holds the page as it is on disk again.
            state.RefreshBook();
            state.Notify();
        }
    }

    private static async Task<AdoptMessage?> TryAdoptAsync(
        AppState state, Guid noteBlockKey, Func<Book, InsertPosition> position, ILogger logger)
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
            // The editor's text may not be saved, so nothing is adopted behind its back.
            logger.LogError(ex, "Saving the editor before adopting a note failed");
            return new AdoptMessage("NoteAdoptFailed", true);
        }

        // The flush may have changed the state, so everything is read after it.
        if (state.Session is not { } session || state.CurrentBook is not { } book)
        {
            return null;
        }

        var bookPath = book.Page.FilePath;
        if (book.Page.IsReadOnly)
        {
            return new AdoptMessage("NoteAdoptBookReadOnly", true);
        }

        var notePage = session.Vault.FindBlockByKey(noteBlockKey)?.Page;
        if (notePage is { IsReadOnly: true })
        {
            return new AdoptMessage("NoteAdoptNoteReadOnly", true);
        }

        if (session.HasOpenConflict(bookPath) || (notePage is not null && session.HasOpenConflict(notePage.FilePath)))
        {
            return new AdoptMessage("NoteAdoptConflict", true);
        }

        AdoptResult result;
        try
        {
            result = session.Links.Adopt(book, noteBlockKey, position(book));
        }
        catch (FileChangedExternallyException)
        {
            session.HandleExternalChange(bookPath);
            return new AdoptMessage("NoteAdoptExternalChange", true);
        }
        catch (ReadOnlyPageException)
        {
            return new AdoptMessage("NoteAdoptBookReadOnly", true);
        }
        catch (InvalidOperationException) when (notePage is { IsReadOnly: true })
        {
            return new AdoptMessage("NoteAdoptNoteReadOnly", true);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Includes the page writer refusing a result that would not parse safely (nothing was written).
            logger.LogWarning(ex, "Adopting a note failed");
            return new AdoptMessage("NoteAdoptFailed", true);
        }

        if (notePage is not null)
        {
            try
            {
                session.Search.UpdatePage(session.Notes, notePage.FilePath);
            }
            catch (Exception ex)
            {
                // The note is adopted; a stale search index (an aid, rebuilt on opening) must not hide that.
                logger.LogWarning(ex, "Updating the search index after adopting failed");
            }
        }

        return result.NoteUpdatePending ? new AdoptMessage("NoteUpdatePending", false) : null;
    }
}
