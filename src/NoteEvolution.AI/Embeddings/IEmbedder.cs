namespace NoteEvolution.AI.Embeddings;

/// <summary>Turns texts into embedding vectors.</summary>
public interface IEmbedder
{
    int Dimensions { get; }

    /// <summary>
    /// Embeds <paramref name="texts"/>, which already carry their e5 prefix (<c>query: </c> or <c>passage: </c>).
    /// Returns one L2-normalized vector of <see cref="Dimensions"/> per text, in order; an empty or whitespace text gives a zero vector.
    /// </summary>
    IReadOnlyList<float[]> Embed(IReadOnlyList<string> texts, CancellationToken ct = default);
}
