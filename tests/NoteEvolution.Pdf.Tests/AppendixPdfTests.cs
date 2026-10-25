using System.Security.Cryptography;
using System.Text;
using NoteEvolution.Core.Books;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Actions;
using UglyToad.PdfPig.Annotations;

namespace NoteEvolution.Pdf.Tests;

public class AppendixPdfTests
{
    private const string Anna = "00000000-0000-4000-8000-0000000000a1";
    private const string Carl = "00000000-0000-4000-8000-0000000000a2";
    private const string Dora = "00000000-0000-4000-8000-0000000000a3";
    private const string Buch = "00000000-0000-4000-8000-0000000000b1";
    private const string Video = "00000000-0000-4000-8000-0000000000b2";
    private const string Unzitiert = "00000000-0000-4000-8000-0000000000b3";
    private const string Achtsamkeit = "00000000-0000-4000-8000-0000000000c1";
    private const string Unbenutzt = "00000000-0000-4000-8000-0000000000c2";

    private static string Cite(string id, string? locator = null) =>
        "{{quelle ((" + id + "))" + (locator is null ? "" : " " + locator) + "}}";

    private const string AppendixText =
        "- # Quellen\n  anhang:: quellen\n" +
        $"\t- Das Buch\n\t  id:: {Buch}\n\t  art:: buch\n\t  autoren:: (({Carl}))\n\t  jahr:: 2020\n\t  url:: https://example.org/buch\n\t  herausgeber:: Verlag X\n" +
        $"\t- Ein Video\n\t  id:: {Video}\n\t  art:: video\n" +
        $"\t- Nie zitiert\n\t  id:: {Unzitiert}\n\t  art:: buch\n" +
        "- # Menschen, die mich inspiriert haben\n  anhang:: menschen\n" +
        $"\t- Dora Fern\n\t  id:: {Dora}\n\t  kuerzel:: DF\n" +
        $"\t- Carl Muster\n\t  id:: {Carl}\n\t  kuerzel:: CM\n\t  themen:: Mut, Ruhe\n\t  Kluger Kopf.\n" +
        $"\t- Anna Beispiel\n\t  id:: {Anna}\n\t  kuerzel:: AB\n\t  themen:: Wahrheit\n" +
        "- # Glossar\n  anhang:: glossar\n" +
        $"\t- Unbenutzt\n\t  id:: {Unbenutzt}\n" +
        $"\t- Achtsamkeit\n\t  id:: {Achtsamkeit}\n\t  formen:: achtsam\n\t  Wach im Augenblick sein.\n";

    /// <summary>The appendix in file order: sources, people, glossary, so the PDF's own order shows.</summary>
    private static string BookText(string body, bool withAppendix = true) =>
        "title:: Buch: Test\ntype:: book\n\n" + body + (withAppendix ? AppendixText : "");

    private static string Body() =>
        "- # Erstes\n" +
        $"\t- Ein Gedanke von (({Anna})) und (({Anna})) dazu. Achtsamkeit zuerst, achtsam später. {Cite(Buch, "S. 12")}\n" +
        "- # Zweites\n" +
        $"\t- Nochmal Achtsamkeit und {Cite(Video)} und {Cite(Buch)}.\n";

    private static List<string> Pages(string bookText, Guid? scope = null)
    {
        var book = PdfSamples.Load(bookText);
        return PdfExportTests.PageTexts(PdfExportTests.Export(book, scope).Bytes);
    }

