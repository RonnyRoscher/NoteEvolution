using Microsoft.Extensions.Logging;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Links;
using NoteEvolution.Core.Model;
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

    /// <summary>
    /// A note block with sub-points as a heading of the reference section's level at the end of that section (see
    /// <see cref="ILinkService.AdoptAsHeading"/>).
    /// </summary>
    AsHeading,

    /// <summary>A note block with sub-points as a heading one level below the reference section, before its sub-sections.</summary>
    AsSubheading,
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
        RunAsync(state, noteBlockKey, (links, book) => NewTextBlock(links.Adopt(book, noteBlockKey, position(book))), logger);

    /// <summary>
    /// Whether <paramref name="variant"/> is offered for <paramref name="noteBlock"/>: the heading variants only for a
    /// note block with sub-points, a sub-heading not below a level-6 heading (<see cref="ReferenceSection"/> in the
    /// current book); the other variants always.
    /// </summary>
    public static bool Offers(AppState state, Block noteBlock, AdoptVariant variant) => variant switch
    {
        AdoptVariant.AsHeading => noteBlock.Children.Count > 0,
        AdoptVariant.AsSubheading => noteBlock.Children.Count > 0
                                     && !(state.CurrentBook is { } book && ReferenceSection(state, book)?.Level >= MaxHeadingLevel),
        _ => true,
    };

    /// <summary>
    /// The section a note adopted as a heading is placed relative to: the section of the current element (for a heading
    /// its own), without one the section a note goes to without a current element (see <see cref="AdoptAsync(AppState, Guid, AdoptVariant, string, ILogger)"/>).
    /// </summary>
    public static OutlineNode? ReferenceSection(AppState state, Book book) =>
        book.FindNode(CurrentElement(state, book) is { } element ? BookElements.SectionOf(book, element) : FallbackSection(state, book));

    /// <summary>The deepest heading level (<c>######</c>).</summary>
    private const int MaxHeadingLevel = 6;

    /// <summary>The cursor's element, else <see cref="AppState.CurrentElement"/>, looked up in <paramref name="book"/>.</summary>
    private static BookElement? CurrentElement(AppState state, Book book) =>
        (state.Cursor is { } c ? BookElements.Find(book, c.Key) : null)
        ?? (state.CurrentElement is { } current ? BookElements.Find(book, current.Key) : null);

    /// <summary>Without a cursor in the book the last current section applies; without one, the end of the book (the root).</summary>
    private static Guid FallbackSection(AppState state, Book book) =>
        book.FindNode(state.CurrentSectionKey) is { Block: not null }
            ? state.CurrentSectionKey
            : BookElements.LastHeadingKey(book) ?? Guid.Empty;

    /// <summary>
    /// Adopts the note block <paramref name="noteBlockKey"/> into the current book the way <paramref name="variant"/>
    /// says (spec 4), relative to <see cref="AppState.CurrentElement"/> and at <see cref="AppState.Cursor"/>'s offset,
    /// both read after the editor's text was saved; with the same checks as the overload with an
    /// <see cref="InsertPosition"/>. The offset is only used for the cursor's own element: when the book does not have
    /// it (the current element is a fallback), the note goes after the fallback element instead. Without a current
    /// element the note becomes a new text block at the end of the last current section, else at the end of the book.
    /// A new text block or heading is revealed in the editor; after adopting into an existing element that element is
    /// revealed, so the user sees where the text went (the cursor goes to its start). The heading variants place the
    /// heading relative to <see cref="ReferenceSection"/>; <paramref name="untitledTitle"/> is the title of a heading
    /// from a note block whose first line is blank.
    /// </summary>
    /// <returns>The message to show, or <c>null</c> if the note was adopted without remark.</returns>
    public static Task<AdoptMessage?> AdoptAsync(
        AppState state, Guid noteBlockKey, AdoptVariant variant, string untitledTitle, ILogger logger) =>
        RunAsync(state, noteBlockKey, (links, book) => Adopt(links, state, book, noteBlockKey, variant, untitledTitle), logger);

    /// <summary>
    /// Adopts the note into the element the variant and the cursor name (see the mapping in spec 4), or as a heading;
    /// also returns the element to reveal: the new text block or heading, or the existing element the note went into.
    /// </summary>
    private static (bool NoteUpdatePending, Guid? Reveal) Adopt(
        ILinkService links, AppState state, Book book, Guid noteBlockKey, AdoptVariant variant, string untitledTitle)
    {
        // The current element is checked against the book just read from the vault, not the state's (older) one.
        if (variant is AdoptVariant.AsHeading or AdoptVariant.AsSubheading)
        {
            var section = ReferenceSection(state, book)!;
            var heading = links.AdoptAsHeading(book, noteBlockKey, section.Key, variant == AdoptVariant.AsSubheading, untitledTitle);
            return (heading.NoteUpdatePending, heading.HeadingKey);
        }

        var cursor = state.Cursor is { } c ? BookElements.Find(book, c.Key) : null;
        if (CurrentElement(state, book) is not { } element)
        {
            return NewTextBlock(links.Adopt(book, noteBlockKey, new InsertPosition.SectionEnd(FallbackSection(state, book))));
        }

        // The cursor's offset belongs to its own element; it is never applied to a fallback element.
        if (variant == AdoptVariant.AtCursor && cursor is null)
        {
            variant = AdoptVariant.After;
        }

        var offset = state.Cursor?.Offset ?? 0;
        return (element.Kind, variant) switch
        {
            (ElementKind.Heading, _) => NewTextBlock(links.Adopt(book, noteBlockKey, new InsertPosition.SectionStart(element.Key))),
            (ElementKind.TextBlock, AdoptVariant.After) => NewTextBlock(links.Adopt(book, noteBlockKey, new InsertPosition.After(element.Key))),
            (_, AdoptVariant.AtCursor) => Into(links.AdoptInto(book, noteBlockKey, new IntoPosition.AtCursor(element.Key, offset)), element.Key),
            (_, AdoptVariant.After) => Into(links.AdoptInto(book, noteBlockKey, new IntoPosition.AfterDetail(element.Key)), element.Key),
            _ => Into(links.AdoptInto(book, noteBlockKey, new IntoPosition.FirstChild(element.Key)), element.Key),
        };
    }

    /// <summary>The result of an adoption that made a new text block, which is revealed.</summary>
    private static (bool NoteUpdatePending, Guid? Reveal) NewTextBlock(AdoptResult result) => (result.NoteUpdatePending, result.TextBlockKey);

    /// <summary>The result of an adoption into the existing element <paramref name="element"/>, which is revealed.</summary>
    private static (bool NoteUpdatePending, Guid? Reveal) Into(AdoptResult result, Guid element) => (result.NoteUpdatePending, element);

    /// <summary>
    /// Runs an adoption (<paramref name="adopt"/>, which also names the element to reveal) with the checks
    /// of <see cref="AdoptAsync(AppState, Guid, Func{Book, InsertPosition}, ILogger)"/>, then reveals that element.
    /// </summary>
    private static async Task<AdoptMessage?> RunAsync(
        AppState state, Guid noteBlockKey, Func<ILinkService, Book, (bool NoteUpdatePending, Guid? Reveal)> adopt, ILogger logger)
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
        AppState state, Guid noteBlockKey, Func<ILinkService, Book, (bool NoteUpdatePending, Guid? Reveal)> adopt, ILogger logger)
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

        bool noteUpdatePending;
        Guid? reveal;
        try
        {
            (noteUpdatePending, reveal) = adopt(session.Links, book);
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

        return (noteUpdatePending ? new AdoptMessage("NoteUpdatePending", false) : null, reveal);
    }
}
