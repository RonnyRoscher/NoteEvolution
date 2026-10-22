using System.Text.RegularExpressions;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Actions;
using UglyToad.PdfPig.Annotations;
using UglyToad.PdfPig.Content;

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
        return [.. pdf.GetPages().Select(p => Normalize(Text(ReadingOrder(p))))];
    }

    /// <summary>The words with a space between them, except where one word directly continues the one before it.</summary>
    private static string Text(List<Word> words)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            var touches = i > 0 && Math.Abs(words[i].BoundingBox.Bottom - words[i - 1].BoundingBox.Bottom) < 1
                && Math.Abs(words[i].BoundingBox.Left - words[i - 1].BoundingBox.Right) < 0.5;
            sb.Append(i == 0 || touches ? "" : " ").Append(words[i].Text);
        }

        return sb.ToString();
    }

    /// <summary>
    /// The words of the page line by line from the top, left to right: a linked cross reference is drawn after the
    /// rest of its paragraph, so the content stream order is not the reading order.
    /// </summary>
    private static List<Word> ReadingOrder(Page page)
    {
        var lines = new List<List<Word>>();
        foreach (var word in page.GetWords().OrderByDescending(w => w.BoundingBox.Bottom))
        {
            // Words of one line share their baseline up to a fraction of a point.
            if (lines.Count > 0 && Math.Abs(lines[^1][0].BoundingBox.Bottom - word.BoundingBox.Bottom) < 1)
            {
                lines[^1].Add(word);
            }
            else
            {
                lines.Add([word]);
            }
        }

        return [.. lines.SelectMany(line => line.OrderBy(w => w.BoundingBox.Left))];
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
    public void Pdf_RelevantAndRemarkBlocks_Missing()
    {
        var text = ExportAndRead(
            "title:: Statusbuch\ntype:: book\n\n" +
            "- # Kapitel\n" +
            "\t- Entwurfsblock\n" +
            "\t- Relevanter Block\n\t  status:: relevant\n" +
            "\t- Bemerkungsblock\n\t  status:: bemerkung\n" +
            "\t- Notizblock #notiz\n" +
            "\t- Letzter Block\n");

        Assert.Contains("Entwurfsblock", text);
        Assert.Contains("Letzter Block", text);
        Assert.DoesNotContain("Relevanter Block", text);
        Assert.DoesNotContain("Bemerkungsblock", text);
        Assert.DoesNotContain("Notizblock", text);
        Assert.DoesNotContain("status::", text);
    }

    [Fact]
    public void Pdf_ExcludedDetail_SkipsItsSubtree()
    {
        var text = ExportAndRead(
            "title:: Statusbuch\ntype:: book\n\n" +
            "- # Kapitel\n" +
            "\t- Hauptabsatz\n" +
            "\t\t- Vorheriger Detail\n" +
            "\t\t- Relevantes Detail\n\t\t  status:: relevant\n" +
            "\t\t\t- Kind des Relevanten\n" +
            "\t\t\t\t- Enkel des Relevanten\n" +
            "\t\t- Bemerkungsdetail\n\t\t  status:: bemerkung\n" +
            "\t\t\t- Kind der Bemerkung\n" +
            "\t\t- Folgender Detail\n");

        Assert.Contains("Hauptabsatz", text);
        Assert.Contains("Vorheriger Detail", text);
        Assert.Contains("Folgender Detail", text);
        Assert.DoesNotContain("Relevantes Detail", text);
        Assert.DoesNotContain("Kind des Relevanten", text);
        Assert.DoesNotContain("Enkel des Relevanten", text);
        Assert.DoesNotContain("Bemerkungsdetail", text);
        Assert.DoesNotContain("Kind der Bemerkung", text);
    }

    [Fact]
    public void Pdf_ExcludedBlock_SkipsDetailsWhateverTheirStatus()
    {
        var text = ExportAndRead(
            "title:: Statusbuch\ntype:: book\n\n" +
            "- # Kapitel\n" +
            "\t- Relevanter Block\n\t  status:: relevant\n" +
            "\t\t- Fertiges Detail\n\t\t  status:: fertig\n" +
            "\t\t- Entwurfsdetail\n" +
            "\t- Sichtbarer Block\n");

        Assert.Contains("Sichtbarer Block", text);
        Assert.DoesNotContain("Relevanter Block", text);
        Assert.DoesNotContain("Fertiges Detail", text);
        Assert.DoesNotContain("Entwurfsdetail", text);
    }

    [Fact]
    public void Pdf_WordingAndDone_Exported()
    {
        var text = ExportAndRead(
            "title:: Statusbuch\ntype:: book\n\n" +
            "- # Kapitel\n" +
            "\t- Formulierter Block\n\t  status:: formulierung\n" +
            "\t\t- Fertiges Detail\n\t\t  status:: fertig\n" +
            "\t- Fertiger Block\n\t  status:: fertig\n" +
            "\t- Notiz mit Status #notiz\n\t  status:: formulierung\n");

        Assert.Contains("Formulierter Block", text);
        Assert.Contains("Fertiges Detail", text);
        Assert.Contains("Fertiger Block", text);
        Assert.Contains("Notiz mit Status", text);
    }

    [Fact]
    public void Export_CleansLinksAndTags_ResolvesRefs_EmphasisMarkersDisappear()
    {
        var text = ExportAndRead(PdfSamples.SpecBookWithTwoChapters);

        // The reference points to a text block in "Vertrauen", which starts on page 3.
        Assert.Contains("Ein Satz mit Seitenlink und sowie → Vertrauen (S. 3).", text);
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
        Assert.DoesNotContain("Angst baut Widerstand", text);

        // A reference to a block outside the scope names its target, without page.
        Assert.Contains("sowie → Vertrauen.", text);
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

    [Fact]
    public void Export_RefToHeading_ShowsTitleAndPage()
    {
        var pages = PageTexts(Export(PdfSamples.Load(PdfSamples.BookWithRefs)).Bytes);

        // Title page, table of contents, then one page per chapter: Abendroutine is on page 3.
        Assert.StartsWith("Abendroutine", pages[2]);
        Assert.Contains("Siehe → Abendroutine (S. 3) und", pages[4]);
    }

    [Fact]
    public void Export_RefToTextBlock_ShowsSectionTitleAndPageOfTheBlock()
    {
        var pages = PageTexts(Export(PdfSamples.Load(PdfSamples.BookWithRefs)).Bytes);

        Assert.Contains("Am Morgen langsam beginnen.", pages[3]);
        Assert.Contains("und → Morgenroutine (S. 4).", pages[4]);
    }

    [Fact]
    public void Export_RefToExcludedOrMissingTarget_HasNoPage()
    {
        var pages = PageTexts(Export(PdfSamples.Load(PdfSamples.BookWithRefs)).Bytes);

        Assert.Contains("Auch → Verweise und → (Verweis fehlt) zählen.", pages[4]);
        Assert.DoesNotContain("Ausgelassener Block", string.Join("\n", pages));
    }

    [Fact]
    public void Export_Refs_LinkToTheirTargetPage_OnlyWhenTheTargetIsExported()
    {
        using var pdf = PdfDocument.Open(Export(PdfSamples.Load(PdfSamples.BookWithRefs)).Bytes);
        var page = pdf.GetPage(5);

        var links = page.GetAnnotations().Where(a => a.Type == AnnotationType.Link)
            .GroupBy(l => Assert.IsAssignableFrom<AbstractGoToAction>(l.Action).Destination.PageNumber)
            .OrderBy(g => g.Key)
            .ToList();
        var words = ReadingOrder(page);

        // Links to the pages of the exported targets only; none for the excluded or the missing one.
        Assert.Equal([3, 4], links.Select(g => g.Key));
        foreach (var (link, title) in links.Zip(new[] { "Abendroutine", "Morgenroutine" }))
        {
            // The linked area covers the whole reference, from the arrow to the parenthesis after the page number,
            // without gaps.
            var start = words.FindIndex(w => w.Text == title) - 1;
            var end = words.FindIndex(start, w => w.Text.Contains(')'));
            Assert.Equal("→", words[start].Text);
            var parts = link.Select(l => l.Rectangle).OrderBy(r => r.Left).ToList();
            Assert.InRange(words[start].BoundingBox.Left, parts[0].Left - 1, parts[0].Left + 1);
            Assert.InRange(words[end].BoundingBox.Right, parts[^1].Right - 1, parts[^1].Right + 1);
            Assert.All(parts.Zip(parts.Skip(1)), pair => Assert.InRange(pair.Second.Left - pair.First.Right, -1, 1));
        }

        // The linked reference sits on the line of the text around it.
        var siehe = words.Single(w => w.Text == "Siehe");
        Assert.InRange(words.First(w => w.Text == "→").BoundingBox.Bottom, siehe.BoundingBox.Bottom - 0.5, siehe.BoundingBox.Bottom + 0.5);
    }

    [Fact]
    public void Export_RefWithTitleLongerThanALine_StillExports()
    {
        var title = string.Join(" ", Enumerable.Repeat("Sehr langer Kapiteltitel", 12));
        var book = PdfSamples.Load(
            "title:: Langbuch\ntype:: book\n\n" +
            $"- # {title}\n  id:: {PdfSamples.HeadingTargetId}\n" +
            $"- # Verweise\n\t- Siehe **(({PdfSamples.HeadingTargetId}))** dort.\n");

        var text = AllText(Export(book, size: PdfPageSize.A5).Bytes);

        Assert.Contains("(S. 3) dort.", text);
    }

    [Fact]
    public void Export_BookWithoutRefs_HasNoLinksInTheBody()
    {
        using var pdf = PdfDocument.Open(Export(PdfSamples.Load(PdfSamples.BookWithNoteSubtree)).Bytes);

        Assert.Empty(pdf.GetPage(3).GetAnnotations());
    }
}