    [Fact]
    public void Appendix_PartsInOrder_OnlyUsedEntries_InToc()
    {
        var pages = Pages(BookText(Body()));

        // Title, TOC, chapter 1, chapter 2, then one page for each of glossary, people, sources.
        Assert.Equal(7, pages.Count);
        Assert.StartsWith("Glossar Achtsamkeit Wach im Augenblick sein.", pages[4]);
        Assert.StartsWith("Menschen, die mich inspiriert haben", pages[5]);
        Assert.StartsWith("Quellen", pages[6]);
        var toc = pages[1];
        Assert.Contains("Glossar 5", toc);
        Assert.Contains("Menschen, die mich inspiriert haben 6", toc);
        Assert.Contains("Quellen 7", toc);
        // Alphabetical by name, only used people (Dora is neither cited nor an author), authors of a cited source count.
        Assert.Contains("Anna Beispiel (AB)", pages[5]);
        Assert.True(pages[5].IndexOf("Anna Beispiel", StringComparison.Ordinal) < pages[5].IndexOf("Carl Muster", StringComparison.Ordinal));
        Assert.Contains("Wahrheit", pages[5]);
        Assert.DoesNotContain("Dora", string.Join(" ", pages));
        Assert.DoesNotContain("Unbenutzt", string.Join(" ", pages));
        Assert.DoesNotContain("Nie zitiert", string.Join(" ", pages));
    }

    [Fact]
    public void Appendix_PartWithoutUsedEntries_IsLeftOut()
    {
        var pages = Pages(BookText("- # Erstes\n\t- Ein Satz ohne alles.\n"));

        Assert.Equal(3, pages.Count);
        Assert.DoesNotContain("Glossar", string.Join(" ", pages));
        Assert.DoesNotContain("Quellen", string.Join(" ", pages));
    }

    [Fact]
    public void Text_PersonAndSourceSuperscriptsLinked_GlossaryFirstOnly()
    {
        var bytes = PdfExportTests.Export(PdfSamples.Load(BookText(Body()))).Bytes;
        var pages = PdfExportTests.PageTexts(bytes);

        // The raised numbers and abbreviations sit on a baseline of their own, so the extraction lists them apart.
        Assert.Contains("AB AB 1", pages[2]);
        Assert.Contains("Ein Gedanke von und dazu. Achtsamkeit zuerst, achtsam später. S. 12", pages[2]);
        Assert.Contains("2 1 Nochmal Achtsamkeit und und .", pages[3]);

        using var pdf = PdfDocument.Open(bytes);
        var firstChapter = Links(pdf.GetPage(3));
        // AB twice and the source number to the people and sources pages, the glossary mark to the glossary page; the
        // second occurrence of the term and the form "achtsam" carry no link.
        Assert.Equal([5, 6, 6, 7], firstChapter);
        Assert.Equal([7, 7], Links(pdf.GetPage(4)));
    }

    [Fact]
    public void Text_GlossaryMarkInBoldAndAfterCitation_LandsOnTheWord()
    {
        var body = "- # Erstes\n" +
                   $"\t- Davor {Cite(Buch, "S. 3")} und **Achtsamkeit** hier.\n";
        var pages = Pages(BookText(body));

        Assert.Contains("Davor S. 3 und Achtsamkeit hier.", pages[2]);
        using var pdf = PdfDocument.Open(PdfExportTests.Export(PdfSamples.Load(BookText(body))).Bytes);
        // The mark and the citation.
        Assert.Equal(2, Links(pdf.GetPage(3)).Count);
    }

    [Fact]
    public void SourceLine_Format_MissingPartsDropped()
    {
        var pages = Pages(BookText(Body()));
        var sources = pages[6];

        Assert.Contains("[1] Carl Muster: Das Buch. Verlag X, 2020. https://example.org/buch", sources);
        Assert.Contains("[2] Ein Video", sources);
        Assert.DoesNotContain("[2] Ein Video.", sources);
        Assert.DoesNotContain("[3]", sources);
        // The numbers go by the first citation: Buch 1, Video 2.
        Assert.True(sources.IndexOf("[1]", StringComparison.Ordinal) < sources.IndexOf("[2]", StringComparison.Ordinal));
    }

