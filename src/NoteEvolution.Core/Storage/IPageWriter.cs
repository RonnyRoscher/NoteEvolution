using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Storage;

/// <summary>The only way NoteEvolution writes a page back to the vault.</summary>
public interface IPageWriter
{
    /// <exception cref="ReadOnlyPageException">The page is read-only.</exception>
    /// <exception cref="InvalidOperationException">The serialized page would not parse safely; nothing is written.</exception>
    /// <exception cref="FileChangedExternallyException">
    /// The file on disk is no longer what the page was loaded from or last saved as; nothing is written. Handle the
    /// change (<see cref="ExternalChangeHandler.Handle"/>) and save the resulting page.
    /// </exception>
    /// <exception cref="IOException">Writing failed on all 3 attempts (200 ms apart).</exception>
    void Save(Page page);
}
