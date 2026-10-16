using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using NoteEvolution.Core.Books;

namespace NoteEvolution.AI.Understanding;

/// <summary>A note's stored placement check and whether it is stale (made for an older text of the note or state of the book).</summary>
public sealed record NotePlacement(PlacementVerdict Verdict, bool Stale);

/// <summary>Where a note's placement check stands: nothing yet, current, only an older one, or given up after too many failures.</summary>
public enum PlacementState { Missing, Current, Stale, Failed }

/// <summary>
/// <see cref="IUnderstanding"/> over the <see cref="UnderstandingStore"/>. Only entries whose vector comes from
/// <c>embedModel</c> count. Headings are found by the fingerprints <see cref="UnderstandingItems.ForBook"/> gives the
/// snapshot (computed once per <see cref="Book"/> instance, see <see cref="Items"/>), falling back to the newest entry of
/// the same identity. Notes are found through the key→fingerprint map the session passes to <see cref="UpdateNotes"/>;
/// they have no stale fallback, so an edited note is compared by its text alone until its new core statement is there.
/// A note without a usable entry is not looked up again until the store reports a change for its fingerprint
/// (<see cref="UnderstandingStore.EntryChanged"/>). A store that was closed (the
/// vault switched while a search runs) or fails to read gives no understanding instead of an exception. Safe to call from
/// several threads.
/// </summary>
public sealed class StoreUnderstanding : IUnderstanding
{
    private readonly UnderstandingStore _store;
    private readonly string _chatModel;
    private readonly string _embedModel;
    private readonly ConditionalWeakTable<Book, BookItems> _books = new();

    /// <summary>Core-statement vectors found so far, by fingerprint; an entry with a vector never changes for one embed model.</summary>
    private readonly ConcurrentDictionary<string, float[]> _noteVectors = new(StringComparer.Ordinal);

    /// <summary>Note fingerprints the store had no usable entry for, until it reports a change for them.</summary>
    private readonly ConcurrentDictionary<string, byte> _noteMisses = new(StringComparer.Ordinal);

    /// <summary>Counts the store's changes, so a miss read before a change is not remembered after it.</summary>
    private int _storeChanges;

    private IReadOnlyDictionary<Guid, string> _notes = new Dictionary<Guid, string>();

    /// <summary>The book the placement lookups are for, with its notes' key → (fingerprint, identity) map.</summary>
    private PlacementNotes _placementNotes = new(string.Empty, new Dictionary<Guid, (string, string)>());

    /// <summary>Parsed placement of a fingerprint (null = none or unreadable), until the store reports any change.</summary>
    private readonly ConcurrentDictionary<string, PlacementVerdict?> _placementsByFingerprint = new(StringComparer.Ordinal);

    /// <summary>Parsed latest placement of an identity (null = none or unreadable), until the store reports any change.</summary>
    private readonly ConcurrentDictionary<string, PlacementVerdict?> _placementsByIdentity = new(StringComparer.Ordinal);

    public StoreUnderstanding(UnderstandingStore store, string chatModel, string embedModel)
    {
        _store = store;
        _chatModel = chatModel;
        _embedModel = embedModel;
        store.EntryChanged += OnEntryChanged;
    }

    /// <summary>
    /// The headings of the book snapshot as <see cref="UnderstandingItems.ForBook"/> gives them, computed once per
    /// <see cref="Book"/> instance (a snapshot never changes). Safe to call from any thread.
    /// </summary>
    public IReadOnlyList<UnderstandingItem> Items(Book book) => BookItemsOf(book).List;

    /// <summary><see cref="Items"/> if they were computed for this <see cref="Book"/> instance already, else null (computes nothing).</summary>
    public IReadOnlyList<UnderstandingItem>? CachedItems(Book book) => _books.TryGetValue(book, out var items) ? items.List : null;

    /// <summary>
    /// Replaces the notes' session key → <see cref="UnderstandingItems.NoteFingerprint"/> map (notes without a core
    /// statement may be left out). Called by the session on the UI thread; the map is copied.
    /// </summary>
    public void UpdateNotes(IReadOnlyDictionary<Guid, string> keyToFingerprint)
    {
        var notes = new Dictionary<Guid, string>(keyToFingerprint);
        Volatile.Write(ref _notes, notes);

        var live = notes.Values.ToHashSet(StringComparer.Ordinal);
        foreach (var fingerprint in _noteVectors.Keys)
            if (!live.Contains(fingerprint)) _noteVectors.TryRemove(fingerprint, out _);
        foreach (var fingerprint in _noteMisses.Keys)
            if (!live.Contains(fingerprint)) _noteMisses.TryRemove(fingerprint, out _);
    }

