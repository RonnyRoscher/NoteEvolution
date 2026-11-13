using NoteEvolution.AI.Embeddings;

namespace NoteEvolution.AI.Relevance;

/// <summary>
/// Finds the note already used in a book that says nearly the same as an open note, by comparing their embeddings
/// (both are notes, so both carry the same role marker and their similarity is comparable).
/// </summary>
public static class NoteDuplicates
{
    /// <summary>From this similarity on an open note counts as saying nearly the same as a used one. An estimate, to be set from the logged scores.</summary>
    public const float Threshold = 0.92f;

    /// <summary>From this similarity on a pair is worth logging, so that <see cref="Threshold"/> can be judged from below as well.</summary>
    public const float NearThreshold = 0.85f;

    public static bool IsDuplicate(float score) => score >= Threshold;

    /// <summary>
    /// The used note most similar to <paramref name="note"/> with its cosine similarity, the first of equals; null if
    /// none can be compared (another dimension, no meaning) or none is similar at all.
    /// </summary>
    public static (Guid Key, float Score)? Nearest(float[] note, IEnumerable<(Guid Key, float[] Vector)> used)
    {
        (Guid Key, float Score)? best = null;
        foreach (var (key, vector) in used)
        {
            if (vector.Length != note.Length)
            {
                continue;
            }

            var score = VectorMath.Cosine(note, vector);
            if (score > 0 && (best is null || score > best.Value.Score))
            {
                best = (key, score);
            }
        }

        return best;
    }
}
