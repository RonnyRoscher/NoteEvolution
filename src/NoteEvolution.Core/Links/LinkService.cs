using System.Security.Cryptography;
using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Storage;
using NoteEvolution.Core.Vaults;

namespace NoteEvolution.Core.Links;

public sealed class LinkService(IVault vault, IPageWriter writer, UndoManager undo, PendingStore pending) : ILinkService
{
    private const string SourceKey = "source";
    private const string UsedInKey = "used-in";

    public AdoptResult Adopt(Book book, Guid noteBlockKey, InsertPosition position)
    {
        var bookPage = book.Page;
        EnsureWritable(bookPage);
        if (vault.FindBlockByKey(noteBlockKey) is not { } found || Book.IsBook(found.Page))
        {
            throw new ArgumentException("No note block has this key.", nameof(noteBlockKey));
        }

        var (notePage, note) = found;
        if (notePage.IsReadOnly)
        {
            throw new InvalidOperationException(
                $"Die Notizdatei '{notePage.FilePath}' kann nicht sicher gelesen werden: {notePage.ParseError}");
        }

        var (parent, index) = Resolve(book, position);

        var noteLines = note.Lines.ToList();
        var noteWasDirty = note.IsDirty;
        var noteId = note.EnsureId();
        var copy = note.CloneDetached(withoutProperties: true);
        bookPage.InsertBlock(parent, index, copy);
        var textBlockId = copy.EnsureId();
        copy.SetProperty(SourceKey, SourceValue.Format([noteId]));
        try
        {
            SaveBook(bookPage, () => bookPage.RemoveBlock(copy));
        }
        catch
        {
            // The book does not reference the note, so a new id:: must not reach the note file later.
            note.RestoreLines(noteLines, noteWasDirty);
            throw;
        }

        var noteUpdatePending = UpdateNote(notePage, note, book.LinkName, textBlockId, remove: false).Pending;

        var bookPath = bookPage.FilePath;
        var linkName = book.LinkName;
        undo.Push(new UndoAction("Übernehmen", () => UndoAdopt(bookPath, linkName, textBlockId, noteId)));
        if (!noteUpdatePending)
        {
            RetryPending();
        }

        return new AdoptResult(copy.Key, textBlockId, noteUpdatePending);
    }

    public void RemoveSource(Book book, Guid textBlockKey, Guid noteBlockId)
    {
        var page = book.Page;
        EnsureWritable(page);
        var block = FindTextBlock(book, textBlockKey);
        var sources = SourceValue.Parse(block.GetProperty(SourceKey) ?? "").ToList();
        if (sources.RemoveAll(id => id == noteBlockId) == 0)
        {
            return;
        }

        var lines = block.Lines.ToList();
        var wasDirty = block.IsDirty;
        if (sources.Count == 0)
        {
            block.RemoveProperty(SourceKey);
        }
        else
        {
            block.SetProperty(SourceKey, SourceValue.Format(sources));
        }

        SaveBook(page, () => block.RestoreLines(lines, wasDirty));

        var noteUpdatePending = block.Id is { } bookBlockId
                                && RemoveUsageOfFound(noteBlockId, book.LinkName, bookBlockId) is { Pending: true };
        if (!noteUpdatePending)
        {
            RetryPending();
        }
    }

    public void DeleteTextBlock(Book book, Guid textBlockKey)
    {
        var page = book.Page;
        EnsureWritable(page);
        var block = FindTextBlock(book, textBlockKey);
        var parent = block.Parent;
        var index = IndexOf(page, block);
        page.RemoveBlock(block);
        SaveBook(page, () => page.RestoreBlock(parent, index, block));

        var sources = SourceValue.Parse(block.GetProperty(SourceKey) ?? "");
        RemoveUsagesOfDeleted(page.FilePath, book.LinkName, block.Id, sources, new DeletedBlock(block, parent?.Key, index));
    }

    public void ApplySyncEffects(Book book, IReadOnlyList<SyncEffect> effects)
    {
        foreach (var effect in effects)
        {
            switch (effect)
            {
                case BlockSplit split:
                    foreach (var noteId in split.Sources.Distinct())
                    {
                        AddUsage(noteId, new UsedInEntry(book.LinkName, split.NewBookBlockId));
                    }

                    break;
                case BlockDeleted deleted:
                    var removed = deleted.Removed is { } block ? new DeletedBlock(block, deleted.ParentKey, deleted.Index) : null;
                    RemoveUsagesOfDeleted(book.Page.FilePath, book.LinkName, deleted.BookBlockId, deleted.Sources, removed);
                    break;
            }
        }
    }

