using NoteEvolution.Core.Books;

namespace NoteEvolution.Core.Repetitions;

public enum RepetitionKind { Literal, Semantic }

/// <summary>
/// A repetition between two blocks (by key, A before B in the book). Literal ones carry their shared runs and the shared
/// word count; semantic ones their similarity. Accepted ones are known and not counted.
/// </summary>
public sealed record Repetition(
    RepetitionKind Kind, Guid A, Guid B, IReadOnlyList<SharedRun> Runs, int Words, float Score, bool Accepted);

/// <summary>
/// All repetitions found in one book, the counts derived from them, and how far the semantic comparison got. Accepted
/// pairs stay in <see cref="All"/> but are not counted and not offered by <see cref="Of"/> and <see cref="In"/>.
/// </summary>
public sealed class RepetitionSet
{
    private readonly Book? _book;
    private readonly Dictionary<Guid, ComparedBlock> _blocks;
    private readonly IReadOnlyList<Repetition> _open;
    private readonly Dictionary<Guid, List<Repetition>> _openByBlock;
    private readonly Dictionary<OutlineNode, int> _countIn;

    private RepetitionSet(
        Book? book, Dictionary<Guid, ComparedBlock> blocks, IReadOnlyList<Repetition> all, int embedded, int total)
    {
        _book = book;
        _blocks = blocks;
        All = all;
        Embedded = embedded;
        Total = total;
        _open = [.. all.Where(r => !r.Accepted)];

        _openByBlock = [];
        foreach (var repetition in _open)
        {
            foreach (var key in new[] { repetition.A, repetition.B }.Distinct())
            {
                if (!_openByBlock.TryGetValue(key, out var list))
                {
                    _openByBlock[key] = list = [];
                }

                list.Add(repetition);
            }
        }

        _countIn = [];
        if (book is not null && _open.Count > 0)
        {
            Precompute(book.Root);
        }
    }

    public static RepetitionSet Empty { get; } = new(null, [], [], 0, 0);

    /// <summary>Built for one book. <paramref name="acceptedIds"/> holds pairs of block ids (Block.Id), in either order.</summary>
    public static RepetitionSet Create(
        Book book, IReadOnlyList<ComparedBlock> blocks, IReadOnlyList<LiteralPair> literal,
        IReadOnlyList<SemanticPair> semantic, IReadOnlySet<(Guid, Guid)> acceptedIds, int embedded, int total)
    {
        var position = new Dictionary<Guid, int>();
        for (var i = 0; i < blocks.Count; i++)
        {
            position[blocks[i].Key] = i;
        }

        int Position(Guid key) => position.GetValueOrDefault(key, int.MaxValue);

        bool Accepted(Guid a, Guid b) =>
            book.FindTextBlock(a)?.Block.Id is { } idA && book.FindTextBlock(b)?.Block.Id is { } idB
            && (acceptedIds.Contains((idA, idB)) || acceptedIds.Contains((idB, idA)));

        var literalRepetitions = literal
            .OrderBy(p => Position(p.A)).ThenBy(p => Position(p.B))
            .Select(p => new Repetition(RepetitionKind.Literal, p.A, p.B, p.Runs, p.Words, 1f, Accepted(p.A, p.B)));
        var semanticRepetitions = semantic
            .OrderBy(p => Position(p.A)).ThenBy(p => Position(p.B))
            .Select(p => new Repetition(RepetitionKind.Semantic, p.A, p.B, [], 0, p.Score, Accepted(p.A, p.B)));

        return new RepetitionSet(
            book, blocks.ToDictionary(b => b.Key), [.. literalRepetitions, .. semanticRepetitions], embedded, total);
    }

    /// <summary>Whether this set belongs to <paramref name="book"/> (the same instance, not a book that was shown before).</summary>
    public bool IsFor(Book? book) => book is not null && ReferenceEquals(_book, book);

    /// <summary>Blocks that have a vector.</summary>
    public int Embedded { get; }

    /// <summary>Compared blocks.</summary>
    public int Total { get; }

    public bool Complete => Embedded >= Total;

    /// <summary>Literal repetitions first, then semantic ones; each in book order (A, then B).</summary>
    public IReadOnlyList<Repetition> All { get; }

    /// <summary>The compared block of a text block; null if it is not compared.</summary>
    public ComparedBlock? Block(Guid key) => _blocks.GetValueOrDefault(key);

    /// <summary>The repetitions that are not accepted and have the block on one side.</summary>
    public IReadOnlyList<Repetition> Of(Guid textBlockKey) =>
        _openByBlock.TryGetValue(textBlockKey, out var list) ? list : [];

    /// <summary>Not accepted pairs with at least one block in the set of keys, each pair once.</summary>
    public int Count(IReadOnlySet<Guid> textBlockKeys) => _open.Count(r => Touches(r, textBlockKeys));

    /// <summary>As <see cref="Count"/> for the section with all its sub-sections.</summary>
    public int CountIn(OutlineNode section) => _countIn.GetValueOrDefault(section);

    /// <summary>The not accepted repetitions with at least one block in the set of keys, each once.</summary>
    public IReadOnlyList<Repetition> In(IReadOnlySet<Guid> textBlockKeys) => [.. _open.Where(r => Touches(r, textBlockKeys))];

    private static bool Touches(Repetition repetition, IReadOnlySet<Guid> keys) =>
        keys.Contains(repetition.A) || keys.Contains(repetition.B);

    private void Precompute(OutlineNode node)
    {
        _countIn[node] = Count(node.AllTextBlocks().Select(textBlock => textBlock.Key).ToHashSet());
        foreach (var child in node.Children.Where(child => child.AppendixKind == AppendixKind.None))
        {
            Precompute(child);
        }
    }
}
