using System.Security.Cryptography;
using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Storage;
using NoteEvolution.Core.Text;
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
        var (notePage, note) = FindWritableNote(noteBlockKey);

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

    public AdoptResult AdoptInto(Book book, Guid noteBlockKey, IntoPosition position)
    {
        var bookPage = book.Page;
        EnsureWritable(bookPage);
        var (notePage, note) = FindWritableNote(noteBlockKey);

        var (textBlock, element) = position switch
        {
            IntoPosition.AtCursor at => FindElement(book, at.ElementKey, detailOnly: false),
            IntoPosition.AfterDetail after => FindElement(book, after.DetailKey, detailOnly: true),
            IntoPosition.FirstChild first => FindElement(book, first.ElementKey, detailOnly: false),
            _ => throw new ArgumentException("Unknown position.", nameof(position)),
        };
        var copy = note.CloneDetached(withoutProperties: true);
        string? content = null;
        if (position is IntoPosition.AtCursor cursor)
        {
            content = InsertText(element.Content, isDetail: element != textBlock, cursor.Offset, copy.Content);
            Block.ValidateContent(content);
        }

        var linesBefore = CaptureSubtree(textBlock);
        var noteLines = note.Lines.ToList();
        var noteWasDirty = note.IsDirty;
        var noteId = note.EnsureId();
        List<Block> inserted;
        switch (position)
        {
            case IntoPosition.AtCursor:
                element.SetContent(content!);
                inserted = copy.TakeChildren();
                for (var i = 0; i < inserted.Count; i++)
                {
                    inserted[i].Parent = null;
                    bookPage.InsertBlock(element, i, inserted[i]);
                }

                break;
            case IntoPosition.AfterDetail:
                bookPage.InsertBlock(element.Parent, IndexOf(bookPage, element) + 1, copy);
                inserted = [copy];
                break;
            default:
                bookPage.InsertBlock(element, 0, copy);
                inserted = [copy];
                break;
        }

        var textBlockId = textBlock.EnsureId();
        var sourceAdded = AddSource(textBlock, noteId);
        try
        {
            SaveBook(bookPage, () =>
            {
                inserted.ForEach(bookPage.RemoveBlock);
                RestoreSubtree(linesBefore, markDirty: false);
            });
        }
        catch
        {
            // The book does not reference the note, so a new id:: must not reach the note file later.
            note.RestoreLines(noteLines, noteWasDirty);
            throw;
        }

        var adopted = new AdoptedInto(inserted, linesBefore, CaptureSubtree(textBlock));
        var noteUpdatePending = UpdateNote(notePage, note, book.LinkName, textBlockId, remove: false).Pending;

        var bookPath = bookPage.FilePath;
        var linkName = book.LinkName;
        undo.Push(new UndoAction(
            "Übernehmen", () => UndoAdoptInto(bookPath, linkName, textBlockId, sourceAdded ? noteId : null, adopted)));
        if (!noteUpdatePending)
        {
            RetryPending();
        }

        return new AdoptResult(textBlock.Key, textBlockId, noteUpdatePending);
    }

    public void Link(Book book, Guid textBlockKey, Guid noteBlockKey)
    {
        var bookPage = book.Page;
        EnsureWritable(bookPage);
        var textBlock = FindTextBlock(book, textBlockKey);
        var (notePage, note) = FindWritableNote(noteBlockKey);

        var textLines = textBlock.Lines.ToList();
        var textWasDirty = textBlock.IsDirty;
        var noteLines = note.Lines.ToList();
        var noteWasDirty = note.IsDirty;
        var textBlockId = textBlock.EnsureId();
        var noteId = note.EnsureId();
        AddSource(textBlock, noteId);

        if (!textBlock.Lines.SequenceEqual(textLines))
        {
            try
            {
                SaveBook(bookPage, () => textBlock.RestoreLines(textLines, textWasDirty));
            }
            catch
            {
                // The book does not reference the note, so a new id:: must not reach the note file later.
                note.RestoreLines(noteLines, noteWasDirty);
                throw;
            }
        }

        ReplaceIdlessEntry(note, book.LinkName, textBlockId);
        if (!UpdateNote(notePage, note, book.LinkName, textBlockId, remove: false).Pending)
        {
            RetryPending();
        }
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
        EnsureWritable(book.Page);
        DeleteBlock(book, FindTextBlock(book, textBlockKey));
    }

    public void DeleteDetail(Book book, Guid detailKey)
    {
        EnsureWritable(book.Page);
        DeleteBlock(book, FindElement(book, detailKey, detailOnly: true).Element);
    }

    public void DeleteSection(Book book, Guid headingKey)
    {
        EnsureWritable(book.Page);
        DeleteBlock(
            book,
            book.FindNode(headingKey)?.Block ?? throw new ArgumentException("No heading has this key.", nameof(headingKey)));
    }

    public Guid MergeTextBlocks(Book book, IReadOnlyList<IReadOnlyList<Guid>> groups)
    {
        var page = book.Page;
        EnsureWritable(page);
        var merges = groups.Select(group => MergeGroup(book, group)).ToList();
        if (merges.Count == 0)
        {
            throw new ArgumentException("No group to merge.", nameof(groups));
        }

        if (merges.SelectMany(blocks => blocks).GroupBy(block => block).Any(same => same.Count() > 1))
        {
            throw new ArgumentException("A text block is given twice.", nameof(groups));
        }

        var contents = merges.Select(blocks => string.Join("\n\n", blocks.Select(block => block.Content))).ToList();
        contents.ForEach(Block.ValidateContent);

        var before = PageStructureSnapshot.Capture(page);
        var moves = new List<(Guid FirstId, Guid RemovedId, IReadOnlyList<Guid> Sources)>();
        foreach (var (blocks, content) in merges.Zip(contents))
        {
            var first = blocks[0];
            var later = blocks.Skip(1).ToList();
            foreach (var block in later)
            {
                var sources = SourceValue.Parse(block.GetProperty(SourceKey) ?? "");
                if (block.Id is { } removedId && sources.Count > 0)
                {
                    // A linked block: the notes' usages of it move to the first block, which needs an id for that.
                    moves.Add((first.EnsureId(), removedId, [.. sources.Distinct()]));
                }

                foreach (var noteId in sources)
                {
                    AddSource(first, noteId);
                }
            }

            first.SetContent(content);
            foreach (var block in later)
            {
                foreach (var detail in block.Children.ToList())
                {
                    page.MoveBlock(detail, first, first.Children.Count);
                }

                page.RemoveBlock(block);
            }
        }

        SaveBook(page, () => before.RestoreInto(page));
        var after = PageStructureSnapshot.Capture(page);

        var changes = new List<(NoteChange Change, Guid FirstId)>();
        foreach (var (firstId, removedId, sources) in moves)
        {
            foreach (var noteId in sources)
            {
                if (RemoveUsageOfFound(noteId, book.LinkName, removedId, replacement: firstId) is { } change)
                {
                    // Undo puts back the usage of the removed block.
                    changes.Add((change with { BookBlockId = removedId }, firstId));
                }
            }
        }

        var bookPath = page.FilePath;
        var linkName = book.LinkName;
        undo.Push(new UndoAction("Zusammenfügen", () => UndoMerge(bookPath, linkName, before, after, changes)));
        if (changes.All(c => !c.Change.Pending))
        {
            RetryPending();
        }

        return merges[0][0].Key;
    }

    /// <summary>The blocks of one group for <see cref="MergeTextBlocks"/>: at least two text blocks with the same parent.</summary>
    private static List<Block> MergeGroup(Book book, IReadOnlyList<Guid> group)
    {
        var blocks = group.Select(key => FindTextBlock(book, key)).ToList();
        if (blocks.Count < 2)
        {
            throw new ArgumentException("A group needs at least two text blocks.", nameof(group));
        }

        if (blocks.Any(block => block.Parent != blocks[0].Parent))
        {
            throw new ArgumentException("The text blocks of a group must have the same parent block.", nameof(group));
        }

        return blocks;
    }

    /// <summary>
    /// Removes the book block with its subtree, saves the book, then removes the usages of all linked blocks in the
    /// subtree from their notes and records the undo action „Löschen“.
    /// </summary>
    private void DeleteBlock(Book book, Block block)
    {
        var page = book.Page;
        var parent = block.Parent;
        var index = IndexOf(page, block);
        page.RemoveBlock(block);
        SaveBook(page, () => page.RestoreBlock(parent, index, block));

        RemoveUsagesOfDeleted(
            page.FilePath, book.LinkName, SourceValue.LinkedBlocksIn(block), new DeletedBlock(block, parent?.Key, index));
    }

    public void ApplySyncEffects(Book book, IReadOnlyList<SyncEffect> effects)
    {
        var handled = new HashSet<Block>();
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
                case BlockDeleted { Removed: { } removed } deleted:
                    // All effects of one removed subtree share the top block: one undo restores them together.
                    if (handled.Add(removed))
                    {
                        var linked = effects.OfType<BlockDeleted>().Where(e => e.Removed == removed).Select(e => (e.BookBlockId, e.Sources));
                        RemoveUsagesOfDeleted(
                            book.Page.FilePath, book.LinkName, linked, new DeletedBlock(removed, deleted.ParentKey, deleted.Index));
                    }

                    break;
                case BlockDeleted deleted:
                    RemoveUsagesOfDeleted(book.Page.FilePath, book.LinkName, [(deleted.BookBlockId, deleted.Sources)], null);
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

    /// <summary>
    /// The note block with this key and its page. Throws before anything changed if there is no such note block
    /// or its page is read-only.
    /// </summary>
    private (Page Page, Block Block) FindWritableNote(Guid noteBlockKey)
    {
        if (vault.FindBlockByKey(noteBlockKey) is not { } found || Book.IsBook(found.Page))
        {
            throw new ArgumentException("No note block has this key.", nameof(noteBlockKey));
        }

        if (found.Page.IsReadOnly)
        {
            throw new InvalidOperationException(
                $"Die Notizdatei '{found.Page.FilePath}' kann nicht sicher gelesen werden: {found.Page.ParseError}");
        }

        return found;
    }

    /// <summary>
    /// Replaces the first <c>used-in::</c> entry for this book that has no block reference by the precise entry,
    /// in place; does nothing if the precise entry is already there or there is no such entry.
    /// </summary>
    private static void ReplaceIdlessEntry(Block note, string linkName, Guid bookBlockId)
    {
        var entries = UsedInValue.Parse(note.GetProperty(UsedInKey) ?? "").ToList();
        bool SameBook(UsedInEntry e) => string.Equals(e.PageName, linkName, StringComparison.OrdinalIgnoreCase);
        if (entries.Any(e => SameBook(e) && e.BookBlockId == bookBlockId))
        {
            return;
        }

        var index = entries.FindIndex(e => SameBook(e) && e.BookBlockId is null);
        if (index >= 0)
        {
            entries[index] = new UsedInEntry(linkName, bookBlockId);
            note.SetProperty(UsedInKey, UsedInValue.Format(entries));
        }
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
    /// Removes the usage from the note found by id; with <paramref name="replacement"/>, the entry is rewritten to that
    /// book block in place instead (see <see cref="UpdateNote"/>). If no note has that id (e.g. its <c>id::</c> was never
    /// written), an open add for this usage is dropped instead, so a later retry cannot bring it back.
    /// </summary>
    private NoteChange? RemoveUsageOfFound(Guid noteId, string linkName, Guid bookBlockId, Guid? replacement = null)
    {
        if (FindNote(noteId) is { } note)
        {
            return replacement is { } newId
                ? UpdateNote(note.Page, note.Block, linkName, newId, remove: false, replacing: bookBlockId)
                : UpdateNote(note.Page, note.Block, linkName, bookBlockId, remove: true);
        }

        ClearPending(noteId, linkName, bookBlockId);
        return null;
    }

    /// <summary>
    /// After book blocks were removed and the book saved: removes the usages of the removed linked blocks
    /// (<c>id::</c> and sources) from their notes and, if the removed top block is known, records the undo action
    /// „Löschen“ that puts it back with all these usages.
    /// </summary>
    private void RemoveUsagesOfDeleted(
        string bookPath, string linkName, IEnumerable<(Guid Id, IReadOnlyList<Guid> Sources)> linked, DeletedBlock? removed)
    {
        var changes = new List<NoteChange>();
        foreach (var (id, sources) in linked)
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
    /// Puts the text block with its details back as it was before <see cref="AdoptInto"/> (lines, without the inserted
    /// blocks), then removes the note's usage if <see cref="AdoptInto"/> added the note as a source
    /// (<paramref name="addedNoteId"/>). Refused before anything changes if the text block's subtree is no longer
    /// exactly as <see cref="AdoptInto"/> left it.
    /// </summary>
    private void UndoAdoptInto(string bookPath, string linkName, Guid textBlockId, Guid? addedNoteId, AdoptedInto adopted)
    {
        var page = CurrentPage(bookPath);
        if (page.AllBlocks().FirstOrDefault(b => b.Id == textBlockId) is not { } textBlock || !SubtreeIs(textBlock, adopted.After))
        {
            throw new InvalidOperationException("Der Textblock wurde seit dem Übernehmen geändert.");
        }

        var places = adopted.Inserted.Select(block => (Block: block, block.Parent, Index: IndexOf(page, block))).ToList();
        adopted.Inserted.ForEach(page.RemoveBlock);
        RestoreSubtree(adopted.Before, markDirty: true);
        SaveBook(page, () =>
        {
            foreach (var (block, parent, index) in places)
            {
                page.RestoreBlock(parent, index, block);
            }

            RestoreSubtree(adopted.After, markDirty: true);
        });

        var noteUpdatePending = addedNoteId is { } noteId && RemoveUsageOfFound(noteId, linkName, textBlockId) is { Pending: true };
        if (!noteUpdatePending)
        {
            RetryPending();
        }
    }

    /// <summary>
    /// Puts the deleted block back with its original lines. A note still exactly as the delete left it gets its
    /// original lines back; any other note gets the usage added again. Changes are undone newest first, so that
    /// several changes of the same note are each checked against the state they left.
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

        // The editor's own undo may have put the block back already (with its key, relinked under a new id); restoring
        // it again would give two blocks with the same key, and every later save another linked copy.
        var removedKeys = Subtree(block).Select(b => b.Key).ToHashSet();
        if (page.AllBlocks().Any(b => removedKeys.Contains(b.Key)))
        {
            throw new InvalidOperationException("Der gelöschte Textblock ist schon wieder im Buch.");
        }

        page.RestoreBlock(parent, index, block);
        SaveBook(page, () => page.RemoveBlock(block));

        if (RestoreNotes(linkName, changes))
        {
            RetryPending();
        }
    }

    /// <summary>
    /// Puts the book back as it was before <see cref="MergeTextBlocks"/>, then the notes as in <see cref="RestoreNotes"/>
    /// (a rewrite that only waited in <c>pending.json</c> loses its open add of the first block's usage). Refused before
    /// anything changes if the book is no longer exactly as the merge left it.
    /// </summary>
    private void UndoMerge(
        string bookPath, string linkName, PageStructureSnapshot before, PageStructureSnapshot after,
        IReadOnlyList<(NoteChange Change, Guid FirstId)> changes)
    {
        var page = CurrentPage(bookPath);
        if (!after.Matches(page))
        {
            throw new InvalidOperationException("Das Buch wurde seit dem Zusammenfügen geändert.");
        }

        before.RestoreInto(page);
        SaveBook(page, () => after.RestoreInto(page));

        foreach (var (change, firstId) in changes.Where(c => c.Change.Pending))
        {
            ClearPending(change.NoteId, linkName, firstId);
        }

        if (RestoreNotes(linkName, [.. changes.Select(c => c.Change)]))
        {
            RetryPending();
        }
    }

    /// <summary>
    /// Undoes usage changes of notes: a note still exactly as the change left it gets its original lines back; any other
    /// note gets the usage (<see cref="NoteChange.BookBlockId"/>) added again. Changes are undone newest first, so that
    /// several changes of the same note are each checked against the state they left. Returns whether every note write
    /// succeeded.
    /// </summary>
    private bool RestoreNotes(string linkName, IReadOnlyList<NoteChange> changes)
    {
        var allWritten = true;
        foreach (var change in changes.Reverse())
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

        return allWritten;
    }

    /// <summary>
    /// Adds or removes one <c>used-in::</c> entry of a note and writes the note page. An add with
    /// <paramref name="replacing"/> rewrites the entry for that book block in place instead, or drops it if the added
    /// entry is there already (see <see cref="ApplyEntry"/>). A read-only page is left unchanged; for it, and when the
    /// write fails, the change goes to <c>pending.json</c>, a rewrite as an add and a remove (the in-memory note keeps a
    /// change that failed to write). Without a book block id there is no pending entry: the exception is passed on.
    /// </summary>
    private NoteChange UpdateNote(Page page, Block note, string linkName, Guid? bookBlockId, bool remove, Guid? replacing = null)
    {
        var noteId = note.Id ?? throw new InvalidOperationException("The note block has no id.");
        var before = note.Lines.ToList();
        bool pendingNow;
        if (page.IsReadOnly)
        {
            if (bookBlockId is not { } id)
            {
                throw new ReadOnlyPageException($"Die Datei '{page.FilePath}' ist schreibgeschützt: {page.ParseError}");
            }

            Queue(new PendingNoteUpdate(noteId, Locate(page, note), linkName, id, remove));
            pendingNow = true;
        }
        else
        {
            ApplyEntry(note, linkName, bookBlockId, remove, replacing);
            pendingNow = SaveNote(page, note, noteId, linkName, bookBlockId, remove);
        }

        if (replacing is { } replaced)
        {
            if (pendingNow)
            {
                Queue(new PendingNoteUpdate(noteId, Locate(page, note), linkName, replaced, Remove: true));
            }
            else
            {
                ClearPending(noteId, linkName, replaced);
            }
        }

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

    /// <summary>
    /// Adds or removes the <c>used-in::</c> entry for this book block. An add with <paramref name="replacing"/> puts the
    /// entry in place of the first entry for that book block and drops the others, or only drops them if the entry is
    /// there already; without such an entry it is a plain add.
    /// </summary>
    private static void ApplyEntry(Block note, string linkName, Guid? bookBlockId, bool remove, Guid? replacing = null)
    {
        var entries = UsedInValue.Parse(note.GetProperty(UsedInKey) ?? "").ToList();
        bool Matches(UsedInEntry e) => IsEntry(e, bookBlockId);
        bool IsEntry(UsedInEntry e, Guid? id) =>
            e.BookBlockId == id && string.Equals(e.PageName, linkName, StringComparison.OrdinalIgnoreCase);

        var replaced = !remove && replacing is { } old ? entries.FindIndex(e => IsEntry(e, old)) : -1;
        if (replaced >= 0)
        {
            if (!entries.Any(Matches))
            {
                entries[replaced] = new UsedInEntry(linkName, bookBlockId);
            }

            entries.RemoveAll(e => IsEntry(e, replacing));
        }
        else if (remove ? entries.RemoveAll(Matches) == 0 : entries.Any(Matches))
        {
            return;
        }
        else if (!remove)
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

    /// <summary>
    /// The text block block and the element block with <paramref name="key"/>: the text block itself or one of its
    /// details (with <paramref name="detailOnly"/> only a detail).
    /// </summary>
    private static (Block TextBlock, Block Element) FindElement(Book book, Guid key, bool detailOnly)
    {
        if (BookElements.Find(book, key) is { Kind: ElementKind.TextBlock or ElementKind.Detail } element
            && (!detailOnly || element.Kind == ElementKind.Detail))
        {
            var textBlock = BookElements.TextBlockOf(book, element)!;
            var block = element.Kind == ElementKind.TextBlock
                ? textBlock.Block
                : textBlock.Paragraphs.First(p => p.Block.Key == key).Block;
            return (textBlock.Block, block);
        }

        throw new ArgumentException(
            detailOnly ? "No detail has this key." : "No text block or detail has this key.", "position");
    }

    /// <summary>
    /// <paramref name="content"/> with <paramref name="insert"/> (block content) put in at <paramref name="offset"/> of the
    /// text as the editor shows it (<see cref="IntoPosition.AtCursor.Offset"/>); joined as editor text and escaped once, so
    /// that only real line starts get escaped. A detail's editor text has no <c>#notiz</c> tag
    /// (<see cref="BookSnapshot.ParagraphText"/>): there the offset counts the text between the tags, and every tag stays
    /// as it is where it is. At a tag the insertion goes on the side where the tag keeps the space
    /// <see cref="NoteTag.Remove"/> takes away (a tag without one gets it), so the editor then shows exactly its text
    /// with the insertion.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is outside the shown text.</exception>
    private static string InsertText(string content, bool isDetail, int offset, string insert)
    {
        var text = BlockTextEscape.Unescape(content);
        var note = BlockTextEscape.Unescape(insert);
        IReadOnlyList<(int Index, int Length)> tags = isDetail ? NoteTag.RemovedSpans(text) : [];
        var shown = 0;
        var start = 0;
        foreach (var (index, length) in tags)
        {
            var segment = text[start..index];
            var segmentLength = InlineMarkdown.TextLength(segment);
            var tag = text.Substring(index, length);
            // At the tag: before it if the tag's space is on its left (or it has none), else after it (next segment).
            if (offset < shown + segmentLength || (offset == shown + segmentLength && !tag.EndsWith(' ')))
            {
                var (before, after) = InlineMarkdown.SplitText(segment, offset - shown);
                var space = after.Length == 0 && !tag.StartsWith(' ') ? " " : "";
                return BlockTextEscape.Escape(text[..start] + before + note + space + after + text[index..]);
            }

            shown += segmentLength;
            start = index + length;
        }

        var (head, rest) = InlineMarkdown.SplitText(text[start..], offset - shown);
        return BlockTextEscape.Escape(text[..start] + head + note + rest);
    }

    /// <summary>Adds <paramref name="noteId"/> to the block's <c>source::</c> unless it is there; returns whether it was added.</summary>
    private static bool AddSource(Block textBlock, Guid noteId)
    {
        var sources = SourceValue.Parse(textBlock.GetProperty(SourceKey) ?? "").ToList();
        if (sources.Contains(noteId))
        {
            return false;
        }

        sources.Add(noteId);
        textBlock.SetProperty(SourceKey, SourceValue.Format(sources));
        return true;
    }

    private static List<SubtreeBlock> CaptureSubtree(Block root) =>
        [.. Subtree(root).Select(block => new SubtreeBlock(block, block.Parent, [.. block.Lines], block.IsDirty))];

    /// <summary>The subtree of <paramref name="root"/> consists of exactly the captured blocks, with their parents and lines.</summary>
    private static bool SubtreeIs(Block root, IReadOnlyList<SubtreeBlock> captured)
    {
        var current = Subtree(root).ToList();
        return current.Count == captured.Count
               && current.Zip(captured).All(pair =>
                   pair.First == pair.Second.Block && pair.First.Parent == pair.Second.Parent
                   && pair.First.Lines.SequenceEqual(pair.Second.Lines));
    }

    /// <summary>Gives every captured block its captured lines back, marked dirty or with its captured dirty flag.</summary>
    private static void RestoreSubtree(IEnumerable<SubtreeBlock> captured, bool markDirty)
    {
        foreach (var entry in captured)
        {
            if (!entry.Block.Lines.SequenceEqual(entry.Lines) || entry.Block.IsDirty != entry.IsDirty)
            {
                entry.Block.RestoreLines(entry.Lines, markDirty || entry.IsDirty);
            }
        }
    }

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

    /// <summary>A block of a text block's subtree with its parent, lines and dirty flag at one moment.</summary>
    private sealed record SubtreeBlock(Block Block, Block? Parent, IReadOnlyList<RawLine> Lines, bool IsDirty);

    /// <summary>
    /// What <see cref="AdoptInto"/> did: the blocks it inserted into the text block's subtree, and the subtree before
    /// and after (as saved).
    /// </summary>
    private sealed record AdoptedInto(List<Block> Inserted, IReadOnlyList<SubtreeBlock> Before, IReadOnlyList<SubtreeBlock> After);

    private static IEnumerable<Block> Subtree(Block block) => block.Children.SelectMany(Subtree).Prepend(block);

    private sealed class UndoAction(string description, Action undo) : IUndoAction
    {
        public string Description => description;

        public void Undo() => undo();
    }
}
