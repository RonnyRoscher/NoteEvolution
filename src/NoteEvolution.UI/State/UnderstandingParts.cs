using Microsoft.Extensions.Logging;
using NoteEvolution.AI.Language;
using NoteEvolution.AI.Relevance;
using NoteEvolution.AI.Understanding;
using NoteEvolution.Core.Links;

namespace NoteEvolution.UI.State;

/// <summary>
/// The language model's parts for a vault (<see cref="VaultSession"/>); the relevance service uses the understanding over
/// the store, and <see cref="Placements"/> keeps the placement checks of these parts.
/// </summary>
internal sealed class UnderstandingParts : IDisposable
{
    public UnderstandingParts(
        IChatModel chat, string chatModel, string embedModel, UnderstandingStore store, StoreUnderstanding understanding,
        RelevanceService relevance, UnderstandingWorker worker, ILogger logger)
    {
        Chat = chat;
        ChatModel = chatModel;
        EmbedModel = embedModel;
        Store = store;
        Understanding = understanding;
        Relevance = relevance;
        Worker = worker;
        Placements = new PlacementWork(chatModel, embedModel, store, understanding, relevance, logger);
    }

    public IChatModel Chat { get; }

    public string ChatModel { get; }

    /// <summary>The embedding model whose vectors the understanding uses.</summary>
    public string EmbedModel { get; }

    public StoreUnderstanding Understanding { get; }

    public RelevanceService Relevance { get; }

    public UnderstandingWorker Worker { get; }

    public UnderstandingStore Store { get; }

    /// <summary>The placement checks' prompts, the checks last built and the placement map of these parts.</summary>
    public PlacementWork Placements { get; }

    /// <summary>The last work applied waits for the current book's summaries before its placement checks are built.</summary>
    public bool PlacementsWaiting
    {
        get => Volatile.Read(ref _placementsWaiting) != 0;
        set => Interlocked.Exchange(ref _placementsWaiting, value ? 1 : 0); // a full fence before Settled is read
    }

    /// <summary>How many times a heading or core statement was settled (stored, embedded or given up) so far.</summary>
    public int Settled => Interlocked.CompareExchange(ref _settled, 0, 0);

    /// <summary>Counts a heading or core settled, then tells whether the last work applied waits for summaries or cores.</summary>
    public bool Settle()
    {
        Interlocked.Increment(ref _settled);
        return PlacementsWaiting;
    }

    private int _placementsWaiting;
    private int _settled;
    private int _failures;

    /// <summary>
    /// Remembers the key of the request with the server unless there is none, and returns the key remembered before:
    /// the request that ended (also after the next one started).
    /// </summary>
    public Guid? SwapLastRequest(Guid? current)
    {
        lock (_notesGate)
        {
            var last = _lastRequest;
            _lastRequest = current ?? last;
            return last;
        }
    }

    private Guid? _lastRequest;

    /// <summary>Remembers how many headings and core statements the worker gave up and returns the number remembered before.</summary>
    public int SwapFailures(int failed) => Interlocked.Exchange(ref _failures, failed);

    // The notes' items of the last snapshot computed, and the snapshot last given to the understanding; under _notesGate.
    private readonly Lock _notesGate = new();
    private (IReadOnlyList<NoteSnapshot> Snapshot, IReadOnlyList<UnderstandingItem> Items)? _noteItems;
    private IReadOnlyList<NoteSnapshot>? _notesGiven;

    /// <summary>The notes' items (notes long enough for a core statement, in the snapshot's order); computed once per snapshot.</summary>
    public IReadOnlyList<UnderstandingItem> NoteItems(IReadOnlyList<NoteSnapshot> snapshot)
    {
        if (CachedNoteItems(snapshot) is { } known)
        {
            return known;
        }

        IReadOnlyList<UnderstandingItem> items =
        [
            .. snapshot
                .Select(note => UnderstandingItems.ForNote(note.Key, note.Text, note.Content, note.FilePath, ChatModel))
                .OfType<UnderstandingItem>(),
        ];
        lock (_notesGate)
        {
            _noteItems = (snapshot, items);
        }

        return items;
    }

    /// <summary>The notes' items if they were computed for <paramref name="snapshot"/> already, else null.</summary>
    public IReadOnlyList<UnderstandingItem>? CachedNoteItems(IReadOnlyList<NoteSnapshot> snapshot)
    {
        lock (_notesGate)
        {
            return _noteItems is { } known && ReferenceEquals(known.Snapshot, snapshot) ? known.Items : null;
        }
    }

    /// <summary>Whether <paramref name="snapshot"/> was not the last one given to the understanding (and marks it given).</summary>
    public bool TakeNotesChanged(IReadOnlyList<NoteSnapshot> snapshot)
    {
        lock (_notesGate)
        {
            if (ReferenceEquals(_notesGiven, snapshot))
            {
                return false;
            }

            _notesGiven = snapshot;
            return true;
        }
    }

    /// <summary>The worker paused because the server is unreachable or the model missing, and got no answer since.</summary>
    public bool ServerAway
    {
        get => Volatile.Read(ref _serverAway);
        set => Volatile.Write(ref _serverAway, value);
    }

    private bool _serverAway;

    /// <summary>The worker first (so nothing is stored any more), then the store; searches still running find no understanding.</summary>
    public void Dispose()
    {
        Worker.Dispose();
        Store.Dispose();
    }
}

/// <summary>What a note's understanding items are computed from (and whether it is used, and where), read on the UI thread.</summary>
internal sealed record NoteSnapshot(Guid Key, string Text, string Content, string FilePath, bool Used,
    IReadOnlyList<UsedInEntry> Usages);
