using NoteEvolution.Core.Books;
using NoteEvolution.Core.Links;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Storage;
using NoteEvolution.Core.Vaults;

namespace NoteEvolution.Core.Assistants;

/// <summary>
/// Outcome of <see cref="HandledConverter.Apply"/>: how many notes were converted and which selected notes were
/// skipped because their page is read-only (it could not be parsed safely and is never written).
/// </summary>
public sealed record HandledResult(int Converted, IReadOnlyList<NoteBlock> Skipped);

/// <summary>
/// One-time assistant: converts the old <c>[handled]</c> marker at the start of a note into
/// <c>used-in:: [[Book]]</c>, or, for a note with a confirmed placement, into a full link with a book text block.
/// This is the only operation allowed to change a note's content.
/// </summary>
public sealed class HandledConverter(INoteRepository notes, IPageWriter writer, ILinkService links)
{
    private const string Marker = "[handled]";

    /// <summary>
    /// Every note block (any depth) whose content starts with <c>[handled]</c> (case-sensitive), in vault order.
    /// Notes on read-only pages are listed too; <see cref="Apply"/> skips them.
    /// </summary>
    public IReadOnlyList<NoteBlock> Find() =>
    [
        .. notes.All().Where(n => n.Block.Content.StartsWith(Marker, StringComparison.Ordinal)),
    ];

    /// <summary>
    /// For each selected note: removes <c>[handled]</c> and one following space from the first line.
    /// A note without a placement also gets <c>used-in:: [[Book]]</c> (appended to an existing property unless the
    /// book is already listed; no block reference, no <c>id::</c>); each changed page is saved exactly once for all
    /// such notes. A note with a placement (<paramref name="placements"/> maps a note block key to the key of a
    /// text block of <paramref name="book"/>) is instead fully linked with that text block by
    /// <see cref="ILinkService.Link"/>, which saves the book and then the note page including the removed prefix
    /// (if only the note page cannot be written, <see cref="ILinkService.Link"/> records the used-in update in
    /// <c>pending.json</c> and the prefix removal stays in memory). <see cref="HandledResult.Converted"/> counts both kinds.
    /// Notes on read-only pages are left untouched and returned in <see cref="HandledResult.Skipped"/>.
    /// Pages are processed one after the other, on each page first the notes with a placement; if a write throws
    /// (for example <see cref="FileChangedExternallyException"/> or <see cref="IOException"/>, or an
    /// <see cref="ArgumentException"/> for an unknown placement), the exception propagates, what was already written
    /// stays written, and the remaining notes are not processed. The prefix removal of the note whose
    /// <see cref="ILinkService.Link"/> threw is reverted in memory.
    /// <see cref="Book"/> is a snapshot: callers rebuild it with <see cref="Book.Load"/> after linking placements.
    /// </summary>
    public HandledResult Apply(
        IEnumerable<NoteBlock> selected, Book book, IReadOnlyDictionary<Guid, Guid>? placements = null)
    {
        var skipped = new List<NoteBlock>();
        var byPage = new Dictionary<Page, List<NoteBlock>>();
        foreach (var note in selected.Distinct())
        {
            if (note.Page.IsReadOnly)
            {
                skipped.Add(note);
                continue;
            }

            if (!byPage.TryGetValue(note.Page, out var list))
            {
                byPage[note.Page] = list = [];
            }
            list.Add(note);
        }

        var converted = 0;
        foreach (var (page, pageNotes) in byPage)
        {
            var unplaced = new List<NoteBlock>();
            foreach (var note in pageNotes)
            {
                if (placements is not null && placements.TryGetValue(note.Block.Key, out var textBlockKey))
                {
                    LinkPlaced(note.Block, book, textBlockKey);
                    converted++;
                }
                else
                {
                    unplaced.Add(note);
                }
            }

            if (unplaced.Count > 0)
            {
                foreach (var note in unplaced)
                {
                    Convert(note.Block, book);
                }
                writer.Save(page);
                converted += unplaced.Count;
            }
        }

        return new HandledResult(converted, skipped);
    }

    private void LinkPlaced(Block block, Book book, Guid textBlockKey)
    {
        var lines = block.Lines.ToList();
        var wasDirty = block.IsDirty;
        StripMarker(block);
        try
        {
            links.Link(book, textBlockKey, block.Key);
        }
        catch
        {
            block.RestoreLines(lines, wasDirty);
            throw;
        }
    }

    private static void StripMarker(Block block)
    {
        var content = block.Content;
        if (content.StartsWith(Marker, StringComparison.Ordinal))
        {
            var rest = content[Marker.Length..];
            block.SetContent(rest.StartsWith(' ') ? rest[1..] : rest);
        }
    }

    private static void Convert(Block block, Book book)
    {
        StripMarker(block);

        var entries = block.GetProperty("used-in") is { } existing ? [.. UsedInValue.Parse(existing)] : new List<UsedInEntry>();
        if (!entries.Any(e => string.Equals(e.PageName, book.LinkName, StringComparison.OrdinalIgnoreCase)))
        {
            entries.Add(new UsedInEntry(book.LinkName, null));
            block.SetProperty("used-in", UsedInValue.Format(entries));
        }
    }
}
