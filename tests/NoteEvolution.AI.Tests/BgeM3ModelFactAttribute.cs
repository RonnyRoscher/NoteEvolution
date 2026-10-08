using NoteEvolution.AI.Model;

namespace NoteEvolution.AI.Tests;

/// <summary>
/// A fact that runs against the real installed bge-m3 model. The model directory is
/// <c>%APPDATA%/NoteEvolution/models/bge-m3-int8</c>, or <c>NOTEEVOLUTION_BGE_M3_DIR</c> when set.
/// The test is skipped when a catalogued model file is missing there.
/// </summary>
public sealed class BgeM3ModelFactAttribute : FactAttribute
{
    public static ModelInfo Model => ModelCatalog.BgeM3;

    public static string ModelDirectory { get; } =
        Environment.GetEnvironmentVariable("NOTEEVOLUTION_BGE_M3_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "NoteEvolution", "models", ModelCatalog.BgeM3.Id);

    public BgeM3ModelFactAttribute() => Skip = MissingReason();

    private static string? MissingReason()
    {
        var missing = Model.Files
            .Select(f => Path.Combine(ModelDirectory, f.RelativePath.Replace('/', Path.DirectorySeparatorChar)))
            .FirstOrDefault(p => !File.Exists(p));
        return missing is null ? null : $"Real model file missing: {missing}";
    }
}
