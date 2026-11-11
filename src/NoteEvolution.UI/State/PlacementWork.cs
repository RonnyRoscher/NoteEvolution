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

    /// <summary>Measures in the log what a change of the book costs.</summary>
    public PlacementChurn Churn { get; } = new(store.Fingerprints, fingerprint => store.Get(fingerprint)?.Text, logger, new SectionNames());

    /// <summary>The sections' names seen in this session's builds, to read stored results across renames (shared with <see cref="Churn"/>).</summary>
    public SectionNames Names => Churn.Names;

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
    /// next feed looks for them again. A note whose stored result still holds for its changed request keeps it (see
    /// <see cref="Keep"/>).
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
        var keep = new KeepCount();
        var (stored, latest) = StoredPlacements(book.LinkName);
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

            var identity = note.Identity;
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

                items.Add(Keep(book, UnderstandingItems.ForPlacement(note.Key, identity, book.LinkName, prompt, chatModel),
                    stored, latest, keep));
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

        WriteKept(keep);
        Names.Remember(book); // after deciding against it: a path it took over from a deleted section is not the latter's
        Churn.Keep(book.LinkName, keep.Kept, keep.Upgraded, keep.Reasons, keep.Rechecks);
        if (withoutTopics.Count == 0 && waiting.Count == 0)
        {
            lock (_gate)
            {
                _placements = (book, notes, items);
            }
        }

        return new PlacementBuild(items, waiting, withoutTopics, keep.Reasons, keep.Kept, keep.Upgraded, keep.Rechecks);
    }

    /// <summary>The fingerprint suffix of a check made once more for an older result whose section cannot be recognized (spec 3).</summary>
    private const string RepathSuffix = "|repath";

    /// <summary>
    /// Fingerprints whose stored result has a basis (or cannot be read): an older result is upgraded once, so later builds
    /// of the session need not read it again. A result never loses its basis.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _withBasis = new(StringComparer.Ordinal);

    /// <summary>The stored fingerprints and the latest placement check of every identity of the book; none from a closed or failing store.</summary>
    private (IReadOnlySet<string> Stored, IReadOnlyDictionary<string, UnderstandingEntry> Latest) StoredPlacements(string book)
    {
        try
        {
            return (store.Fingerprints(), store.LatestPlacements(book));
        }
        catch (Exception ex) when (ex is ObjectDisposedException or DbException)
        {
            return (new HashSet<string>(), new Dictionary<string, UnderstandingEntry>());
        }
    }

    /// <summary>
    /// The check the worker gets for a note (spec 3, 4). A check whose fingerprint is stored stays as it is, unless its
    /// result was stored without path and basis by an older version (also when it is not the identity's latest, e.g. a
    /// reverted edit, or a note that shared its identity with another before they were numbered): then the section is
    /// recognized by its summary (the request is the same as then) and path and basis are written to it under the
    /// check's identity, or, if that is not unique, it is made once more (<see cref="RepathSuffix"/>). A check not stored
    /// yet takes the fingerprint of the note's latest result when it was made by today's chat model and
    /// <see cref="PlacementKeep.Decide"/> keeps it, so the worker sees it done and the understanding as current; a kept
    /// result whose sections were renamed is written with today's names (<see cref="PlacementKeep.Renamed"/>). What is
    /// written is written at the end of the build (<see cref="WriteKept"/>). A stored result is read only when needed.
    /// </summary>
    private UnderstandingItem Keep(Book book, UnderstandingItem item, IReadOnlySet<string> stored,
        IReadOnlyDictionary<string, UnderstandingEntry> latest, KeepCount count)
    {
        var repath = item.Fingerprint + RepathSuffix;
        if (stored.Contains(repath))
        {
            return item with { Fingerprint = repath };
        }

        var entry = latest.GetValueOrDefault(item.Identity);
        if (stored.Contains(item.Fingerprint))
        {
            if (_withBasis.ContainsKey(item.Fingerprint))
            {
                return item;
            }

            var own = entry?.Fingerprint == item.Fingerprint ? entry : StoredEntry(item.Fingerprint);
            if (own is null)
            {
                return item; // not readable now: looked at again by the next build
            }

            if (PlacementCheck.Deserialize(own.Text) is not { Basis: null } old)
            {
                _withBasis.TryAdd(item.Fingerprint, 0);
                return item;
            }

            if (Upgrade(item, own, old) is { } upgraded)
            {
                count.Upgrades.Add(upgraded);
                return item;
            }

            count.Recheck(repath, new Recheck(RecheckReason.NoBasis, Earlier(item.Check!, old)));
            return item with { Fingerprint = repath };
        }

        if (entry is null || PlacementCheck.Deserialize(entry.Text) is not { } verdict)
        {
            return item; // never checked (or unreadable): checked as before
        }

        if (entry.ChatModel != chatModel)
        {
            count.Recheck(item.Fingerprint, new Recheck(RecheckReason.Model, Earlier(item.Check!, verdict)));
            return item;
        }

        var reason = PlacementKeep.Decide(verdict, item.Check!, book, Names, out var newCandidates);
        if (reason != RecheckReason.None)
        {
            count.Recheck(item.Fingerprint, new Recheck(reason, Earlier(item.Check!, verdict),
                newCandidates.Count > 0 ? new NewCandidates(item.Title, newCandidates) : null));
            return item;
        }

        count.Kept++;
        if (PlacementKeep.Renamed(verdict, book, Names) is { } renamed)
        {
            count.Renames.Add(entry with { Text = PlacementCheck.Serialize(renamed) });
        }

        return item with { Fingerprint = entry.Fingerprint };
    }

    /// <summary>
    /// For the log: the section <paramref name="verdict"/> had chosen, by its stored path, or for an older result without
    /// one by today's offered sections with its summary (none when no section has it any more).
    /// </summary>
    internal static EarlierChoice Earlier(PlacementCheckPrompt prompt, PlacementVerdict verdict) =>
        verdict.SectionPath is { } path
            ? new EarlierChoice([path], verdict.AfterText)
            : new EarlierChoice(
                [.. prompt.Topics.Where(topic => verdict.Summary is not null && topic.Summary == verdict.Summary && topic.Path.Length > 0)
                    .Select(topic => topic.Path).Distinct(StringComparer.Ordinal)],
                null);

    /// <summary>The entry stored under <paramref name="fingerprint"/>; none from a closed or failing store.</summary>
    private UnderstandingEntry? StoredEntry(string fingerprint)
    {
        try
        {
            return store.Get(fingerprint);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or DbException)
        {
            return null;
        }
    }

    /// <summary>
    /// An older result stored for the item (same fingerprint) with path and basis from the item's request and the item's
    /// identity, when its summary is that of exactly one offered section; its place is not known any more, so it becomes a
    /// new place at the end of the section. Null if the section is not recognized.
    /// </summary>
    private static UnderstandingEntry? Upgrade(UnderstandingItem item, UnderstandingEntry entry, PlacementVerdict verdict)
    {
        var prompt = item.Check!;
        var topics = prompt.Topics.Where(topic => verdict.Summary is not null && topic.Summary == verdict.Summary).ToList();
        if (topics.Count != 1)
        {
            return null;
        }

        // Today's keys: the stored ones are from the session that made the result.
        var upgraded = verdict with
        {
            SectionKey = topics[0].SectionKey, AfterTextBlockKey = null, EmbeddingSectionKey = prompt.EmbeddingSectionKey,
        };
        return entry with { Identity = item.Identity, Text = PlacementCheck.Serialize(upgraded, prompt) };
    }

    /// <summary>
    /// Stores the build's upgraded older results and its kept results with renamed sections in one transaction (one change
    /// reported). A closed or failing store keeps them as they were: the upgraded ones are looked at again by the next
    /// build, the renamed ones are still found by their keys in this session.
    /// </summary>
    private void WriteKept(KeepCount count)
    {
        if (count.Upgrades.Count + count.Renames.Count == 0)
        {
            return;
        }

        try
        {
            store.PutAll([.. count.Upgrades, .. count.Renames]);
            count.Upgraded = count.Upgrades.Count;
            foreach (var upgraded in count.Upgrades)
            {
                _withBasis.TryAdd(upgraded.Fingerprint, 0);
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or DbException)
        {
            logger.LogWarning(ex, "Storing the placements upgraded with their section path or renamed failed");
        }
    }

    /// <summary>
    /// The fingerprint the placement map gives the note of <paramref name="item"/> for the same request when it differs
    /// (a kept result, see <see cref="Keep"/>), else null: "Genauer prüfen" stores its verdict under it, so it replaces
    /// the one shown.
    /// </summary>
    public string? MapFingerprint(UnderstandingItem item) =>
        PlacementItems?.FirstOrDefault(known => known.Key == item.Key && known.Identity == item.Identity
            && known.Fingerprint != item.Fingerprint && known.Check is { } check && item.Check is { } asked
            && check.System == asked.System && check.User == asked.User && check.Schema == asked.Schema)?.Fingerprint;

    /// <summary>How many results a build kept and upgraded, and why the others are checked again.</summary>
    private sealed class KeepCount
    {
        public Dictionary<RecheckReason, int> Reasons { get; } = [];

        /// <summary>The checks made again, by fingerprint, with why and what the earlier result had chosen.</summary>
        public Dictionary<string, Recheck> Rechecks { get; } = new(StringComparer.Ordinal);

        /// <summary>Older results with path and basis added, to be written at the end of the build.</summary>
        public List<UnderstandingEntry> Upgrades { get; } = [];

        /// <summary>Kept results with today's names of their renamed sections, to be written at the end of the build.</summary>
        public List<UnderstandingEntry> Renames { get; } = [];

        /// <summary>Results kept for a changed request (the upgraded ones not counted).</summary>
        public int Kept { get; set; }

        /// <summary>Older results upgraded and stored.</summary>
        public int Upgraded { get; set; }

        public void Recheck(string fingerprint, Recheck recheck)
        {
            Reasons[recheck.Reason] = Reasons.GetValueOrDefault(recheck.Reason) + 1;
            Rechecks[fingerprint] = recheck;
        }
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
        Churn.Checks(book, checks);
        PlacementBook = book;
        PlacementItems = checks;
        WithoutTopics = withoutTopics;
    }
}

/// <summary>
/// The placement checks built for a book's unused notes, the notes waiting for their core statement, and those without
/// topics to offer; for the measurement, how many stored results were kept, how many older ones upgraded, and why the
/// others are checked again (counted, and by the fingerprint of their check).
/// </summary>
internal sealed record PlacementBuild(IReadOnlyList<UnderstandingItem> Items, IReadOnlyDictionary<Guid, string> WaitingForCores,
    IReadOnlySet<Guid> WithoutTopics, IReadOnlyDictionary<RecheckReason, int> Reasons, int Kept, int Upgraded,
    IReadOnlyDictionary<string, Recheck> Rechecks);
