using System.Text.Json;
using System.Text.Json.Serialization;
using NoteEvolution.Core.Format;

namespace NoteEvolution.UI.State;

public enum ThemeChoice
{
    /// <summary>Follows the system setting (<c>prefers-color-scheme</c>).</summary>
    System,
    Light,
    Dark,
}

/// <summary>The user's layout and display choices, kept in <c>ui.json</c> in <see cref="Platform.IPlatformServices.UserDataDirectory"/>.</summary>
public sealed class UiSettings
{
    public const string FileName = "ui.json";

    public const int MinFontSizePt = 10;

    public const int MaxFontSizePt = 20;

    public const int MinLineWidthCh = 50;

    public const int MaxLineWidthCh = 100;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The vault folder opened last; opened again on start.</summary>
    public string? LastVault { get; set; }

    public double OutlineWidth { get; set; } = 260;

    public double NotesWidth { get; set; } = 380;

    public bool OutlineCollapsed { get; set; }

    public bool NotesCollapsed { get; set; }

    public ThemeChoice Theme { get; set; } = ThemeChoice.System;

    public int FontSizePt { get; set; } = 12;

    public int LineWidthCh { get; set; } = 70;

    /// <summary>The notes pane hides notes that are already used in a book.</summary>
    public bool HideUsed { get; set; }

    /// <summary>The hint not to edit the same book in Logseq at the same time has been shown.</summary>
    public bool LogseqHintShown { get; set; }

    /// <summary>The user chose "not now" in the AI model dialog: it is no longer shown after opening a vault.</summary>
    public bool AiModelDeclined { get; set; }

    /// <summary>The id of the AI model in use (<see cref="AI.Model.ModelInfo.Id"/>); null for the catalog's default.</summary>
    public string? AiModelId { get; set; }

    /// <summary>
    /// The book chosen last in each vault: the vault folder (full path) to the book page's path relative to it, with
    /// <c>/</c> separators. Use <see cref="LastBookOf"/> and <see cref="RememberBook"/>.
    /// </summary>
    public Dictionary<string, string> LastBooks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The book page chosen last in <paramref name="vaultRoot"/>, relative to it; null if none was chosen there.</summary>
    public string? LastBookOf(string vaultRoot) => LastBooks.GetValueOrDefault(VaultKey(vaultRoot));

    /// <summary>Remembers <paramref name="pagePath"/> (a file inside <paramref name="vaultRoot"/>) as the vault's book.</summary>
    public void RememberBook(string vaultRoot, string pagePath) =>
        LastBooks[VaultKey(vaultRoot)] = Path.GetRelativePath(vaultRoot, pagePath).Replace('\\', '/');

    private static string VaultKey(string vaultRoot) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(vaultRoot));

    /// <summary>Reads <c>ui.json</c> from <paramref name="dir"/>; a missing or unreadable file gives the defaults, hand-edited values are clamped to their range.</summary>
    public static UiSettings Load(string dir)
    {
        var path = Path.Combine(dir, FileName);
        try
        {
            var settings = File.Exists(path) ? JsonSerializer.Deserialize<UiSettings>(File.ReadAllBytes(path), Json) ?? new() : new();
            settings.FontSizePt = Math.Clamp(settings.FontSizePt, MinFontSizePt, MaxFontSizePt);
            settings.LineWidthCh = Math.Clamp(settings.LineWidthCh, MinLineWidthCh, MaxLineWidthCh);

            // JSON gives a case-sensitive dictionary; vault paths compare without case, as Windows does.
            var books = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (vault, page) in settings.LastBooks ?? [])
            {
                books[vault] = page;
            }

            settings.LastBooks = books;
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    /// <summary>Writes <c>ui.json</c> to <paramref name="dir"/> (created if needed), replacing the file atomically.</summary>
    public void Save(string dir)
    {
        Directory.CreateDirectory(dir);
        AtomicFile.Write(Path.Combine(dir, FileName), JsonSerializer.SerializeToUtf8Bytes(this, Json));
    }
}
