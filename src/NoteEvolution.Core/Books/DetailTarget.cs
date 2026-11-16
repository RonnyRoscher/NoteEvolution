namespace NoteEvolution.Core.Books;

/// <summary>Where <see cref="ManuscriptEditor.MoveDetail"/> puts a detail (sub-point) with the details below it.</summary>
public abstract record DetailTarget
{
    private DetailTarget()
    {
    }

    /// <summary>
    /// First sub-point (depth 1) of the text block. The detail's own text block is a target as well: a detail that is
    /// not its first sub-point moves to the front; the first sub-point itself is already there.
    /// </summary>
    public sealed record FirstOf(Guid TextBlockKey) : DetailTarget;

    /// <summary>Directly after the detail and its deeper details, at the same depth.</summary>
    public sealed record After(Guid DetailKey) : DetailTarget;

    /// <summary>A text block of its own: first text block of the section (<see cref="Guid.Empty"/> = prologue).</summary>
    public sealed record SectionStart(Guid SectionKey) : DetailTarget;

    /// <summary>A text block of its own: last text block of the section (<see cref="Guid.Empty"/> = prologue).</summary>
    public sealed record SectionEnd(Guid SectionKey) : DetailTarget;
}
