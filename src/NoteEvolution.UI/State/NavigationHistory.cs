namespace NoteEvolution.UI.State;

/// <summary>
/// An active block in the history (spec 3): the book by link name and the element's key (a heading, text block or
/// detail; <see cref="Guid.Empty"/> = the start of the book).
/// </summary>
public sealed record NavEntry(string BookLink, Guid ElementKey);

/// <summary>
/// The back/forward history of the active blocks for the running session (spec 3), like a browser's: a new entry after
/// going back drops the forward entries, two equal entries in a row are kept once, and the oldest entries fall away
/// beyond the capacity. Not thread-safe; <see cref="AppState"/> guards it.
/// </summary>
public sealed class NavigationHistory(int capacity = 50)
{
    /// <summary>How long the cursor has to stay in another block before that block is recorded.</summary>
    public static readonly TimeSpan DwellTime = TimeSpan.FromSeconds(2);

    private readonly List<NavEntry> _entries = [];

    /// <summary>The index of the current entry in <see cref="_entries"/>; −1 while empty.</summary>
    private int _index = -1;

    /// <summary>The entry the history stands at; <c>null</c> while empty.</summary>
    public NavEntry? Current => _index >= 0 ? _entries[_index] : null;

    public bool CanGoBack => _index > 0;

    public bool CanGoForward => _index < _entries.Count - 1;

    /// <summary>
    /// Makes <paramref name="entry"/> the current entry, after the current one, dropping the forward entries; a duplicate
    /// of the current entry changes nothing (the forward entries stay).
    /// </summary>
    public void Record(NavEntry entry)
    {
        if (entry == Current)
        {
            return;
        }

        _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);
        _entries.Add(entry);
        if (_entries.Count > capacity)
        {
            _entries.RemoveRange(0, _entries.Count - capacity);
        }

        _index = _entries.Count - 1;
    }

    /// <summary>
    /// Steps back to the nearest earlier entry that <paramref name="exists"/>, removing the ones that do not; <c>null</c>
    /// (staying) if there is none.
    /// </summary>
    public NavEntry? Back(Func<NavEntry, bool> exists)
    {
        while (_index > 0)
        {
            var candidate = _entries[_index - 1];
            if (exists(candidate) && candidate != _entries[_index])
            {
                _index--;
                return candidate;
            }

            // Gone, or equal to the current entry after a removal: dropped, so no duplicates follow each other.
            _entries.RemoveAt(_index - 1);
            _index--;
        }

        return null;
    }

    /// <summary>
    /// Steps forward to the nearest later entry that <paramref name="exists"/>, removing the ones that do not; <c>null</c>
    /// (staying) if there is none.
    /// </summary>
    public NavEntry? Forward(Func<NavEntry, bool> exists)
    {
        while (_index < _entries.Count - 1)
        {
            var candidate = _entries[_index + 1];
            if (exists(candidate) && candidate != _entries[_index])
            {
                _index++;
                return candidate;
            }

            _entries.RemoveAt(_index + 1);
        }

        return null;
    }

    public void Clear()
    {
        _entries.Clear();
        _index = -1;
    }
}
