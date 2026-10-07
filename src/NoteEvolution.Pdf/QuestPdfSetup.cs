using System.Reflection;
using QuestPDF.Drawing;
using QuestPDF.Infrastructure;

namespace NoteEvolution.Pdf;

/// <summary>One-time QuestPDF setup: license and the embedded EB Garamond (OFL) fonts.</summary>
internal static class QuestPdfSetup
{
    public const string FontFamily = "EB Garamond";

    private static readonly string[] FontFiles =
        ["EBGaramond-Regular", "EBGaramond-Italic", "EBGaramond-Bold", "EBGaramond-BoldItalic"];

    // The static constructor runs exactly once, thread-safe, before the first call of EnsureInitialized.
    static QuestPdfSetup()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var file in FontFiles)
        {
            using var stream = assembly.GetManifestResourceStream($"Fonts/{file}.ttf")
                ?? throw new InvalidOperationException($"Embedded font {file}.ttf is missing.");
            FontManager.RegisterFontFromStream(stream);
        }
    }

    public static void EnsureInitialized()
    {
    }
}
