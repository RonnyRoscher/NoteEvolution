namespace NoteEvolution.AI.Model;

/// <summary>Where the model files live: <c>&lt;userDataDirectory&gt;/models/&lt;model id&gt;/</c> (in the user profile, not in the vault).</summary>
public sealed class ModelStore(string userDataDirectory)
{
    public string DirectoryOf(ModelInfo model) => Path.Combine(userDataDirectory, "models", model.Id);

    public string PathOf(ModelInfo model, ModelFile file) =>
        Path.Combine(DirectoryOf(model), file.RelativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>True when every file exists with its catalogued size. No hashing here (too slow at startup); the download verifies the hash.</summary>
    public bool IsInstalled(ModelInfo model) => model.Files.All(f => HasSize(PathOf(model, f), f.Size));

    /// <summary>
    /// Deletes every folder under <c>models/</c> except the one of <paramref name="keep"/>. Returns one <c>"&lt;folder&gt;: &lt;message&gt;"</c>
    /// per folder that could not be deleted (the rest is still processed); never throws for IO or permission errors.
    /// </summary>
    public IReadOnlyList<string> DeleteAllExcept(ModelInfo keep)
    {
        var modelsRoot = Path.Combine(userDataDirectory, "models");
        var errors = new List<string>();
        try
        {
            if (!Directory.Exists(modelsRoot))
            {
                return errors;
            }

            foreach (var folder in Directory.GetDirectories(modelsRoot))
            {
                var name = Path.GetFileName(folder);
                if (string.Equals(name, keep.Id, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (TryDelete(folder) is { } error)
                {
                    errors.Add(error);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"models: {ex.Message}");   // the folder listing itself failed
        }

        return errors;
    }

    /// <summary>Deletes the folder of <paramref name="model"/>. Null on success or when it does not exist, else <c>"&lt;folder&gt;: &lt;message&gt;"</c>; never throws for IO or permission errors.</summary>
    public string? Delete(ModelInfo model) => TryDelete(DirectoryOf(model));

    private static string? TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{Path.GetFileName(folder)}: {ex.Message}";
        }
    }

    internal static bool HasSize(string path, long size)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length == size;
    }
}
