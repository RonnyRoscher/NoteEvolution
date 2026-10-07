using NoteEvolution.Core.Books;

namespace NoteEvolution.Pdf;

public enum PdfPageSize
{
    A4,
    A5,
}

public sealed record PdfOptions(PdfPageSize Size);

/// <summary>What the export noticed on the way, e.g. images that could not be embedded.</summary>
public sealed record PdfExportReport(IReadOnlyList<string> Warnings);

public interface IPdfExporter
{
    /// <summary>
    /// Writes a readable PDF of <paramref name="book"/> to <paramref name="output"/>: title page, table of contents and
    /// the text of the outline. <paramref name="scopeKey"/> is the key of an outline node to export on its own (with
    /// its heading and subtree); <c>null</c> or <see cref="Guid.Empty"/> exports the whole book.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="scopeKey"/> names no outline node of the book.</exception>
    PdfExportReport Export(Book book, Guid? scopeKey, PdfOptions options, Stream output);
}
