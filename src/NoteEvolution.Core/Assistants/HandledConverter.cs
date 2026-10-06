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
/// <c>used-in:: [[Book]]</c>. This is the only operation allowed to change a note's content.
/// </summary>
public sealed class HandledConverter(INoteRepository notes, IPageWriter writer)
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
    /// For each selected note: removes <c>[handled]</c> and one following space from the first line, and links the
    /// book via <c>used-in::</c> (appending to an existing property unless the book is already listed; no block
    /// reference, no <c>id::</c>). Each changed page is saved exactly once.
    /// Notes on read-only pages are left untouched and returned in <see cref="HandledResult.Skipped"/>.
    /// Pages are saved one after the other; if a save throws (for example
    /// <see cref="FileChangedExternallyException"/> or <see cref="IOException"/>), the exception propagates,
    /// pages already saved stay saved, and the remaining pages are not written.
    /// </summary>
    public HandledResult Apply(IEnumerable<NoteBlock> selected, Book book)
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
            foreach (var note in pageNotes)
            {
                Convert(note.Block, book);
            }
            writer.Save(page);
            converted += pageNotes.Count;
        }

        return new HandledResult(converted, skipped);
    }

    private static void Convert(Block block, Book book)
    {
        var content = block.Content;
        if (content.StartsWith(Marker, StringComparison.Ordinal))
        {
            var rest = content[Marker.Length..];
            block.SetContent(rest.StartsWith(' ') ? rest[1..] : rest);
        }

        var entries = block.GetProperty("used-in") is { } existing ? [.. UsedInValue.Parse(existing)] : new List<UsedInEntry>();
        if (!entries.Any(e => string.Equals(e.PageName, book.LinkName, StringComparison.OrdinalIgnoreCase)))
        {
            entries.Add(new UsedInEntry(book.LinkName, null));
            block.SetProperty("used-in", UsedInValue.Format(entries));
        }
    }
}
