using System.Text;
using System.Text.RegularExpressions;
using NoteEvolution.Core.Books;
using NoteEvolution.Pdf.Hyphenation;
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
    public void Structure_EmptyBook_HasNoNumberedEmptyPage()
    {
        var result = ExportPrint(PdfSamples.Load("title:: Leer\ntype:: book\n\n"), new PrintLayout(120, 190));
        var pages = PageTexts(result.Bytes);

        Assert.Equal(2, pages.Count);
        Assert.Equal("", pages[1]);
        Assert.Equal(2, result.Report.PageCount);
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

    private const string WordsChapter = "Lange Wörter";
    private const string HyphenationWarning = "Silbentrennung nicht verfügbar";

    /// <summary>German sentences with long words; no hyphen and no digit anywhere.</summary>
    private const string LongWords =
        "Die Donaudampfschifffahrtsgesellschaft beschäftigte Kapitäne, deren Verantwortungsbewusstsein und " +
        "Zuverlässigkeit weithin bekannt waren. Gemeinschaftsunterkünfte, Versicherungsbedingungen und " +
        "Rechtsschutzversicherungen bestimmten den Arbeitsalltag der Besatzungsmitglieder.";

    /// <summary>One chapter of <paramref name="paragraphs"/> paragraphs, each <paramref name="sentences"/> times <see cref="LongWords"/>.</summary>
    private static Book LongWordsBook(int paragraphs = 12, int sentences = 1)
    {
        var sb = new StringBuilder($"title:: Silbenbuch\ntype:: book\n\n- # {WordsChapter}\n");
        for (var i = 0; i < paragraphs; i++)
        {
            sb.Append("\t- ").AppendJoin(' ', Enumerable.Repeat(LongWords, sentences)).Append('\n');
        }

        return PdfSamples.Load(sb.ToString());
    }

    /// <summary>
    /// The text lines of a page from the top, each with its words left to right; the line of a page number alone is
    /// left out (the samples have no digits).
    /// </summary>
    private static List<List<Word>> Lines(Page page)
    {
        var lines = new List<List<Word>>();
        foreach (var word in page.GetWords().OrderByDescending(w => w.BoundingBox.Bottom))
        {
            if (lines.Count > 0 && Math.Abs(lines[^1][0].BoundingBox.Bottom - word.BoundingBox.Bottom) < 1)
            {
                lines[^1].Add(word);
            }
            else
            {
                lines.Add([word]);
            }
        }

        return [.. lines.Select(l => l.OrderBy(w => w.BoundingBox.Left).ToList()).Where(l => !(l.Count == 1 && l[0].Text.All(char.IsDigit)))];
    }

    /// <summary>
    /// The text of a line. PdfPig reports the syllables between soft hyphens as words of their own that touch (or,
    /// kerned, overlap) each other; they are joined without a space. U+00AD itself does not appear in the text.
    /// </summary>
    private static string TextOf(List<Word> line)
    {
        var sb = new StringBuilder(line[0].Text);
        for (var i = 1; i < line.Count; i++)
        {
            sb.Append(line[i].BoundingBox.Left - line[i - 1].BoundingBox.Right < 0.5 ? "" : " ").Append(line[i].Text);
        }

        return sb.ToString();
    }

    /// <summary>
    /// The right edge of a line's text. QuestPDF justifies the line without its line-end hyphen and hangs the hyphen
    /// into the margin, so a trailing "-" is left out.
    /// </summary>
    private static double RightEdgeOfText(List<Word> line)
    {
        var letters = line.SelectMany(w => w.Letters).ToList();
        var last = letters.Count > 1 && letters[^1].Value == "-" ? letters[^2] : letters[^1];
        return last.BoundingBox.Right;
    }

    /// <summary>The text lines of the pages from the 1-based <paramref name="firstPage"/> on.</summary>
    private static List<string> LineTexts(byte[] bytes, int firstPage = 1)
    {
        using var pdf = PdfDocument.Open(bytes);
        return [.. pdf.GetPages().Where(p => p.Number >= firstPage).SelectMany(p => Lines(p).Select(TextOf))];
    }

    private static bool EndsWithHyphen(string line) => line.EndsWith('-');

    /// <summary>
    /// The running text of <paramref name="lines"/>: a line ending in a hyphen is joined to the next without a space
    /// (the samples contain no real hyphens), U+00AD is removed, white space collapsed.
    /// </summary>
    private static string Dehyphenate(IEnumerable<string> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            sb.Append(EndsWithHyphen(line) ? line[..^1] : line + " ");
        }

        return Regex.Replace(sb.ToString().Replace("­", ""), @"\s+", " ").Trim();
    }

    private static int BodyStart(byte[] bytes, string chapter) => FirstPageStartingWith(PageTexts(bytes), chapter);

    [Fact]
    public void Text_IsJustified()
    {
        // One paragraph over several pages: every line of its second page is a full line. It goes on for more than a
        // page after that, so where exactly it ends does not matter.
        var bytes = ExportPrint(LongWordsBook(1, 24), new PrintLayout(120, 190)).Bytes;
        var page = BodyStart(bytes, WordsChapter) + 1;

        using var pdf = PdfDocument.Open(bytes);
        Assert.True(pdf.NumberOfPages >= page + 2, "the paragraph goes on beyond the next page");
        var lines = Lines(pdf.GetPage(page));
        Assert.True(lines.Count >= 20, "a full page");
        var right = (page % 2 == 1 ? 120 - 14 : 120 - 18) * Mm;
        Assert.All(lines, line => Assert.InRange(RightEdgeOfText(line), right - 1.5, right + 1.5));
        // The hanging hyphen stays close to the margin's edge.
        Assert.All(lines, line => Assert.True(line.Max(w => w.BoundingBox.Right) < right + 4, TextOf(line)));
    }

    [Fact]
    public void Text_IsHyphenated()
    {
        var bytes = ExportPrint(LongWordsBook(), new PrintLayout(120, 190)).Bytes;

        Assert.DoesNotContain('-', LongWords);
        Assert.Contains(LineTexts(bytes), EndsWithHyphen);
    }

    [Fact]
    public void Text_IsTheSameAsInTheSimplePdf()
    {
        var book = LongWordsBook();
        var print = ExportPrint(book, new PrintLayout(120, 190)).Bytes;
        var simple = Export(book, size: PdfPageSize.A5).Bytes;

        var printText = Dehyphenate(LineTexts(print, BodyStart(print, WordsChapter)));
        var simpleText = Dehyphenate(LineTexts(simple, BodyStart(simple, WordsChapter)));

        Assert.StartsWith(WordsChapter, printText);
        Assert.Equal(simpleText.Split(' '), printText.Split(' '));
    }

    [Fact]
    public void Headings_AreNotHyphenated()
    {
        // Each word fits on a line, all of them do not.
        const string title = "Lebensgeschichten Freundschaften Verantwortung Gemeinschaftssinn Zuversicht";
        var bytes = ExportPrint(
            PdfSamples.Load($"title:: Titelbuch\ntype:: book\n\n- # {title}\n\t- Kurzer Text.\n"), new PrintLayout(120, 190)).Bytes;
        var body = BodyStart(bytes, "Lebensgeschichten");

        var lines = LineTexts(bytes, body);
        Assert.NotEqual(title, lines[0]);
        Assert.StartsWith("Lebensgeschichten", lines[0]);
        Assert.DoesNotContain(LineTexts(bytes), EndsWithHyphen);
    }

    [Fact]
    public void Paragraph_DoesNotStartWithASingleLineAtThePageEnd()
    {
        var target = Guid.Parse("00000000-0000-4000-8000-0000000000d6");
        for (var offset = 1; offset <= 30; offset++)
        {
            // The first paragraph grows by about a line per book, so the page breaks fall at every point of the
            // marked paragraphs, each of them at least two lines long. Paragraph 6 is a referenced text block.
            var sb = new StringBuilder("title:: Schusterbuch\ntype:: book\n\n- # Kapitel\n\t- Anfang");
            sb.Append(string.Concat(Enumerable.Repeat(" Gedanke", offset * 6))).Append('\n');
            for (var k = 1; k <= 12; k++)
            {
                sb.Append($"\t- §{k}§ Wer fragt, der lernt, und wer lernt, der fragt weiter, bis aus Neugier Gewissheit " +
                          "wird und aus Gewissheit wieder eine neue Frage folgt.\n");
                if (k == 6)
                {
                    sb.Append($"\t  id:: {target}\n");
                }
            }

            sb.Append($"\t- Siehe (({target})).\n");
            var bytes = ExportPrint(PdfSamples.Load(sb.ToString()), new PrintLayout(120, 190)).Bytes;

            using var pdf = PdfDocument.Open(bytes);
            var pages = pdf.GetPages().Select(p => Lines(p).Select(TextOf).ToList()).ToList();
            for (var i = 0; i < pages.Count - 1; i++)
            {
                if (pages[i].Count > 0 && pages[i][^1].StartsWith('§'))
                {
                    Assert.Fail($"Book {offset}, page {i + 1} ends with the first line of a paragraph: {pages[i][^1]}");
                }
            }

            // The reference names the page where the paragraph starts.
            var reference = Regex.Match(string.Join(" ", pages.Last()), @"\(S\. (\d+)\)");
            Assert.True(reference.Success, $"Book {offset}: reference");
            Assert.Equal(pages.FindIndex(p => p.Any(l => l.StartsWith("§6§", StringComparison.Ordinal))) + 1, int.Parse(reference.Groups[1].Value));
        }
    }

    [Fact]
    public void MissingPatterns_ExportWithoutHyphenation_AndWarn()
    {
        using var stream = new MemoryStream();
        var book = LongWordsBook();
        var report = new QuestPdfExporter(() => null).Export(
            book, VaultRootOf(book), null, new PdfOptions(PdfPageSize.A5, new PrintLayout(120, 190)), stream);

        Assert.Single(report.Warnings, w => w == HyphenationWarning);
        Assert.DoesNotContain(LineTexts(stream.ToArray()), EndsWithHyphen);
    }

    [Fact]
    public void Print_WithPatterns_DoesNotWarn() =>
        Assert.DoesNotContain(HyphenationWarning, ExportPrint(LongWordsBook(), new PrintLayout(120, 190)).Report.Warnings);

    [Fact]
    public void SimplePdf_HasNoHyphenationAndIsLeftAligned()
    {
        var (bytes, report) = Export(LongWordsBook(1, 12), size: PdfPageSize.A5);

        Assert.Empty(report.Warnings);
        Assert.DoesNotContain(LineTexts(bytes), EndsWithHyphen);
        using var pdf = PdfDocument.Open(bytes);
        var rights = Lines(pdf.GetPage(BodyStart(bytes, WordsChapter) + 1)).Select(l => l.Max(w => w.BoundingBox.Right)).ToList();
        Assert.True(rights.Max() - rights.Min() > 3, "ragged right edge");
    }

    [Fact]
    public void LongUnbreakableWord_DoesNotFail()
    {
        // Letter runs too short for the hyphenator: no break inside the token at all.
        var token = string.Concat(Enumerable.Repeat("a1b2c3d4/", 23));
        Assert.True(token.Length >= 200);
        Assert.DoesNotContain(Hyphenator.SoftHyphen, Hyphenator.German!.Hyphenate(token));

        var (bytes, report) = ExportPrint(
            PdfSamples.Load($"title:: Linkbuch\ntype:: book\n\n- # Verweis\n\t- Siehe {token} dort.\n"), new PrintLayout(100, 100));

        using var pdf = PdfDocument.Open(bytes);
        Assert.Equal(pdf.NumberOfPages, report.PageCount);
        Assert.Contains("dort.", string.Join(" ", PageTexts(bytes)));
    }

    private static (byte[] Bytes, PdfExportReport Report) ExportA5OrPrint(Book book, bool print) =>
        print ? ExportPrint(book, new PrintLayout(120, 190)) : Export(book, size: PdfPageSize.A5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PushedHeading_ContentsNameThePageItLandsOn(bool print)
    {
        // The filler grows by a paragraph per book, so for some books the level-2 heading no longer has the room
        // EnsureSpace asks for and moves to the next page.
        var landedOnTop = false;
        for (var filler = 14; filler <= 20; filler++)
        {
            var sb = new StringBuilder("title:: Kapitelbuch\ntype:: book\n\n- # Kapitel\n");
            sb.Append(string.Concat(Enumerable.Repeat("\t- Ein kurzer Satz.\n", filler)));
            sb.Append("\t- ## Unterkapitel\n\t\t- Text danach.\n");
            var pages = PageTexts(ExportA5OrPrint(PdfSamples.Load(sb.ToString()), print).Bytes);

            var contents = pages.FindIndex(p => p.StartsWith("Inhaltsverzeichnis", StringComparison.Ordinal));
            var number = int.Parse(Regex.Match(pages[contents], @"Unterkapitel (\d+)").Groups[1].Value);
            var actual = pages.FindIndex(contents + 1, p => p.Contains("Unterkapitel", StringComparison.Ordinal)) + 1;
            Assert.True(number == actual, $"Book {filler}: contents say {number}, the heading is on {actual}");
            landedOnTop |= pages[actual - 1].StartsWith("Unterkapitel", StringComparison.Ordinal);
        }

        Assert.True(landedOnTop, "some book moves the heading to the next page");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PushedImage_ReferenceNamesThePageItLandsOn(bool print)
    {
        using var dir = new TempDir();
        var assets = Directory.CreateDirectory(Path.Combine(dir.Path, "assets")).FullName;
        File.WriteAllBytes(Path.Combine(assets, "bild.png"), PdfSamples.PngOfSize(400, 300));
        const string target = "00000000-0000-4000-8000-0000000000f1";
        for (var filler = 12; filler <= 20; filler++)
        {
            // A referenced text block that starts with a picture.
            var sb = new StringBuilder("title:: Bilderbuch\ntype:: book\n\n- # Kapitel\n");
            sb.Append(string.Concat(Enumerable.Repeat("\t- Ein kurzer Satz.\n", filler)));
            sb.Append($"\t- ![Bild](../assets/bild.png)\n\t  id:: {target}\n\t- Siehe (({target})).\n");
            var bytes = ExportA5OrPrint(PdfSamples.Load(sb.ToString(), Path.Combine(dir.Path, "pages", "Buch.md")), print).Bytes;

            var reference = int.Parse(Regex.Match(string.Join(" ", PageTexts(bytes)), @"\(S\. (\d+)\)").Groups[1].Value);
            using var pdf = PdfDocument.Open(bytes);
            var actual = pdf.GetPages().Single(p => p.GetImages().Any()).Number;
            Assert.True(reference == actual, $"Book {filler}: the reference says {reference}, the picture is on {actual}");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PageCount_WithoutHeadings(bool print)
    {
        var (bytes, report) = ExportA5OrPrint(PdfSamples.Load("title:: Leer\ntype:: book\n\n- Nur ein Absatz\n"), print);

        using var pdf = PdfDocument.Open(bytes);
        Assert.Equal(print ? 3 : 2, pdf.NumberOfPages);
        Assert.Equal(pdf.NumberOfPages, report.PageCount);
    }

    [Fact]
    public void SimplePdf_WithoutPatterns_DoesNotWarn()
    {
        using var stream = new MemoryStream();
        var book = LongWordsBook();
        var report = new QuestPdfExporter(() => null).Export(book, VaultRootOf(book), null, new PdfOptions(PdfPageSize.A5), stream);

        Assert.DoesNotContain(HyphenationWarning, report.Warnings);
    }

    /// <summary>The start and end of each stroked straight segment on the page, in PDF points from the bottom left.</summary>
    private static List<(double X1, double Y1, double X2, double Y2)> StrokedLines(Page page) =>
    [
        .. page.Paths.Where(path => path.IsStroked)
            .SelectMany(path => path)
            .SelectMany(subpath => subpath.Commands.OfType<UglyToad.PdfPig.Core.PdfSubpath.Line>())
            .Select(line => (line.From.X, line.From.Y, line.To.X, line.To.Y)),
    ];

    [Fact]
    public void CropMarks_EightStrokesAtTheTrimCorners()
    {
        // 120 x 190 mm, 3 mm bleed, marks: the trim edge lies 10 mm inside the sheet edge.
        var bytes = ExportPrint(PdfSamples.Load(PdfSamples.SpecBookWithTwoChapters), new PrintLayout(120, 190, 3, true)).Bytes;
        var offset = 10 * Mm;
        var (width, height) = (140 * Mm, 210 * Mm);
        var expected = new List<(double, double, double, double)>();
        foreach (var (x, outX) in new[] { (offset, -1), (width - offset, 1) })
        {
            foreach (var (y, outY) in new[] { (offset, -1), (height - offset, 1) })
            {
                // In line with the trim edges, from 3 mm to 8 mm away from the corner: outside the bleed.
                expected.Add((x + outX * 3 * Mm, y, x + outX * 8 * Mm, y));
                expected.Add((x, y + outY * 3 * Mm, x, y + outY * 8 * Mm));
            }
        }

        using var pdf = PdfDocument.Open(bytes);
        foreach (var page in new[] { pdf.GetPage(1), pdf.GetPage(pdf.NumberOfPages) })
        {
            var lines = StrokedLines(page);
            Assert.Equal(8, lines.Count);
            Assert.All(expected, e => Assert.Contains(lines, l =>
                (Near(l.X1, e.Item1) && Near(l.Y1, e.Item2) && Near(l.X2, e.Item3) && Near(l.Y2, e.Item4))
                || (Near(l.X1, e.Item3) && Near(l.Y1, e.Item4) && Near(l.X2, e.Item1) && Near(l.Y2, e.Item2))));
        }

        static bool Near(double a, double b) => Math.Abs(a - b) < 0.1;
    }

    [Fact]
    public void CropMarks_NoneWithoutMarks()
    {
        var bytes = ExportPrint(PdfSamples.Load(PdfSamples.SpecBookWithTwoChapters), new PrintLayout(120, 190, 3)).Bytes;

        using var pdf = PdfDocument.Open(bytes);
        Assert.Empty(StrokedLines(pdf.GetPage(1)));
        Assert.Empty(StrokedLines(pdf.GetPage(pdf.NumberOfPages)));
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
