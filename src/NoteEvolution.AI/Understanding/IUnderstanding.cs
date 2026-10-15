using NoteEvolution.Core.Books;

namespace NoteEvolution.AI.Understanding;

/// <summary>
/// The language model's understanding as the relevance search reads it: topic summaries of headings and core statements
/// of notes, each with its meaning vector from the current embedding model. Safe to call from several threads; works on
/// <see cref="Book"/> snapshots and session keys, never on the vault model.
/// </summary>
public interface IUnderstanding
{
    /// <summary>
    /// The heading's topic summary with its vector: the entry for its current content, else the newest entry of the same
    /// heading (stale), else null.
    /// </summary>
    (string Text, float[] Vector)? Heading(Book book, OutlineNode heading);

    /// <summary>The vector of the note's core statement for its current text, or null (none yet, or the note is too short).</summary>
    float[]? NoteCore(Guid noteKey);

    /// <summary>The core-statement vectors of all notes that have one, by session key.</summary>
    IReadOnlyList<(Guid Key, float[] Vector)> NoteCores();
}
