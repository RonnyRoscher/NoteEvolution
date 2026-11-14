using System.Text;
using System.Text.RegularExpressions;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using static NoteEvolution.Pdf.Tests.PdfExportTests;

namespace NoteEvolution.Pdf.Tests;

public class PrintPdfTests
{
    private const double Mm = 72 / 25.4;
    private const string LongChapter = "Langes Kapitel";

    internal static (byte[] Bytes, PdfExportReport Report) ExportPrint(Book book, PrintLayout layout, Guid? scope = null)
    {
        using var stream = new MemoryStream();
        var report = new QuestPdfExporter().Export(
            book, VaultRootOf(book), scope, new PdfOptions(PdfPageSize.A5, layout), stream);
        return (stream.ToArray(), report);
    }

    /// <summary>One chapter with enough long paragraphs for several pages.</summary>
    private static Book LongBook()
    {
        var sb = new StringBuilder("title:: Langbuch\ntype:: book\n\n");
        sb.Append($"- # {LongChapter}\n");
        for (var i = 1; i <= 80; i++)
        {
            sb.Append($"\t- Absatz {i}: Wer fragt, der lernt, und wer lernt, der fragt weiter, bis aus Neugier Gewissheit wird " +
                "und aus Gewissheit wieder eine neue Frage, die den Weg zum nächsten Gedanken öffnet.\n");
        }

        return PdfSamples.Load(sb.ToString());
    }

    /// <summary><paramref name="count"/> chapters "Kapitel 1" … each with one short paragraph.</summary>
    private static Book BookWithChapters(int count)
    {
        var sb = new StringBuilder("title:: Kapitelbuch\ntype:: book\n\n");
        for (var i = 1; i <= count; i++)
        {
            sb.Append($"- # Kapitel {i}\n\t- Text von Kapitel {i}.\n");
        }

        return PdfSamples.Load(sb.ToString());
    }

    private static void AssertPageSizes(byte[] bytes, double widthMm, double heightMm)
    {
        using var pdf = PdfDocument.Open(bytes);
        Assert.All(pdf.GetPages(), p =>
        {
            Assert.InRange(p.Width, widthMm * Mm - 1, widthMm * Mm + 1);
            Assert.InRange(p.Height, heightMm * Mm - 1, heightMm * Mm + 1);
        });
    }

    /// <summary>The 1-based number of the first page that starts with <paramref name="text"/>.</summary>
    private static int FirstPageStartingWith(List<string> pages, string text) =>
        pages.FindIndex(p => p.StartsWith(text, StringComparison.Ordinal)) + 1;

    [Fact]
    public void PageSize_IsTheTrimSize()
    {
        var bytes = ExportPrint(PdfSamples.Load(PdfSamples.SpecBookWithTwoChapters), new PrintLayout(120, 190)).Bytes;

        using var pdf = PdfDocument.Open(bytes);
        Assert.All(pdf.GetPages(), p =>
        {
            Assert.InRange(p.Width, 340.16 - 1, 340.16 + 1);
            Assert.InRange(p.Height, 538.58 - 1, 538.58 + 1);
        });
    }

    [Fact]
    public void PageSize_WithBleed() =>
        AssertPageSizes(ExportPrint(PdfSamples.Load(PdfSamples.SpecBookWithTwoChapters), new PrintLayout(120, 190, 3)).Bytes, 126, 196);

    [Fact]
    public void PageSize_WithBleedAndMarks() =>
        AssertPageSizes(ExportPrint(PdfSamples.Load(PdfSamples.SpecBookWithTwoChapters), new PrintLayout(120, 190, 3, true)).Bytes, 140, 210);

    [Fact]
    public void CropMarksWithoutBleed_AreIgnored() =>
        AssertPageSizes(ExportPrint(PdfSamples.Load(PdfSamples.SpecBookWithTwoChapters), new PrintLayout(120, 190, 0, true)).Bytes, 120, 190);

    [Theory]
    [InlineData(90f, 190f, 0f)]
    [InlineData(120f, 190f, 11f)]
    public void InvalidLayout_Throws(float width, float height, float bleed)
    {
        var book = PdfSamples.Load(PdfSamples.SpecBookWithTwoChapters);

        Assert.Throws<ArgumentOutOfRangeException>(() => ExportPrint(book, new PrintLayout(width, height, bleed)));
    }

