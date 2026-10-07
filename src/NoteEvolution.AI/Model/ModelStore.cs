namespace NoteEvolution.AI.Model;

/// <summary>Where the model files live: <c>&lt;userDataDirectory&gt;/models/&lt;model id&gt;/</c> (in the user profile, not in the vault).</summary>
public sealed class ModelStore(string userDataDirectory)
{
    public string DirectoryOf(ModelInfo model) => Path.Combine(userDataDirectory, "models", model.Id);

    public string PathOf(ModelInfo model, ModelFile file) =>
        Path.Combine(DirectoryOf(model), file.RelativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>True when every file exists with its catalogued size. No hashing here (too slow at startup); the download verifies the hash.</summary>
    public bool IsInstalled(ModelInfo model) => model.Files.All(f => HasSize(PathOf(model, f), f.Size));

    internal static bool HasSize(string path, long size)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length == size;
    }
}
