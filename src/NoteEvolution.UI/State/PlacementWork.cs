using System.Collections.Concurrent;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using NoteEvolution.AI.Embeddings;
using NoteEvolution.AI.Relevance;
using NoteEvolution.AI.Understanding;
using NoteEvolution.Core.Books;

namespace NoteEvolution.UI.State;

/// <summary>
/// The placement checks of one set of <see cref="UnderstandingParts"/> (spec 3): whether a book's summaries are settled
/// (<see cref="Ready"/>) and the notes' core statements (<see cref="CoresSettled"/>), the checks' prompts (cached per
/// book instance and note text) and the checks built for a book and notes' snapshot, and the placement map last given to
/// the understanding (<see cref="UpdateMap"/>).
/// </summary>
internal sealed class PlacementWork(
    string chatModel, string embedModel, UnderstandingStore store, StoreUnderstanding understanding, RelevanceService relevance,
    ILogger logger)
{
    internal static readonly IReadOnlyDictionary<Guid, string> NoNotes = new Dictionary<Guid, string>();
    internal static readonly IReadOnlySet<Guid> NoKeys = new HashSet<Guid>();

    /// <summary>The book (link name) the understanding's placement map was last given for, and from which checks; under the session's feed gate.</summary>
    public string? PlacementBook { get; private set; }

    public IReadOnlyList<UnderstandingItem>? PlacementItems { get; private set; }

    /// <summary>
    /// The unused notes of the placement map's book that got no check as there was no topic to offer them; read by the
    /// coverage in the background.
    /// </summary>
    public IReadOnlySet<Guid> WithoutTopics
    {
        get => Volatile.Read(ref _withoutTopics);
        private set => Volatile.Write(ref _withoutTopics, value);
    }

    private IReadOnlySet<Guid> _withoutTopics = new HashSet<Guid>();

    /// <summary>Whether the placement map was last given for another book than <paramref name="book"/> (link name).</summary>
    public bool IsOtherBook(string? book) => !string.Equals(PlacementBook, book, StringComparison.Ordinal);

    /// <summary>
    /// Whether every heading of the book (<paramref name="bookItems"/>, children first) has a summary the prompt can use
    /// (with a vector from the current embedding model, as <see cref="StoreUnderstanding.Heading"/> requires) or will not
    /// get one: it failed <see cref="UnderstandingWorker.MaxAttempts"/> times, or one of its sub-headings will not (the
    /// worker skips it then). Reads the store; a closed or failing store means no.
    /// </summary>
    public bool Ready(IReadOnlyList<UnderstandingItem> bookItems)
    {
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var item in bookItems)
            {
                if (store.Get(item.Fingerprint) is { Kind: UnderstandingKind.Heading, Vector: not null } entry
                    && entry.EmbedModel == embedModel)
                {
                    continue;
                }

                if (store.Attempts(item.Fingerprint) < UnderstandingWorker.MaxAttempts
                    && !item.ChildFingerprints.Any(skipped.Contains))
                {
                    return false;
                }

                skipped.Add(item.Fingerprint);
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or DbException)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Tells of a note's core statement item (null for a note too short to get one) whether it is settled: stored with a
    /// vector from the current embedding model (as <see cref="StoreUnderstanding.NoteCore"/> needs it), given up after
    /// <see cref="UnderstandingWorker.MaxAttempts"/>, or not needed. Reads the store once for all notes; a closed or
    /// failing store settles none.
    /// </summary>
    public Func<UnderstandingItem?, bool> CoresSettled()
    {
        try
        {
            var stored = store.Fingerprints();
            var withoutVector = store.WithoutVector(embedModel).ToHashSet(StringComparer.Ordinal);
            var attempts = store.AllAttempts();
            return core => core is null
                || (stored.Contains(core.Fingerprint) && !withoutVector.Contains(core.Fingerprint))
                || attempts.GetValueOrDefault(core.Fingerprint) >= UnderstandingWorker.MaxAttempts;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or DbException)
        {
            return core => core is null;
        }
    }

    // The placement prompts per book instance and note text, and the placement checks last built (for which book and
    // snapshot; under _gate).
    private readonly ConditionalWeakTable<Book, ConcurrentDictionary<string, PlacementCheckPrompt>> _prompts = new();
    private (Book Book, IReadOnlyList<NoteSnapshot> Notes, IReadOnlyList<UnderstandingItem> Items)? _placements;
    private readonly Lock _gate = new();

    /// <summary>
    /// The placement check's prompt for the note (its <see cref="UnderstandingItems.NoteText"/>) in the book snapshot, as
    /// "Genauer prüfen" asks it: built once per book instance and text if <paramref name="remember"/>ed (a prompt
    /// without topics is not, the embedder may have failed). Reads the semantic index and the store; any thread.
    /// </summary>
    public PlacementCheckPrompt Prompt(Book book, Guid noteKey, string noteText, bool remember)
    {
        var prompts = _prompts.GetValue(book, _ => new ConcurrentDictionary<string, PlacementCheckPrompt>(StringComparer.Ordinal));
        if (prompts.TryGetValue(noteText, out var known))
        {
            return known;
        }

        var passage = EmbeddingText.PassagePrefix + noteText;
        var topics = relevance.Topics(noteKey, passage, book, PlacementCheck.TopicLimit);
        var places = relevance.Placements(noteKey, passage, book, int.MaxValue)
            .GroupBy(hit => hit.SectionKey)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<PlacementHit>)[.. group]);
        var prompt = PlacementCheck.Prompt(book, noteText, topics, places, section => understanding.Heading(book, section)?.Text);
        return remember && prompt.Topics.Count > 0 ? prompts.GetOrAdd(noteText, prompt) : prompt;
    }

    /// <summary>The placement checks built for this book instance and snapshot, else null.</summary>
    public IReadOnlyList<UnderstandingItem>? Cached(Book book, IReadOnlyList<NoteSnapshot> snapshot)
    {
        lock (_gate)
        {
            return _placements is { } known && ReferenceEquals(known.Book, book) && ReferenceEquals(known.Notes, snapshot)
                ? known.Items
                : null;
        }
    }

    /// <summary>
    /// The placement checks of the unused notes in snapshot order (in the background). A note whose check cannot be built
    /// is logged and left out (spec 8), as is one without topics to offer; null when <paramref name="superseded"/> (a later
    /// feed started for another book instance or notes' snapshot; one for the same, e.g. a section change, still gets them
    /// remembered) or <paramref name="stopped"/> (the vault closed, the parts were replaced). A note whose core statement
    /// is not settled yet (see <see cref="CoresSettled"/>) gets no check either, as its prompt would rank the topics
    /// without the core: it is listed as waiting instead (its stored placement shows as stale meanwhile). The checks are
    /// remembered for the snapshots unless a note had no topics (the embedder may have failed) or waits for its core: the
    /// next feed looks for them again.
    /// </summary>
    /// <param name="noteItems">The notes' core statement items of the snapshot.</param>
    /// <param name="building">Called before a note's check is built (the book and the note's key); for tests.</param>
    public PlacementBuild? Build(Book book, IReadOnlyList<NoteSnapshot> notes, IReadOnlyList<UnderstandingItem> noteItems,
        Func<bool> superseded, Func<bool> stopped, Action<Book, Guid>? building)
    {
        var items = new List<UnderstandingItem>();
        var waiting = new Dictionary<Guid, string>();
        var withoutTopics = new HashSet<Guid>();
        var cores = noteItems.ToDictionary(item => item.Key);
        var coreSettled = CoresSettled();
        foreach (var note in notes)
        {
            if (note.Used)
            {
                continue;
            }

            if (superseded() || stopped())
            {
                return null; // nobody would take them (newer snapshots, the vault closed, the model's parts replaced)
            }

            var identity = UnderstandingItems.NoteIdentity(note.Content, note.FilePath);
            if (!coreSettled(cores.GetValueOrDefault(note.Key)))
            {
                waiting[note.Key] = identity;
                continue;
            }

            try
            {
                building?.Invoke(book, note.Key);
                var prompt = Prompt(book, note.Key, note.Text, remember: true);
                if (prompt.Topics.Count == 0)
                {
                    withoutTopics.Add(note.Key);
                    continue; // nothing to choose from, as for "Genauer prüfen"
                }

                items.Add(UnderstandingItems.ForPlacement(note.Key, identity, book.LinkName, prompt, chatModel));
            }
            catch (Exception ex)
            {
                if (stopped())
                {
                    return null; // the vault closed or the model's parts were replaced meanwhile: nothing to warn about
                }

                logger.LogWarning(ex, "Building the placement check of a note failed");
            }
        }

        if (withoutTopics.Count == 0 && waiting.Count == 0)
        {
            lock (_gate)
            {
                _placements = (book, notes, items);
            }
        }

        return new PlacementBuild(items, waiting, withoutTopics);
    }

    /// <summary>
    /// Gives the understanding the placement map of <paramref name="book"/> (link name): the notes' checks by key, and the
    /// notes waiting for their core statement without a fingerprint (their last result shows as stale). Call it under the
    /// session's feed gate.
    /// </summary>
    public void UpdateMap(string book, IReadOnlyList<UnderstandingItem> checks, IReadOnlyDictionary<Guid, string> waitingForCores,
        IReadOnlySet<Guid> withoutTopics)
    {
        var placements = new Dictionary<Guid, (string Fingerprint, string Identity)>();
        foreach (var item in checks)
        {
            placements[item.Key] = (item.Fingerprint, item.Identity);
        }

        // A note waiting for its core statement has no check yet (no fingerprint): its last result shows as stale.
        foreach (var (key, identity) in waitingForCores)
        {
            placements[key] = (string.Empty, UnderstandingItems.PlacementIdentity(book, identity));
        }

        understanding.UpdatePlacements(book, placements);
        PlacementBook = book;
        PlacementItems = checks;
        WithoutTopics = withoutTopics;
    }
}

/// <summary>The placement checks built for a book's unused notes, the notes waiting for their core statement, and those without topics to offer.</summary>
internal sealed record PlacementBuild(IReadOnlyList<UnderstandingItem> Items, IReadOnlyDictionary<Guid, string> WaitingForCores,
    IReadOnlySet<Guid> WithoutTopics);
