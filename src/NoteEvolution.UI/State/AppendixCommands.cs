using Microsoft.Extensions.Logging;
using NoteEvolution.Core.Books;

namespace NoteEvolution.UI.State;

/// <summary>
/// Changes of the appendix (glossary, people, sources) on the book file, with one undo entry each. It follows the
/// structure commands of the manuscript (<see cref="ManuscriptCommands"/>): the editor saves its text first, a
/// read-only page or an open conflict refuses the change.
/// </summary>
public static class AppendixCommands
{
    /// <summary>The resource key of the undo entry of every change of the appendix.</summary>
    public const string UndoKey = "UndoAppendix";

    /// <summary>
    /// <see cref="Apply"/> after the editor saved its text (text it could not save refuses the change).
    /// </summary>
    public static async Task<AdoptMessage?> ApplyAsync(AppState state, Action<Book> change, string undoDescription, ILogger logger)
    {
        var (message, session, book) = await ManuscriptCommands.PrepareAsync(state, logger);
        return Run(state, message, session, book, change, undoDescription, logger);
    }

    /// <summary>
    /// Runs <paramref name="change"/> on the current book (the vault's one), saves its page and pushes a
    /// <see cref="StructureUndo"/> (<paramref name="undoDescription"/>), then takes the book from the vault again
    /// (<see cref="AppState.RefreshBook"/>) and raises <see cref="AppState.Notify"/>. Text the editor holds unsaved, a
    /// read-only page and an open conflict refuse the change; so does a change that throws before anything changed
    /// (<c>SectionCommandFailed</c>). If saving fails for another reason than a change by another program or a conflict,
    /// the page is read from the file again.
    /// </summary>
    /// <returns>The message to show, or <c>null</c> if the change ran (or there is no book).</returns>
    public static AdoptMessage? Apply(AppState state, Action<Book> change, string undoDescription, ILogger logger)
    {
        var (message, session, book) = ManuscriptCommands.Prepare(state);
        return Run(state, message, session, book, change, undoDescription, logger);
    }

    private static AdoptMessage? Run(
        AppState state, AdoptMessage? refusal, VaultSession? session, Book? book, Action<Book> change, string undoDescription, ILogger logger)
    {
        try
        {
            if (session is null || book is null)
            {
                return refusal;
            }

            var page = book.Page;
            var before = PageStructureSnapshot.Capture(page);
            try
            {
                change(book);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // Refused before anything changed; the page may hold half of it, so it is read again.
                logger.LogWarning(ex, "The change of the appendix was refused");
                PageReload.FromDisk(session, page);
                return new AdoptMessage("SectionCommandFailed", true);
            }

            if (!session.TrySave(page, out var error))
            {
                return ManuscriptCommands.SaveFailed(session, page, error);
            }

            session.Undo.Push(new StructureUndo(undoDescription, session, page.FilePath, before, PageStructureSnapshot.Capture(page)));
            return null;
        }
        finally
        {
            // After a failure the vault holds the page as it is on disk again.
            state.RefreshBook();
            state.Notify();
        }
    }
}