    /// <summary>
    /// Sets the book (by link name) whose notes <see cref="Placement"/> and <see cref="PlacementStateOf"/> answer for, and
    /// their session key → (placement fingerprint, placement identity) map. Called by the session on the UI thread; the map is copied.
    /// </summary>
    public void UpdatePlacements(string bookLinkName, IReadOnlyDictionary<Guid, (string Fingerprint, string Identity)> notes) =>
        Volatile.Write(ref _placementNotes, new PlacementNotes(bookLinkName, new Dictionary<Guid, (string, string)>(
            notes.Select(n => KeyValuePair.Create(n.Key, (n.Value.Fingerprint, n.Value.Identity))))));

    /// <summary>
    /// The note's stored placement: the one for its current fingerprint, else the latest of its identity marked stale.
    /// Null if there is none, it is unreadable, its section or place is no longer in <paramref name="book"/>, or
    /// <paramref name="book"/> is not the book given to <see cref="UpdatePlacements"/>.
    /// </summary>
    public NotePlacement? Placement(Book book, Guid noteKey)
    {
        var map = Volatile.Read(ref _placementNotes);
        if (map.BookLinkName != book.LinkName || !map.Notes.TryGetValue(noteKey, out var note)) return null;

        var stale = false;
        var verdict = CurrentPlacement(note.Fingerprint);
        if (verdict is null)
        {
            verdict = LatestPlacement(note.Identity);
            stale = true;
        }

        if (verdict is null || book.FindNode(verdict.SectionKey) is null) return null;
        if (verdict.AfterTextBlockKey is { } after && book.FindTextBlock(after) is null) return null;
        return new NotePlacement(verdict, stale);
    }

    /// <summary>
    /// The state of the note's placement check for the book given to <see cref="UpdatePlacements"/>; whether a stale
    /// result is still usable in the book is for <see cref="Placement"/> to say.
    /// </summary>
    public PlacementState PlacementStateOf(Guid noteKey)
    {
        if (!Volatile.Read(ref _placementNotes).Notes.TryGetValue(noteKey, out var note)) return PlacementState.Missing;
        if (CurrentPlacement(note.Fingerprint) is not null) return PlacementState.Current;

        try
        {
            if (_store.Attempts(note.Fingerprint) >= UnderstandingWorker.MaxAttempts) return PlacementState.Failed;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SqliteException)
        {
            return PlacementState.Missing;
        }

        return LatestPlacement(note.Identity) is not null ? PlacementState.Stale : PlacementState.Missing;
    }

    private PlacementVerdict? CurrentPlacement(string fingerprint) => CachedPlacement(_placementsByFingerprint, fingerprint,
        () => _store.Get(fingerprint));

    private PlacementVerdict? LatestPlacement(string identity) => CachedPlacement(_placementsByIdentity, identity,
        () => _store.LatestByIdentity(UnderstandingKind.Placement, identity));

    /// <summary>A cached lookup; a failed read is not cached, and neither is a result read while the store changed.</summary>
    private PlacementVerdict? CachedPlacement(ConcurrentDictionary<string, PlacementVerdict?> cache, string key, Func<UnderstandingEntry?> read)
    {
        if (cache.TryGetValue(key, out var cached)) return cached;

        var changes = Volatile.Read(ref _storeChanges);
        var (entry, failed) = Read(read);
        if (failed) return null;

        var verdict = entry is { Kind: UnderstandingKind.Placement } ? PlacementCheck.Deserialize(entry.Text) : null;
        // Remembered first, then checked: a change reported meanwhile takes it back (here or in OnEntryChanged).
        cache[key] = verdict;
        if (Volatile.Read(ref _storeChanges) != changes) cache.TryRemove(key, out _);
        return verdict;
    }

