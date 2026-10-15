using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using NoteEvolution.Core.Books;

namespace NoteEvolution.AI.Understanding;

/// <summary>
/// <see cref="IUnderstanding"/> over the <see cref="UnderstandingStore"/>. Only entries whose vector comes from
/// <c>embedModel</c> count. Headings are found by the fingerprints <see cref="UnderstandingItems.ForBook"/> gives the
/// snapshot (computed once per <see cref="Book"/> instance), falling back to the newest entry of the same identity. Notes
/// are found through the key→fingerprint map the session passes to <see cref="UpdateNotes"/>; they have no stale fallback,
/// so an edited note is compared by its text alone until its new core statement is there. A store that was closed (the
/// vault switched while a search runs) or fails to read gives no understanding instead of an exception. Safe to call from
/// several threads.
/// </summary>
public sealed class StoreUnderstanding : IUnderstanding
{
    private readonly UnderstandingStore _store;
    private readonly string _chatModel;
    private readonly string _embedModel;
    private readonly ConditionalWeakTable<Book, IReadOnlyDictionary<Guid, (string Fingerprint, string Identity)>> _headings = new();

    /// <summary>Core-statement vectors found so far, by fingerprint; an entry with a vector never changes for one embed model.</summary>
    private readonly ConcurrentDictionary<string, float[]> _noteVectors = new(StringComparer.Ordinal);

    private IReadOnlyDictionary<Guid, string> _notes = new Dictionary<Guid, string>();

    public StoreUnderstanding(UnderstandingStore store, string chatModel, string embedModel)
    {
        _store = store;
        _chatModel = chatModel;
        _embedModel = embedModel;
    }

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
    }

    public (string Text, float[] Vector)? Heading(Book book, OutlineNode heading)
    {
        var headings = _headings.GetValue(book, b => UnderstandingItems.ForBook(b, _chatModel)
            .ToDictionary(item => item.Key, item => (item.Fingerprint, item.Identity)));
        if (!headings.TryGetValue(heading.Key, out var ids)) return null;

        var entry = Read(() => Usable(_store.Get(ids.Fingerprint), UnderstandingKind.Heading)
                    ?? Usable(_store.LatestByIdentity(UnderstandingKind.Heading, ids.Identity), UnderstandingKind.Heading));
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
        if (Read(() => Usable(_store.Get(fingerprint), UnderstandingKind.Note)) is not { } entry) return null;
        _noteVectors[fingerprint] = entry.Vector!;
        return entry.Vector;
    }

    /// <summary>Runs a store read; a closed or failing store gives null.</summary>
    private static UnderstandingEntry? Read(Func<UnderstandingEntry?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SqliteException)
        {
            return null;
        }
    }

    /// <summary>The entry if it is of the kind and has a vector from the current embed model, else null.</summary>
    private UnderstandingEntry? Usable(UnderstandingEntry? entry, UnderstandingKind kind) =>
        entry is { Vector: not null } && entry.Kind == kind && entry.EmbedModel == _embedModel ? entry : null;
}
