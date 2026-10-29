using NoteEvolution.Core.Model;
using NoteEvolution.Core.Storage;

namespace NoteEvolution.UI.State;

/// <summary>
/// What became of a click on a day mark: <paramref name="Saved"/> as clicked, else the message to show
/// (<paramref name="MessageKey"/>, <c>null</c> if the file already had the clicked state), how many revisions of its
/// own the refusal brings (a merged external change brings one), and whether the page was read from its file again.
/// </summary>
public sealed record DayMarkResult(bool Saved, string? MessageKey = null, int OwnRevisions = 0, bool Reloaded = false);

/// <summary>Setting or clearing a mark of a journal day („nicht relevant“, „später“), shared by the day card and the note bar.</summary>
public static class DayMarkSaving
{
    /// <summary>
    /// Applies <paramref name="change"/> (it returns <c>true</c> if it changed the page) to the day's page as the vault
    /// has it now and saves it through the session like other note writes. A read-only page or an open conflict is refused
    /// before anything changes; an external change is merged by the session; after any other failure the page is read
    /// from its file again.
    /// </summary>
    public static DayMarkResult Apply(VaultSession session, Page shown, Func<Page, bool> change)
    {
        var page = session.Vault.FindPageByPath(shown.FilePath) ?? shown;
        if (page.IsReadOnly)
        {
            return new DayMarkResult(false, "JournalDayReadOnly");
        }

        if (session.HasOpenConflict(page.FilePath))
        {
            return new DayMarkResult(false, "NoteAdoptConflict");
        }

        if (!change(page))
        {
            // The file already has the clicked state (the view was behind): show the page's state.
            return new DayMarkResult(false);
        }

        if (session.TrySave(page, out var error))
        {
            return new DayMarkResult(true);
        }

        if (session.HasOpenConflict(page.FilePath))
        {
            return new DayMarkResult(false, "NoteAdoptConflict");
        }

        if (error is FileChangedExternallyException)
        {
            return new DayMarkResult(false, "JournalDayExternalChange", OwnRevisions: 1);
        }

        PageReload.FromDisk(session, page);
        return new DayMarkResult(false, "JournalDayFailed", Reloaded: true);
    }
}
