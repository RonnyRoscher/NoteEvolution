using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Storage;

namespace NoteEvolution.UI.State;

/// <summary>Takes pages from their files again after an operation that changed them in memory failed to save.</summary>
internal static class PageReload
{
    /// <summary>
    /// Replaces <paramref name="page"/> in the vault by what its file holds now; its blocks keep their keys where they
    /// have a counterpart (<see cref="RuntimeKeys.Carry"/>), so what refers to them by key (a chosen place, the cursor)
    /// stays valid. If the file cannot be read the vault keeps the page as it is; callers then report that the change
    /// was not saved.
    /// </summary>
    public static void FromDisk(VaultSession session, Page page)
    {
        try
        {
            var reloaded = LogseqParser.Parse(page.FilePath, File.ReadAllBytes(page.FilePath));
            RuntimeKeys.Carry(page, reloaded);
            session.Vault.ReplacePage(reloaded);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable as well: nothing more can be done here.
        }
    }
}
