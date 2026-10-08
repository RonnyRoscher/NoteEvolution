using Microsoft.Extensions.Logging;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Links;
using NoteEvolution.Core.Storage;

namespace NoteEvolution.UI.State;

/// <summary>A message about an adoption: the key of its text in <c>Strings.resx</c> and whether it reports an error.</summary>
public sealed record AdoptMessage(string Key, bool IsError);

/// <summary>Where a note is adopted relative to the current element (spec 4).</summary>
public enum AdoptVariant
{
    /// <summary>Into the text of the cursor's element, at the cursor.</summary>
    AtCursor,

    /// <summary>A new text block (or detail of the same depth) after the current element.</summary>
    After,

    /// <summary>As the first details below the current text block or detail.</summary>
    Below,
}

/// <summary>Adopting a note into the current book (spec 4 and 5.2), shared by the note cards and the editor's drop target.</summary>
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
    public static Task<AdoptMessage?> AdoptAsync(
        AppState state, Guid noteBlockKey, Func<Book, InsertPosition> position, ILogger logger) =>
        RunAsync(state, noteBlockKey, (links, book) => (links.Adopt(book, noteBlockKey, position(book)), false), logger);

    /// <summary>
    /// Adopts the note block <paramref name="noteBlockKey"/> into the current book the way <paramref name="variant"/>
    /// says (spec 4), relative to <see cref="AppState.CurrentElement"/> and at <see cref="AppState.Cursor"/>'s offset,
    /// both read after the editor's text was saved; with the same checks as the overload with an
    /// <see cref="InsertPosition"/>. The offset is only used for the cursor's own element: when the book does not have
    /// it (the current element is a fallback), the note goes after the fallback element instead. Without a current
    /// element the note becomes a new text block at the end of the last current section, else at the end of the book.
    /// A new text block is revealed in the editor; adopting into an existing element leaves the cursor where it is.
    /// </summary>
    /// <returns>The message to show, or <c>null</c> if the note was adopted without remark.</returns>
    public static Task<AdoptMessage?> AdoptAsync(AppState state, Guid noteBlockKey, AdoptVariant variant, ILogger logger) =>
        RunAsync(state, noteBlockKey, (links, book) => Adopt(links, state, book, noteBlockKey, variant), logger);

    /// <summary>
    /// Adopts the note into the element the variant and the cursor name (see the mapping in spec 4); also returns
    /// whether that made a new text block.
    /// </summary>
    private static (AdoptResult Result, bool NewTextBlock) Adopt(
        ILinkService links, AppState state, Book book, Guid noteBlockKey, AdoptVariant variant)
    {
        // The current element is checked against the book just read from the vault, not the state's (older) one.
        var cursor = state.Cursor is { } c ? BookElements.Find(book, c.Key) : null;
        var element = cursor ?? (state.CurrentElement is { } current ? BookElements.Find(book, current.Key) : null);
        if (element is null)
        {
            // Without a cursor in the book the last current section applies; without one, the end of the book.
            var section = book.FindNode(state.CurrentSectionKey) is { Block: not null }
                ? state.CurrentSectionKey
                : BookElements.LastHeadingKey(book) ?? Guid.Empty;
            return (links.Adopt(book, noteBlockKey, new InsertPosition.SectionEnd(section)), true);
        }

        // The cursor's offset belongs to its own element; it is never applied to a fallback element.
        if (variant == AdoptVariant.AtCursor && cursor is null)
        {
            variant = AdoptVariant.After;
        }

        var offset = state.Cursor?.Offset ?? 0;
        return (element.Kind, variant) switch
        {
            (ElementKind.Heading, _) => (links.Adopt(book, noteBlockKey, new InsertPosition.SectionStart(element.Key)), true),
            (ElementKind.TextBlock, AdoptVariant.After) => (links.Adopt(book, noteBlockKey, new InsertPosition.After(element.Key)), true),
            (_, AdoptVariant.AtCursor) => (links.AdoptInto(book, noteBlockKey, new IntoPosition.AtCursor(element.Key, offset)), false),
            (_, AdoptVariant.After) => (links.AdoptInto(book, noteBlockKey, new IntoPosition.AfterDetail(element.Key)), false),
            _ => (links.AdoptInto(book, noteBlockKey, new IntoPosition.FirstChild(element.Key)), false),
        };
    }

    /// <summary>
    /// Runs an adoption (<paramref name="adopt"/>, which also says whether it made a new text block) with the checks
    /// of <see cref="AdoptAsync(AppState, Guid, Func{Book, InsertPosition}, ILogger)"/>; a new text block is revealed.
    /// </summary>
    private static async Task<AdoptMessage?> RunAsync(
        AppState state, Guid noteBlockKey, Func<ILinkService, Book, (AdoptResult Result, bool NewTextBlock)> adopt, ILogger logger)
    {
        Guid? reveal = null;
        try
        {
            (var message, reveal) = await TryAdoptAsync(state, noteBlockKey, adopt, logger);
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

    private static async Task<(AdoptMessage? Message, Guid? Reveal)> TryAdoptAsync(
        AppState state, Guid noteBlockKey, Func<ILinkService, Book, (AdoptResult Result, bool NewTextBlock)> adopt, ILogger logger)
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
            return (new AdoptMessage("NoteAdoptFailed", true), null);
        }

        // The flush may have saved the page or changed the state, so everything is read after it, the book from the vault.
        if (state.Session is not { } session || state.CurrentBook is not { } current
            || session.Vault.FindBook(current.LinkName) is not { } book)
        {
            return (null, null);
        }

        var bookPath = book.Page.FilePath;
        if (book.Page.IsReadOnly)
        {
            return (new AdoptMessage("NoteAdoptBookReadOnly", true), null);
        }

        var notePage = session.Vault.FindBlockByKey(noteBlockKey)?.Page;
        if (notePage is { IsReadOnly: true })
        {
            return (new AdoptMessage("NoteAdoptNoteReadOnly", true), null);
        }

        if (session.HasOpenConflict(bookPath) || (notePage is not null && session.HasOpenConflict(notePage.FilePath)))
        {
            return (new AdoptMessage("NoteAdoptConflict", true), null);
        }

        AdoptResult result;
        bool newTextBlock;
        try
        {
            (result, newTextBlock) = adopt(session.Links, book);
        }
        catch (FileChangedExternallyException)
        {
            session.HandleExternalChange(bookPath);
            return (new AdoptMessage("NoteAdoptExternalChange", true), null);
        }
        catch (ReadOnlyPageException)
        {
            return (new AdoptMessage("NoteAdoptBookReadOnly", true), null);
        }
        catch (InvalidOperationException) when (notePage is { IsReadOnly: true })
        {
            return (new AdoptMessage("NoteAdoptNoteReadOnly", true), null);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Includes the page writer refusing a result that would not parse safely (nothing was written).
            logger.LogWarning(ex, "Adopting a note failed");
            return (new AdoptMessage("NoteAdoptFailed", true), null);
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

        return (result.NoteUpdatePending ? new AdoptMessage("NoteUpdatePending", false) : null, newTextBlock ? result.TextBlockKey : null);
    }
}
