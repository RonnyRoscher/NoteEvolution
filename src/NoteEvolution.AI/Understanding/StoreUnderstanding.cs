using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using NoteEvolution.Core.Books;

namespace NoteEvolution.AI.Understanding;

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

    /// <summary>The store stored something for the fingerprint (on the writer's thread): its miss no longer holds.</summary>
    private void OnEntryChanged(string fingerprint)
    {
        Interlocked.Increment(ref _storeChanges);
        _noteMisses.TryRemove(fingerprint, out _);
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

    /// <summary>A book snapshot's heading items, in <see cref="UnderstandingItems.ForBook"/> order and by session key.</summary>
    private sealed record BookItems(IReadOnlyList<UnderstandingItem> List, IReadOnlyDictionary<Guid, UnderstandingItem> ByKey);

    /// <summary>The entry if it is of the kind and has a vector from the current embed model, else null.</summary>
    private UnderstandingEntry? Usable(UnderstandingEntry? entry, UnderstandingKind kind) =>
        entry is { Vector: not null } && entry.Kind == kind && entry.EmbedModel == _embedModel ? entry : null;
}
