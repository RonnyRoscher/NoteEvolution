namespace NoteEvolution.UI.State;

/// <summary>What the local AI is doing, as the header shows it.</summary>
public enum AiState
{
    /// <summary>The app runs without an AI runtime.</summary>
    Off,

    /// <summary>The model is not installed; search is full text only.</summary>
    ModelMissing,

    /// <summary>The model is being downloaded.</summary>
    Downloading,

    /// <summary>The notes are being embedded; queries may already give partial results.</summary>
    Indexing,

    /// <summary>Every note is indexed.</summary>
    Ready,

    /// <summary>The model could not be loaded or the indexing failed; search is full text only.</summary>
    Failed,
}

/// <param name="Done">Notes indexed so far (while <see cref="AiState.Indexing"/> or <see cref="AiState.Ready"/>).</param>
/// <param name="Total">Notes to index.</param>
/// <param name="Error">The failure's message when <paramref name="State"/> is <see cref="AiState.Failed"/>.</param>
public sealed record AiStatus(AiState State, int Done, int Total, string? Error)
{
    public static AiStatus Off { get; } = new(AiState.Off, 0, 0, null);

    public static AiStatus Of(AiState state) => new(state, 0, 0, null);

    public static AiStatus Failed(string error) => new(AiState.Failed, 0, 0, error);
}