    public void AddUsage(Guid noteBlockId, UsedInEntry entry) =>
        ChangeUsage(noteBlockId, entry.PageName, entry.BookBlockId, remove: false);

    public void RemoveUsage(Guid noteBlockId, string bookLinkName, Guid? bookBlockId) =>
        ChangeUsage(noteBlockId, bookLinkName, bookBlockId, remove: true);

    public int RetryPending()
    {
        var done = 0;
        foreach (var update in pending.Load())
        {
            if (TryComplete(update))
            {
                pending.Remove(update);
                done++;
            }
        }

        return done;
    }

    private void ChangeUsage(Guid noteBlockId, string bookLinkName, Guid? bookBlockId, bool remove)
    {
        if (FindNote(noteBlockId) is { } note)
        {
            if (!UpdateNote(note.Page, note.Block, bookLinkName, bookBlockId, remove).Pending)
            {
                RetryPending();
            }
        }
        else if (remove)
        {
            ClearPending(noteBlockId, bookLinkName, bookBlockId);
        }
    }

    /// <summary>
    /// Removes the usage from the note found by id. If no note has that id (e.g. its <c>id::</c> was never
    /// written), an open add for this usage is dropped instead, so a later retry cannot bring it back.
    /// </summary>
    private NoteChange? RemoveUsageOfFound(Guid noteId, string linkName, Guid bookBlockId)
    {
        if (FindNote(noteId) is { } note)
        {
            return UpdateNote(note.Page, note.Block, linkName, bookBlockId, remove: true);
        }

        ClearPending(noteId, linkName, bookBlockId);
        return null;
    }

    /// <summary>
    /// After a book block was removed and the book saved: removes its usages from the source notes (none without a
    /// block id) and, if the block is known, records the undo action „Löschen“ that puts it back.
    /// </summary>
    private void RemoveUsagesOfDeleted(
        string bookPath, string linkName, Guid? bookBlockId, IEnumerable<Guid> sources, DeletedBlock? removed)
    {
        var changes = new List<NoteChange>();
        if (bookBlockId is { } id)
        {
            foreach (var noteId in sources.Distinct())
            {
                if (RemoveUsageOfFound(noteId, linkName, id) is { } change)
                {
                    changes.Add(change);
                }
            }
        }

        if (removed is not null)
        {
            undo.Push(new UndoAction(
                "Löschen", () => UndoDelete(bookPath, linkName, removed.ParentKey, removed.Index, removed.Block, changes)));
        }

        if (changes.All(c => !c.Pending))
        {
            RetryPending();
        }
    }

    /// <summary>Removes the adopted book block (found by its id) and the note's usage; the note keeps its <c>id::</c>.</summary>
    private void UndoAdopt(string bookPath, string linkName, Guid textBlockId, Guid noteId)
    {
        var page = CurrentPage(bookPath);
        if (page.AllBlocks().FirstOrDefault(b => b.Id == textBlockId) is { } block)
        {
            var parent = block.Parent;
            var index = IndexOf(page, block);
            page.RemoveBlock(block);
            SaveBook(page, () => page.RestoreBlock(parent, index, block));
        }

        var noteUpdatePending = RemoveUsageOfFound(noteId, linkName, textBlockId) is { Pending: true };
        if (!noteUpdatePending)
        {
            RetryPending();
        }
    }

    /// <summary>
    /// Puts the deleted block back with its original lines. A note still exactly as the delete left it gets its
    /// original lines back; any other note gets the usage added again.
    /// </summary>
    private void UndoDelete(
        string bookPath, string linkName, Guid? parentKey, int index, Block block, IReadOnlyList<NoteChange> changes)
    {
        var page = CurrentPage(bookPath);
        Block? parent = null;
        if (parentKey is { } key)
        {
            parent = page.AllBlocks().FirstOrDefault(b => b.Key == key)
                     ?? throw new InvalidOperationException("Der Abschnitt des gelöschten Textblocks existiert nicht mehr.");
        }

        page.RestoreBlock(parent, index, block);
        SaveBook(page, () => page.RemoveBlock(block));

        var allWritten = true;
        foreach (var change in changes)
        {
            if (FindNote(change.NoteId) is not { } note)
            {
                continue;
            }

            if (!note.Page.IsReadOnly && note.Block.Lines.SequenceEqual(change.After))
            {
                note.Block.RestoreLines(change.Before, isDirty: true);
                allWritten &= !SaveNote(note.Page, note.Block, change.NoteId, linkName, change.BookBlockId, remove: false);
            }
            else
            {
                allWritten &= !UpdateNote(note.Page, note.Block, linkName, change.BookBlockId, remove: false).Pending;
            }
        }

        if (allWritten)
        {
            RetryPending();
        }
    }

