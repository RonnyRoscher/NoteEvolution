using Microsoft.Extensions.Logging;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Storage;
using NoteEvolution.UI.Editor;

namespace NoteEvolution.UI.State;

/// <summary>What the editor shows: the session and book it is from and its snapshot (after a save: the one saved).</summary>
/// <param name="FromEditor">
/// The snapshot is the editor's own document, just saved; the book may hold its texts slightly normalized (ruling R33).
/// </param>
public sealed record ShownText(VaultSession Session, string BookLink, SectionSnapshot Snapshot, bool FromEditor = false);

/// <summary>
/// The editor's save pipeline without the UI (spec 5.3): editor document → snapshot → <see cref="BookSync.Apply"/> →
/// <see cref="VaultSession.TrySave"/> (R26) → the links (<see cref="Core.Links.ILinkService.ApplySyncEffects"/>,
/// <see cref="Core.Links.ILinkService.RetryPending"/>) and a fresh book view. A refused document or a failed save never
/// leaves the shared page different from the file without either saving it later or taking the file's version again
/// (ruling R31). Call it on the UI thread.
/// </summary>
public sealed class BookTextSaver(AppState state, ILogger logger)
{
    /// <summary>Effects of applied changes whose page is not saved yet; passed to the notes once it is.</summary>
    private readonly List<(VaultSession Session, string BookLink, SyncEffect Effect)> _unsavedEffects = [];

    /// <summary>Pages left partly changed by a failed apply that could not be restored: never saved from here.</summary>
    private readonly HashSet<Page> _poisoned = new(ReferenceEqualityComparer.Instance);

    /// <summary>What the editor shows; <c>null</c> before the first document.</summary>
    public ShownText? Shown { get; set; }

    /// <summary>The editor's latest document that is not saved yet.</summary>
    public string? Pending { get; set; }

    /// <summary>The editor must show the book again, even if the snapshot looks the same.</summary>
    public bool ReloadNeeded { get; set; }

    /// <summary>The message to show (a key in <c>Strings.resx</c>).</summary>
    public AdoptMessage? Message { get; set; }

    /// <summary>
    /// Saves <see cref="Pending"/> into the shown book. On a write failure the file's version is read again and the
    /// text stays pending for the next try; if even reading fails, the page keeps the text and
    /// <see cref="SaveDirtyPage"/> saves it later.
    /// </summary>
    public void Save()
    {
        if (Pending is not { } json || Shown is not { } shown)
        {
            return;
        }

        Pending = null;
        var session = shown.Session;
        string? refusal = null;
        if (!ReferenceEquals(session, state.Session) || !TryWritableBook(shown, out var book, out refusal))
        {
            if (refusal is not null)
            {
                logger.LogWarning("The editor's text for {Book} was not saved ({Reason})", shown.BookLink, refusal);
                Message = new AdoptMessage(refusal, true);
                if (refusal == "EditorSaveFailed")
                {
                    // A partly changed page that cannot be read again: the text waits for the next try.
                    Pending = json;
                }
                else
                {
                    ReloadNeeded = true;
                }
            }

            return;
        }

        if (!TryApply(shown, book, json, out var snapshot, out var result))
        {
            return;
        }

        if (!result.Changed)
        {
            Shown = shown with { Snapshot = snapshot, FromEditor = true };
            return;
        }

        var page = book.Page;
        if (session.TrySave(page, out var error))
        {
            Shown = shown with { Snapshot = snapshot, FromEditor = true };
            Message = null;
            QueueEffects(session, book.LinkName, result.Effects);
            ApplyUnsavedEffects();
            state.RefreshBook();
            state.Notify();
            return;
        }

        if (error is FileChangedExternallyException || session.HasOpenConflict(page.FilePath))
        {
            // The session merged the text with the other program's version and saved it, or reported a conflict that
            // keeps it; the editor shows the result. The links follow once the page is saved.
            QueueEffects(session, book.LinkName, result.Effects);
            Message = new AdoptMessage("EditorExternalChange", false);
            ReloadNeeded = true;
            return;
        }

        Message = new AdoptMessage("EditorSaveFailed", true);
        if (ReloadFromDisk(session, page))
        {
            // The page is the file's again; the text waits for the next save (the editor keeps showing it).
            Pending = json;
            return;
        }

        // The file cannot even be read: the page keeps the text and is saved by SaveDirtyPage, then the links follow.
        Shown = shown with { Snapshot = snapshot, FromEditor = true };
        QueueEffects(session, book.LinkName, result.Effects);
    }

