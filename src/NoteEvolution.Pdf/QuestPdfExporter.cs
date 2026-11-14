using System.Text;
using NoteEvolution.Core.Books;
using QuestPDF.Elements;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace NoteEvolution.Pdf;

/// <summary>Spec 8.1: a simple, readable book PDF set in EB Garamond.</summary>
public sealed class QuestPdfExporter : IPdfExporter
{
    private const string TocTitle = "Inhaltsverzeichnis";
    private const float BodySize = 11;
    private const float CaptionSize = 9;
    private const float Points = 72f / 25.4f;

    public PdfExportReport Export(
        Book book, string vaultRoot, Guid? scopeKey, PdfOptions options, Stream output, Func<Guid, bool>? knownElsewhere = null)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultRoot);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        if (options.Print is { } layout)
        {
            return ExportPrint(book, vaultRoot, scopeKey, PrintGeometry.From(layout), output, knownElsewhere);
        }

        QuestPdfSetup.EnsureInitialized();

        using var content = PdfContent.Build(book, vaultRoot, scopeKey, knownElsewhere);
        var (pageSize, margin) = options.Size switch
        {
            PdfPageSize.A4 => (PageSizes.A4, 25f * Points),
            PdfPageSize.A5 => (PageSizes.A5, 18f * Points),
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };

        // TotalPages is not known while the document is laid out, so the last page is found by watching the page
        // numbers go by in a condition that never shows anything. Layout runs more than once; the maximum is safe.
        var pageCount = 0;

        void Configure(PageDescriptor page)
        {
            page.Background().ShowIf(context =>
            {
                pageCount = Math.Max(pageCount, context.PageNumber);
                return false;
            });
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
                page.Content().AlignMiddle().Column(column => ComposeTitlePage(column, content, 1));
            });

            if (headings.Count > 0)
            {
                document.Page(page =>
                {
                    Numbered(page);
                    page.Content().Column(column => ComposeToc(column, headings, 1));
                });
            }

            document.Page(page =>
            {
                Numbered(page);
                page.Content().Column(column => ComposeBody(column, content, contentHeight, 1));
            });
        }).GeneratePdf(output);

        return new PdfExportReport(content.Warnings, pageCount);
    }

    /// <summary>
    /// The PDF for a printed book: trim size plus bleed (and crop marks), mirrored margins (the inner margin is on the
    /// left of odd, right-hand pages), and the book structure title page, blank page, contents, body. The body always
    /// starts on a right-hand page; title and blank pages carry no number.
    /// </summary>
    private static PdfExportReport ExportPrint(
        Book book, string vaultRoot, Guid? scopeKey, PrintGeometry geometry, Stream output, Func<Guid, bool>? knownElsewhere)
    {
        QuestPdfSetup.EnsureInitialized();
        using var content = PdfContent.Build(book, vaultRoot, scopeKey, knownElsewhere);
        var headings = content.Elements.OfType<HeadingElement>().ToList();
        var scale = geometry.BodySize / BodySize;
        var offset = geometry.Offset;

        // As in the simple PDF: the last page number seen while laying out is the page count.
        var pageCount = 0;

        // The page left blank after contents that end on a right-hand page; it gets no number. Found in the first
        // layout pass (QuestPDF lays the document out twice), used by the footer in the second.
        var blankPage = 0;

        static bool Odd(ShowIfContext context) => context.PageNumber % 2 == 1;
        static bool Even(ShowIfContext context) => context.PageNumber % 2 == 0;

        void Configure(PageDescriptor page, bool numbered, Action<IContainer> compose)
        {
            page.Background().ShowIf(context =>
            {
                pageCount = Math.Max(pageCount, context.PageNumber);
                return false;
            });
            page.Size(geometry.PageWidth, geometry.PageHeight);
            page.Margin(0);
            page.DefaultTextStyle(style => style.FontFamily(QuestPdfSetup.FontFamily).FontSize(geometry.BodySize).LineHeight(PrintGeometry.LineHeight));

            // Mirrored side margins: a Row cannot switch its items per page, but a decoration can, so the content is
            // turned on its side, gets the side margins as the decoration's before (left) and after (right) strips,
            // and is turned back. ExtendHorizontal().AlignRight() keeps a partly filled page at the top.
            page.Content().PaddingTop(offset + geometry.Top).RotateLayoutCounterclockwise().Decoration(decoration =>
            {
                decoration.Before().Column(strip =>
                {
                    strip.Item().ShowIf(Odd).Height(offset + geometry.Inner);
                    strip.Item().ShowIf(Even).Height(offset + geometry.Outer);
                });
                decoration.After().Column(strip =>
                {
                    strip.Item().ShowIf(Odd).Height(offset + geometry.Outer);
                    strip.Item().ShowIf(Even).Height(offset + geometry.Inner);
                });
                compose(decoration.Content().ExtendHorizontal().AlignRight().RotateLayoutClockwise());
            });

            // The page number in the middle of the bottom margin, centred on the type area. The items exist on
            // unnumbered pages too: a footer with an empty column would take no height and the content would grow
            // into the bottom margin.
            bool Shown(ShowIfContext context) => numbered && context.PageNumber != blankPage;
            page.Footer().Height(offset + geometry.Bottom).Column(footer =>
            {
                footer.Item().ShowIf(context => Odd(context) && Shown(context))
                    .PaddingLeft(offset + geometry.Inner).PaddingRight(offset + geometry.Outer)
                    .Height(geometry.Bottom).AlignMiddle().AlignCenter().Text(text => text.CurrentPageNumber());
                footer.Item().ShowIf(context => Even(context) && Shown(context))
                    .PaddingLeft(offset + geometry.Outer).PaddingRight(offset + geometry.Inner)
                    .Height(geometry.Bottom).AlignMiddle().AlignCenter().Text(text => text.CurrentPageNumber());
            });

            if (geometry.CropMarks)
            {
                page.Foreground().Svg(size => CropMarks(geometry, size));
            }
        }

        Document.Create(document =>
        {
            document.Page(page => Configure(page, false,
                // Extend: the turned content takes only the room it needs, which would put the title at the top left.
                container => container.Extend().AlignMiddle().Column(column => ComposeTitlePage(column, content, scale))));
            document.Page(page => Configure(page, false, container => container.Text("")));

            if (headings.Count > 0)
            {
                document.Page(page => Configure(page, true, container => container.Column(column =>
                {
                    ComposeToc(column, headings, scale);
                    // Contents ending on a right-hand page get a blank left-hand page, so the body starts on the right.
                    column.Item().ShowIf(context =>
                    {
                        if (Odd(context))
                        {
                            blankPage = context.PageNumber + 1;
                        }

                        return Odd(context);
                    }).PageBreak();
                })));
            }

            document.Page(page => Configure(page, true,
                container => container.Column(column => ComposeBody(column, content, geometry.ContentHeight, scale))));
        }).GeneratePdf(output);

        return new PdfExportReport(content.Warnings, pageCount);
    }

    /// <summary>
    /// Two 0.25 pt lines at each trim corner, 5 mm long, in line with the trim edges and starting at the bleed
    /// distance from the corner, so they stay outside the bleed.
    /// </summary>
    private static string CropMarks(PrintGeometry geometry, Size size)
    {
        var length = PrintGeometry.MarkLengthMm * PrintGeometry.Points;
        var svg = new StringBuilder(FormattableString.Invariant(
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{size.Width}\" height=\"{size.Height}\" viewBox=\"0 0 {size.Width} {size.Height}\">"));
        foreach (var (x, outwardX) in new[] { (geometry.Offset, -1), (geometry.PageWidth - geometry.Offset, 1) })
        {
            foreach (var (y, outwardY) in new[] { (geometry.Offset, -1), (geometry.PageHeight - geometry.Offset, 1) })
            {
                Line(x + outwardX * geometry.Bleed, y, x + outwardX * (geometry.Bleed + length), y);
                Line(x, y + outwardY * geometry.Bleed, x, y + outwardY * (geometry.Bleed + length));
            }
        }

        return svg.Append("</svg>").ToString();

        void Line(float x1, float y1, float x2, float y2) => svg.Append(FormattableString.Invariant(
            $"<line x1=\"{x1}\" y1=\"{y1}\" x2=\"{x2}\" y2=\"{y2}\" stroke=\"black\" stroke-width=\"0.25\"/>"));
    }

    private static void ComposeTitlePage(ColumnDescriptor column, PdfContent content, float scale)
    {
        column.Item().Text(content.Title).FontSize(28 * scale).Bold().AlignCenter();
        if (content.Dedication is { } dedication)
        {
            column.Item().PaddingTop(32).Text(dedication).Italic().AlignCenter();
        }
    }

    /// <param name="scale">The body size relative to the simple PDF's 11 pt; the title is scaled with it.</param>
    private static void ComposeToc(ColumnDescriptor column, List<HeadingElement> headings, float scale)
    {
        column.Item().PaddingBottom(18).Text(TocTitle).FontSize(20 * scale).Bold();
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

    /// <param name="contentHeight">The height of the type area, which limits pictures.</param>
    /// <param name="scale">The body size relative to the simple PDF's 11 pt; headings and captions are scaled with it.</param>
    private static void ComposeBody(ColumnDescriptor column, PdfContent content, float contentHeight, float scale)
    {
        var first = true;
        foreach (var element in content.Elements)
        {
            switch (element)
            {
                case HeadingElement { Level: 1 } heading:
                    if (!first)
                    {
                        column.Item().PageBreak();
                    }

                    ComposeHeading(column, heading, 20 * scale, 0, 16);
                    break;
                case HeadingElement { Level: 2 } heading:
                    ComposeHeading(column, heading, 16 * scale, 12, 8);
                    break;
                case HeadingElement heading:
                    ComposeHeading(column, heading, 13 * scale, 8, 6);
                    break;
                case ParagraphElement paragraph:
                    Anchored(column, paragraph).PaddingBottom(8).Text(text =>
                    {
                        foreach (var run in paragraph.Runs)
                        {
                            if (run.RefTarget is { } target)
                            {
                                ComposeRef(text, run, content.Ref(target));
                            }
                            else
                            {
                                ComposeRun(text, run);
                            }
                        }
                    });
                    break;
                case ImageElement image:
                    // Full text width, but never taller than the page: tall images shrink instead of failing. Without
                    // EnsureSpace the area-fitting image would also shrink to whatever is left of the current page.
                    // The caption is part of one unbreakable item with its picture, which therefore leaves room for it;
                    // a caption longer than that room shrinks into it instead of failing the layout.
                    var reserve = image.Caption.Length > 0 ? contentHeight / 5 : 0;
                    Anchored(column, image).PaddingBottom(8).EnsureSpace(contentHeight / 4).ShowEntire().Column(picture =>
                    {
                        picture.Item().AlignCenter().MaxHeight(contentHeight - 8 - reserve).Image(image.Image).FitArea();
                        if (image.Caption.Length > 0)
                        {
                            picture.Item().PaddingTop(4).MaxHeight(reserve - 4).ScaleToFit().AlignCenter().Text(image.Caption).FontSize(CaptionSize * scale).Italic().AlignCenter();
                        }
                    });
                    break;
            }

            first = false;
        }
    }

    /// <summary>A new column item, as the section of a referenced text block if the element starts one.</summary>
    private static IContainer Anchored(ColumnDescriptor column, PdfElement element) =>
        element.Anchor is { } sectionId ? column.Item().Section(sectionId) : column.Item();

    /// <summary>
    /// Spec 2.5: "→ Titel (S. n)", all of it a link to the target; "→ Titel" alone (no page, no link) for a target
    /// outside the export. A page number span cannot be a link, so "(S. n)" is a small linked inline element; the
    /// title stays a linked span, so a long one wraps like the text around it (an inline element cannot wrap).
    /// </summary>
    private static void ComposeRef(TextDescriptor text, PdfRun run, (string Label, string? SectionId) reference)
    {
        if (reference.SectionId is not { } sectionId)
        {
            Emphasize(text.Span(reference.Label), run);
            return;
        }

        // Deliberately two adjacent link annotations to the same target (one more per line a long title wraps onto):
        // QuestPDF cannot link a page number span, and a single inline element holding the whole reference cannot wrap
        // (a title longer than a line fails the layout).
        Emphasize(text.SectionLink(reference.Label + " ", sectionId), run);
        // Bottom keeps the element's baseline on the line's (within a fraction of a point).
        text.Element(TextInjectedElementAlignment.Bottom).SectionLink(sectionId).Text(page =>
        {
            Emphasize(page.Span("(S. "), run);
            Emphasize(page.BeginPageNumberOfSection(sectionId), run);
            Emphasize(page.Span(")"), run);
        });
    }

    /// <summary>A text run: a link if it names a target in the PDF; raised and small as a superscript, dotted underline as a glossary mark.</summary>
    private static void ComposeRun(TextDescriptor text, PdfRun run)
    {
        var span = run.LinkTarget is { } target ? text.SectionLink(run.Text, target) : text.Span(run.Text);
        Emphasize(span, run);
        switch (run.Kind)
        {
            case PdfRunKind.Superscript:
                span.Superscript();
                break;
            case PdfRunKind.GlossaryMark:
                span.Underline().DecorationDotted();
                break;
        }
    }

    private static void Emphasize(TextSpanDescriptor span, PdfRun run)
    {
        if (run.Bold)
        {
            span.Bold();
        }

        if (run.Italic)
        {
            span.Italic();
        }
    }

    private static void ComposeHeading(ColumnDescriptor column, HeadingElement heading, float size, float before, float after) =>
        column.Item().Section(heading.SectionId).EnsureSpace(size * 6)
            .PaddingTop(before).PaddingBottom(after).Text(heading.Title).FontSize(size).Bold();
}
