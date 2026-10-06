using NoteEvolution.Core.Books;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace NoteEvolution.Pdf;

/// <summary>Spec 8.1: a simple, readable book PDF set in EB Garamond.</summary>
public sealed class QuestPdfExporter : IPdfExporter
{
    private const string TocTitle = "Inhaltsverzeichnis";
    private const float BodySize = 11;
    private const float Points = 72f / 25.4f;

    public PdfExportReport Export(Book book, Guid? scopeKey, PdfOptions options, Stream output)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        QuestPdfSetup.EnsureInitialized();

        using var content = PdfContent.Build(book, scopeKey);
        var (pageSize, margin) = options.Size switch
        {
            PdfPageSize.A4 => (PageSizes.A4, 25f * Points),
            PdfPageSize.A5 => (PageSizes.A5, 18f * Points),
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };

        void Configure(PageDescriptor page)
        {
            page.Size(pageSize);
            page.Margin(margin);
            page.DefaultTextStyle(style => style.FontFamily(QuestPdfSetup.FontFamily).FontSize(BodySize).LineHeight(1.4f));
        }

        void Numbered(PageDescriptor page)
        {
            Configure(page);
            page.Footer().AlignCenter().Text(text => text.CurrentPageNumber());
        }

        var headings = content.Elements.OfType<HeadingElement>().ToList();
        var contentHeight = pageSize.Height - 2 * margin;

        Document.Create(document =>
        {
            document.Page(page =>
            {
                Configure(page);
                page.Content().AlignMiddle().Column(column => ComposeTitlePage(column, content));
            });

            if (headings.Count > 0)
            {
                document.Page(page =>
                {
                    Numbered(page);
                    page.Content().Column(column => ComposeToc(column, headings));
                });
            }

            document.Page(page =>
            {
                Numbered(page);
                page.Content().Column(column => ComposeBody(column, content.Elements, contentHeight));
            });
        }).GeneratePdf(output);

        return new PdfExportReport(content.Warnings);
    }

    private static void ComposeTitlePage(ColumnDescriptor column, PdfContent content)
    {
        column.Item().Text(content.Title).FontSize(28).Bold().AlignCenter();
        if (content.Dedication is { } dedication)
        {
            column.Item().PaddingTop(32).Text(dedication).Italic().AlignCenter();
        }
    }

    private static void ComposeToc(ColumnDescriptor column, List<HeadingElement> headings)
    {
        column.Item().PaddingBottom(18).Text(TocTitle).FontSize(20).Bold();
        var topLevel = headings.Min(h => h.Level);
        foreach (var heading in headings)
        {
            column.Item().PaddingBottom(3).PaddingLeft((heading.Level - topLevel) * 16).Row(row =>
            {
                row.RelativeItem().Text(text =>
                {
                    var link = text.SectionLink(heading.Title, heading.SectionId);
                    if (heading.Level == topLevel)
                    {
                        link.Bold();
                    }
                });
                row.ConstantItem(36).AlignRight().Text(text => text.BeginPageNumberOfSection(heading.SectionId));
            });
        }
    }

    private static void ComposeBody(
        ColumnDescriptor column, IReadOnlyList<PdfElement> elements, float contentHeight)
    {
        var first = true;
        foreach (var element in elements)
        {
            switch (element)
            {
                case HeadingElement { Level: 1 } heading:
                    if (!first)
                    {
                        column.Item().PageBreak();
                    }

                    ComposeHeading(column, heading, 20, 0, 16);
                    break;
                case HeadingElement { Level: 2 } heading:
                    ComposeHeading(column, heading, 16, 12, 8);
                    break;
                case HeadingElement heading:
                    ComposeHeading(column, heading, 13, 8, 6);
                    break;
                case ParagraphElement paragraph:
                    column.Item().PaddingBottom(8).Text(text =>
                    {
                        foreach (var run in paragraph.Runs)
                        {
                            var span = text.Span(run.Text);
                            if (run.Bold)
                            {
                                span.Bold();
                            }

                            if (run.Italic)
                            {
                                span.Italic();
                            }
                        }
                    });
                    break;
                case ImageElement image:
                    // Full text width, but never taller than the page: tall images shrink instead of failing. Without
                    // EnsureSpace the area-fitting image would also shrink to whatever is left of the current page.
                    column.Item().PaddingBottom(8).EnsureSpace(contentHeight / 4).AlignCenter().Image(image.Image).FitArea();
                    break;
            }

            first = false;
        }
    }

    private static void ComposeHeading(ColumnDescriptor column, HeadingElement heading, float size, float before, float after) =>
        column.Item().Section(heading.SectionId).EnsureSpace(size * 6)
            .PaddingTop(before).PaddingBottom(after).Text(heading.Title).FontSize(size).Bold();
}