    [Theory]
    [InlineData(120f, 190f, 0f, false, 18, 14)]
    [InlineData(120f, 190f, 3f, true, 28, 24)]
    [InlineData(170f, 220f, 0f, false, 22, 17)]
    public void Margins_AreMirrored(float width, float height, float bleed, bool marks, double oddLeftMm, double evenLeftMm)
    {
        var bytes = ExportPrint(LongBook(), new PrintLayout(width, height, bleed, marks)).Bytes;
        var bodyStart = FirstPageStartingWith(PageTexts(bytes), LongChapter);

        using var pdf = PdfDocument.Open(bytes);
        var body = pdf.GetPages().Where(p => p.Number >= bodyStart).ToList();
        Assert.True(body.Count(p => p.Number % 2 == 1) >= 2 && body.Count(p => p.Number % 2 == 0) >= 2, "several body pages");
        Assert.All(body, page =>
        {
            var expected = (page.Number % 2 == 1 ? oddLeftMm : evenLeftMm) * Mm;
            var left = page.GetWords().Min(w => w.BoundingBox.Left);
            Assert.InRange(left, expected - 1.5, expected + 1.5);
        });
    }

    [Theory]
    [InlineData(0f, false)]
    [InlineData(3f, true)]
    public void Margins_TopAndBottom(float bleed, bool marks)
    {
        var offset = (bleed + (marks ? 7 : 0)) * Mm;
        var bytes = ExportPrint(LongBook(), new PrintLayout(120, 190, bleed, marks)).Bytes;
        var full = FirstPageStartingWith(PageTexts(bytes), LongChapter) + 1;

        using var pdf = PdfDocument.Open(bytes);
        var page = pdf.GetPage(full);
        var words = page.GetWords().ToList();
        var number = Assert.Single(words, w => w.Text == full.ToString());
        var lineHeight = 10.5 * 1.35;

        var firstLineTop = page.Height - offset - words.Max(w => w.BoundingBox.Top);
        Assert.InRange(firstLineTop, 16 * Mm - lineHeight, 16 * Mm + lineHeight);
        Assert.All(words.Where(w => w != number), w => Assert.True(w.BoundingBox.Bottom - offset >= 20 * Mm - 1, w.Text));
        // The page number lies inside the trim area, below the type area.
        Assert.True(number.BoundingBox.Bottom > offset, "inside the trim area");
        Assert.True(number.BoundingBox.Top - offset < 20 * Mm, "below the type area");
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(30, false)]
    public void Structure_TitleBlankContentsBody(int chapters, bool contentsOnOnePage)
    {
        var pages = PageTexts(ExportPrint(BookWithChapters(chapters), new PrintLayout(120, 190)).Bytes);

        Assert.StartsWith("Kapitelbuch", pages[0]);
        Assert.Equal("", pages[1]);
        Assert.StartsWith("Inhaltsverzeichnis", pages[2]);
        var first = FirstPageStartingWith(pages, "Kapitel 1 ");
        Assert.Equal(5, first);
        if (contentsOnOnePage)
        {
            // The contents end on page 3 (odd): page 4 is left blank.
            Assert.Equal("", pages[3]);
        }
        else
        {
            // The contents fill pages 3 and 4 exactly.
            Assert.Contains("Kapitel 30", pages[3]);
            Assert.DoesNotContain("Inhaltsverzeichnis", pages[3]);
        }
    }

    [Fact]
    public void Structure_ContentsPageNumbersMatch()
    {
        var pages = PageTexts(ExportPrint(BookWithChapters(30), new PrintLayout(120, 190)).Bytes);
        var contents = pages[2] + " " + pages[3];

        for (var i = 1; i <= 30; i++)
        {
            var match = Regex.Match(contents, $@"Kapitel {i} (\d+)");
            Assert.True(match.Success, $"Kapitel {i} in the contents");
            // The contents fill pages 3 and 4 (the second one starts with an entry); the body starts on page 5.
            var actual = pages.FindIndex(4, p => p.StartsWith($"Kapitel {i} ", StringComparison.Ordinal)) + 1;
            Assert.Equal(actual, int.Parse(match.Groups[1].Value));
        }
    }

    [Fact]
    public void Structure_NoHeadings()
    {
        var pages = PageTexts(ExportPrint(
            PdfSamples.Load("title:: Leer\ntype:: book\n\n- Nur ein Absatz\n"), new PrintLayout(120, 190)).Bytes);

        Assert.Equal(3, pages.Count);
        Assert.Equal("", pages[1]);
        Assert.StartsWith("Nur ein Absatz", pages[2]);
    }

