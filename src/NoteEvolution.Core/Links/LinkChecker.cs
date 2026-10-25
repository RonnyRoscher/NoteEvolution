using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Storage;
using NoteEvolution.Core.Vaults;

namespace NoteEvolution.Core.Links;

/// <summary>A book block names the note in <c>source::</c>, but the note has no matching <c>used-in::</c> entry.</summary>
public sealed record MissingUsage(Guid NoteBlockId, UsedInEntry Entry);

/// <summary>A <c>used-in::</c> entry whose book or book block does not exist.</summary>
/// <param name="NoteBlockKey"><see cref="Block.Key"/> of the note block carrying the entry.</param>
public sealed record OrphanUsage(Guid NoteBlockKey, UsedInEntry Entry);

/// <summary>A <c>source::</c> reference to a note that does not exist.</summary>
/// <param name="TextBlockKey"><see cref="Block.Key"/> of the book block (text block or paragraph) carrying the source.</param>
public sealed record BrokenSource(string BookLinkName, Guid TextBlockKey, Guid MissingNoteId);

/// <summary>A reference that needs an appendix entry and has none (<see cref="AppendixIndex.MissingTargets"/>).</summary>
/// <param name="ElementKey">The element holding the <c>{{quelle}}</c> macro, or the source entry whose <c>autoren::</c> names the person.</param>
/// <param name="MissingId">The id of the source or person that is no entry.</param>
/// <param name="IsAuthor">Whether it is an author id of a source entry (otherwise a source citation in the text).</param>
public sealed record MissingAppendixRef(string BookLinkName, Guid ElementKey, Guid MissingId, bool IsAuthor);

/// <summary>An appendix section after the first of its kind: it counts as appendix, but is not used.</summary>
public sealed record DuplicateAppendixSection(string BookLinkName, Guid SectionKey, string Title);

/// <summary>Several people with the same abbreviation (ignoring case); nothing is changed, the names are told.</summary>
public sealed record DuplicateAbbreviation(string BookLinkName, string Abbreviation, IReadOnlyList<string> Names);

/// <param name="MissingCrossRefs">The block references <c>((id))</c> in the books whose target is gone, in book order.</param>
/// <param name="MissingAppendixRefs">
/// The <c>{{quelle}}</c> macros without a source entry and the <c>autoren::</c> ids without a person entry. A missing
/// <c>((id))</c> in the text stays in <paramref name="MissingCrossRefs"/>: after a deletion a person reference cannot
/// be told from a cross reference.
/// </param>
/// <param name="DuplicateAppendixSections">Appendix sections after the first of their kind (a hint only).</param>
/// <param name="DuplicateAbbreviations">Abbreviations several people share (a hint only).</param>
public sealed record LinkCheckReport(
    IReadOnlyList<MissingUsage> Missing,
    IReadOnlyList<OrphanUsage> Orphans,
    IReadOnlyList<BrokenSource> Broken,
    IReadOnlyList<CrossRefSource> MissingCrossRefs,
    IReadOnlyList<MissingAppendixRef> MissingAppendixRefs,
    IReadOnlyList<DuplicateAppendixSection> DuplicateAppendixSections,
    IReadOnlyList<DuplicateAbbreviation> DuplicateAbbreviations);

public enum OrphanResolution
{
    /// <summary>Replace the entry by the page link alone (<c>[[Book]]</c>, place unknown).</summary>
    MarkUnknown,

    /// <summary>Remove the entry.</summary>
    Remove,
}

/// <summary>
/// The link check (spec 5.4): compares <c>source::</c> on the books' blocks with <c>used-in::</c> on the notes.
/// <see cref="Analyze"/> only reads; <see cref="FixMissing"/> and <see cref="Resolve"/> change notes through
/// <see cref="ILinkService"/> (which records updates for read-only or unwritable notes in <c>pending.json</c>);
/// <see cref="RemoveMissingCrossRefs"/> changes book texts and saves them through the page writer.
/// </summary>
public sealed class LinkChecker(IVault vault, ILinkService links, IPageWriter writer)
{
    private const string SourceKey = "source";
    private const string UsedInKey = "used-in";
    private const string AuthorsKey = "autoren";