    [Fact]
    public void SourceLine_AuthorLinksToThePersonAndPartsDropIndividually()
    {
        var onlyYear = $"\t- Nur Jahr\n\t  id:: {Buch}\n\t  jahr:: 1999\n";
        var book = "title:: Buch: Test\ntype:: book\n\n- # Erstes\n" + $"\t- Satz {Cite(Buch)}\n" +
                   "- # Quellen\n  anhang:: quellen\n" + onlyYear;
        var pages = Pages(book);

        Assert.Contains("[1] Nur Jahr. 1999", pages[^1]);

        var bytes = PdfExportTests.Export(PdfSamples.Load(BookText(Body()))).Bytes;
        using var pdf = PdfDocument.Open(bytes);
        // The author's name on the sources page links to the people page.
        Assert.Contains(6, Links(pdf.GetPage(7)));
    }

    [Fact]
    public void PartialExport_OnlyThatSectionsEntries_BookNumbers()
    {
        var book = PdfSamples.Load(BookText(Body()));
        var second = book.ContentChildren.Last().Key;
        var pages = PdfExportTests.PageTexts(PdfExportTests.Export(book, second).Bytes);

        // The second chapter cites the video (book number 2) and the book (1); it uses the glossary term, but its first
        // occurrence in the book is in the first chapter, so only the persons and sources of this section are listed.
        Assert.Contains("2 1 Nochmal Achtsamkeit und und .", pages[2]);
        // Carl is the author of the cited book, Anna is cited in the first chapter only.
        Assert.StartsWith("Menschen, die mich inspiriert haben Carl Muster (CM)", pages[^2]);
        var last = pages[^1];
        Assert.StartsWith("Quellen", last);
        Assert.DoesNotContain("Glossar", string.Join(" ", pages));
        Assert.Contains("[1] Carl Muster: Das Buch", last);
        Assert.Contains("[2] Ein Video", last);
        Assert.DoesNotContain("Anna Beispiel", string.Join(" ", pages));
    }

    [Fact]
    public void PartialExport_FirstChapter_ListsItsOwnEntries()
    {
        var book = PdfSamples.Load(BookText(Body()));
        var first = book.ContentChildren.First().Key;
        var text = string.Join("\n", PdfExportTests.PageTexts(PdfExportTests.Export(book, first).Bytes));

        Assert.Contains("Anna Beispiel (AB)", text);
        Assert.Contains("Achtsamkeit", text);
        Assert.Contains("[1] Carl Muster: Das Buch", text);
        Assert.DoesNotContain("Ein Video", text);
    }

    [Fact]
    public void Export_AppendixNodeAsScope_Throws()
    {
        var book = PdfSamples.Load(BookText(Body()));
        var appendixKey = book.AppendixSection(AppendixKind.Glossary)!.Key;

        Assert.Throws<ArgumentException>(() => PdfExportTests.Export(book, appendixKey));
    }

    [Fact]
    public void Text_UnknownSourceAndMissingPerson_ShowMissing()
    {
        var gone = "00000000-0000-4000-8000-0000000000ff";
        var text = string.Join("\n", Pages(BookText($"- # Erstes\n\t- Ein {Cite(gone)} Satz.\n")));

        Assert.Contains("(Verweis fehlt)", text);
    }

    [Fact]
    public void BookWithoutAppendix_PdfUnchanged()
    {
        // Hashes of the extracted texts, taken before the appendix change.
        Assert.Equal(
            "c51f74100bdc49bcc2f2c74f655890cf0c3e169f67109500f4666b203d192e17",
            HashOf(PdfSamples.SpecBookWithTwoChapters));
        Assert.Equal("44ec9ce348928465dc1140ce0a93c12d4a5a75f19ef2d723d3aaeb98b8a51e2b", HashOf(PdfSamples.BookWithRefs));
    }

    private static string HashOf(string bookText) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            PdfExportTests.AllText(PdfExportTests.Export(PdfSamples.Load(bookText)).Bytes)))).ToLowerInvariant();

    /// <summary>The target pages of the page's links in reading order of the annotations.</summary>
    private static List<int> Links(UglyToad.PdfPig.Content.Page page) =>
    [
        .. page.GetAnnotations().Where(a => a.Type == AnnotationType.Link)
            .Select(a => Assert.IsAssignableFrom<AbstractGoToAction>(a.Action).Destination.PageNumber)
            .Order(),
    ];
}
