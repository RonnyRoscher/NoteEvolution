using System.Globalization;
using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Books;

/// <summary>Reads, formats and stamps the <c>created::</c> / <c>updated::</c> properties of book text blocks.</summary>
public static class BlockDates
{
    public const string CreatedKey = "created";
    public const string UpdatedKey = "updated";

    private const string MinuteFormat = "yyyy-MM-ddTHH:mm";
    private static readonly string[] LocalFormats = [MinuteFormat, "yyyy-MM-ddTHH:mm:ss"];

    /// <summary>The creation time as local wall-clock time; <c>null</c> if missing or unreadable.</summary>
    public static DateTime? Created(Block block) => Read(block, CreatedKey);

    /// <summary>The time of the last change as local wall-clock time; <c>null</c> if missing or unreadable.</summary>
    public static DateTime? Updated(Block block) => Read(block, UpdatedKey);

    /// <summary>The property value for <paramref name="time"/>: local time, minute precision, invariant culture.</summary>
    public static string Format(DateTimeOffset time) =>
        time.ToLocalTime().ToString(MinuteFormat, CultureInfo.InvariantCulture);

    /// <summary>Sets both dates of a newly created block.</summary>
    public static void StampNew(Block block, DateTimeOffset now)
    {
        var value = Format(now);
        block.SetProperty(CreatedKey, value);
        block.SetProperty(UpdatedKey, value);
    }

    /// <summary>Sets <c>updated::</c>; <c>created::</c> is kept as it is (a missing one stays missing).</summary>
    public static void StampChanged(Block block, DateTimeOffset now) =>
        block.SetProperty(UpdatedKey, Format(now));

    private static DateTime? Read(Block block, string key)
    {
        var value = block.GetProperty(key)?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (DateTime.TryParseExact(value, LocalFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return local;
        }

        // Values carrying a zone (Z or an offset) are converted to local time.
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var offset)
            ? offset.LocalDateTime
            : null;
    }
}