    /// <summary>
    /// Reads the vault and reports missing usages, orphans, broken sources and cross references without target
    /// (<see cref="CrossRefIndex.Missing"/> of every book). Orphans are found on every note
    /// block with a <c>used-in::</c> entry, with or without <c>id::</c> (e.g. notes converted from <c>[handled]</c>).
    /// </summary>
    public LinkCheckReport Analyze()
    {
        var missing = new List<MissingUsage>();
        var broken = new List<BrokenSource>();
        foreach (var book in vault.Books)
        {
            foreach (var block in book.Page.AllBlocks())
            {
                if (block.Id is not { } blockId)
                {
                    continue;
                }

                foreach (var noteId in SourceValue.Parse(block.GetProperty(SourceKey) ?? "").Distinct())
                {
                    if (FindNote(noteId) is not { } note)
                    {
                        broken.Add(new BrokenSource(book.LinkName, block.Key, noteId));
                    }
                    else if (!UsedInOf(note.Block).Any(e => IsEntryFor(e, book.LinkName, blockId)))
                    {
                        missing.Add(new MissingUsage(noteId, new UsedInEntry(book.LinkName, blockId)));
                    }
                }
            }
        }

        return new LinkCheckReport(
            missing, FindOrphans(), broken, [.. vault.Books.SelectMany(book => CrossRefIndex.Of(book, KnownInVault).Missing)],
            [.. vault.Books.SelectMany(MissingAppendixRefsOf)],
            [.. vault.Books.SelectMany(book => book.DuplicateAppendixSections.Select(
                section => new DuplicateAppendixSection(book.LinkName, section.Key, section.Title)))],
            [.. vault.Books.SelectMany(DuplicateAbbreviationsOf)]);
    }

    private static IEnumerable<MissingAppendixRef> MissingAppendixRefsOf(Book book) =>
        AppendixIndex.Of(book).MissingTargets.Select(
            m => new MissingAppendixRef(book.LinkName, m.ElementKey, m.MissingId, book.IsInAppendix(m.ElementKey)));

    private static IEnumerable<DuplicateAbbreviation> DuplicateAbbreviationsOf(Book book) =>
        Appendix.People(book)
            .Where(person => person.Abbreviation.Trim().Length > 0)
            .GroupBy(person => person.Abbreviation.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => new DuplicateAbbreviation(book.LinkName, group.Key, [.. group.Select(person => person.Name)]));

    /// <summary>
    /// Removes the block references of <see cref="LinkCheckReport.MissingCrossRefs"/> whose target is still gone from
    /// their texts (with one adjacent space, the rest of the text stays; <see cref="BlockRefs.Remove"/>) and saves each
    /// changed book through the page writer. Only books with entries in the report are looked at; read-only ones are
    /// left out. No <c>updated::</c>: the text is unchanged but for a reference that led nowhere.
    /// </summary>
    /// <returns>The number of references removed.</returns>
    /// <exception cref="IOException">
    /// Saving failed (or another exception of <see cref="IPageWriter.Save"/>): that book's blocks are put back as they
    /// were, books saved before stay saved.
    /// </exception>
    public int RemoveMissingCrossRefs(LinkCheckReport report) => RemoveMissingCrossRefs(report, out _);

    /// <inheritdoc cref="RemoveMissingCrossRefs(LinkCheckReport)"/>
    /// <param name="report">The check whose missing cross references go.</param>
    /// <param name="readOnly">The number of the report's entries left in place because their book is read-only.</param>
    public int RemoveMissingCrossRefs(LinkCheckReport report, out int readOnly)
    {
        var removed = 0;
        readOnly = 0;
        var keys = report.MissingCrossRefs.Select(m => m.ElementKey).ToHashSet();
        foreach (var book in vault.Books.Where(book => book.Page.AllBlocks().Any(b => keys.Contains(b.Key))))
        {
            if (book.Page.IsReadOnly)
            {
                readOnly += report.MissingCrossRefs.Count(m => book.Page.AllBlocks().Any(b => b.Key == m.ElementKey));
                continue;
            }

            // Only what is still missing now: a target may have come back since the check.
            var current = Book.Load(book.Page);
            var stillMissing = CrossRefIndex.Of(current, KnownInVault).Missing.Select(m => (m.ElementKey, m.TargetId)).ToHashSet();
            var byElement = report.MissingCrossRefs
                .Where(m => stillMissing.Contains((m.ElementKey, m.TargetId)))
                .GroupBy(m => m.ElementKey)
                .ToList();
            var changed = new List<(Block Block, IReadOnlyList<RawLine> Lines, bool WasDirty)>();
            foreach (var element in byElement)
            {
                if (book.Page.AllBlocks().FirstOrDefault(b => b.Key == element.Key) is not { } block)
                {
                    continue;
                }

                var ids = element.Select(m => m.TargetId).ToHashSet();
                var count = BlockRefs.Find(block.Content).Count(r => ids.Contains(r.Id));
                changed.Add((block, [.. block.Lines], block.IsDirty));
                block.SetContent(BlockRefs.Remove(block.Content, ids));
                removed += count;
            }

            if (changed.Count == 0)
            {
                continue;
            }

            try
            {
                writer.Save(book.Page);
            }
            catch
            {
                foreach (var (block, lines, wasDirty) in changed)
                {
                    block.RestoreLines(lines, wasDirty);
                }

                throw;
            }
        }

        return removed;
    }

