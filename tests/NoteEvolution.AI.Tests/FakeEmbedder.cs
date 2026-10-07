using NoteEvolution.AI.Embeddings;

namespace NoteEvolution.AI.Tests;

/// <summary>
/// Deterministic stand-in for the ONNX embedder: a text's vector is the L2-normalized histogram of its characters
/// (after the e5 prefix) over <see cref="Dims"/> dimensions, so a query and a passage with the same text are identical.
/// Records every call. It ignores the cancellation token; <see cref="OnEmbed"/> can block or cancel instead.
/// Thread-safe.
/// </summary>
public sealed class FakeEmbedder : IEmbedder
{
    public const int Dims = 16;

    private readonly Lock _gate = new();
    private readonly List<IReadOnlyList<string>> _batches = [];

    public int Dimensions => Dims;

    /// <summary>When true, every call throws <see cref="InvalidOperationException"/> with <see cref="FailureMessage"/>.</summary>
    public bool Throws { get; set; }

    public const string FailureMessage = "fake embedder failure";

    /// <summary>Runs at the start of every call with the 1-based call number and the call's token.</summary>
    public Action<int, CancellationToken>? OnEmbed { get; set; }

    /// <summary>The texts of every call so far, in call order (also calls that threw).</summary>
    public IReadOnlyList<IReadOnlyList<string>> Batches
    {
        get
        {
            lock (_gate) return [.. _batches];
        }
    }

    public int Calls => Batches.Count;

    public IReadOnlyList<string> Texts => [.. Batches.SelectMany(b => b)];

    public IReadOnlyList<float[]> Embed(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        int call;
        lock (_gate)
        {
            _batches.Add([.. texts]);
            call = _batches.Count;
        }

        OnEmbed?.Invoke(call, ct);
        if (Throws) throw new InvalidOperationException(FailureMessage);
        return [.. texts.Select(VectorFor)];
    }

    /// <summary>The vector this fake returns for <paramref name="text"/>; a zero vector for an empty text.</summary>
    public static float[] VectorFor(string text)
    {
        foreach (var prefix in new[] { EmbeddingText.PassagePrefix, EmbeddingText.QueryPrefix })
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                text = text[prefix.Length..];
                break;
            }
        }

        var vector = new float[Dims];
        foreach (var c in text.Where(c => !char.IsWhiteSpace(c))) vector[c % Dims] += 1;
        var length = MathF.Sqrt(vector.Sum(x => x * x));
        if (length > 0)
        {
            for (var i = 0; i < Dims; i++) vector[i] /= length;
        }

        return vector;
    }
}