    /// <summary>
    /// Adds or removes one <c>used-in::</c> entry of a note and writes the note page. A read-only page is left
    /// unchanged; for it, and when the write fails, the change goes to <c>pending.json</c> (the in-memory note
    /// keeps a change that failed to write). Without a book block id there is no pending entry: the exception is passed on.
    /// </summary>
    private NoteChange UpdateNote(Page page, Block note, string linkName, Guid? bookBlockId, bool remove)
    {
        var noteId = note.Id ?? throw new InvalidOperationException("The note block has no id.");
        var before = note.Lines.ToList();
        if (page.IsReadOnly)
        {
            if (bookBlockId is not { } id)
            {
                throw new ReadOnlyPageException($"Die Datei '{page.FilePath}' ist schreibgeschützt: {page.ParseError}");
            }

            Queue(new PendingNoteUpdate(noteId, Locate(page, note), linkName, id, remove));
            return new NoteChange(noteId, bookBlockId, before, before, Pending: true);
        }

        ApplyEntry(note, linkName, bookBlockId, remove);
        var pendingNow = SaveNote(page, note, noteId, linkName, bookBlockId, remove);
        return new NoteChange(noteId, bookBlockId, before, note.Lines.ToList(), pendingNow);
    }

    /// <summary>Writes the note page if it has changes; returns <c>true</c> if the write failed and was queued.</summary>
    private bool SaveNote(Page page, Block note, Guid noteId, string linkName, Guid? bookBlockId, bool remove)
    {
        try
        {
            if (page.IsDirty)
            {
                writer.Save(page);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException && bookBlockId is not null)
        {
            Queue(new PendingNoteUpdate(noteId, Locate(page, note), linkName, bookBlockId.Value, remove));
            return true;
        }

        // The file now has the latest state for this usage; an older open update must not overwrite it.
        ClearPending(noteId, linkName, bookBlockId);
        return false;
    }

    /// <summary>Records <paramref name="update"/> as the only open update for its usage (the latest wish wins).</summary>
    private void Queue(PendingNoteUpdate update)
    {
        ClearPending(update.NoteBlockId, update.BookLinkName, update.BookBlockId);
        pending.Add(update);
    }

    private void ClearPending(Guid noteId, string linkName, Guid? bookBlockId)
    {
        foreach (var update in pending.Load())
        {
            if (update.NoteBlockId == noteId && update.BookBlockId == bookBlockId
                && string.Equals(update.BookLinkName, linkName, StringComparison.OrdinalIgnoreCase))
            {
                pending.Remove(update);
            }
        }
    }

    /// <summary>Finds the note by id, else by locator (then sets the id for an add), applies the update and writes it.</summary>
    private bool TryComplete(PendingNoteUpdate update)
    {
        if ((FindNote(update.NoteBlockId) ?? Locate(update.Locator)) is not { } note || note.Page.IsReadOnly)
        {
            return false;
        }

        if (!update.Remove && note.Block.Id is null)
        {
            note.Block.SetProperty("id", update.NoteBlockId.ToString("D"));
        }

        ApplyEntry(note.Block, update.BookLinkName, update.BookBlockId, update.Remove);
        try
        {
            if (note.Page.IsDirty)
            {
                writer.Save(note.Page);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void ApplyEntry(Block note, string linkName, Guid? bookBlockId, bool remove)
    {
        var entries = UsedInValue.Parse(note.GetProperty(UsedInKey) ?? "").ToList();
        bool Matches(UsedInEntry e) =>
            e.BookBlockId == bookBlockId && string.Equals(e.PageName, linkName, StringComparison.OrdinalIgnoreCase);

        if (remove ? entries.RemoveAll(Matches) == 0 : entries.Any(Matches))
        {
            return;
        }

        if (!remove)
        {
            entries.Add(new UsedInEntry(linkName, bookBlockId));
        }

        if (entries.Count == 0)
        {
            note.RemoveProperty(UsedInKey);
        }
        else
        {
            note.SetProperty(UsedInKey, UsedInValue.Format(entries));
        }
    }

    /// <summary>
    /// Writes a book page. If that fails, <paramref name="revert"/> undoes the edit on this instance, the vault gets
    /// the page as it is on disk again, and the exception is passed on.
    /// </summary>
    private void SaveBook(Page page, Action revert)
    {
        try
        {
            writer.Save(page);
        }
        catch
        {
            revert();
            try
            {
                vault.ReplacePage(LogseqParser.Parse(page.FilePath, File.ReadAllBytes(page.FilePath)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable as well: the vault keeps the reverted instance.
            }

            throw;
        }
    }

    private (Page Page, Block Block)? FindNote(Guid noteId) =>
        vault.FindBlockById(noteId) is { } found && !Book.IsBook(found.Page) ? found : null;

    /// <summary>The block at the locator's tree path, if it has no id yet and its content still has the recorded hash.</summary>
    private (Page Page, Block Block)? Locate(NoteLocator locator)
    {
        if (vault.FindPageByPath(Path.Combine(vault.Root, locator.PagePath)) is not { } page || Book.IsBook(page))
        {
            return null;
        }

        Block? block = null;
        var level = page.Roots;
        foreach (var index in locator.TreePath)
        {
            if (index < 0 || index >= level.Count)
            {
                return null;
            }

            block = level[index];
            level = block.Children;
        }

        return block is { Id: null } && Hash(block.Content) == locator.ContentSha256 ? (page, block) : null;
    }

    private NoteLocator Locate(Page page, Block note)
    {
        var path = new List<int>();
        for (var block = note; block is not null; block = block.Parent)
        {
            path.Insert(0, IndexOf(page, block));
        }

        var relative = Path.GetRelativePath(vault.Root, page.FilePath).Replace('\\', '/');
        return new NoteLocator(relative, [.. path], Hash(note.Content));
    }

    private Page CurrentPage(string path)
    {
        var page = vault.FindPageByPath(path)
                   ?? throw new InvalidOperationException($"Die Datei '{path}' ist nicht mehr im Vault.");
        EnsureWritable(page);
        return page;
    }

    private static void EnsureWritable(Page page)
    {
        if (page.IsReadOnly)
        {
            throw new ReadOnlyPageException($"Die Datei '{page.FilePath}' ist schreibgeschützt: {page.ParseError}");
        }
    }

    private static Block FindTextBlock(Book book, Guid textBlockKey) =>
        book.FindTextBlock(textBlockKey)?.Block
        ?? throw new ArgumentException("No text block has this key.", nameof(textBlockKey));

    /// <summary>Parent block and child index for <paramref name="position"/>; the items of a section are its block's children.</summary>
    private static (Block? Parent, int Index) Resolve(Book book, InsertPosition position)
    {
        switch (position)
        {
            case InsertPosition.After after:
            {
                var block = book.FindTextBlock(after.TextBlockKey)?.Block
                            ?? throw new ArgumentException("No text block has this key.", nameof(position));
                return (block.Parent, IndexOf(book.Page, block) + 1);
            }

            case InsertPosition.SectionStart start:
            {
                var (section, items) = FindSection(book, start.SectionKey);
                return (section.Block, Math.Max(items.FindIndex(i => i is TextBlock), 0));
            }

            case InsertPosition.SectionEnd end:
            {
                var (section, items) = FindSection(book, end.SectionKey);
                return (section.Block, items.FindLastIndex(i => i is TextBlock) + 1);
            }

            default:
                throw new ArgumentException("Unknown position.", nameof(position));
        }
    }

    private static (OutlineNode Section, List<BookItem> Items) FindSection(Book book, Guid sectionKey) =>
        book.FindNode(sectionKey) is { } section
            ? (section, section.Items.ToList())
            : throw new ArgumentException("No section has this key.", "position");

    private static int IndexOf(Page page, Block block)
    {
        var siblings = block.Parent?.Children ?? page.Roots;
        for (var i = 0; i < siblings.Count; i++)
        {
            if (siblings[i] == block)
            {
                return i;
            }
        }

        throw new ArgumentException("The block does not belong to this page.", nameof(block));
    }

    private static string Hash(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    /// <summary>What a note looked like before and after one usage change.</summary>
    private sealed record NoteChange(
        Guid NoteId, Guid? BookBlockId, IReadOnlyList<RawLine> Before, IReadOnlyList<RawLine> After, bool Pending);

    /// <summary>A removed book block and where it was (parent key, <c>null</c> = root level; index among the siblings).</summary>
    private sealed record DeletedBlock(Block Block, Guid? ParentKey, int Index);

    private sealed class UndoAction(string description, Action undo) : IUndoAction
    {
        public string Description => description;

        public void Undo() => undo();
    }
}
