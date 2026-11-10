using NoteEvolution.AI.Understanding;
using NoteEvolution.Core.Books;

namespace NoteEvolution.UI.State;

/// <summary>
/// Why a note's stored placement is checked again (spec 4), or <see cref="None"/> when it is kept; <see cref="Model"/>: it
/// was made by another chat model or with another form of the request (prompt version, layout, system prompt, schema).
/// </summary>
internal enum RecheckReason { None, NoteText, SectionGone, NewCandidate, Rebuilt, NoBasis, Model }

/// <summary>
/// Decides whether a note's latest placement still holds for a changed request (spec 4): only another form of the
/// request (<see cref="PlacementBasis.Request"/>; the chat model is for the caller to compare), a changed note text, a
/// chosen section that is gone, a new candidate that may beat the chosen one, or a rebuilt book check it again. Other
/// summaries, other or vanished places, renamed sections found by their key (<see cref="SectionNames"/>) and another
/// order of the sections do not. The request is always compared with the basis of the last real check, not with the
/// previous request. Pure, apart from what <see cref="SectionNames"/> remembered.
/// </summary>
internal static class PlacementKeep
{
    /// <summary>
    /// Whether <paramref name="latest"/> (the note's latest stored result) must be checked again for
    /// <paramref name="prompt"/> (today's request in <paramref name="book"/>), by the first rule that holds. The stored
    /// sections are read with today's names (<paramref name="names"/>; none: by their keys and paths only).
    /// </summary>
    public static RecheckReason Decide(PlacementVerdict latest, PlacementCheckPrompt prompt, Book book, SectionNames? names = null)
    {
        if (latest.Basis is not { } basis || prompt.Basis is not { } today)
        {
            return RecheckReason.NoBasis;
        }

        if (basis.Request is null || basis.Request != today.Request)
        {
            return RecheckReason.Model; // made with another form of the request
        }

        if (basis.NoteHash != today.NoteHash)
        {
            return RecheckReason.NoteText;
        }

        names ??= new SectionNames();
        if (names.Find(book, latest.SectionKey, latest.SectionPath) is not { } section)
        {
            return RecheckReason.SectionGone;
        }

        // The basis' sections by today's names, so a rename alone changes nothing; a section gone matches none.
        var chosen = SectionPath.Of(section);
        var offered = Today(book, names, basis.Offered, basis.OfferedKeys).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var chapters = Today(book, names, basis.TopChapters, basis.TopChapterKeys).OfType<string>().ToHashSet(StringComparer.Ordinal);

        // A path that is not ranked counts as last.
        var ranking = today.Ranking.ToList();
        int Rank(string path) => ranking.IndexOf(path) is >= 0 and var rank ? rank : int.MaxValue;
        var chosenRank = Rank(chosen);

        if (today.TopChapters.Any(chapter => !chapters.Contains(chapter))
            || today.Offered.Any(path => !offered.Contains(path) && Rank(path) < chosenRank))
        {
            return RecheckReason.NewCandidate;
        }

        return today.Offered.Distinct(StringComparer.Ordinal).Count(offered.Contains) * 2 < basis.Offered.Count
            ? RecheckReason.Rebuilt
            : RecheckReason.None;
    }

    /// <summary>
    /// <paramref name="latest"/> (kept by <see cref="Decide"/>) with today's names and keys when one of its sections was
    /// renamed: the chosen section's and the embedding's path, and the basis' paths, so they still resolve after a
    /// restart. A section gone keeps its stored path and key. Null when nothing was renamed (nothing to write).
    /// </summary>
    public static PlacementVerdict? Renamed(PlacementVerdict latest, Book book, SectionNames? names = null)
    {
        names ??= new SectionNames();
        if (latest.Basis is not { } basis || names.Find(book, latest.SectionKey, latest.SectionPath) is not { } section)
        {
            return null;
        }

        var embedding = latest.EmbeddingSectionPath is { } stored ? names.Find(book, latest.EmbeddingSectionKey, stored) : null;
        var (offered, offeredKeys) = Rename(book, names, basis.Offered, basis.OfferedKeys);
        var (chapters, chapterKeys) = Rename(book, names, basis.TopChapters, basis.TopChapterKeys);
        var (ranking, _) = Rename(book, names, basis.Ranking, null);
        var path = SectionPath.Of(section);
        var embeddingPath = embedding is null ? latest.EmbeddingSectionPath : SectionPath.Of(embedding);
        if (path == latest.SectionPath && embeddingPath == latest.EmbeddingSectionPath
            && offered.SequenceEqual(basis.Offered) && chapters.SequenceEqual(basis.TopChapters) && ranking.SequenceEqual(basis.Ranking))
        {
            return null;
        }

        return latest with
        {
            SectionKey = section.Key, SectionPath = path,
            EmbeddingSectionKey = embedding?.Key ?? latest.EmbeddingSectionKey, EmbeddingSectionPath = embeddingPath,
            Basis = basis with { Offered = offered, TopChapters = chapters, Ranking = ranking, OfferedKeys = offeredKeys, TopChapterKeys = chapterKeys },
        };
    }

