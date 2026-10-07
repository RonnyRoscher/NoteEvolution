using NoteEvolution.AI.Embeddings;

namespace NoteEvolution.AI.Semantic;

/// <summary>
/// In-memory index of embeddings for nearest-neighbour queries by cosine similarity (linear scan, which is plenty for
/// the few thousand blocks of a vault). Safe to call from several threads; stored vectors are never shared with callers.
/// </summary>
public sealed class VectorIndex
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly Dictionary<string, HashSet<Guid>> _keysByPage = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Entry(string Page, float[] Vector);

    public int Count
    {
        get
        {
            lock (_gate) return _entries.Count;
        }
    }

    /// <summary>Adds or replaces the vector of <paramref name="key"/>; the key now belongs to <paramref name="pagePath"/>.</summary>
    public void Set(Guid key, string pagePath, float[] vector)
    {
        var entry = new Entry(pagePath, (float[])vector.Clone());
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var old)) Detach(key, old.Page);
            _entries[key] = entry;
            if (!_keysByPage.TryGetValue(pagePath, out var keys)) _keysByPage[pagePath] = keys = [];
            keys.Add(key);
        }
    }

    /// <summary>Removes every vector of the page; the path is compared ignoring case.</summary>
    public void RemovePage(string pagePath)
    {
        lock (_gate)
        {
            if (!_keysByPage.Remove(pagePath, out var keys)) return;
            foreach (var key in keys) _entries.Remove(key);
        }
    }

    /// <summary>A copy of the page's stored vectors by key (empty if the page has none); the path is compared ignoring case.</summary>
    public Dictionary<Guid, float[]> VectorsOfPage(string pagePath)
    {
        lock (_gate)
        {
            if (!_keysByPage.TryGetValue(pagePath, out var keys)) return [];
            return keys.ToDictionary(key => key, key => (float[])_entries[key].Vector.Clone());
        }
    }

    public void Remove(Guid key)
    {
        lock (_gate)
        {
            if (!_entries.Remove(key, out var entry)) return;
            Detach(key, entry.Page);
        }
    }

    /// <summary>A copy of the stored vector, or null if the key is not indexed.</summary>
    public float[]? Get(Guid key)
    {
        lock (_gate) return _entries.TryGetValue(key, out var entry) ? (float[])entry.Vector.Clone() : null;
    }

    /// <summary>
    /// The up to <paramref name="k"/> entries most similar to <paramref name="query"/>, best first. Entries with a
    /// similarity of 0 or less (this includes zero vectors) and entries of another dimension are left out.
    /// <paramref name="include"/> runs outside the lock, so a slow predicate never blocks writers.
    /// </summary>
    public IReadOnlyList<(Guid Key, float Score)> Nearest(float[] query, int k, Func<Guid, bool> include)
    {
        if (k <= 0) return [];

        KeyValuePair<Guid, Entry>[] snapshot;
        lock (_gate) snapshot = [.. _entries];

        // min-heap of the best k so far: the weakest of them sits on top
        var best = new PriorityQueue<Guid, float>();
        foreach (var (key, entry) in snapshot)
        {
            if (entry.Vector.Length != query.Length) continue;
            var score = VectorMath.Cosine(query, entry.Vector);
            if (score <= 0) continue;
            if (best.Count >= k && best.TryPeek(out _, out var weakest) && score <= weakest) continue;
            if (!include(key)) continue;

            if (best.Count >= k) best.Dequeue();
            best.Enqueue(key, score);
        }

        var result = new List<(Guid Key, float Score)>(best.Count);
        while (best.TryDequeue(out var key, out var score)) result.Add((key, score));
        result.Reverse();
        return result;
    }

    private void Detach(Guid key, string page)
    {
        if (!_keysByPage.TryGetValue(page, out var keys)) return;
        keys.Remove(key);
        if (keys.Count == 0) _keysByPage.Remove(page);
    }
}
