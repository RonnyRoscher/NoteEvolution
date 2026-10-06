using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;

namespace NoteEvolution.UI.State;

/// <summary>Takes pages from their files again after an operation that changed them in memory failed to save.</summary>
internal static class PageReload
{
    /// <summary>
    /// Replaces <paramref name="page"/> in the vault by what its file holds now. If the file cannot be read the vault
    /// keeps the page as it is; callers then report that the change was not saved.
    /// </summary>
    public static void FromDisk(VaultSession session, Page page)
    {
        try
        {
            session.Vault.ReplacePage(LogseqParser.Parse(page.FilePath, File.ReadAllBytes(page.FilePath)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable as well: nothing more can be done here.
        }
    }
}