    /// <summary>
    /// Removes the <see cref="LinkCheckReport.MissingAppendixRefs"/> that are still missing now: the source macros
    /// from their texts (<see cref="SourceCitations.Remove"/>, the rest of the text stays) and the author ids from the
    /// <c>autoren::</c> of their source entries (the source stays). Each changed book is saved through the page
    /// writer; read-only books are left out. No <c>updated::</c>, as with <see cref="RemoveMissingCrossRefs(LinkCheckReport)"/>.
    /// </summary>
    /// <returns>The number of macros and author ids removed.</returns>
    /// <exception cref="IOException">Saving failed: that book's blocks are put back as they were.</exception>
    public int RemoveMissingAppendixRefs(LinkCheckReport report) => RemoveMissingAppendixRefs(report, out _);

    /// <inheritdoc cref="RemoveMissingAppendixRefs(LinkCheckReport)"/>
    /// <param name="report">The check whose missing appendix references go.</param>
    /// <param name="readOnly">The number of the report's entries left in place because their book is read-only.</param>
    public int RemoveMissingAppendixRefs(LinkCheckReport report, out int readOnly)
    {
        var removed = 0;
        readOnly = 0;
        foreach (var book in vault.Books.Where(book => report.MissingAppendixRefs.Any(m => m.BookLinkName == book.LinkName)))
        {
            var mine = report.MissingAppendixRefs.Where(m => m.BookLinkName == book.LinkName).ToList();
            if (book.Page.IsReadOnly)
            {
                readOnly += mine.Count;
                continue;
            }

            // Only what is still missing now: an entry may have been added since the check.
            var stillMissing = AppendixIndex.Of(Book.Load(book.Page)).MissingTargets.ToHashSet();
            var changed = new List<(Block Block, IReadOnlyList<RawLine> Lines, bool WasDirty)>();
            foreach (var element in mine.Where(m => stillMissing.Contains((m.ElementKey, m.MissingId))).GroupBy(m => m.ElementKey))
            {
                if (book.Page.AllBlocks().FirstOrDefault(b => b.Key == element.Key) is not { } block)
                {
                    continue;
                }

                var ids = element.Select(m => m.MissingId).ToHashSet();
                changed.Add((block, [.. block.Lines], block.IsDirty));
                if (element.Any(m => m.IsAuthor))
                {
                    removed += RemoveAuthors(block, ids);
                }
                else
                {
                    removed += SourceCitations.Find(block.Content).Count(c => ids.Contains(c.Id));
                    block.SetContent(SourceCitations.Remove(block.Content, ids));
                }
            }

            if (changed.Count == 0)
            {
                continue;
            }

            try
            {
                writer.Save(book.Page);
            }
            catch
            {
                foreach (var (block, lines, wasDirty) in changed)
                {
                    block.RestoreLines(lines, wasDirty);
                }

                throw;
            }
        }

        return removed;
    }

    /// <summary>Drops <paramref name="ids"/> from the block's <c>autoren::</c> (the property goes if none is left); returns how many went.</summary>
    private static int RemoveAuthors(Block block, IReadOnlySet<Guid> ids)
    {
        var all = BlockRefs.Find(block.GetProperty(AuthorsKey) ?? "").Select(r => r.Id).ToList();
        var kept = all.Where(id => !ids.Contains(id)).ToList();
        if (kept.Count == 0)
        {
            block.RemoveProperty(AuthorsKey);
        }
        else if (kept.Count < all.Count)
        {
            block.SetProperty(AuthorsKey, string.Join(", ", kept.Select(BlockRefs.Format)));
        }

        return all.Count - kept.Count;
    }

