namespace NoteEvolution.AI.Model;

/// <summary>One downloadable file of a model; <see cref="RelativePath"/> is also its place below the model directory.</summary>
public sealed record ModelFile(string RelativePath, string Url, string Sha256, long Size);

public sealed record ModelInfo(string Id, string DisplayName, IReadOnlyList<ModelFile> Files, int Dimensions, int MaxTokens)
{
    public long TotalSize => Files.Sum(f => f.Size);
}

/// <summary>The models the app knows. Every file is pinned to a fixed revision and verified by SHA-256.</summary>
public static class ModelCatalog
{
    // Commit of https://huggingface.co/Xenova/multilingual-e5-small (never "main": the files must not change under us).
    private const string Revision = "761b726dd34fb83930e26aab4e9ac3899aa1fa78";

    public static ModelInfo E5Small { get; } = new(
        "multilingual-e5-small-int8",
        "multilingual-e5-small (int8)",
        [
            Pinned("onnx/model_quantized.onnx", "f80102d3f2a1229f387d3c81909990d8945513e347b0eab049f7de3c6f98c193", 118_308_185),
            Pinned("sentencepiece.bpe.model", "cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865", 5_069_051),
        ],
        Dimensions: 384,
        MaxTokens: 512);

    private static ModelFile Pinned(string path, string sha256, long size) =>
        new(path, $"https://huggingface.co/Xenova/multilingual-e5-small/resolve/{Revision}/{path}", sha256, size);
}
