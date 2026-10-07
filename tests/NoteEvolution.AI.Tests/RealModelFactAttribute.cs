using NoteEvolution.AI.Model;

namespace NoteEvolution.AI.Tests;

/// <summary>
/// A fact that runs against the real installed e5 model. The model directory is
/// <c>%APPDATA%/NoteEvolution/models/multilingual-e5-small-int8</c>, or <c>NOTEEVOLUTION_MODEL_DIR</c> when set.
/// The test is skipped when a catalogued model file (or, with <see cref="NeedsTokenizerJson"/>, the test-only
/// <c>tokenizer.json</c>) is missing there.
/// </summary>
public sealed class RealModelFactAttribute : FactAttribute
{
    public static ModelInfo Model => ModelCatalog.E5Small;

    public static string ModelDirectory { get; } =
        Environment.GetEnvironmentVariable("NOTEEVOLUTION_MODEL_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "NoteEvolution", "models", ModelCatalog.E5Small.Id);

    /// <summary>The Hugging Face fast tokenizer of the same revision (ruling S1: test-only, never checked in).</summary>
    public static string TokenizerJsonPath => Path.Combine(ModelDirectory, "tokenizer.json");

    public static string PathOf(string relativePath) =>
        Path.Combine(ModelDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public static string SentencePiecePath => PathOf(Model.Files.Single(f => f.RelativePath.EndsWith(".model")).RelativePath);

    public RealModelFactAttribute() => Skip = MissingReason(needsTokenizerJson: false);

    public bool NeedsTokenizerJson
    {
        get;
        set
        {
            field = value;
            Skip = MissingReason(value);
        }
    }

    private static string? MissingReason(bool needsTokenizerJson)
    {
        var required = Model.Files.Select(f => PathOf(f.RelativePath));
        if (needsTokenizerJson) required = required.Append(TokenizerJsonPath);
        var missing = required.FirstOrDefault(p => !File.Exists(p));
        return missing is null ? null : $"Real model file missing: {missing}";
    }
}
