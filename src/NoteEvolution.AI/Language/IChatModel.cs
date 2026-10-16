namespace NoteEvolution.AI.Language;

/// <summary>
/// Per-request generation settings: answer length cap, sampling temperature, whether the answer must be a JSON object,
/// and optionally the JSON schema it must follow (implies <paramref name="Json"/>).
/// </summary>
public sealed record ChatOptions(int MaxTokens, double Temperature = 0.2, bool Json = false, string? Schema = null);

/// <summary>A model installed on the language-model server.</summary>
public sealed record ChatModelInfo(string Name, long SizeBytes);

/// <summary>One progress line of a model pull; <c>Total</c> is 0 while the server does not report a size.</summary>
public sealed record PullProgress(string Status, long Completed, long Total);

/// <summary>A local language-model server (Ollama): reachability, installed models, chat completion and model download.</summary>
public interface IChatModel
{
    /// <summary>The server version, or <c>null</c> when the server is unreachable.</summary>
    Task<string?> VersionAsync(CancellationToken ct);

    Task<IReadOnlyList<ChatModelInfo>> ModelsAsync(CancellationToken ct);

    /// <summary>One non-streamed chat turn; returns the answer text. Throws <see cref="ChatModelUnavailableException"/> when the server fails.</summary>
    Task<string> ChatAsync(string model, string system, string user, ChatOptions options, CancellationToken ct);

    /// <summary>Downloads a model, reporting progress; returns once the server reports success.</summary>
    Task PullAsync(string model, IProgress<PullProgress>? progress, CancellationToken ct);
}

/// <summary>The language-model server is unreachable, answered with an error, or reported a failure.</summary>
public sealed class ChatModelUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
