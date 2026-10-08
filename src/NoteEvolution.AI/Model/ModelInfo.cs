namespace NoteEvolution.AI.Model;

/// <summary>One downloadable file of a model; <see cref="RelativePath"/> is also its place below the model directory.</summary>
public sealed record ModelFile(string RelativePath, string Url, string Sha256, long Size);

/// <summary>How the token vectors of the encoder are condensed into one sentence vector.</summary>
public enum Pooling
{
    /// <summary>Mean over all (non-padding) tokens.</summary>
    Mean,
    /// <summary>The vector of the first ([CLS]) token.</summary>
    Cls,
}

public sealed record ModelInfo(
    string Id,
    string DisplayName,
    IReadOnlyList<ModelFile> Files,
    int Dimensions,
    int MaxTokens,
    Pooling Pooling = Pooling.Mean,
    string QueryPrefix = "query: ",
    string PassagePrefix = "passage: ")
{
    public long TotalSize => Files.Sum(f => f.Size);

    /// <summary>Rough peak RAM while embedding: twice the largest file (weights plus runtime copy) and 150 MB for the rest.</summary>
    public long MemoryEstimate => 2 * Files.Max(f => f.Size) + 150_000_000;
}

/// <summary>The models the app knows. Every file is pinned to a fixed revision and verified by SHA-256.</summary>
public static class ModelCatalog
{
    private const string SentencePieceSha256 = "cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865";
    private const long SentencePieceSize = 5_069_051;

    // Revisions are commits of https://huggingface.co/Xenova/<repo> (never "main": the files must not change under us).
    public static ModelInfo E5Small { get; } = new(
        "multilingual-e5-small-int8",
        "e5-small",
        [
            Pinned("multilingual-e5-small", "761b726dd34fb83930e26aab4e9ac3899aa1fa78",
                "onnx/model_quantized.onnx", "f80102d3f2a1229f387d3c81909990d8945513e347b0eab049f7de3c6f98c193", 118_308_185),
            Pinned("multilingual-e5-small", "761b726dd34fb83930e26aab4e9ac3899aa1fa78",
                "sentencepiece.bpe.model", SentencePieceSha256, SentencePieceSize),
        ],
        Dimensions: 384,
        MaxTokens: 512);

    public static ModelInfo E5Base { get; } = new(
        "multilingual-e5-base-int8",
        "e5-base",
        [
            Pinned("multilingual-e5-base", "1ec9243030a27d1a115d5c340572074c125b58b2",
                "onnx/model_quantized.onnx", "df7a9a29309e3ad491e1783adf8baee710262cc06079c7cbab63c630277fac94", 278_647_662),
            Pinned("multilingual-e5-base", "1ec9243030a27d1a115d5c340572074c125b58b2",
                "sentencepiece.bpe.model", SentencePieceSha256, SentencePieceSize),
        ],
        Dimensions: 768,
        MaxTokens: 512);

    public static ModelInfo E5Large { get; } = new(
        "multilingual-e5-large-int8",
        "e5-large",
        [
            Pinned("multilingual-e5-large", "00fc3aeb3dbb95842de2ac1961d33c6319acf57b",
                "onnx/model_quantized.onnx", "0a8d65db9a36f810ba5da15249f13145fcdc7890e6656f1fd38cd8b7c4db1fca", 561_768_762),
            Pinned("multilingual-e5-large", "00fc3aeb3dbb95842de2ac1961d33c6319acf57b",
                "sentencepiece.bpe.model", SentencePieceSha256, SentencePieceSize),
        ],
        Dimensions: 1024,
        MaxTokens: 512);

    public static ModelInfo BgeM3 { get; } = new(
        "bge-m3-int8",
        "bge-m3",
        [
            Pinned("bge-m3", "4de13258303883538bd53b696b452bf8099f0858",
                "onnx/model_quantized.onnx", "0826f8c1ab9edf1801db86c61919d4d108e8bfc0b809ec823ad366882ff0b77d", 569_694_530),
            Pinned("bge-m3", "4de13258303883538bd53b696b452bf8099f0858",
                "sentencepiece.bpe.model", SentencePieceSha256, SentencePieceSize),
        ],
        Dimensions: 1024,
        MaxTokens: 512,
        Pooling: Pooling.Cls,
        QueryPrefix: "",
        PassagePrefix: "");

    /// <summary>All selectable models; the first one is the default.</summary>
    public static IReadOnlyList<ModelInfo> All { get; } = [E5Small, E5Base, E5Large, BgeM3];

    public static ModelInfo Default => E5Small;

    /// <summary>The catalog entry with this id, or null for an unknown or missing id.</summary>
    public static ModelInfo? Find(string? id) => All.FirstOrDefault(m => m.Id == id);

    private static ModelFile Pinned(string repo, string revision, string path, string sha256, long size) =>
        new(path, $"https://huggingface.co/Xenova/{repo}/resolve/{revision}/{path}", sha256, size);
}
