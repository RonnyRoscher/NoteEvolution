namespace NoteEvolution.UI.State;

/// <summary>
/// Keeps the cards with an open "Wohin damit?" list (<see cref="AppState.WhereToOpenNotes"/>) at their place when a tab
/// of the notes pane computes its list again (package B, spec 3: the list stays open, an adopted note does not
/// disappear from it), also when the new list (its top N, or "Verwendete ausblenden") would leave them out.
/// </summary>
public static class WhereToPins
{
    /// <summary>
    /// The list to show instead of <paramref name="shown"/>: <paramref name="fresh"/>, with every entry of
    /// <paramref name="shown"/> whose note has an open list (<paramref name="open"/>) at its former index (its entry
    /// from <paramref name="fresh"/> if there is one, else the former entry), in the order of those indexes.
    /// </summary>
    /// <returns>The list, and the keys of the entries it only holds because their list is open.</returns>
    public static (List<T> Items, HashSet<Guid> KeptOnly) Merge<T>(
        IReadOnlyList<T> shown, IReadOnlyList<T> fresh, Func<T, Guid> keyOf, IReadOnlySet<Guid> open)
    {
        var pinned = shown.Select((item, index) => (Item: item, Index: index)).Where(entry => open.Contains(keyOf(entry.Item))).ToList();
        if (pinned.Count == 0)
        {
            return ([.. fresh], []);
        }

        var freshByKey = new Dictionary<Guid, T>();
        foreach (var item in fresh)
        {
            freshByKey.TryAdd(keyOf(item), item);
        }

        var pinnedKeys = pinned.Select(entry => keyOf(entry.Item)).ToHashSet();
        var items = fresh.Where(item => !pinnedKeys.Contains(keyOf(item))).ToList();
        var keptOnly = new HashSet<Guid>();
        foreach (var (item, index) in pinned)
        {
            var key = keyOf(item);
            if (!freshByKey.TryGetValue(key, out var current))
            {
                current = item;
                keptOnly.Add(key);
            }

            items.Insert(Math.Min(index, items.Count), current);
        }

        return (items, keptOnly);
    }

    /// <summary>
    /// Removes from <paramref name="items"/> the entries kept only for a list (<paramref name="keptOnly"/>, see
    /// <see cref="Merge{T}"/>) that is closed now, and forgets them in <paramref name="keptOnly"/>.
    /// </summary>
    /// <returns>Whether an entry was removed.</returns>
    public static bool Release<T>(List<T> items, HashSet<Guid> keptOnly, Func<T, Guid> keyOf, IReadOnlySet<Guid> open)
    {
        if (keptOnly.All(open.Contains))
        {
            return false;
        }

        items.RemoveAll(item => keptOnly.Contains(keyOf(item)) && !open.Contains(keyOf(item)));
        keptOnly.IntersectWith(open);
        return true;
    }
}
