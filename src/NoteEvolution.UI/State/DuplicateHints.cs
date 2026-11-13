using Microsoft.Extensions.Logging;
using NoteEvolution.AI.Relevance;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Vaults;

namespace NoteEvolution.UI.State;

/// <summary>
/// An open note says nearly the same as a note the book already uses: the used note, the book element that names it in
/// its <c>source::</c> (where a click goes), that element's text block (where the open note can be linked as a further
/// source) and the similarity in percent.
/// </summary>
public sealed record DuplicateHint(Guid UsedNoteKey, Guid ElementKey, Guid TextBlockKey, int Percent);

/// <summary>
/// The duplicate hints of the open notes for one book (<see cref="NoteDuplicates"/>), computed when a card asks and
/// kept until the book, a page or the semantic index changes. Not thread-safe: the UI thread's.
/// </summary>
internal sealed class DuplicateHints(ILogger logger)
{
    /// <summary>How much of a note's text a log line shows.</summary>
    private const int LogExcerpt = 40;

    /// <summary>A note the book uses: its block, its vector and where the book names it.</summary>
    private sealed record Used(Block Block, float[] Vector, Guid ElementKey, Guid TextBlockKey);

    private readonly Dictionary<Guid, DuplicateHint?> _hints = [];
    private readonly HashSet<(Guid Note, Guid Used)> _logged = [];
    private Book? _book;
    private object? _stamp;
    private List<Used>? _used;

    /// <summary>A page changed: notes may be used now, or say something else.</summary>
    public void Clear()
    {
        _book = null;
        _used = null;
        _hints.Clear();
    }

    /// <summary>
    /// The hint for <paramref name="note"/> in <paramref name="book"/>, or null: the note is used, is not embedded
    /// (yet), or no note the book uses reaches <see cref="NoteDuplicates.Threshold"/>. The note's own sub-points and
    /// ancestors do not count (their texts overlap by construction). <paramref name="stamp"/> stands for the state of
    /// the vectors: another one drops what was computed.
    /// </summary>
    public DuplicateHint? Of(NoteBlock note, Book book, INoteRepository notes, Func<Guid, float[]?> vectorOf, object? stamp)
    {
        if (note.IsUsed)
        {
            return null;
        }

        if (!ReferenceEquals(book, _book) || !ReferenceEquals(stamp, _stamp))
        {
            Clear();
            _book = book;
            _stamp = stamp;
        }

        if (_hints.TryGetValue(note.Key, out var known))
        {
            return known;
        }

        if (vectorOf(note.Key) is not { } vector)
        {
            return null;
        }

        _used ??= UsedNotes(book, notes, vectorOf);
        var candidates = _used.Where(used => !Related(used.Block, note.Block)).ToList();
        DuplicateHint? hint = null;
        if (NoteDuplicates.Nearest(vector, candidates.Select(used => (used.Block.Key, used.Vector))) is { } nearest)
        {
            var used = candidates.First(u => u.Block.Key == nearest.Key);
            var duplicate = NoteDuplicates.IsDuplicate(nearest.Score);
            if (nearest.Score >= NoteDuplicates.NearThreshold && _logged.Add((note.Key, nearest.Key)))
            {
                logger.LogInformation(
                    "Duplicate candidate {Score:F3} hint={Hint}: \"{Note}\" ~ used \"{Used}\"",
                    nearest.Score, duplicate, Excerpt.FirstLine(note.Block.Content, LogExcerpt), Excerpt.FirstLine(used.Block.Content, LogExcerpt));
            }

            if (duplicate)
            {
                hint = new DuplicateHint(nearest.Key, used.ElementKey, used.TextBlockKey, (int)Math.Round(nearest.Score * 100));
            }
        }

        _hints[note.Key] = hint;
        return hint;
    }

    /// <summary>
    /// The notes the book names in a <c>source::</c> that are embedded, each once with the first element naming it, in
    /// book order.
    /// </summary>
    private static List<Used> UsedNotes(Book book, INoteRepository notes, Func<Guid, float[]?> vectorOf)
    {
        var places = new Dictionary<Guid, (Guid Element, Guid TextBlock)>();
        foreach (var textBlock in TextBlocks(book.Root))
        {
            foreach (var link in textBlock.Links)
            {
                places.TryAdd(link.NoteId, (link.BlockKey, textBlock.Key));
            }
        }

        var used = new List<Used>();
        if (places.Count == 0)
        {
            return used;
        }

        foreach (var note in notes.All())
        {
            if (note.Block.Id is { } id && places.Remove(id, out var place) && vectorOf(note.Key) is { } vector)
            {
                used.Add(new Used(note.Block, vector, place.Element, place.TextBlock));
            }
        }

        return used;
    }

    private static IEnumerable<TextBlock> TextBlocks(OutlineNode node) =>
        node.TextBlocks.Concat(node.Children.SelectMany(TextBlocks));

    /// <summary>One block is the other or stands below it.</summary>
    private static bool Related(Block a, Block b) => Within(a, b) || Within(b, a);

    private static bool Within(Block block, Block ancestor)
    {
        for (var current = block; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }
}
