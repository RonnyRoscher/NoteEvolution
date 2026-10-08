using System.Globalization;

namespace NoteEvolution.UI.State;

/// <summary>Sizes of the AI models as the dialogs show them (the figures are estimates for the user).</summary>
public static class ModelSizes
{
    /// <summary>Whole megabytes, rounded to tens above 100 MB.</summary>
    public static long Megabytes(long bytes)
    {
        var mb = (long)Math.Round(bytes / 1_000_000.0);
        return mb > 100 ? (long)Math.Round(mb / 10.0) * 10 : mb;
    }

    /// <summary>Gigabytes with one decimal, in the current culture.</summary>
    public static string Gigabytes(long bytes) =>
        (bytes / 1_000_000_000.0).ToString("0.0", CultureInfo.CurrentCulture);
}
