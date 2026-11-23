namespace NoteEvolution.UI.Editor;

/// <summary>How a repetition is marked in the text (repetitions tab spec 3).</summary>
public enum RepetitionMarkKind
{
    /// <summary>A shared run of words: underlined.</summary>
    Literal,

    /// <summary>A sentence that says the same as one elsewhere: underlined dotted, in a second colour.</summary>
    Semantic,

    /// <summary>What makes the current hit of the repetitions tab a repetition: highlighted, its block gets a border.</summary>
    Current,
}

/// <summary>A stretch of text the editor marks as a repetition, as a decoration.</summary>
/// <param name="ParagraphKey">The text block (its own text) or detail that holds it.</param>
/// <param name="Start">The offset in the paragraph's text as the editor shows it (as <see cref="GlossaryMark.Start"/>).</param>
/// <param name="Length">The length in the same units.</param>
/// <param name="Kind">How it is marked.</param>
/// <param name="Title">The tooltip: the other places of the repetition.</param>
public sealed record RepetitionTextMark(Guid ParagraphKey, int Start, int Length, RepetitionMarkKind Kind, string Title);
