namespace NoteEvolution.Core.Links;

/// <summary>
/// Thrown by an undo that restored every page it could but left the <see cref="Paths"/> as they are, because they were
/// changed since the operation (or are no longer in the vault, or are read-only). The other pages are restored.
/// </summary>
public sealed class PagesNotRestoredException(IReadOnlyList<string> paths)
    : InvalidOperationException($"Nicht zurückgenommen, weil inzwischen geändert: {string.Join(", ", paths)}")
{
    /// <summary>The full paths of the pages that were left as they are.</summary>
    public IReadOnlyList<string> Paths { get; } = paths;
}
