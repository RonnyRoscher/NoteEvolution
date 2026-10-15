using Microsoft.Extensions.Localization;
using NoteEvolution.UI.Resources;

namespace NoteEvolution.UI.State;

/// <summary>Remaining and expected durations as the app shows them (spec 11.1).</summary>
public static class Durations
{
    /// <summary>
    /// Under a minute "42 s", under an hour "1:20" (minutes and seconds), from an hour "1 h 10 min"; whole seconds
    /// (rounded), a negative span counts as zero.
    /// </summary>
    public static string Format(TimeSpan span, IStringLocalizer<Strings> l)
    {
        var seconds = Math.Max(0, (long)Math.Round(span.TotalSeconds, MidpointRounding.AwayFromZero));
        if (seconds < 60)
        {
            return l["DurationSeconds", seconds];
        }

        if (seconds < 3600)
        {
            return l["DurationMinutes", seconds / 60, seconds % 60];
        }

        return l["DurationHours", seconds / 3600, seconds / 60 % 60];
    }
}
