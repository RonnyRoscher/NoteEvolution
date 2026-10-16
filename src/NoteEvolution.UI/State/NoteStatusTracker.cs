using System.Data.Common;
using Microsoft.Extensions.Logging;
using NoteEvolution.AI.Semantic;
using NoteEvolution.AI.Understanding;
using NoteEvolution.Core.Books;

namespace NoteEvolution.UI.State;

/// <summary>
/// The notes' status of a <see cref="VaultSession"/> (the note cards' dots, spec 7.2): put together in the background from
/// the snapshots of the last work applied (<see cref="Use"/>), at most once per <see cref="VaultSession.NoteStatusDelay"/>,
/// reading again only what changed (<see cref="Schedule"/>).
/// </summary>
internal sealed class NoteStatusTracker : IDisposable
{
    private readonly ILogger _logger;
    private readonly bool _enabled;
    private readonly Func<UnderstandingParts?> _parts;
    private readonly Func<SemanticIndex?> _index;
    private readonly Action<Action, string> _dispatch;

    /// <param name="enabled">False without a language model: nothing is ever put together.</param>
    /// <param name="parts">The language model's current parts (null while it does not run).</param>
    /// <param name="index">The semantic index (null until it exists).</param>
    /// <param name="dispatch">Runs an action on the UI thread; never throws.</param>
    public NoteStatusTracker(TimeProvider time, ILogger logger, bool enabled, Func<UnderstandingParts?> parts,
        Func<SemanticIndex?> index, Action<Action, string> dispatch)
    {
        _logger = logger;
        _enabled = enabled;
        _parts = parts;
        _index = index;
        _dispatch = dispatch;
        _throttle = new Throttle(time, VaultSession.NoteStatusDelay, () => Task.Run(Build), logger, "Putting the notes' status together");
    }

    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>The snapshots of the last work applied, which the notes' status is put together from.</summary>
    private volatile StatusSource? _source;

    /// <summary>The notes' status inputs (without the running request) and the parts they were read with; null until put together.</summary>
    private volatile NoteStatuses? _statuses;

    // Putting the notes' status together at most once per NoteStatusDelay, with what changed since (under _gate).
    private readonly Throttle _throttle;
    private StatusChanges _changed = new();

    private int _reads;
    private int _builds;

    /// <summary>How many notes the status builds read from the store and the understanding; for tests.</summary>
    public int Reads => Volatile.Read(ref _reads);

    /// <summary>How many status builds ended; for tests.</summary>
    public int Builds => Volatile.Read(ref _builds);

    /// <summary>Called in the background when a status build is done, just before <see cref="Changed"/> is dispatched; for tests.</summary>
    public Action? Raising { get; set; }

    /// <summary>The notes' status is being put together now; for tests.</summary>
    public bool Building => _throttle.Running;

    /// <summary><see cref="VaultSession.NoteStatusChanged"/>; raised on the UI thread.</summary>
    public event Action? Changed;

    /// <summary><see cref="VaultSession.NoteStatus"/>; call it on the UI thread.</summary>
    public NoteStatusInput? Of(Guid noteKey)
    {
        if (_parts() is not { } current || _statuses is not { } statuses || !ReferenceEquals(statuses.Parts, current)
            || !statuses.Inputs.TryGetValue(noteKey, out var input))
        {
            return null;
        }

        var running = current.Worker.Progress.Current is { } request && request.Key == noteKey ? request.Kind : (UnderstandingKind?)null;
        return input with { LanguageModel = !current.ServerAway, Running = running };
    }

    /// <summary><see cref="VaultSession.EmbeddingProgress"/>; call it on the UI thread.</summary>
    public (int Done, int Total)? EmbeddingProgress =>
        _parts() is { } current && _statuses is { } statuses && ReferenceEquals(statuses.Parts, current) ? statuses.Embedding : null;

    /// <summary>
    /// The work applied came from these snapshots; the same snapshots again (a focus change) keep the source, so only what
    /// changed is read again.
    /// </summary>
    /// <param name="noteItems">The notes' core-statement items.</param>
    /// <param name="placementItems">The placement checks the understanding was given for the book.</param>
    public void Use(UnderstandingParts current, IReadOnlyList<NoteSnapshot> notes, Book? book,
        IReadOnlyList<UnderstandingItem> noteItems, IReadOnlyList<UnderstandingItem> placementItems)
    {
        if (_source is not { } source || !source.Of(current, notes, book, noteItems, placementItems))
        {
            _source = new StatusSource(current, notes, book, noteItems, placementItems);
        }
    }

