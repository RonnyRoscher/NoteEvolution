namespace NoteEvolution.Core.Links;

/// <summary>
/// Thrown by an undo that went on after pages could not be written (or are read-only now): the <see cref="Paths"/> are
/// not (fully) taken back yet; what is left waits in <c>pending.json</c> where it can. The other pages are taken back.
/// </summary>
public sealed class PagesNotRestoredException(IReadOnlyList<string> paths)
    : InvalidOperationException($"Nicht vollständig zurückgenommen, weil nicht schreibbar: {string.Join(", ", paths)}")
{
    /// <summary>The full paths of the pages that are not (fully) taken back.</summary>
    public IReadOnlyList<string> Paths { get; } = paths;
}
