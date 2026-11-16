using System.Text.Json;
using System.Text.Json.Serialization;
using NoteEvolution.Core.Format;
using NoteEvolution.Pdf;

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

    public const double DefaultRecentListHeight = 180;

    public const double MinRecentListHeight = 60;

    /// <summary>The tallest stored height of the "Zuletzt geändert" list that is believed; a larger value is a broken file.</summary>
    public const double MaxRecentListHeight = 4000;

    public const double MinSplitRatio = 0.2;

    public const double MaxSplitRatio = 0.8;

    public const double DefaultSplitRatio = 0.5;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The vault folder opened last; opened again on start.</summary>
    public string? LastVault { get; set; }

    public double OutlineWidth { get; set; } = 260;

    public double NotesWidth { get; set; } = 380;

    /// <summary>The height in pixels of the "Zuletzt geändert" list above the outline (spec "Blockdaten" 4.3).</summary>
    public double RecentListHeight { get; set; } = DefaultRecentListHeight;

    /// <summary>The "Zuletzt geändert" list shows only its header; <see cref="RecentListHeight"/> stays for expanding it.</summary>
    public bool RecentListCollapsed { get; set; }

    /// <summary>The book is shown in two halves, each with its own outline and editor.</summary>
    public bool SplitView { get; set; }

    /// <summary>The share of the upper half in the book area when split, <see cref="MinSplitRatio"/> to <see cref="MaxSplitRatio"/>.</summary>
    public double SplitRatio { get; set; } = DefaultSplitRatio;

    public bool OutlineCollapsed { get; set; }

    public bool NotesCollapsed { get; set; }

    public ThemeChoice Theme { get; set; } = ThemeChoice.System;

    public int FontSizePt { get; set; } = 12;

    public int LineWidthCh { get; set; } = 70;

    /// <summary>The notes pane hides notes that are already used in a book.</summary>
    public bool HideUsed { get; set; }

    /// <summary>The journal hides days marked "nicht relevant" for the current book (<c>not-relevant-for::</c>).</summary>
    public bool HideNotRelevant { get; set; }

    /// <summary>The journal hides days marked "später" (<c>later:: true</c>).</summary>
    public bool HideLater { get; set; }

    /// <summary>The outline shows a coverage bar next to each heading (spec 6.2).</summary>
    public bool ShowCoverage { get; set; }

    /// <summary>
    /// The editor marks text blocks that repeat elsewhere with "≈ n" at their right edge (repetitions spec 6); the section
    /// bar's counter is shown either way. Components that show it follow a change when <see cref="AppState.Notify"/> is called.
    /// </summary>
    public bool ShowRepetitions { get; set; } = true;

    /// <summary>The way a note was adopted last; the note cards' "Übernehmen" button adopts and is labelled this way.</summary>
    public AdoptVariant LastAdoptVariant { get; set; } = AdoptVariant.AtCursor;

    /// <summary>The hint not to edit the same book in Logseq at the same time has been shown.</summary>
    public bool LogseqHintShown { get; set; }

    /// <summary>The user chose "not now" in the AI model dialog: it is no longer shown after opening a vault.</summary>
    public bool AiModelDeclined { get; set; }

    /// <summary>The id of the AI model in use (<see cref="AI.Model.ModelInfo.Id"/>); null for the catalog's default.</summary>
    public string? AiModelId { get; set; }

    /// <summary>The local language model (Ollama) is used; off until the user sets it up (spec 7).</summary>
    public bool LanguageModelEnabled { get; set; }

    /// <summary>The address of the language-model server.</summary>
    public string LanguageModelAddress { get; set; } = "http://127.0.0.1:11434";

    /// <summary>The language model to ask (an Ollama model name).</summary>
    public string LanguageModelName { get; set; } = "qwen2.5:7b";

    /// <summary>The user paused the language model's background computation (header switch, spec 4).</summary>
    public bool LanguageModelPaused { get; set; }

    /// <summary>The one-time offer to set up the language model has been shown.</summary>
    public bool LanguageModelOffered { get; set; }

    /// <summary>The header shows the system meter (CPU, memory, free disk space; spec 11.2).</summary>
    public bool SystemMeterVisible { get; set; } = true;

    /// <summary>The keys of the print formats in the PDF dialog (width x height in mm) and "custom".</summary>
    public static readonly string[] PdfPrintFormats = ["120x190", "135x215", "148x210", "155x220", "170x220", "custom"];

    public const string DefaultPdfPrintFormat = "120x190";

    public const double DefaultPdfPrintWidthMm = 120;

    public const double DefaultPdfPrintHeightMm = 190;

    /// <summary>The PDF dialog has "Druck" chosen instead of A4 or A5.</summary>
    public bool PdfPrint { get; set; }

    /// <summary>The simple page size of the PDF dialog.</summary>
    public PdfPageSize PdfSize { get; set; } = PdfPageSize.A4;

    /// <summary>One of <see cref="PdfPrintFormats"/>.</summary>
    public string PdfPrintFormat { get; set; } = DefaultPdfPrintFormat;

    /// <summary>The width in mm of the custom print format.</summary>
    public double PdfPrintWidthMm { get; set; } = DefaultPdfPrintWidthMm;

    /// <summary>The height in mm of the custom print format.</summary>
    public double PdfPrintHeightMm { get; set; } = DefaultPdfPrintHeightMm;

    public double PdfBleedMm { get; set; }

    public bool PdfCropMarks { get; set; }

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
            if (!(settings.RecentListHeight is >= MinRecentListHeight and <= MaxRecentListHeight))
            {
                settings.RecentListHeight = DefaultRecentListHeight;
            }

            if (!(settings.SplitRatio is >= MinSplitRatio and <= MaxSplitRatio))
            {
                settings.SplitRatio = DefaultSplitRatio;
            }

            if (!Enum.IsDefined(settings.LastAdoptVariant))
            {
                settings.LastAdoptVariant = AdoptVariant.AtCursor;
            }

            if (!Enum.IsDefined(settings.PdfSize))
            {
                settings.PdfSize = PdfPageSize.A4;
            }

            if (!PdfPrintFormats.Contains(settings.PdfPrintFormat))
            {
                settings.PdfPrintFormat = DefaultPdfPrintFormat;
            }

            if (!(settings.PdfPrintWidthMm is >= PrintLayout.MinSizeMm and <= PrintLayout.MaxSizeMm
                  && settings.PdfPrintHeightMm is >= PrintLayout.MinSizeMm and <= PrintLayout.MaxSizeMm))
            {
                settings.PdfPrintWidthMm = DefaultPdfPrintWidthMm;
                settings.PdfPrintHeightMm = DefaultPdfPrintHeightMm;
            }

            if (!(settings.PdfBleedMm is >= PrintLayout.MinBleedMm and <= PrintLayout.MaxBleedMm))
            {
                settings.PdfBleedMm = 0;
            }

            // JSON gives a case-sensitive dictionary; vault paths compare without case, as Windows does.
            var books = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (vault, page) in settings.LastBooks ?? [])
            {
                books[vault] = page;
            }

            settings.LastBooks = books;
            var defaults = new UiSettings();
            settings.LanguageModelAddress ??= defaults.LanguageModelAddress;
            settings.LanguageModelName ??= defaults.LanguageModelName;
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