    /// <summary>
    /// Puts the notes' status together <see cref="VaultSession.NoteStatusDelay"/> from now unless that is pending already;
    /// while a build runs, once more a delay after it ended, so <see cref="Changed"/> comes at most once per delay.
    /// Only the notes of the stored <paramref name="fingerprint"/> and the <paramref name="keys"/> are read again, all of
    /// them with <paramref name="all"/> or new snapshots (see <see cref="Build"/>); with
    /// <paramref name="embeddings"/> the embeddings of all of them. Any thread; never throws.
    /// </summary>
    public void Schedule(string? fingerprint = null, IEnumerable<Guid?>? keys = null, bool all = false,
        bool embeddings = false)
    {
        lock (_gate)
        {
            if (_disposed || !_enabled)
            {
                return;
            }

            if (fingerprint is not null)
            {
                _changed.Fingerprints.Add(fingerprint);
            }

            foreach (var key in keys ?? [])
            {
                if (key is { } k)
                {
                    _changed.Keys.Add(k);
                }
            }

            _changed.All |= all;
            _changed.Embeddings |= embeddings;
            _throttle.Request();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _throttle.Dispose();
        }
    }

    private bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _disposed;
            }
        }
    }

    /// <summary>
    /// In the background, from the last snapshots: the notes' status, then the event, then the next build if one was asked
    /// for. New snapshots (another feed) or <see cref="StatusChanges.All"/> read every note; otherwise only the notes whose
    /// entries were stored or whose request started or ended are read again (the store forgets its cached placements at
    /// every change, so reading all notes each time would cost a query or three per note).
    /// </summary>
    private void Build()
    {
        try
        {
            StatusChanges changes;
            lock (_gate)
            {
                changes = _changed;
                _changed = new StatusChanges();
            }

            if (_source is { } source && ReferenceEquals(_parts(), source.Parts))
            {
                var known = _statuses;
                NoteStatuses statuses;
                if (changes.All || known is null || !ReferenceEquals(known.Source, source))
                {
                    statuses = new NoteStatuses(source.Parts, source, Inputs(source, source.Notes), CountEmbeddings(source));
                }
                else
                {
                    var notes = changes.Keys
                        .Concat(changes.Fingerprints.SelectMany(fingerprint => source.KeysOf.GetValueOrDefault(fingerprint) ?? []))
                        .Distinct()
                        .Select(key => source.NoteOf.GetValueOrDefault(key))
                        .OfType<NoteSnapshot>()
                        .ToList();
                    var inputs = new Dictionary<Guid, NoteStatusInput>(known.Inputs);
                    var changed = changes.Embeddings && UpdateEmbeddings(inputs);
                    if (notes.Count == 0 && !changed)
                    {
                        return; // nothing to tell
                    }

                    foreach (var (key, input) in Inputs(source, notes))
                    {
                        inputs[key] = input;
                    }

                    statuses = new NoteStatuses(source.Parts, source, inputs, CountEmbeddings(source));
                }

                _statuses = statuses;
                Raising?.Invoke();
                _dispatch(
                    () =>
                    {
                        if (!IsDisposed)
                        {
                            Changed?.Invoke();
                        }
                    },
                    "the notes' status");
            }
        }
        catch (Exception ex)
        {
            // The store was closed or failed: the last status stays, and the next change reads every note again.
            if (ex is not (ObjectDisposedException or DbException))
            {
                _logger.LogError(ex, "Putting the notes' status together failed");
            }

            lock (_gate)
            {
                _changed.All = true;
            }
        }
        finally
        {
            Interlocked.Increment(ref _builds);
        }
    }

    /// <summary>How many of the top-level notes have an embedding (in memory, no store reads); null without a semantic index.</summary>
    private (int Done, int Total)? CountEmbeddings(StatusSource source) =>
        _index() is { } semantic
            ? (source.NoteOf.Keys.Count(key => semantic.VectorOf(key) is not null), source.NoteOf.Count)
            : null;

    /// <summary>Reads the embeddings of the known inputs again (in memory, no store reads); whether one changed.</summary>
    private bool UpdateEmbeddings(Dictionary<Guid, NoteStatusInput> inputs)
    {
        if (_index() is not { } semantic)
        {
            return false;
        }

        var changed = false;
        foreach (var (key, input) in inputs.ToList())
        {
            var embedded = semantic.VectorOf(key) is not null;
            if (input.Embedded != embedded)
            {
                inputs[key] = input with { Embedded = embedded };
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// The status inputs of <paramref name="notes"/>: the core statement from the store by the note's item (only notes
    /// long enough have one), the embedding from the index, the placement in the current book. A used note gets none (no
    /// dot, also if it is unused by now, until the next feed brings its snapshot) and nothing is read for it.
    /// </summary>
    private Dictionary<Guid, NoteStatusInput> Inputs(StatusSource source, IEnumerable<NoteSnapshot> notes)
    {
        var current = source.Parts;
        var semantic = _index();
        var inputs = new Dictionary<Guid, NoteStatusInput>();
        foreach (var note in notes)
        {
            if (note.Used)
            {
                continue;
            }

            Interlocked.Increment(ref _reads);

            var needsCore = source.CoreOf.TryGetValue(note.Key, out var fingerprint);
            var coreDone = needsCore && current.Store.Get(fingerprint!) is { Kind: UnderstandingKind.Note };
            var coreFailed = needsCore && !coreDone && current.Store.Attempts(fingerprint!) >= UnderstandingWorker.MaxAttempts;

            // Current and stale from the placement still usable in the book; given up only for the current fingerprint.
            var placement = source.Book is { } book ? current.Understanding.Placement(book, note.Key) : null;
            var state = source.Book is null ? PlacementState.Missing
                : placement is not { Stale: false } && current.Understanding.PlacementStateOf(note.Key) == PlacementState.Failed
                    ? PlacementState.Failed
                : placement is null ? PlacementState.Missing
                : placement.Stale ? PlacementState.Stale
                : PlacementState.Current;
            var fit = state == PlacementState.Current ? placement!.Verdict.Fit : (PlacementFit?)null;
            bool? embedded = semantic is null ? null : semantic.VectorOf(note.Key) is not null;
            inputs[note.Key] = new NoteStatusInput(false, true, embedded, needsCore, coreDone, coreFailed, state, null, fit);
        }

        return inputs;
    }

    /// <summary>What the notes' status is put together from: the parts, the notes' snapshot and items, and the current book.</summary>
    private sealed class StatusSource
    {
        /// <param name="noteItems">The notes' core-statement items.</param>
        /// <param name="placementItems">The placement checks the understanding was given for the book.</param>
        public StatusSource(UnderstandingParts parts, IReadOnlyList<NoteSnapshot> notes, Book? book,
            IReadOnlyList<UnderstandingItem> noteItems, IReadOnlyList<UnderstandingItem> placementItems)
        {
            Parts = parts;
            Notes = notes;
            Book = book;
            _noteItems = noteItems;
            _placementItems = placementItems;
            NoteOf = notes.DistinctBy(note => note.Key).ToDictionary(note => note.Key);
            CoreOf = noteItems.DistinctBy(item => item.Key).ToDictionary(item => item.Key, item => item.Fingerprint);
            KeysOf = noteItems.Concat(placementItems)
                .GroupBy(item => item.Fingerprint, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<Guid>)[.. group.Select(item => item.Key)], StringComparer.Ordinal);
        }

        public UnderstandingParts Parts { get; }

        public IReadOnlyList<NoteSnapshot> Notes { get; }

        public Book? Book { get; }

        public IReadOnlyDictionary<Guid, NoteSnapshot> NoteOf { get; }

        /// <summary>The fingerprint of the note's core statement, for notes long enough to have one.</summary>
        public IReadOnlyDictionary<Guid, string> CoreOf { get; }

        /// <summary>The notes whose core statement or placement check has the fingerprint.</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<Guid>> KeysOf { get; }

        private readonly IReadOnlyList<UnderstandingItem> _noteItems;
        private readonly IReadOnlyList<UnderstandingItem> _placementItems;

        /// <summary>Whether this source is made of these very snapshots.</summary>
        public bool Of(UnderstandingParts parts, IReadOnlyList<NoteSnapshot> notes, Book? book,
            IReadOnlyList<UnderstandingItem> noteItems, IReadOnlyList<UnderstandingItem> placementItems) =>
            ReferenceEquals(Parts, parts) && ReferenceEquals(Notes, notes) && ReferenceEquals(Book, book)
            && ReferenceEquals(_noteItems, noteItems) && ReferenceEquals(_placementItems, placementItems);
    }

    /// <summary>The notes' status inputs by key, read with <paramref name="Parts"/> from <paramref name="Source"/>.</summary>
    private sealed record NoteStatuses(UnderstandingParts Parts, StatusSource Source, IReadOnlyDictionary<Guid, NoteStatusInput> Inputs,
        (int Done, int Total)? Embedding);

    /// <summary>What changed since the notes' status was last put together.</summary>
    private sealed class StatusChanges
    {
        public HashSet<string> Fingerprints { get; } = new(StringComparer.Ordinal);

        public HashSet<Guid> Keys { get; } = [];

        public bool All { get; set; }

        /// <summary>Notes were embedded: the known inputs' embeddings are read again.</summary>
        public bool Embeddings { get; set; }
    }
}
