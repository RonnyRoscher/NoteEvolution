using NoteEvolution.Core.Books;
using NoteEvolution.Core.Links;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Storage;

namespace NoteEvolution.UI.State;

/// <summary>
/// Undoes a page change that left a <see cref="PageStructureSnapshot"/> before and after it (the structure commands
/// of the manuscript, a change of the appendix): puts the page of <c>path</c> back to its state before the command
/// and saves it, but only while the page is exactly what the command left; a later change (e.g. a title typed into a
/// new heading) is never overwritten.
/// </summary>
internal sealed class StructureUndo(
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

    /// <summary>Saves the page after an undo; if that fails, the vault takes the file's version again where it should.</summary>
    /// <exception cref="InvalidOperationException">Saving failed.</exception>
    internal static void SaveUndone(VaultSession session, Page page, string path)
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
