using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Storage;
using NoteEvolution.Core.Text;
using NoteEvolution.Core.Vaults;

namespace NoteEvolution.Core.Links;

/// <param name="clock">
/// The time for the <c>created::</c> / <c>updated::</c> dates of the text blocks the service adopts, merges or changes
/// (<see cref="BlockDates"/>); without a clock nothing is stamped.
/// </param>
public sealed partial class LinkService(IVault vault, IPageWriter writer, UndoManager undo, PendingStore pending, IClock? clock = null)
    : ILinkService
{
    private const string SourceKey = "source";
    private const string UsedInKey = "used-in";
    private const string IdKey = "id";
    private const string CollapsedKey = "collapsed";

    /// <summary>The status of every block an adopt creates (<c>status:: relevant</c>).</summary>
    private static readonly string Relevant = BlockStatuses.FileValue(BlockStatus.Relevant)!;

    public AdoptResult Adopt(Book book, Guid noteBlockKey, InsertPosition position, bool includeChildren = true, bool hideUsed = false)
    {
        var bookPage = book.Page;
        EnsureWritable(bookPage);
        var (notePage, note) = FindWritableNote(noteBlockKey);

        var (parent, index) = Resolve(book, position);

        var copy = CopyOf(note, includeChildren, hideUsed);
        var ownOnly = LeavesOutSubPoints(note, includeChildren, hideUsed);
        var noteLines = note.Lines.ToList();
        var noteWasDirty = note.IsDirty;
        var noteId = note.EnsureId();
        bookPage.InsertBlock(parent, index, copy);
        var textBlockId = copy.EnsureId();
        copy.SetProperty(SourceKey, SourceValue.Format([noteId]));
        copy.SetProperty(BlockStatuses.Key, Relevant);
        if (clock is not null)
        {
            BlockDates.StampNew(copy, clock.Now);
        }

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

        var change = UpdateNote(notePage, note, book.LinkName, textBlockId, remove: false, withoutChildren: ownOnly);
        var noteUpdatePending = change.Pending;

        var bookPath = bookPage.FilePath;
        var linkName = book.LinkName;
        undo.Push(new UndoAction("Übernehmen", () => UndoAdopt(bookPath, linkName, textBlockId, change)));
        if (!noteUpdatePending)
        {
            RetryPending();
        }

        return new AdoptResult(copy.Key, textBlockId, noteUpdatePending);
    }

    public AdoptResult AdoptInto(Book book, Guid noteBlockKey, IntoPosition position, bool includeChildren = true, bool hideUsed = false)
    {
        var bookPage = book.Page;
        EnsureWritable(bookPage);
        var (notePage, note) = FindWritableNote(noteBlockKey);

        // An appendix entry is no element (BookElements.Find): FindElement refuses it like an unknown key.
        var (textBlock, element) = position switch
        {
            IntoPosition.AtCursor at => FindElement(book, at.ElementKey, detailOnly: false),
            IntoPosition.AfterDetail after => FindElement(book, after.DetailKey, detailOnly: true),
            IntoPosition.FirstChild first => FindElement(book, first.ElementKey, detailOnly: false),
            _ => throw new ArgumentException("Unknown position.", nameof(position)),
        };
        var copy = CopyOf(note, includeChildren, hideUsed);
        var ownOnly = LeavesOutSubPoints(note, includeChildren, hideUsed);

        // The new top-level details are relevant: at the cursor the note's sub-bullets, else the copy. Set while they
        // are detached, so that one without a place for it (an unclosed code fence) throws before anything changed.
        IReadOnlyList<Block> tops = position is IntoPosition.AtCursor ? copy.Children : [copy];
        foreach (var top in tops)
        {
            top.SetProperty(BlockStatuses.Key, Relevant);
        }

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
        StampChanged(textBlock);
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
        var change = UpdateNote(notePage, note, book.LinkName, textBlockId, remove: false, withoutChildren: ownOnly);
        var noteUpdatePending = change.Pending;

        var bookPath = bookPage.FilePath;
        var linkName = book.LinkName;
        undo.Push(new UndoAction(
            "Übernehmen", () => UndoAdoptInto(bookPath, linkName, textBlockId, change, sourceAdded, adopted)));
        if (!noteUpdatePending)
        {
            RetryPending();
        }

        return new AdoptResult(textBlock.Key, textBlockId, noteUpdatePending);
    }

    public HeadingAdoptResult AdoptAsHeading(
        Book book, Guid noteBlockKey, Guid sectionKey, bool asSubheading, string untitledTitle, bool includeChildren = true,
        bool hideUsed = false)
    {
        var bookPage = book.Page;
        EnsureWritable(bookPage);
        var (notePage, note) = FindWritableNote(noteBlockKey);
        List<Block> subPoints = includeChildren ? [.. NoteBlock.ShownChildren(note, hideUsed)] : [];
        var ownOnly = LeavesOutSubPoints(note, includeChildren, hideUsed);
        if (includeChildren && subPoints.Count == 0)
        {
            throw new ArgumentException("The note block has no sub-points.", nameof(noteBlockKey));
        }

        var section = book.FindNode(sectionKey) ?? throw new ArgumentException("No section has this key.", nameof(sectionKey));
        if (book.IsInAppendix(sectionKey))
        {
            throw new ArgumentException("A note is not adopted into the appendix.", nameof(sectionKey));
        }

        var level = asSubheading ? section.Level + 1 : Math.Max(section.Level, 1);
        if (level > OutlineEditor.MaxLevel)
        {
            throw new ArgumentException($"A heading below level {section.Level} would exceed level {OutlineEditor.MaxLevel}.", nameof(sectionKey));
        }

        // A sub-heading goes before the section's first sub-section, a heading of the same level right after the
        // section's subtree: no existing block changes its section. A top-level heading goes before the appendix.
        var firstSubsection = section.Items.ToList().FindIndex(item => item is OutlineNode);
        var (parent, index) = asSubheading
            ? (section.Block, firstSubsection >= 0 ? firstSubsection : section.Items.Count)
            : section.Block is { } sectionBlock
                ? (sectionBlock.Parent, IndexOf(bookPage, sectionBlock) + 1)
                : (null, bookPage.Roots.Count);
        if (parent is null)
        {
            index = book.BeforeAppendix(index);
        }

        // Everything is built detached first, so that a text that would not stay one block throws before anything changed.
        var text = NoteTag.Has(note.Content) ? NoteTag.Remove(note.Content) : note.Content;
        var (title, further) = HeadingText.Split(text);
        var heading = Block.CreateDetached(HeadingText.WithTitle("", level, title.Length > 0 ? title : untitledTitle));
        var adopted = new List<(Block Note, Block Copy)>();
        if (further is not null)
        {
            if (HeadingText.StartsAsHeading(further))
            {
                // As a text block of its own it would be read as a heading, and the note would not be linked.
                throw new ArgumentException("The note block's further text would start with a heading.", nameof(noteBlockKey));
            }

            adopted.Add((note, Block.CreateDetached(further)));
        }

        adopted.AddRange(subPoints.Select(child => (child, CopyOf(child, true, hideUsed))));

        List<(Block Block, List<RawLine> Lines, bool WasDirty)> notesBefore =
            [.. subPoints.Prepend(note).Select(block => (block, block.Lines.ToList(), block.IsDirty))];
        var noteId = note.EnsureId();
        bookPage.InsertBlock(parent, index, heading);
        var links = new List<(Block Note, Guid NoteId, Guid TextBlockId)>();
        foreach (var (source, copy) in adopted)
        {
            var sourceId = source.EnsureId();
            bookPage.InsertBlock(heading, heading.Children.Count, copy);
            var textBlockId = copy.EnsureId();
            copy.SetProperty(SourceKey, SourceValue.Format([sourceId]));
            copy.SetProperty(BlockStatuses.Key, Relevant);
            if (clock is not null)
            {
                BlockDates.StampNew(copy, clock.Now);
            }

            links.Add((source, sourceId, textBlockId));
        }

        try
        {
            SaveBook(bookPage, () => bookPage.RemoveBlock(heading));
        }
        catch
        {
            // The book does not reference the notes, so new id:: lines must not reach the note file later.
            foreach (var (block, blockLines, wasDirty) in notesBefore)
            {
                block.RestoreLines(blockLines, wasDirty);
            }

            throw;
        }

        var headingAfter = CaptureSubtree(heading);

        // A note block without a text block of its own counts as used by the book. The entry is written with the
        // sub-points' entries (they are on the same page); without a block reference it cannot wait in pending.json.
        var linkName = book.LinkName;
        var bookUsageAdded = false;
        NoteChange? bookUsage = null;
        if (links.All(link => link.Note != note))
        {
            bookUsageAdded = !UsedInValue.Parse(note.GetProperty(UsedInKey) ?? "")
                .Any(e => e.BookBlockId is null && string.Equals(e.PageName, linkName, StringComparison.OrdinalIgnoreCase));
            var before = note.Lines.ToList();
            var aloneBefore = UsedAloneOnly(note);
            AddEntry(note, linkName, null, ownOnly);
            bookUsage = new NoteChange(noteId, null, before, note.Lines.ToList(), Pending: false, aloneBefore);
            if (links.Count == 0)
            {
                // No sub-point's entry follows to write the page (a heading without sub-points).
                SaveNote(notePage, note, noteId, linkName, null, remove: false);
            }
        }

        var noteUpdatePending = false;
        var changes = new List<NoteChange>();
        foreach (var (source, _, textBlockId) in links)
        {
            // The note's own further text carries its usage; the sub-points stay unused when they were left out. A
            // sub-point goes with its (shown) sub-points.
            var change = UpdateNote(notePage, source, linkName, textBlockId, remove: false, withoutChildren: source == note && ownOnly);
            noteUpdatePending |= change.Pending;
            changes.Add(change);
        }

        var bookPath = bookPage.FilePath;
        var headingKey = heading.Key;
        undo.Push(new UndoAction(
            "Übernehmen", () => UndoAdoptAsHeading(bookPath, linkName, headingKey, headingAfter, changes, bookUsage, bookUsageAdded)));
        if (!noteUpdatePending)
        {
            RetryPending();
        }

        return new HeadingAdoptResult(headingKey, noteUpdatePending);
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
        var (textBlock, detail) = FindElement(book, detailKey, detailOnly: true);
        var lines = textBlock.Lines.ToList();
        var wasDirty = textBlock.IsDirty;
        StampChanged(textBlock);
        try
        {
            DeleteBlock(book, detail);
        }
        catch
        {
            textBlock.RestoreLines(lines, wasDirty);
            throw;
        }
    }

    /// <summary>Gives a book text block <c>updated::</c> (a change of its text or details); nothing without a clock.</summary>
    private void StampChanged(Block textBlock)
    {
        if (clock is not null)
        {
            BlockDates.StampChanged(textBlock, clock.Now);
        }
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

        var contents = merges.Select(MergedContent).ToList();
        contents.ForEach(Block.ValidateContent);
        var plans = merges.Select(blocks => PlanMerge(blocks, takeOverId: true)).ToList();
        EnsureUnreferenced(book, [.. plans.SelectMany(plan => plan.RemovedIds).Distinct()]);

        RunMerge(book, moves =>
        {
            foreach (var ((blocks, content), plan) in merges.Zip(contents).Zip(plans))
            {
                var first = blocks[0];
                var later = blocks.Skip(1).ToList();
                if (plan.TakenId is { } takenId)
                {
                    first.SetProperty(IdKey, takenId);
                }

                foreach (var property in plan.Carried)
                {
                    first.SetProperty(property.Key, property.Value);
                }

                if (plan.EarliestCreated is { } created)
                {
                    first.SetProperty(BlockDates.CreatedKey, created);
                }

                foreach (var block in later)
                {
                    var sources = SourceValue.Parse(block.GetProperty(SourceKey) ?? "");
                    if (block.Id is { } removedId && removedId != first.Id && sources.Count > 0)
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
                StampChanged(first);
                foreach (var block in later)
                {
                    foreach (var detail in block.Children.ToList())
                    {
                        page.MoveBlock(detail, first, first.Children.Count);
                    }

                    page.RemoveBlock(block);
                }
            }

            // References to a removed block now point to its group's first block, which has an id whenever one goes.
            RetargetReferences(
                page, merges.Zip(plans).SelectMany(m => m.Second.RemovedIds.Select(id => (id, m.First[0].Id!.Value))));
        });

        return merges[0][0].Key;
    }

    public Guid MergeDetails(Book book, Guid elementKey)
    {
        var page = book.Page;
        EnsureWritable(page);
        if (BookElements.Find(book, elementKey) is not { Kind: ElementKind.TextBlock or ElementKind.Detail })
        {
            throw new ArgumentException("No text block or detail has this key.", nameof(elementKey));
        }

        var (textBlock, head) = FindElement(book, elementKey, detailOnly: false);
        var merged = BookElements.DetailsToMerge(head);
        if (merged.Count == 0)
        {
            throw new ArgumentException("The element has no detail to merge.", nameof(elementKey));
        }

        List<Block> blocks = [head, .. merged];
        var content = MergedContent(blocks);
        Block.ValidateContent(content);
        // collapsed:: is the Logseq view state of a detail that goes: it must not collapse (or block) the head.
        var plan = PlanMerge(blocks, takeOverId: false, ignoredKeys: [CollapsedKey]);
        EnsureUnreferenced(book, plan.RemovedIds);

        RunMerge(book, moves =>
        {
            foreach (var property in plan.Carried)
            {
                head.SetProperty(property.Key, property.Value);
            }

            // Only a text block has dates: a detail head does not take a created:: over.
            if (head == textBlock && plan.EarliestCreated is { } created)
            {
                head.SetProperty(BlockDates.CreatedKey, created);
            }

            foreach (var detail in merged)
            {
                // A linked detail: its sources go to the text block (a detail has no source of its own after this), and
                // the notes' usages of it move to the text block, which needs an id for that.
                var sources = SourceValue.Parse(detail.GetProperty(SourceKey) ?? "");
                if (detail.Id is { } removedId && sources.Count > 0)
                {
                    moves.Add((textBlock.EnsureId(), removedId, [.. sources.Distinct()]));
                }

                foreach (var noteId in sources)
                {
                    AddSource(textBlock, noteId);
                }
            }

            head.SetContent(content);
            StampChanged(textBlock);
            foreach (var detail in head.Children.Where(merged.Contains).ToList())
            {
                // The Bemerkung (remark) details below a merged detail stay: they come up to the head, where the detail
                // was.
                var index = IndexOf(page, detail) + 1;
                foreach (var note in BookElements.NoteDetailsBelow(detail))
                {
                    page.MoveBlock(note, head, index++);
                }

                page.RemoveBlock(detail);
            }

            // A detail is no reference target, but a reference to a merged one is kept valid: it points to the text block,
            // which gets an id only for that.
            if (ReferencesAny(page, plan.RemovedIds))
            {
                var textBlockId = textBlock.EnsureId();
                RetargetReferences(page, plan.RemovedIds.Select(id => (id, textBlockId)));
            }
        });

        return head.Key;
    }

    /// <summary>Whether a block text of <paramref name="page"/> refers (<c>((id))</c>) to one of <paramref name="ids"/>.</summary>
    private static bool ReferencesAny(Page page, IReadOnlyCollection<Guid> ids) =>
        ids.Count > 0 && page.AllBlocks().Any(block => BlockRefs.Find(block.Content).Any(r => ids.Contains(r.Id)));

    /// <summary>
    /// Points the references <c>((id))</c> in the block texts of <paramref name="page"/> from each removed id to its
    /// remaining one; other lines of those blocks stay byte-identical. No <c>updated::</c>: this is no edit of the text.
    /// </summary>
    private static void RetargetReferences(Page page, IEnumerable<(Guid From, Guid To)> moves)
    {
        var map = moves
            .Where(move => move.From != move.To)
            .DistinctBy(move => move.From)
            .ToDictionary(move => move.From, move => move.To);
        if (map.Count == 0)
        {
            return;
        }

        foreach (var block in page.AllBlocks().ToList())
        {
            if (BlockRefs.Find(block.Content).Any(r => map.ContainsKey(r.Id)))
            {
                block.SetContent(BlockRefs.Retarget(block.Content, map));
            }
        }
    }

    /// <summary>
    /// Runs a merge on the book page (<paramref name="apply"/>, which records the usages to move: the notes'
    /// <c>used-in::</c> of a removed block that are rewritten to the remaining block), saves the book, rewrites those
    /// usages and records the undo action „Zusammenfügen“ (<see cref="UndoMerge"/>). Whatever fails half-way in
    /// <paramref name="apply"/>, the page is left as it was.
    /// </summary>
    private void RunMerge(Book book, Action<List<(Guid FirstId, Guid RemovedId, IReadOnlyList<Guid> Sources)>> apply)
    {
        var page = book.Page;
        var before = PageStructureSnapshot.Capture(page);
        var moves = new List<(Guid FirstId, Guid RemovedId, IReadOnlyList<Guid> Sources)>();
        try
        {
            apply(moves);
        }
        catch
        {
            before.RestoreInto(page);
            throw;
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
    /// The content of a merged group: the blocks' contents joined by a blank line, blank ones left out (an empty first
    /// block takes the next text); the first block's own content if all are blank.
    /// </summary>
    private static string MergedContent(List<Block> blocks)
    {
        var texts = blocks.Select(block => block.Content).Where(content => !string.IsNullOrWhiteSpace(content)).ToList();
        return texts.Count > 0 ? string.Join("\n\n", texts) : blocks[0].Content;
    }

    /// <summary>
    /// What merging <paramref name="blocks"/> does to the first block's properties besides <c>source::</c> and
    /// <c>status::</c>: the <c>id::</c> it takes over (raw value), the properties it gets from the later blocks, and the
    /// ids that go.
    /// </summary>
    /// <param name="TakenId">
    /// The first later block's <c>id::</c> with a valid id when the first block has none (and ids are taken over);
    /// else <c>null</c>.
    /// </param>
    /// <param name="Carried">
    /// The later blocks' properties (other than <c>id::</c>, <c>source::</c>, <c>status::</c> and the dates) the first
    /// block lacks.
    /// </param>
    /// <param name="RemovedIds">The ids of the later blocks that vanish (not the one taken over).</param>
    /// <param name="EarliestCreated">
    /// The raw <c>created::</c> value of a later block that is earlier than the first block's (or the first block has
    /// no readable one); else <c>null</c>: the first block keeps its own line (or none).
    /// </param>
    private sealed record MergePlan(
        string? TakenId, IReadOnlyList<BlockProperty> Carried, IReadOnlyList<Guid> RemovedIds, string? EarliestCreated);

    /// <summary>
    /// Plans the properties of one group (see <see cref="MergePlan"/>): a later block's property is carried over when
    /// the first block (or an earlier later block) does not have its key yet (ignoring case). With
    /// <paramref name="takeOverId"/> the first block without an <c>id::</c> takes over the first later one's; without
    /// it every later block's id goes (merged details: their usages move to the text block). <c>status::</c>, the dates
    /// and the properties with a key in <paramref name="ignoredKeys"/> are neither carried over nor compared: the first
    /// block keeps its own line (or none), the later blocks' lines go with them; the earliest readable
    /// <c>created::</c> of the group is planned separately (see <see cref="MergePlan"/>), <c>updated::</c> is stamped.
    /// </summary>
    /// <exception cref="MergeRefusedException">Two of the blocks have different values for the same key.</exception>
    private static MergePlan PlanMerge(List<Block> blocks, bool takeOverId, IReadOnlyCollection<string>? ignoredKeys = null)
    {
        var first = blocks[0];
        var later = blocks.Skip(1).ToList();
        var taken = takeOverId && first.Id is null ? later.FirstOrDefault(block => block.Id is not null) : null;
        var keptId = first.Id ?? taken?.Id;

        bool MergedSeparately(BlockProperty property) =>
            string.Equals(property.Key, IdKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(property.Key, SourceKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(property.Key, BlockStatuses.Key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(property.Key, BlockDates.CreatedKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(property.Key, BlockDates.UpdatedKey, StringComparison.OrdinalIgnoreCase)
            || (ignoredKeys?.Contains(property.Key, StringComparer.OrdinalIgnoreCase) ?? false);

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in first.Properties.Where(p => !MergedSeparately(p)))
        {
            values.TryAdd(property.Key, property.Value);
        }

        var carried = new List<BlockProperty>();
        foreach (var property in later.SelectMany(block => block.Properties).Where(p => !MergedSeparately(p)))
        {
            if (values.TryGetValue(property.Key, out var value))
            {
                if (!string.Equals(value, property.Value, StringComparison.Ordinal))
                {
                    throw new MergeRefusedException($"The merged blocks have different values for the property '{property.Key}'.");
                }
            }
            else
            {
                values.Add(property.Key, property.Value);
                carried.Add(property);
            }
        }

        var removedIds = later.Select(block => block.Id).OfType<Guid>().Where(id => id != keptId).Distinct().ToList();
        var firstCreated = BlockDates.Created(first);
        var earliest = later
            .Where(block => BlockDates.Created(block) is { } created && (firstCreated is null || created < firstCreated))
            .MinBy(BlockDates.Created);
        return new MergePlan(taken?.GetProperty(IdKey), carried, removedIds, earliest?.GetProperty(BlockDates.CreatedKey));
    }

    /// <summary>
    /// Refuses a merge when a block reference <c>((id))</c> to one of <paramref name="removedIds"/> stands anywhere in
    /// the vault (page lines before the first block, block texts, property values) other than in a <c>used-in::</c>
    /// value (which the merge rewrites), a <c>source::</c> value of the book itself, or a block text of the book (whose
    /// cross references the merge retargets).
    /// </summary>
    /// <exception cref="MergeRefusedException">Such a reference exists.</exception>
    private void EnsureUnreferenced(Book book, IReadOnlyList<Guid> removedIds)
    {
        if (removedIds.Count == 0)
        {
            return;
        }

        var references = removedIds.Select(id => $"(({id:D}))").ToList();
        bool Mentions(string text) => references.Any(reference => text.Contains(reference, StringComparison.OrdinalIgnoreCase));

        foreach (var page in vault.Pages)
        {
            var isBook = ReferenceEquals(page, book.Page);
            bool Rewritten(BlockProperty property) =>
                string.Equals(property.Key, UsedInKey, StringComparison.OrdinalIgnoreCase)
                || (isBook && string.Equals(property.Key, SourceKey, StringComparison.OrdinalIgnoreCase));

            if (page.PrefixLines.Any(line => Mentions(line.Text))
                || page.AllBlocks().Any(block =>
                    (!isBook && Mentions(block.Content)) || block.Properties.Any(property => !Rewritten(property) && Mentions(property.Value))))
            {
                throw new MergeRefusedException($"The id of a block that would go is referenced in '{page.FilePath}'.");
            }
        }
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

    /// <summary>
    /// Removes the adopted book block (found by its id) and takes the note's usage back (<see cref="UndoUsage"/>); the
    /// note keeps its <c>id::</c>.
    /// </summary>
    private void UndoAdopt(string bookPath, string linkName, Guid textBlockId, NoteChange change)
    {
        var page = CurrentPage(bookPath);
        if (page.AllBlocks().FirstOrDefault(b => b.Id == textBlockId) is { } block)
        {
            var parent = block.Parent;
            var index = IndexOf(page, block);
            page.RemoveBlock(block);
            SaveBook(page, () => page.RestoreBlock(parent, index, block));
        }

        if (!UndoUsage(linkName, change, removeOtherwise: true))
        {
            RetryPending();
        }
    }

    /// <summary>
    /// Takes back the usage an adoption added to a note (<paramref name="change"/>): a note still exactly as the
    /// adoption left it gets its lines from before back, with its <c>used-without-children::</c> state; any other note
    /// loses the usage if <paramref name="removeOtherwise"/>, and while others remain their mark is set as it was before
    /// the adoption (<see cref="NoteChange.AloneBefore"/>): the adoption that is taken back may have removed it, or a
    /// later one adopting the block alone may not have written it. An open add for the usage of a note that is not found
    /// is dropped. Returns whether the note update waits in <c>pending.json</c>.
    /// </summary>
    private bool UndoUsage(string linkName, NoteChange change, bool removeOtherwise)
    {
        if (FindNote(change.NoteId) is not { } note)
        {
            if (change.BookBlockId is { } id)
            {
                ClearPending(change.NoteId, linkName, id);
            }

            return false;
        }

        if (!note.Page.IsReadOnly && note.Block.Lines.SequenceEqual(change.After))
        {
            note.Block.RestoreLines(change.Before, isDirty: true);
            return SaveNote(note.Page, note.Block, change.NoteId, linkName, change.BookBlockId, remove: true, change.AloneBefore);
        }

        // An entry without block reference cannot wait in pending.json for a read-only page.
        if (!removeOtherwise || (note.Page.IsReadOnly && change.BookBlockId is null))
        {
            return false;
        }

        return UpdateNote(note.Page, note.Block, linkName, change.BookBlockId, remove: true, withoutChildren: change.AloneBefore).Pending;
    }

    /// <summary>
    /// Removes the heading <see cref="AdoptAsHeading"/> added (found by its key) with everything below it, then takes
    /// back the usages of its text blocks (<paramref name="changes"/>) and the entry without block reference
    /// (<paramref name="bookUsage"/>, removed from a changed note only if <paramref name="bookUsageAdded"/>), see
    /// <see cref="UndoUsage"/>; the notes keep their <c>id::</c>. Refused before anything changes if the heading with its
    /// subtree is no longer exactly as <see cref="AdoptAsHeading"/> left it (<paramref name="after"/>): text written
    /// below it would go, and the usages of a block split off would stay.
    /// </summary>
    private void UndoAdoptAsHeading(
        string bookPath, string linkName, Guid headingKey, IReadOnlyList<SubtreeBlock> after,
        IReadOnlyList<NoteChange> changes, NoteChange? bookUsage, bool bookUsageAdded)
    {
        var page = CurrentPage(bookPath);
        if (page.AllBlocks().FirstOrDefault(b => b.Key == headingKey) is not { } heading || !SubtreeIs(heading, after))
        {
            throw new InvalidOperationException("Die Überschrift wurde seit dem Übernehmen geändert.");
        }

        var parent = heading.Parent;
        var index = IndexOf(page, heading);
        page.RemoveBlock(heading);
        SaveBook(page, () => page.RestoreBlock(parent, index, heading));

        var noteUpdatePending = false;
        foreach (var change in changes)
        {
            noteUpdatePending |= UndoUsage(linkName, change, removeOtherwise: true);
        }

        if (bookUsage is not null)
        {
            noteUpdatePending |= UndoUsage(linkName, bookUsage, removeOtherwise: bookUsageAdded);
        }

        if (!noteUpdatePending)
        {
            RetryPending();
        }
    }

    /// <summary>
    /// Puts the text block with its details back as it was before <see cref="AdoptInto"/> (lines, without the inserted
    /// blocks), then takes the note's usage back (<see cref="UndoUsage"/>; from a changed note it is removed only if
    /// <see cref="AdoptInto"/> added the note as a source, <paramref name="sourceAdded"/>). Refused before anything
    /// changes if the text block's subtree is no longer exactly as <see cref="AdoptInto"/> left it.
    /// </summary>
    private void UndoAdoptInto(
        string bookPath, string linkName, Guid textBlockId, NoteChange change, bool sourceAdded, AdoptedInto adopted)
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

        var noteUpdatePending = UndoUsage(linkName, change, removeOtherwise: sourceAdded);
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
    /// Puts the book back as it was before <see cref="MergeTextBlocks"/> or <see cref="MergeDetails"/>, then the notes
    /// as in <see cref="RestoreNotes"/> (a rewrite that only waited in <c>pending.json</c> loses its open add of the
    /// remaining block's usage). Refused before anything changes if the book is no longer exactly as the merge left it.
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
                allWritten &= !SaveNote(note.Page, note.Block, change.NoteId, linkName, change.BookBlockId, remove: false, change.AloneBefore);
            }
            else
            {
                // The usage comes back as it was: one the note had only without its sub-points keeps (or gets) the mark.
                allWritten &= !UpdateNote(note.Page, note.Block, linkName, change.BookBlockId, remove: false, withoutChildren: change.AloneBefore).Pending;
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
    private NoteChange UpdateNote(
        Page page, Block note, string linkName, Guid? bookBlockId, bool remove, Guid? replacing = null, bool? withoutChildren = null)
    {
        var noteId = note.Id ?? throw new InvalidOperationException("The note block has no id.");
        var before = note.Lines.ToList();
        var aloneBefore = UsedAloneOnly(note);
        bool pendingNow;
        if (page.IsReadOnly)
        {
            if (bookBlockId is not { } id)
            {
                throw new ReadOnlyPageException($"Die Datei '{page.FilePath}' ist schreibgeschützt: {page.ParseError}");
            }

            Queue(new PendingNoteUpdate(noteId, Locate(page, note), linkName, id, remove, withoutChildren));
            pendingNow = true;
        }
        else
        {
            if (withoutChildren is { } ownOnly && !remove)
            {
                AddEntry(note, linkName, bookBlockId, ownOnly);
            }
            else if (remove)
            {
                RemoveEntry(note, linkName, bookBlockId, withoutChildren);
            }
            else
            {
                ApplyEntry(note, linkName, bookBlockId, remove, replacing);
            }

            pendingNow = SaveNote(page, note, noteId, linkName, bookBlockId, remove, withoutChildren);
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

        return new NoteChange(noteId, bookBlockId, before, note.Lines.ToList(), pendingNow, aloneBefore);
    }

    /// <summary>Writes the note page if it has changes; returns <c>true</c> if the write failed and was queued.</summary>
    private bool SaveNote(Page page, Block note, Guid noteId, string linkName, Guid? bookBlockId, bool remove, bool? withoutChildren = null)
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
            Queue(new PendingNoteUpdate(noteId, Locate(page, note), linkName, bookBlockId.Value, remove, withoutChildren));
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

        if (update.Remove)
        {
            RemoveEntry(note.Block, update.BookLinkName, update.BookBlockId, update.WithoutChildren);
        }
        else if (update.WithoutChildren is { } ownOnly)
        {
            AddEntry(note.Block, update.BookLinkName, update.BookBlockId, ownOnly);
        }
        else
        {
            ApplyEntry(note.Block, update.BookLinkName, update.BookBlockId, remove: false);
        }

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
    /// <returns>Whether the entries changed.</returns>
    private static bool ApplyEntry(Block note, string linkName, Guid? bookBlockId, bool remove, Guid? replacing = null)
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
            return false;
        }
        else if (!remove)
        {
            entries.Add(new UsedInEntry(linkName, bookBlockId));
        }

        if (entries.Count == 0)
        {
            note.RemoveProperty(UsedInKey);
            note.RemoveProperty(UsedInValue.WithoutChildrenKey);
        }
        else
        {
            note.SetProperty(UsedInKey, UsedInValue.Format(entries));
        }

        return true;
    }

    /// <summary>
    /// Removes the <c>used-in::</c> entry (see <see cref="ApplyEntry"/>); with <paramref name="aloneAfter"/>, while other
    /// usages remain, <c>used-without-children:: true</c> is set or removed as it says (taking back an adoption, see
    /// <see cref="UndoUsage"/>). Without it, or when the entry was not there, the mark stays as it is.
    /// </summary>
    private static void RemoveEntry(Block note, string linkName, Guid? bookBlockId, bool? aloneAfter)
    {
        if (ApplyEntry(note, linkName, bookBlockId, remove: true) && aloneAfter is { } alone
            && !string.IsNullOrWhiteSpace(note.GetProperty(UsedInKey)))
        {
            SetWithoutChildren(note, alone);
        }
    }

    /// <summary>Sets or removes <c>used-without-children:: true</c>.</summary>
    private static void SetWithoutChildren(Block note, bool alone)
    {
        if (alone)
        {
            note.SetProperty(UsedInValue.WithoutChildrenKey, "true");
        }
        else
        {
            note.RemoveProperty(UsedInValue.WithoutChildrenKey);
        }
    }

    /// <summary>
    /// Every usage of <paramref name="note"/> takes it alone, without its sub-points: it has none, or
    /// <c>used-without-children:: true</c>.
    /// </summary>
    private static bool UsedAloneOnly(Block note) =>
        string.IsNullOrWhiteSpace(note.GetProperty(UsedInKey)) || note.GetProperty(UsedInValue.WithoutChildrenKey) is not null;

    /// <summary>
    /// Whether an adoption of <paramref name="note"/> leaves sub-points out: it is without <paramref name="includeChildren"/>
    /// and the note has a (shown, see <see cref="NoteBlock.ShownChildren"/>) sub-point. A block without one adopted alone is
    /// the same as with its subtree and gets no mark.
    /// </summary>
    private static bool LeavesOutSubPoints(Block note, bool includeChildren, bool hideUsed) =>
        !includeChildren && NoteBlock.ShownChildren(note, hideUsed).Any();

    /// <summary>
    /// Adds the <c>used-in::</c> entry of an adoption (see <see cref="ApplyEntry"/>) with <c>used-without-children::
    /// true</c> right after it while every usage takes the block alone (<paramref name="ownOnly"/>, and no earlier usage
    /// took its sub-points along); an adoption with sub-points removes it.
    /// </summary>
    private static void AddEntry(Block note, string linkName, Guid? bookBlockId, bool ownOnly)
    {
        var withoutChildren = ownOnly && UsedAloneOnly(note);
        ApplyEntry(note, linkName, bookBlockId, remove: false);
        SetWithoutChildren(note, withoutChildren);
    }

    /// <summary>
    /// Writes a book page. If that fails, <paramref name="revert"/> undoes the edit on this instance, the vault gets
    /// the page as it is on disk again (its blocks keeping their keys, see <see cref="RuntimeKeys.Carry"/>), and the
    /// exception is passed on.
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
                var reloaded = LogseqParser.Parse(page.FilePath, File.ReadAllBytes(page.FilePath));
                RuntimeKeys.Carry(page, reloaded);
                vault.ReplacePage(reloaded);
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
    /// The note's copy for the book: the note block with its subtree, without property lines, and without a
    /// <c>#notiz</c> tag in the note block's own text (<see cref="NoteTag.Remove"/>). Without
    /// <paramref name="includeChildren"/> the copy has no sub-points; with <paramref name="hideUsed"/> it leaves out the
    /// sub-points "Verwendete ausblenden" hides (<see cref="NoteBlock.ShownChildren"/>) with everything below them.
    /// </summary>
    private static Block CopyOf(Block note, bool includeChildren, bool hideUsed)
    {
        var copy = note.CloneDetached(withoutProperties: true);
        if (!includeChildren)
        {
            copy.TakeChildren();
        }
        else if (hideUsed)
        {
            LeaveOutHidden(note, copy);
        }

        if (NoteTag.Has(copy.Content))
        {
            copy.SetContent(NoteTag.Remove(copy.Content));
        }

        return copy;
    }

    /// <summary>
    /// Removes from <paramref name="copy"/> (a clone of <paramref name="note"/>, its sub-points in the same order) the
    /// sub-points that are not among <see cref="NoteBlock.ShownChildren"/> with "Verwendete ausblenden", at every depth.
    /// </summary>
    private static void LeaveOutHidden(Block note, Block copy)
    {
        var shown = NoteBlock.ShownChildren(note, hideUsed: true).ToHashSet();
        for (var i = note.Children.Count - 1; i >= 0; i--)
        {
            if (shown.Contains(note.Children[i]))
            {
                LeaveOutHidden(note.Children[i], copy.Children[i]);
            }
            else
            {
                copy.RemoveChild(copy.Children[i]);
            }
        }
    }

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

    /// <summary>
    /// Parent block and child index for <paramref name="position"/>; the items of a section are its block's children.
    /// A note is not adopted into the appendix: a key there is refused.
    /// </summary>
    private static (Block? Parent, int Index) Resolve(Book book, InsertPosition position)
    {
        var key = position switch
        {
            InsertPosition.After after => after.TextBlockKey,
            InsertPosition.SectionStart start => start.SectionKey,
            InsertPosition.SectionEnd end => end.SectionKey,
            _ => Guid.Empty,
        };
        if (book.IsInAppendix(key))
        {
            throw new ArgumentException("A note is not adopted into the appendix.", nameof(position));
        }

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

    /// <summary>
    /// What a note looked like before and after one usage change; <paramref name="AloneBefore"/>: before it, every usage
    /// took the note without its sub-points (it had none, or <c>used-without-children:: true</c>, see
    /// <see cref="UsedAloneOnly"/>).
    /// </summary>
    private sealed record NoteChange(
        Guid NoteId, Guid? BookBlockId, IReadOnlyList<RawLine> Before, IReadOnlyList<RawLine> After, bool Pending, bool AloneBefore);

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
