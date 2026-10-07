using System.Text.Json;
using NoteEvolution.Core.Format;

namespace NoteEvolution.Core.Vaults;

/// <summary>Per-vault settings stored in <c>.noteevolution/settings.json</c>; missing or damaged files give defaults.</summary>
public sealed class VaultSettings
{
    public const string FolderName = ".noteevolution";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Folders (relative to the vault root) searched recursively for <c>*.md</c> note files.</summary>
    public List<string> NoteFolders { get; set; } = ["journals", "pages"];

    public static VaultSettings Load(string root)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<VaultSettings>(File.ReadAllBytes(PathOf(root)), Json);
            if (settings is { NoteFolders: not null })
            {
                settings.NoteFolders = [.. settings.NoteFolders.Where(f => !string.IsNullOrWhiteSpace(f))];
                return settings;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Settings are an add-on; fall back to defaults.
        }
        return new VaultSettings();
    }

    public void Save(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, FolderName));
        AtomicFile.Write(PathOf(root), JsonSerializer.SerializeToUtf8Bytes(this, Json));
    }

    private static string PathOf(string root) => Path.Combine(root, FolderName, "settings.json");
}
