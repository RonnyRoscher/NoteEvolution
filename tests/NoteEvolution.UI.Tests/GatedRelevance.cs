using NoteEvolution.AI.Relevance;
using NoteEvolution.AI.Search;
using NoteEvolution.Core.Books;

namespace NoteEvolution.UI.Tests;

/// <summary>
/// Relevance that answers as <see cref="Inner"/> does, except that <see cref="Placements"/> waits while the gate is
/// closed (<see cref="Hold"/>) until it is opened again (<see cref="Release"/>), so a test sees the time the places are
/// computed. Thread-safe.
/// </summary>
internal sealed class GatedRelevance(IRelevanceService inner) : IRelevanceService, IDisposable
{
    private readonly ManualResetEventSlim _open = new(true);
    private int _waiting;

    public IRelevanceService Inner { get; } = inner;

    /// <summary>How many <see cref="Placements"/> calls wait at the closed gate now.</summary>
    public int Waiting => Volatile.Read(ref _waiting);

    public void Hold() => _open.Reset();

    public void Release() => _open.Set();

    public bool IsAvailable => Inner.IsAvailable;

    public IReadOnlyList<SearchHit> Relevant(TopicRequest topic, Func<Guid, bool> include, int limit = 30) =>
        Inner.Relevant(topic, include, limit);

    public IReadOnlyList<SectionHit> WhereTo(Guid noteKey, string noteText, Book book, int limit = 5) =>
        Inner.WhereTo(noteKey, noteText, book, limit);

    public IReadOnlyList<PlacementHit> Placements(Guid noteKey, string noteText, Book book, int limit = 3)
    {
        Interlocked.Increment(ref _waiting);
        try
        {
            // Bounded, so a test that forgets to release does not hang the run.
            _open.Wait(TimeSpan.FromSeconds(60));
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }

        return Inner.Placements(noteKey, noteText, book, limit);
    }

    public IReadOnlyList<TopicHit> Topics(Guid noteKey, string noteText, Book book, int limit = 3) =>
        Inner.Topics(noteKey, noteText, book, limit);

    /// <summary>Opens the gate for good (the event is not disposed: a call may still be leaving it).</summary>
    public void Dispose() => _open.Set();
}
