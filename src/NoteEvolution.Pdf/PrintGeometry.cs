namespace NoteEvolution.Pdf;

/// <summary>All lengths in points. Offset: from the sheet edge to the trim edge (bleed, plus 7 mm with crop marks).</summary>
internal sealed record PrintGeometry(
    float PageWidth, float PageHeight, float Offset, float Bleed, bool CropMarks,
    float Inner, float Outer, float Top, float Bottom, float BodySize)
{
    public const float LineHeight = 1.35f;

    /// <summary>The width of the strip outside the bleed that holds the crop marks.</summary>
    public const float MarkAreaMm = 7;

    /// <summary>The length of a crop mark.</summary>
    public const float MarkLengthMm = 5;

    /// <summary>Trim widths below this get the small margins and body size.</summary>
    private const float SmallFormatMm = 140;

    public const float Points = 72f / 25.4f;

    /// <summary>The geometry of <paramref name="layout"/>; crop marks without bleed are ignored.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The layout is outside the limits of <see cref="PrintLayout"/>.</exception>
    public static PrintGeometry From(PrintLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!layout.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(layout), layout, "Print layout outside the limits.");
        }

        var marks = layout.CropMarks && layout.BleedMm > 0;
        var bleed = layout.BleedMm * Points;
        var offset = bleed + (marks ? MarkAreaMm * Points : 0);
        var small = layout.WidthMm < SmallFormatMm;
        return new PrintGeometry(
            layout.WidthMm * Points + 2 * offset,
            layout.HeightMm * Points + 2 * offset,
            offset,
            bleed,
            marks,
            Inner: (small ? 18 : 22) * Points,
            Outer: (small ? 14 : 17) * Points,
            Top: (small ? 16 : 20) * Points,
            Bottom: (small ? 20 : 24) * Points,
            BodySize: small ? 10.5f : 11);
    }

    public float TrimWidth => PageWidth - 2 * Offset;

    public float TrimHeight => PageHeight - 2 * Offset;

    public float ContentHeight => TrimHeight - Top - Bottom;

    public float ContentWidth => TrimWidth - Inner - Outer;
}
