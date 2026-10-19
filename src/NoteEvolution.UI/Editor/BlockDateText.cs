using System.Globalization;
using Microsoft.Extensions.Localization;
using NoteEvolution.UI.Resources;

namespace NoteEvolution.UI.Editor;

/// <summary>What the editor shows of a text block's dates: the text at its top right and its tooltip.</summary>
public sealed record BlockDateLabel(string Text, string Title);

/// <summary>The dates of a text block as the editor shows them (spec "Blockdaten" 3).</summary>
public static class BlockDateText
{
    private const string DateFormat = "dd.MM.yyyy";

    private const string DateTimeFormat = "dd.MM.yyyy HH:mm";

    /// <summary>
    /// <c>"erstellt 12.10.2026 · geändert 19.10.2026 14:32"</c>: the updated date with its time, the created date with
    /// its time only on the same day as the updated one; a missing part is dropped, <c>null</c> without both.
    /// </summary>
    public static string? Of(DateTime? created, DateTime? updated, IStringLocalizer<Strings> l)
    {
        var parts = new List<string>();
        if (created is { } c)
        {
            var sameDay = updated is { } u && u.Date == c.Date;
            parts.Add(l["BlockDatesCreated", Format(c, sameDay ? DateTimeFormat : DateFormat)]);
        }

        if (updated is { } changed)
        {
            parts.Add(l["BlockDatesUpdated", Format(changed, DateTimeFormat)]);
        }

        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    /// <summary>The tooltip: both dates in full, one per line; a missing one is dropped, <c>null</c> without both.</summary>
    public static string? TitleOf(DateTime? created, DateTime? updated, IStringLocalizer<Strings> l)
    {
        var lines = new List<string>();
        if (created is { } c)
        {
            lines.Add(l["BlockDatesCreatedTitle", Format(c, DateTimeFormat)]);
        }

        if (updated is { } u)
        {
            lines.Add(l["BlockDatesUpdatedTitle", Format(u, DateTimeFormat)]);
        }

        return lines.Count > 0 ? string.Join("\n", lines) : null;
    }

    /// <summary>Text and tooltip, or <c>null</c> without both dates.</summary>
    public static BlockDateLabel? LabelOf(DateTime? created, DateTime? updated, IStringLocalizer<Strings> l) =>
        Of(created, updated, l) is { } text ? new BlockDateLabel(text, TitleOf(created, updated, l)!) : null;

    private static string Format(DateTime value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
}
