using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Vaults;

namespace NoteEvolution.Core.Storage;

/// <summary>What <see cref="ExternalChangeHandler.Handle"/> did with an externally changed file.</summary>
public abstract record ExternalChangeOutcome
{
    private ExternalChangeOutcome()
    {
    }

    /// <summary>
    /// The vault now holds <paramref name="Page"/>: the file read again (read-only if it cannot be parsed safely), or the
    /// external version with the local changes merged in (dirty; the caller saves it), or the unchanged local page if
    /// the file still has the content last loaded or saved.
    /// </summary>
    public sealed record Reloaded(Page Page) : ExternalChangeOutcome;

    /// <summary>The file was deleted and the page had no unsaved changes; it is no longer in the vault.</summary>
    public sealed record Removed(string Path) : ExternalChangeOutcome;

    /// <summary>
    /// Unsaved local changes collide with the external version; the user decides with
    /// <see cref="ExternalChangeHandler.Resolve"/>.
    /// </summary>
    /// <param name="Local">The local page with the user's unsaved text.</param>
    /// <param name="External">
    /// The external version; <c>null</c> if the file was deleted or cannot be parsed safely. Then
    /// <paramref name="Conflicts"/> lists every unsaved local block change with <c>External = null</c>.
    /// </param>
    /// <param name="Conflicts">The colliding blocks.</param>
    public sealed record Conflict(Page Local, Page? External, IReadOnlyList<BlockConflict> Conflicts) : ExternalChangeOutcome;
}

/// <summary>
/// Brings the vault up to date with a file that was changed outside NoteEvolution (spec 5.7). Not thread-safe: call it
/// on the thread that edits pages (the UI thread), not on the <see cref="VaultWatcher"/> thread.
/// </summary>
public sealed class ExternalChangeHandler(IVault vault)
{
    private static readonly Dictionary<Guid, ConflictChoice> NoChoices = [];

    /// <summary>
    /// Reads <paramref name="path"/> again and updates the vault:
    /// <list type="bullet">
    /// <item>new file: the page is added (<see cref="ExternalChangeOutcome.Reloaded"/>);</item>
    /// <item>content as last loaded or saved: nothing changes (<c>Reloaded</c> with the same page);</item>
    /// <item>no unsaved local changes: the new version replaces the page, keys carried over (<see cref="RuntimeKeys"/>);</item>
    /// <item>unsaved changes not colliding with the external ones: they are merged (<see cref="PageMerger"/>) and the
    /// merged, dirty page replaces it (the caller saves it);</item>
    /// <item>colliding changes: <see cref="ExternalChangeOutcome.Conflict"/>; the vault keeps the local page until
    /// <see cref="Resolve"/> (saving it meanwhile throws <see cref="FileChangedExternallyException"/>);</item>
    /// <item>file not parseable: it is loaded as a read-only page; unsaved local changes are reported as
    /// <c>Conflict</c> with <c>External = null</c>;</item>
    /// <item>file deleted: the page is removed (<see cref="ExternalChangeOutcome.Removed"/>), or with unsaved changes
    /// <c>Conflict</c> with <c>External = null</c> and the local page stays in the vault (the caller must not save
    /// it before <see cref="Resolve"/>: that would recreate the file).</item>
    /// </list>
    /// Read-only local pages count as unchanged, since they can never be saved.
    /// </summary>
    /// <exception cref="IOException">The file exists but cannot be read (e.g. locked); the vault is unchanged.</exception>
    public ExternalChangeOutcome Handle(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var local = vault.FindPageByPath(fullPath);
        var bytes = ReadIfExists(fullPath);
        if (bytes is null)
        {
            if (local is not null && HasUnsavedChanges(local))
            {
                return new ExternalChangeOutcome.Conflict(local, null, ConflictDetector.LocalChanges(local));
            }

            vault.RemovePage(fullPath);
            return new ExternalChangeOutcome.Removed(fullPath);
        }

        var external = LogseqParser.Parse(local?.FilePath ?? fullPath, bytes);
        if (local is null)
        {
            vault.ReplacePage(external);
            return new ExternalChangeOutcome.Reloaded(external);
        }

        if (bytes.AsSpan().SequenceEqual(local.SavedBytes()))
        {
            return new ExternalChangeOutcome.Reloaded(local);
        }

        if (!HasUnsavedChanges(local))
        {
            RuntimeKeys.Carry(local, external);
            vault.ReplacePage(external);
            return new ExternalChangeOutcome.Reloaded(external);
        }

        if (external.IsReadOnly)
        {
            vault.ReplacePage(external);
            return new ExternalChangeOutcome.Conflict(local, null, ConflictDetector.LocalChanges(local));
        }

        var conflicts = ConflictDetector.Detect(local, external);
        if (conflicts.Count > 0)
        {
            return new ExternalChangeOutcome.Conflict(local, external, conflicts);
        }

        var merged = PageMerger.Merge(local, external, NoChoices);
        vault.ReplacePage(merged);
        return new ExternalChangeOutcome.Reloaded(merged);
    }

    /// <summary>
    /// Applies the user's <paramref name="choices"/> (keyed by <c>BlockConflict.Local.Key</c>, one per conflict) and
    /// puts the result into the vault; saving it is left to the caller (it is dirty where it differs from the file).
    /// With an external version the result is <see cref="PageMerger.Merge"/>. Without one (<c>External = null</c>)
    /// the page can only be taken as a whole: if any choice is <see cref="ConflictChoice.Mine"/> or
    /// <see cref="ConflictChoice.Both"/>, the local page goes back into the vault with the file's current content
    /// accepted as its saved state, so that saving it recreates the deleted file or replaces the unreadable one (the
    /// writer's check for external changes passes); otherwise the vault keeps the external state (page removed, or
    /// read-only). Call it before handling further changes of the same file.
    /// </summary>
    /// <exception cref="ArgumentException">A conflict has no entry in <paramref name="choices"/>.</exception>
    /// <exception cref="IOException">The file exists but cannot be read; the vault is unchanged.</exception>
    public void Resolve(ExternalChangeOutcome.Conflict conflict, IReadOnlyDictionary<Guid, ConflictChoice> choices)
    {
        if (conflict.External is not null)
        {
            vault.ReplacePage(PageMerger.Merge(conflict.Local, conflict.External, choices));
            return;
        }

        if (conflict.Conflicts.FirstOrDefault(c => !choices.ContainsKey(c.Local.Key)) is { } open)
        {
            throw new ArgumentException($"No choice for the conflict of block {open.Local.Key}.", nameof(choices));
        }

        var local = conflict.Local;
        if (conflict.Conflicts.Any(c => choices[c.Local.Key] != ConflictChoice.Theirs))
        {
            if (ReadIfExists(local.FilePath) is { } diskBytes)
            {
                local.AcceptDiskAsBase(diskBytes);
            }

            vault.ReplacePage(local);
        }
        else if (ReferenceEquals(vault.FindPageByPath(local.FilePath), local))
        {
            vault.RemovePage(local.FilePath);
        }
    }

    private static bool HasUnsavedChanges(Page page) => page.IsDirty && !page.IsReadOnly;

    private static byte[]? ReadIfExists(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}
