using NoteEvolution.Core.Books;

namespace NoteEvolution.Pdf;

public enum PdfPageSize
{
    A4,
    A5,
}

/// <summary>The page format of a print PDF: trim size, bleed and crop marks, all in millimetres.</summary>
public sealed record PrintLayout(float WidthMm, float HeightMm, float BleedMm = 0, bool CropMarks = false)
{
    public const float MinSizeMm = 100, MaxSizeMm = 300, MinBleedMm = 0, MaxBleedMm = 10;

    /// <summary>Whether the sizes and the bleed are inside the limits.</summary>
    public bool IsValid =>
        WidthMm is >= MinSizeMm and <= MaxSizeMm
        && HeightMm is >= MinSizeMm and <= MaxSizeMm
        && BleedMm is >= MinBleedMm and <= MaxBleedMm;
}

public sealed record PdfOptions(PdfPageSize Size, PrintLayout? Print = null);

/// <summary>What the export noticed on the way, e.g. images that could not be embedded, and the pages it wrote.</summary>
public sealed record PdfExportReport(IReadOnlyList<string> Warnings, int PageCount);

public interface IPdfExporter
{
    /// <summary>
    /// Writes a readable PDF of <paramref name="book"/> to <paramref name="output"/>: title page, table of contents and
    /// the text of the outline. <paramref name="scopeKey"/> is the key of an outline node to export on its own (with
    /// its heading and subtree); <c>null</c> or <see cref="Guid.Empty"/> exports the whole book.
    /// <paramref name="knownElsewhere"/> tells whether an id names a block somewhere in the vault: a reference to such a
    /// block that is no target of the book (a note) is left out as before, not shown as missing (spec 2.1).
    /// Only image files inside <paramref name="vaultRoot"/> are embedded (as the editor shows them,
    /// <see cref="NoteEvolution.Core.Vaults.VaultAssets.Resolve"/>); any other picture gets a placeholder and a warning.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="scopeKey"/> names no outline node of the book.</exception>
    PdfExportReport Export(
        Book book, string vaultRoot, Guid? scopeKey, PdfOptions options, Stream output, Func<Guid, bool>? knownElsewhere = null);
}