    /// <summary>
    /// Adds every missing entry to its note; returns how many were handled (an entry for a read-only note counts
    /// once it is recorded in <c>pending.json</c>). A note's entry <c>[[Book]]</c> without block reference ("Stelle
    /// unbekannt") is removed once the precise entry is added, in a second note write; it stays on a read-only note,
    /// which cannot take an entry without a block reference as a pending update.
    /// </summary>
    public int FixMissing(LinkCheckReport report)
    {
        var handled = 0;
        foreach (var missing in report.Missing)
        {
            if (FindNote(missing.NoteBlockId) is not { } note)
            {
                continue;
            }

            links.AddUsage(missing.NoteBlockId, missing.Entry);
            handled++;
            if (!note.Page.IsReadOnly && UsedInOf(note.Block).Any(e => IsEntryFor(e, missing.Entry.PageName, null)))
            {
                links.RemoveUsage(missing.NoteBlockId, missing.Entry.PageName, null);
            }
        }

        return handled;
    }

    /// <summary>
    /// <see cref="OrphanResolution.Remove"/> removes the entry. <see cref="OrphanResolution.MarkUnknown"/> replaces
    /// an entry with block reference by <c>[[Name]]</c> (added at the end of <c>used-in::</c>, unless the note has it
    /// already); an entry without block reference already is that and stays unchanged. A note without <c>id::</c>
    /// gets one (written together with the change) when the note is changed.
    /// </summary>
    /// <exception cref="InvalidOperationException">The note block no longer exists.</exception>
    /// <exception cref="ReadOnlyPageException">The note's page is read-only; nothing was changed.</exception>
    public void Resolve(OrphanUsage orphan, OrphanResolution resolution)
    {
        var (page, block) = vault.FindBlockByKey(orphan.NoteBlockKey)
                            ?? throw new InvalidOperationException("Der Notizblock existiert nicht mehr.");
        if (page.IsReadOnly)
        {
            throw new ReadOnlyPageException($"Die Datei '{page.FilePath}' ist schreibgeschützt: {page.ParseError}");
        }

        var (name, blockId) = orphan.Entry;
        if (resolution == OrphanResolution.MarkUnknown && blockId is null)
        {
            return;
        }

        var noteId = block.EnsureId();
        if (resolution == OrphanResolution.Remove)
        {
            links.RemoveUsage(noteId, name, blockId);
            return;
        }

        if (!UsedInOf(block).Any(e => IsEntryFor(e, name, null)))
        {
            links.AddUsage(noteId, new UsedInEntry(name, null));
        }

        links.RemoveUsage(noteId, name, blockId);
    }

    private List<OrphanUsage> FindOrphans()
    {
        var orphans = new List<OrphanUsage>();
        foreach (var page in vault.Pages.Where(p => !Book.IsBook(p)))
        {
            foreach (var block in page.AllBlocks())
            {
                foreach (var entry in UsedInOf(block))
                {
                    var book = vault.FindBook(entry.PageName);
                    if (book is null || (entry.BookBlockId is { } id && !book.Page.AllBlocks().Any(b => b.Id == id)))
                    {
                        orphans.Add(new OrphanUsage(block.Key, entry));
                    }
                }
            }
        }

        return orphans;
    }

    /// <summary>Whether the vault has a block with this id: a reference to a note or journal block is not missing.</summary>
    private bool KnownInVault(Guid id) => vault.FindBlockById(id) is not null;

    private (Page Page, Block Block)? FindNote(Guid noteId) =>
        vault.FindBlockById(noteId) is { } found && !Book.IsBook(found.Page) ? found : null;

    private static IReadOnlyList<UsedInEntry> UsedInOf(Block block) => UsedInValue.Parse(block.GetProperty(UsedInKey) ?? "");

    private static bool IsEntryFor(UsedInEntry entry, string linkName, Guid? blockId) =>
        entry.BookBlockId == blockId && string.Equals(entry.PageName, linkName, StringComparison.OrdinalIgnoreCase);
}