    public (string Text, float[] Vector)? Heading(Book book, OutlineNode heading)
    {
        if (!BookItemsOf(book).ByKey.TryGetValue(heading.Key, out var item)) return null;

        var (entry, _) = Read(() => Usable(_store.Get(item.Fingerprint), UnderstandingKind.Heading)
                         ?? Usable(_store.LatestByIdentity(UnderstandingKind.Heading, item.Identity), UnderstandingKind.Heading));
        return entry is null ? null : (entry.Text, entry.Vector!);
    }

    public float[]? NoteCore(Guid noteKey) =>
        Volatile.Read(ref _notes).TryGetValue(noteKey, out var fingerprint) ? NoteVector(fingerprint) : null;

    public IReadOnlyList<(Guid Key, float[] Vector)> NoteCores()
    {
        var result = new List<(Guid, float[])>();
        foreach (var (key, fingerprint) in Volatile.Read(ref _notes))
            if (NoteVector(fingerprint) is { } vector) result.Add((key, vector));
        return result;
    }

    private float[]? NoteVector(string fingerprint)
    {
        if (_noteVectors.TryGetValue(fingerprint, out var cached)) return cached;
        if (_noteMisses.ContainsKey(fingerprint)) return null;

        var changes = Volatile.Read(ref _storeChanges);
        var (entry, failed) = Read(() => Usable(_store.Get(fingerprint), UnderstandingKind.Note));
        if (entry is not null)
        {
            _noteVectors[fingerprint] = entry.Vector!;
            return entry.Vector;
        }

        if (!failed)
        {
            // Remembered first, then checked: a change reported meanwhile takes the miss back (here or in OnEntryChanged).
            _noteMisses[fingerprint] = 0;
            if (Volatile.Read(ref _storeChanges) != changes) _noteMisses.TryRemove(fingerprint, out _);
        }

        return null;
    }

    /// <summary>
    /// The store stored something for the fingerprint (on the writer's thread): its miss no longer holds, and a placement
    /// check stored drops the parsed placements. Core statements and summaries (and their vectors) leave those alone.
    /// </summary>
    private void OnEntryChanged(string fingerprint)
    {
        Interlocked.Increment(ref _storeChanges);
        _noteMisses.TryRemove(fingerprint, out _);
        if (!IsPlacement(fingerprint)) return;

        // The event names no identity, so the latest-of-identity results cannot be told apart: both caches go.
        _placementsByFingerprint.Clear();
        _placementsByIdentity.Clear();
    }

    /// <summary>
    /// Whether the fingerprint is a placement check's: one of the current map's or one looked up already, else as the
    /// store says (another book's check, or "Genauer prüfen" for a note outside the map). A failing read says yes.
    /// </summary>
    private bool IsPlacement(string fingerprint)
    {
        if (_placementsByFingerprint.ContainsKey(fingerprint)
            || Volatile.Read(ref _placementNotes).Notes.Values.Any(note => note.Fingerprint == fingerprint)) return true;

        try
        {
            return _store.KindOf(fingerprint) is not (UnderstandingKind.Heading or UnderstandingKind.Note);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SqliteException)
        {
            return true;
        }
    }

    private BookItems BookItemsOf(Book book) => _books.GetValue(book, b =>
    {
        var list = UnderstandingItems.ForBook(b, _chatModel);
        return new BookItems(list, list.ToDictionary(item => item.Key));
    });

    /// <summary>Runs a store read; a closed or failing store gives no entry and <c>Failed</c>.</summary>
    private static (UnderstandingEntry? Entry, bool Failed) Read(Func<UnderstandingEntry?> read)
    {
        try
        {
            return (read(), false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SqliteException)
        {
            return (null, true);
        }
    }

    private sealed record PlacementNotes(string BookLinkName, IReadOnlyDictionary<Guid, (string Fingerprint, string Identity)> Notes);

    /// <summary>A book snapshot's heading items, in <see cref="UnderstandingItems.ForBook"/> order and by session key.</summary>
    private sealed record BookItems(IReadOnlyList<UnderstandingItem> List, IReadOnlyDictionary<Guid, UnderstandingItem> ByKey);

    /// <summary>The entry if it is of the kind and has a vector from the current embed model, else null.</summary>
    private UnderstandingEntry? Usable(UnderstandingEntry? entry, UnderstandingKind kind) =>
        entry is { Vector: not null } && entry.Kind == kind && entry.EmbedModel == _embedModel ? entry : null;
}