    /// <summary>Today's path of each of <paramref name="paths"/> (with their stored <paramref name="keys"/>, if any); null for one gone.</summary>
    private static IEnumerable<string?> Today(Book book, SectionNames names, IReadOnlyList<string> paths, IReadOnlyList<Guid>? keys) =>
        paths.Select((path, i) => names.Find(book, Key(keys, i, paths.Count), path) is { } node ? SectionPath.Of(node) : null);

    /// <summary>Today's paths and keys of <paramref name="paths"/>; one gone keeps its stored path and key, or <see cref="Guid.Empty"/> for none (see <see cref="SectionNames.Find"/>).</summary>
    private static (IReadOnlyList<string> Paths, IReadOnlyList<Guid> Keys) Rename(Book book, SectionNames names,
        IReadOnlyList<string> paths, IReadOnlyList<Guid>? keys)
    {
        var nodes = paths.Select((path, i) => names.Find(book, Key(keys, i, paths.Count), path)).ToList();
        return ([.. nodes.Select((node, i) => node is null ? paths[i] : SectionPath.Of(node))],
            [.. nodes.Select((node, i) => node?.Key ?? Key(keys, i, paths.Count) ?? Guid.Empty)]);
    }

    /// <summary>The stored key of the <paramref name="i"/>-th path; none when the keys were not stored (or do not match the paths).</summary>
    private static Guid? Key(IReadOnlyList<Guid>? keys, int i, int count) => keys is { } known && known.Count == count ? known[i] : null;
}

/// <summary>
/// The sections' names seen in this session (spec 4): a stored result names its sections by key (valid only in the
/// session that made it, keys are new on every start) and by path (which a rename changes). Every build tells the book it
/// decided against (<see cref="Remember"/>), so a path stored before a rename in this session still finds its section by
/// the key it had then, and a section deleted in this session is not mistaken for a sibling that took its path. A heading
/// whose own lines were changed outside the app (e.g. a property added) is read again with a new key at the same path:
/// it is found by that path. Safe to call from several threads.
/// </summary>
internal sealed class SectionNames
{
    private readonly Lock _gate = new();

    /// <summary>Every section key seen in this session's books, with the path it had when it was first seen.</summary>
    private readonly Dictionary<Guid, string> _firstPaths = [];

    /// <summary>Per book (link name): each path seen in this session, with the key of the section that had it last.</summary>
    private readonly Dictionary<string, Dictionary<string, Guid>> _paths = new(StringComparer.Ordinal);

    /// <summary>Remembers the sections of <paramref name="book"/>; call it once a build decided against it, not before.</summary>
    public void Remember(Book book)
    {
        lock (_gate)
        {
            if (!_paths.TryGetValue(book.LinkName, out var paths))
            {
                _paths[book.LinkName] = paths = new Dictionary<string, Guid>(StringComparer.Ordinal);
            }

            foreach (var node in Below(book.Root))
            {
                var path = SectionPath.Of(node);
                _firstPaths.TryAdd(node.Key, path);
                paths[path] = node.Key;
            }
        }
    }

    /// <summary>
    /// The section of <paramref name="book"/> a stored result names by <paramref name="key"/> and <paramref name="path"/>:
    /// by its key; else by the key the path had in this session (a rename since); else by the path, unless the section
    /// there is one seen in this session at another path (a sibling that took the place of a deleted section): a section
    /// first seen at that path, or new, is the same heading with a new key. <see cref="Guid.Empty"/> is no key (it would
    /// be the root's). Null if there is none.
    /// </summary>
    public OutlineNode? Find(Book book, Guid? key, string? path)
    {
        if (key is { } known && known != Guid.Empty && book.FindNode(known) is { } byKey)
        {
            return byKey;
        }

        Guid? before = null;
        lock (_gate)
        {
            if (path is not null && _paths.TryGetValue(book.LinkName, out var paths) && paths.TryGetValue(path, out var had))
            {
                before = had;
            }
        }

        if (before is { } then && book.FindNode(then) is { } renamed)
        {
            return renamed;
        }

        if (path is null || SectionPath.Find(book, path) is not { } byPath)
        {
            return null;
        }

        lock (_gate)
        {
            return _firstPaths.TryGetValue(byPath.Key, out var first) && first != path ? null : byPath;
        }
    }

    /// <summary>
    /// What a stored choice's section is, for comparing two of them across renames: the key, when seen in this session;
    /// else the key its path had in this session; else the path.
    /// </summary>
    public string Identify(string bookLinkName, Guid key, string path)
    {
        lock (_gate)
        {
            if (_firstPaths.ContainsKey(key))
            {
                return key.ToString();
            }

            return _paths.TryGetValue(bookLinkName, out var paths) && paths.TryGetValue(path, out var had) ? had.ToString() : path;
        }
    }

    private static IEnumerable<OutlineNode> Below(OutlineNode node) => node.Children.SelectMany(child => Below(child).Prepend(child));
}