    /// <summary>
    /// Called by the session before it brings in an external change of <paramref name="path"/>: pending text of that
    /// page goes into the page now (not saved), so the change handling merges it with the external version (spec 5.7)
    /// instead of it overwriting that version later.
    /// </summary>
    public void PutPendingIntoPage(string path)
    {
        if (Pending is not { } json || Shown is not { } shown || !TryWritableBook(shown, out var book, out _)
            || !string.Equals(Path.GetFullPath(book.Page.FilePath), path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Pending = null;
        ReloadNeeded = true;
        if (TryApply(shown, book, json, out _, out var result))
        {
            QueueEffects(shown.Session, book.LinkName, result.Effects);
        }
    }

    /// <summary>
    /// Saves the shown book page if it has unsaved changes (a merge whose save failed, text kept after a failed write),
    /// unless it is read-only, in conflict or left partly changed by a failed apply.
    /// </summary>
    public void SaveDirtyPage()
    {
        if (Shown is not { } shown || !ReferenceEquals(shown.Session, state.Session)
            || shown.Session.Vault.FindBook(shown.BookLink)?.Page is not { IsDirty: true, IsReadOnly: false } page
            || shown.Session.HasOpenConflict(page.FilePath) || _poisoned.Contains(page))
        {
            return;
        }

        if (shown.Session.TrySave(page, out _))
        {
            if (Message?.Key == "EditorSaveFailed")
            {
                Message = null;
            }

            state.RefreshBook();
            state.Notify();
        }
    }

    public void QueueEffects(VaultSession session, string bookLink, IEnumerable<SyncEffect> effects) =>
        _unsavedEffects.AddRange(effects.Select(effect => (session, bookLink, effect)));

    /// <summary>
    /// Passes the queued effects to the notes once their book page is saved (and no conflict is open). After a merge or
    /// a conflict resolution only the effects that still fit the page are applied: a split-off block that is in it, a
    /// deleted block that is not.
    /// </summary>
    public void ApplyUnsavedEffects()
    {
        foreach (var group in _unsavedEffects.GroupBy(e => (e.Session, e.BookLink)).ToList())
        {
            var (session, bookLink) = group.Key;
            if (!ReferenceEquals(session, state.Session) || session.Vault.FindBook(bookLink) is not { } book)
            {
                _unsavedEffects.RemoveAll(e => e.Session == session && e.BookLink == bookLink);
                continue;
            }

            var page = book.Page;
            if (page.IsDirty || page.IsReadOnly || session.HasOpenConflict(page.FilePath))
            {
                continue;
            }

            _unsavedEffects.RemoveAll(e => e.Session == session && e.BookLink == bookLink);
            bool InPage(Guid id) => session.Vault.FindBlockById(id) is { } found && found.Page == page;
            var effects = group.Select(e => e.Effect).Where(effect => effect switch
            {
                BlockSplit split => InPage(split.NewBookBlockId),
                BlockDeleted deleted => !InPage(deleted.BookBlockId),
                _ => false,
            }).ToList();
            if (effects.Count == 0)
            {
                continue;
            }

            try
            {
                session.Links.ApplySyncEffects(book, effects);
                session.Links.RetryPending();
            }
            catch (Exception ex)
            {
                // The book is saved; the link check repairs the notes' used-in entries.
                logger.LogError(ex, "Updating the notes after saving {Path} failed", page.FilePath);
            }

            UpdateSearch(session, effects.SelectMany(effect => effect switch
            {
                BlockSplit split => split.Sources,
                BlockDeleted deleted => deleted.Sources,
                _ => (IReadOnlyList<Guid>)[],
            }));
        }
    }

    /// <summary>
    /// Removes the note from the text block's sources and the usage from the note (after <see cref="Save"/>). Refused
    /// while the text could not be saved, and while a conflict is open for the book or the note page (R26).
    /// </summary>
    public void RemoveSource(Guid textBlockKey, Guid noteId)
    {
        if (Pending is not null)
        {
            // The text could not be saved; nothing is changed behind it.
            return;
        }

        string? refusal = null;
        if (Shown is not { } shown || !TryWritableBook(shown, out var book, out refusal))
        {
            if (refusal == "EditorConflict")
            {
                Message = new AdoptMessage("EditorChipConflict", true);
            }

            return;
        }

        var session = shown.Session;
        var bookPath = book.Page.FilePath;
        if (session.Vault.FindBlockById(noteId)?.Page is { } notePage && session.HasOpenConflict(notePage.FilePath))
        {
            Message = new AdoptMessage("EditorChipConflict", true);
            return;
        }

        try
        {
            session.Links.RemoveSource(book, textBlockKey, noteId);
        }
        catch (FileChangedExternallyException)
        {
            session.HandleExternalChange(bookPath);
            Message = new AdoptMessage("EditorExternalChange", false);
            return;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Includes a read-only page and the writer refusing an unsafe result; nothing was written.
            logger.LogWarning(ex, "Removing a source failed");
            Message = new AdoptMessage("EditorChipFailed", true);
            return;
        }

        UpdateSearch(session, [noteId]);
    }

    /// <summary>
    /// The shown book from the vault, if it may be written: not read-only, no conflict open for it (R26), and not left
    /// partly changed (a page left so is read from the file again first).
    /// </summary>
    private bool TryWritableBook(ShownText shown, out Book book, out string? refusal)
    {
        book = null!;
        refusal = null;
        var session = shown.Session;
        if (session.Vault.FindBook(shown.BookLink) is not { } found)
        {
            return false;
        }

        if (_poisoned.Contains(found.Page))
        {
            if (!ReloadFromDisk(session, found.Page) || session.Vault.FindBook(shown.BookLink) is not { } reloaded)
            {
                refusal = "EditorSaveFailed";
                return false;
            }

            _poisoned.Remove(found.Page);
            found = reloaded;
        }

        refusal = found.Page.IsReadOnly ? "EditorReadOnly"
            : session.HasOpenConflict(found.Page.FilePath) ? "EditorConflict"
            : null;
        book = found;
        return refusal is null;
    }

    /// <summary>
    /// Applies the editor document to the page. Exceptions leave the page as it was before: an
    /// <see cref="ArgumentException"/> changes nothing; after any other exception the page is restored from its state
    /// before (from the file if it had unsaved changes), and if that fails it is never saved from here.
    /// </summary>
    private bool TryApply(ShownText shown, Book book, string json, out SectionSnapshot snapshot, out SyncResult result)
    {
        var page = book.Page;
        var before = page.IsDirty ? null : PageSerializer.Serialize(page);
        try
        {
            snapshot = EditorDocMapper.FromJson(json, shown.Snapshot.ScopeKey, shown.Snapshot.IncludeSubsections);
            result = BookSync.Apply(book, snapshot, shown.Session.Vault);
            return true;
        }
        catch (ArgumentException ex)
        {
            // The page is untouched; the editor shows the book's text again.
            logger.LogWarning(ex, "The editor's text for {Path} cannot be written", page.FilePath);
            Message = new AdoptMessage("EditorSaveInvalid", true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Applying the editor's text to {Path} failed", page.FilePath);
            Message = new AdoptMessage("EditorSaveFailed", true);
            var restored = before is not null ? Replace(shown.Session, page, before) : ReloadFromDisk(shown.Session, page);
            if (!restored)
            {
                _poisoned.Add(page);
            }
        }

        ReloadNeeded = true;
        snapshot = null!;
        result = null!;
        return false;
    }

    /// <summary>Puts the file's version of <paramref name="page"/> into the vault, keeping the block keys the editor uses.</summary>
    private bool ReloadFromDisk(VaultSession session, Page page)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(page.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Reading {Path} again failed", page.FilePath);
            return false;
        }

        return Replace(session, page, bytes);
    }

    private bool Replace(VaultSession session, Page page, byte[] bytes)
    {
        var replacement = LogseqParser.Parse(page.FilePath, bytes);
        RuntimeKeys.Carry(page, replacement);
        session.Vault.ReplacePage(replacement);
        state.RefreshBook();
        return true;
    }

    /// <summary>The search index knows which notes are used; it is updated for the notes whose used-in changed.</summary>
    private void UpdateSearch(VaultSession session, IEnumerable<Guid> noteIds)
    {
        var paths = noteIds.Select(id => session.Vault.FindBlockById(id)?.Page.FilePath).OfType<string>().Distinct();
        foreach (var path in paths)
        {
            try
            {
                session.Search.UpdatePage(session.Notes, path);
            }
            catch (Exception ex)
            {
                // A stale index (an aid, rebuilt on opening) must not hide that the notes were updated.
                logger.LogWarning(ex, "Updating the search index for {Path} failed", path);
            }
        }
    }
}
