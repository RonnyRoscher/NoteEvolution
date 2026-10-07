using NoteEvolution.Core.Vaults;

namespace NoteEvolution.AI.Semantic;

public enum SemanticState
{
    Off,
    Indexing,
    Ready,
    Failed,
}

/// <param name="Done">Notes indexed so far (cache hits included).</param>
/// <param name="Total">Notes of the vault when indexing started.</param>
/// <param name="Error">The failure's message when <paramref name="State"/> is <see cref="SemanticState.Failed"/>.</param>
public record SemanticStatus(SemanticState State, int Done, int Total, string? Error);

/// <summary>The embeddings of all note blocks, kept up to date in the background and queried by the UI.</summary>
public interface ISemanticIndex
{
    SemanticStatus Status { get; }

    /// <summary>Raised whenever <see cref="Status"/> changes, on the background thread that does the indexing.</summary>
    event Action? StatusChanged;

    /// <summary>Indexes every note, reusing cached embeddings of unchanged texts.</summary>
    Task RebuildAsync(INoteRepository notes, CancellationToken ct);

    /// <summary>Replaces the embeddings of one page's notes (after a save or an external change, or when it was removed).</summary>
    Task UpdatePageAsync(INoteRepository notes, string pagePath, CancellationToken ct);

    /// <summary>
    /// Whether queries give useful answers: <see cref="SemanticState.Ready"/>, or <see cref="SemanticState.Indexing"/>
    /// with at least one note indexed (partial results are allowed).
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// The up to <paramref name="k"/> indexed notes most similar to <paramref name="query"/>, best first. Runs on a
    /// snapshot and never waits for indexing, so a key may already have been removed; resolve keys through the repository.
    /// </summary>
    IReadOnlyList<(Guid Key, float Score)> Nearest(float[] query, int k, Func<Guid, bool> include);

    /// <summary>The note's embedding, or null if it is not indexed (yet).</summary>
    float[]? VectorOf(Guid noteKey);

    /// <summary>
    /// Embeds <paramref name="text"/>, which already carries its e5 prefix (see <c>EmbeddingText</c>). Null when the
    /// embedder fails; the failure is logged and <see cref="Status"/> stays as it is.
    /// </summary>
    float[]? EmbedQuery(string text);
}
