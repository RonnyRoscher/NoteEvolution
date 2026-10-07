using System.Text.RegularExpressions;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using UglyToad.PdfPig;

namespace NoteEvolution.Pdf.Tests;

public class PdfExportTests
{
    private static (byte[] Bytes, PdfExportReport Report) Export(
        Book book, Guid? scope = null, PdfPageSize size = PdfPageSize.A4)
    {
        using var stream = new MemoryStream();
        var report = new QuestPdfExporter().Export(book, scope, new PdfOptions(size), stream);
        return (stream.ToArray(), report);
    }

    private static string Normalize(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    private static List<string> PageTexts(byte[] bytes)
    {
        using var pdf = PdfDocument.Open(bytes);
        return [.. pdf.GetPages().Select(p => Normalize(string.Join(" ", p.GetWords().Select(w => w.Text))))];
    }

    private static string AllText(byte[] bytes) => string.Join("\n", PageTexts(bytes));

    private static string ExportAndRead(string bookText) => AllText(Export(PdfSamples.Load(bookText)).Bytes);

    [Fact]
    public void Export_ContainsHeadingsInOrder_NoNotesNoProperties()
    {
        var text = ExportAndRead(PdfSamples.SpecBookWithTwoChapters);

        Assert.True(text.IndexOf("Liebe und Wahrheit", StringComparison.Ordinal) < text.IndexOf("Vertrauen", StringComparison.Ordinal));
        Assert.Contains("Angst baut Widerstand auf", text);
        Assert.Contains("Gute Interpretationsvarianten zu sehen ist trainierbar.", text);
        Assert.DoesNotContain("noch ein Beispiel ergänzen", text);
        Assert.DoesNotContain("id::", text);
        Assert.DoesNotContain("source::", text);
        Assert.DoesNotContain("collapsed::", text);
        Assert.DoesNotContain("#notiz", text);
    }

    [Fact]
    public void Export_SkipsDescendantsOfNoteParagraphs()
    {
        var text = ExportAndRead(PdfSamples.BookWithNoteSubtree);

        Assert.Contains("Vorheriger Absatz", text);
        Assert.Contains("Folgender Absatz", text);
        Assert.DoesNotContain("Arbeitsnotiz", text);
        Assert.DoesNotContain("Kind der Notiz", text);
        Assert.DoesNotContain("Enkel der Notiz", text);
    }

    [Fact]
    public void Export_CleansLinksTagsAndRefs_EmphasisMarkersDisappear()
    {
        var text = ExportAndRead(PdfSamples.SpecBookWithTwoChapters);

        Assert.Contains("Ein Satz mit Seitenlink und sowie .", text);
        Assert.DoesNotContain("[[", text);
        Assert.DoesNotContain("((", text);
        Assert.DoesNotContain("**", text);
        Assert.Contains("Fett und kursiv.", text);
    }

    [Fact]
    public void Export_UnescapesBlockText()
    {
        var text = ExportAndRead(PdfSamples.BookWithBlock("\\- Punkt\n  \\# Marke\n  fazit:\\: gut"));

        Assert.Contains("- Punkt # Marke fazit:: gut", text);
        Assert.DoesNotContain("\\", text);
    }

    [Fact]
    public void Export_TitlePageFirstWithoutPageNumber_OtherPagesNumbered()
    {
        var pages = PageTexts(Export(PdfSamples.Load(PdfSamples.SpecBookWithTwoChapters)).Bytes);

        Assert.StartsWith("Buch: LoveMagic Für alle, die weiterfragen.", pages[0]);
        Assert.DoesNotMatch(@"\d", pages[0]);
        for (var i = 1; i < pages.Count; i++)
        {
            Assert.EndsWith(" " + (i + 1), pages[i]);
        }
    }

    [Fact]
    public void Export_UsesEbGaramond()
    {
        using var pdf = PdfDocument.Open(Export(PdfSamples.Load(PdfSamples.SpecBookWithTwoChapters)).Bytes);
        var fonts = pdf.GetPages().SelectMany(p => p.Letters).Select(l => l.FontName ?? "").Distinct().ToList();

        Assert.All(fonts, f => Assert.Contains("Garamond", f));
        Assert.True(fonts.Count >= 3, "regular, italic and bold are used: " + string.Join(", ", fonts));
    }

    [Fact]
    public void Export_TocPageNumberMatchesChapterPage()
    {
        var pages = PageTexts(Export(PdfSamples.Load(PdfSamples.SpecBookWithTwoChapters)).Bytes);

        // Page 1 is the title page, page 2 the table of contents.
        var toc = Regex.Match(pages[1], Regex.Escape(PdfSamples.ChapterTwoTitle) + @"\s+(\d+)");
        Assert.True(toc.Success, pages[1]);
        var printed = int.Parse(toc.Groups[1].Value);
        var actual = Enumerable.Range(2, pages.Count - 2)
            .First(i => pages[i].StartsWith(PdfSamples.ChapterTwoTitle, StringComparison.Ordinal)) + 1;

        Assert.True(actual > 3, "the chapter starts after a multi-page first chapter");
        Assert.Equal(actual, printed);
    }

    [Fact]
    public void Export_Level1StartsNewPage_LowerLevelsDoNot()
    {
        var pages = PageTexts(Export(PdfSamples.Load(PdfSamples.SpecBookWithTwoChapters)).Bytes);

        Assert.StartsWith("Liebe und Wahrheit Vertrauen", pages[2]);
        Assert.StartsWith(PdfSamples.ChapterTwoTitle, pages[^1]);
        Assert.Contains("Unterabschnitt", pages[^1]);
    }

    [Fact]
    public void Export_MissingImage_PlaceholderAndWarning()
    {
        var book = PdfSamples.Load(
            PdfSamples.BookWithBlock("![x](../assets/fehlt.png)"), Path.Combine(Path.GetTempPath(), "nope", "pages", "Buch.md"));
        var (bytes, report) = Export(book);

        Assert.Contains("[Bild fehlt: ../assets/fehlt.png]", AllText(bytes));
        Assert.Contains("../assets/fehlt.png", Assert.Single(report.Warnings));
    }

    [Fact]
    public void Export_RemoteImage_IsPlaceholderWithWarning()
    {
        var (bytes, report) = Export(PdfSamples.Load(PdfSamples.BookWithBlock("![x](https://example.com/a.png)")));

        Assert.Contains("[Bild fehlt: https://example.com/a.png]", AllText(bytes));
        Assert.Contains("https://example.com/a.png", Assert.Single(report.Warnings));
    }

    [Fact]
    public void Export_UnreadableImage_IsPlaceholderWithWarning()
    {
        using var dir = new TempDir();
        dir.Write("assets/kaputt.png", "kein Bild");
        var book = PdfSamples.Load(PdfSamples.BookWithBlock("![x](../assets/kaputt.png)"), Path.Combine(dir.Path, "pages", "Buch.md"));
        var (bytes, report) = Export(book);

        Assert.Contains("[Bild fehlt: ../assets/kaputt.png]", AllText(bytes));
        Assert.Contains("../assets/kaputt.png", Assert.Single(report.Warnings));
    }

    [Fact]
    public void Export_ExistingImage_IsEmbedded()
    {
        using var dir = new TempDir();
        var assets = Directory.CreateDirectory(Path.Combine(dir.Path, "assets")).FullName;
        File.WriteAllBytes(Path.Combine(assets, "bild eins.png"), PdfSamples.Png);
        var book = PdfSamples.Load(
            PdfSamples.BookWithBlock("Davor ![x](../assets/bild%20eins.png) danach"), Path.Combine(dir.Path, "pages", "Buch.md"));
        var (bytes, report) = Export(book);

        Assert.Empty(report.Warnings);
        using var pdf = PdfDocument.Open(bytes);
        Assert.Equal(1, pdf.GetPages().Sum(p => p.GetImages().Count()));
        var text = AllText(bytes);
        Assert.Contains("Davor", text);
        Assert.Contains("danach", text);
        Assert.DoesNotContain("![", text);
    }

    [Fact]
    public void Export_Scope_OnlySelectedChapter()
    {
        var book = PdfSamples.Load(PdfSamples.SpecBookWithTwoChapters);
        var chapterTwo = book.Root.Children.Single(n => n.Title == PdfSamples.ChapterTwoTitle);
        var text = AllText(Export(book, chapterTwo.Key).Bytes);

        Assert.Contains("Buch: LoveMagic", text);
        Assert.Contains(PdfSamples.ChapterTwoTitle, text);
        Assert.Contains("Text im Unterabschnitt.", text);
        Assert.DoesNotContain("Liebe und Wahrheit", text);
        Assert.DoesNotContain("Vertrauen", text);
    }

    [Fact]
    public void Export_ScopeEmptyGuid_ExportsWholeBook()
    {
        var book = PdfSamples.Load(PdfSamples.SpecBookWithTwoChapters);

        Assert.Equal(PageTexts(Export(book).Bytes).Count, PageTexts(Export(book, Guid.Empty).Bytes).Count);
    }

    [Fact]
    public void Export_UnknownScope_Throws()
    {
        var book = PdfSamples.Load(PdfSamples.SpecBookWithTwoChapters);

        Assert.Throws<ArgumentException>(() => Export(book, Guid.NewGuid()));
    }

    [Theory]
    [InlineData(PdfPageSize.A4, 595.28, 841.89)]
    [InlineData(PdfPageSize.A5, 419.53, 595.28)]
    public void Export_PageSize(PdfPageSize size, double width, double height)
    {
        using var pdf = PdfDocument.Open(Export(PdfSamples.Load(PdfSamples.SpecBookWithTwoChapters), null, size).Bytes);

        Assert.All(pdf.GetPages(), p =>
        {
            Assert.InRange(p.Width, width - 1, width + 1);
            Assert.InRange(p.Height, height - 1, height + 1);
        });
    }

    [Fact]
    public void Export_BookWithoutHeadings_HasNoTocPage()
    {
        var pages = PageTexts(Export(PdfSamples.Load("title:: Leer\ntype:: book\n\n- Nur ein Absatz\n")).Bytes);

        Assert.Equal(2, pages.Count);
        Assert.DoesNotContain("Inhaltsverzeichnis", string.Join("\n", pages));
    }
}