    [Fact]
    public void Structure_SectionScope()
    {
        var book = PdfSamples.Load(PdfSamples.SpecBookWithTwoChapters);
        var chapterTwo = book.Root.Children.Single(n => n.Title == PdfSamples.ChapterTwoTitle);

        var pages = PageTexts(ExportPrint(book, new PrintLayout(120, 190), chapterTwo.Key).Bytes);

        Assert.Equal("", pages[1]);
        Assert.StartsWith("Inhaltsverzeichnis", pages[2]);
        Assert.Equal(1, FirstPageStartingWith(pages, PdfSamples.ChapterTwoTitle) % 2);
    }

    [Fact]
    public void PageNumbers_NotOnTitleAndBlankPages()
    {
        var pages = PageTexts(ExportPrint(BookWithChapters(5), new PrintLayout(120, 190)).Bytes);

        Assert.Equal("Kapitelbuch", pages[0]);
        Assert.Equal("", pages[1]);
        Assert.Equal("", pages[3]);
        Assert.EndsWith(" 3", pages[2]);
        Assert.Equal("Kapitel 1 Text von Kapitel 1. 5", pages[4]);
    }

    [Theory]
    [InlineData(100f)]
    [InlineData(300f)]
    public void ExtremeFormats_DoNotFail(float size)
    {
        using var dir = new TempDir();
        var assets = Directory.CreateDirectory(Path.Combine(dir.Path, "assets")).FullName;
        File.WriteAllBytes(Path.Combine(assets, "hoch.png"), PdfSamples.PngOfSize(300, 1200));
        var sb = new StringBuilder("title:: Extrembuch\ntype:: book\n\n- # Bildkapitel\n\t- ![Ein hohes Bild](../assets/hoch.png)\n");
        for (var i = 1; i <= 80; i++)
        {
            sb.Append($"- # Kapitel {i}\n\t- Text von Kapitel {i}.\n");
        }

        var book = PdfSamples.Load(sb.ToString(), Path.Combine(dir.Path, "pages", "Buch.md"));

        var (bytes, report) = ExportPrint(book, new PrintLayout(size, size, 10, true));

        Assert.Empty(report.Warnings);
        using var pdf = PdfDocument.Open(bytes);
        Assert.Equal(pdf.NumberOfPages, report.PageCount);
        Assert.Equal(1, pdf.GetPages().Sum(p => p.GetImages().Count()));
    }

    [Fact]
    public void TitlePage_IsCentredOnTheTypeArea()
    {
        // 120 x 190 mm with 3 mm bleed and marks (10 mm to the trim edge): the type area of page 1 (odd) spans
        // 28 to 116 mm across and 26 to 180 mm down from the sheet's top edge.
        var bytes = ExportPrint(PdfSamples.Load(PdfSamples.SpecBookWithTwoChapters), new PrintLayout(120, 190, 3, true)).Bytes;

        using var pdf = PdfDocument.Open(bytes);
        var page = pdf.GetPage(1);
        var words = page.GetWords().ToList();
        var title = words.Where(w => w.Text is "Buch:" or "LoveMagic").ToList();
        var centre = (title.Min(w => w.BoundingBox.Left) + title.Max(w => w.BoundingBox.Right)) / 2;
        var middle = page.Height - (words.Max(w => w.BoundingBox.Top) + words.Min(w => w.BoundingBox.Bottom)) / 2;

        Assert.InRange(centre, (28 + 116) / 2.0 * Mm - 2, (28 + 116) / 2.0 * Mm + 2);
        Assert.InRange(middle, (26 + 180) / 2.0 * Mm - 10.5, (26 + 180) / 2.0 * Mm + 10.5);
    }

    [Fact]
    public void SimplePdf_IsUnchanged()
    {
        var bytes = Export(PdfSamples.Load(PdfSamples.SpecBookWithTwoChapters), size: PdfPageSize.A5).Bytes;

        Assert.StartsWith("Inhaltsverzeichnis", PageTexts(bytes)[1]);
        using var pdf = PdfDocument.Open(bytes);
        static double Left(Page page) => page.GetWords().Min(w => w.BoundingBox.Left);
        // Title page, contents, then the body: its pages 2 and 3 are the PDF's pages 4 and 5.
        Assert.InRange(Left(pdf.GetPage(5)), Left(pdf.GetPage(4)) - 0.5, Left(pdf.GetPage(4)) + 0.5);
    }
}
