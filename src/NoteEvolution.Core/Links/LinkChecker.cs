using NoteEvolution.Core.Books;
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

public sealed record LinkCheckReport(
    IReadOnlyList<MissingUsage> Missing, IReadOnlyList<OrphanUsage> Orphans, IReadOnlyList<BrokenSource> Broken);

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
/// <see cref="ILinkService"/> (which records updates for read-only or unwritable notes in <c>pending.json</c>).
/// </summary>
public sealed class LinkChecker(IVault vault, ILinkService links)
{
    private const string SourceKey = "source";
    private const string UsedInKey = "used-in";

    /// <summary>
    /// Reads the vault and reports missing usages, orphans and broken sources. Orphans are found on every note
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

        return new LinkCheckReport(missing, FindOrphans(), broken);
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

    private (Page Page, Block Block)? FindNote(Guid noteId) =>
        vault.FindBlockById(noteId) is { } found && !Book.IsBook(found.Page) ? found : null;

    private static IReadOnlyList<UsedInEntry> UsedInOf(Block block) => UsedInValue.Parse(block.GetProperty(UsedInKey) ?? "");

    private static bool IsEntryFor(UsedInEntry entry, string linkName, Guid? blockId) =>
        entry.BookBlockId == blockId && string.Equals(entry.PageName, linkName, StringComparison.OrdinalIgnoreCase);
}
