using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;

namespace NoteEvolution.Core.Tests.Books;

public class AppendixIndexTests
{
    private const string G1 = "0193a1c2-1b7e-7c1d-9a0f-2b3c4d5e6f70";
    private const string G2 = "0193a1c2-1b7e-7c1d-9a0f-2b3c4d5e6f77";
    private const string P1 = "0193a1c2-1b7e-7c1d-9a0f-2b3c4d5e6f71";
    private const string S1 = "0193a1c2-1b7e-7c1d-9a0f-2b3c4d5e6f72";
    private const string S2 = "0193a1c2-1b7e-7c1d-9a0f-2b3c4d5e6f73";
    private const string S3 = "0193a1c2-1b7e-7c1d-9a0f-2b3c4d5e6f74";
    private const string Gone = "0193a1c2-1b7e-7c1d-9a0f-2b3c4d5e6f79";

    private static string Cite(string id, string? locator = null) =>
        "{{quelle ((" + id + "))" + (locator is null ? "" : " " + locator) + "}}";

    private static string Sources(params string[] ids) =>
        "- # Quellen\n  anhang:: quellen\n" +
        string.Concat(ids.Select(id => $"\t- Quelle {id[^1]}\n\t  id:: {id}\n\t  art:: buch\n"));

    private static Book Load(string body, string appendix = "")
    {
        var text = "title:: Buch: Test\ntype:: book\n\n" + body + appendix;
        return Book.Load(LogseqParser.Parse("Buch - Test.md", Encoding.UTF8.GetBytes(text)));
    }

    private static Guid KeyOf(Book book, string startsWith)
    {
        var texts = book.Root.Children.SelectMany(Flatten).OfType<TextBlock>().ToList();
        return texts.Select(t => t.Block).Concat(texts.SelectMany(t => t.Paragraphs.Select(p => p.Block)))
            .First(b => b.Content.StartsWith(startsWith, StringComparison.Ordinal)).Key;
    }

    private static IEnumerable<BookItem> Flatten(OutlineNode node) =>
        node.Items.SelectMany(i => i is OutlineNode n ? Flatten(n).Prepend(n) : [i]);

    [Fact]
    public void SourceNumbers_FirstCitationOrder_RepeatKeepsNumber_RelevantAndRemarkSkipped()
    {
        var book = Load(
            "- # Eins\n" +
            $"\t- Zuerst {Cite(S2, "S. 3")}\n" +
            $"\t- Skip {Cite(S3)}\n\t  status:: relevant\n" +
            $"\t- Dann {Cite(S1)} und wieder {Cite(S2)}\n" +
            $"\t\t- Detail {Cite(S3)} #notiz\n" +
            "- # Zwei\n" +
            "\t- Text\n\t  status:: bemerkung\n" +
            $"\t\t- Unter {Cite(S3)}\n",
            Sources(S1, S2, S3));
        var index = AppendixIndex.Of(book);

        Assert.Equal(1, index.SourceNumber(Guid.Parse(S2)));
        Assert.Equal(2, index.SourceNumber(Guid.Parse(S1)));
        Assert.Null(index.SourceNumber(Guid.Parse(S3)));
        Assert.Null(index.SourceNumber(Guid.NewGuid()));
        // Left-out blocks still refer to the source.
        Assert.Equal(3, index.ReferenceCount(Guid.Parse(S3)));
        Assert.Equal(2, index.ReferenceCount(Guid.Parse(S2)));
        // The PDF's headings and blocks in book order, without the left-out blocks and the appendix.
        Assert.Equal(
            [book.Root.Children.First().Key, KeyOf(book, "Zuerst"), KeyOf(book, "Dann"), book.Root.Children.ElementAt(1).Key],
            index.ReadingOrder);
    }

    [Fact]
    public void Glossary_FirstOccurrence_WholeWords_FormsAndLongest()
    {
        var body =
            "- # Eins\n" +
            "\t- Eine Achtsamkeitsübung ist es nicht, ÜberAchtsamkeit auch nicht.\n" +
            "\t- Doch Achtsamkeit, ja. Und Achtsamkeit nochmal.\n" +
            "\t- Ein achtsames Leben und achtsamer.\n" +
            "\t- Straße und Über.\n" +
            "\t- Am Ende: ACHTSAMKEIT\n";
        var appendix =
            "- # Glossar\n  anhang:: glossar\n" +
            $"\t- Achtsamkeit\n\t  id:: {G1}\n\t  formen:: achtsam, achtsames, achtsames Leben\n" +
            $"\t- Über\n\t  id:: {G2}\n";
        var book = Load(body, appendix);
        var hits = AppendixIndex.Of(book).GlossaryHits;

        Assert.Equal(2, hits.Count);
        var achtsam = hits.Single(h => h.EntryId == Guid.Parse(G1));
        Assert.Equal(KeyOf(book, "Doch"), achtsam.ParagraphKey);
        Assert.Equal(("Doch ".Length, "Achtsamkeit".Length), (achtsam.Start, achtsam.Length));
        var ueber = hits.Single(h => h.EntryId == Guid.Parse(G2));
        Assert.Equal(KeyOf(book, "Straße"), ueber.ParagraphKey);
        Assert.Equal(("Straße und ".Length, 4), (ueber.Start, ueber.Length));
    }

    [Fact]
    public void Glossary_LongestFormWinsAtTheSameStart()
    {
        var book = Load(
            "- # Eins\n\t- Ein achtsames Leben, achtsam.\n",
            $"- # Glossar\n  anhang:: glossar\n\t- Achtsamkeit\n\t  id:: {G1}\n\t  formen:: achtsames, achtsam, achtsames Leben\n");

        var hit = Assert.Single(AppendixIndex.Of(book).GlossaryHits);

        Assert.Equal(("Ein ".Length, "achtsames Leben".Length), (hit.Start, hit.Length));
    }

    [Fact]
    public void Glossary_NotInHeadings_NotInRefElements()
    {
        var book = Load(
            "- # Achtsamkeit\n" +
            $"\t- (({P1})) [[Achtsamkeit]] [Achtsamkeit](https://x.org) {Cite(S1, "Achtsamkeit")}\n" +
            $"\t- {Cite(S1, "S. 1")} **Achtsamkeit** hier\n" +
            "\t- Relevant Achtsamkeit\n\t  status:: relevant\n",
            Sources(S1) + $"- # Glossar\n  anhang:: glossar\n\t- Achtsamkeit\n\t  id:: {G1}\n");
        var index = AppendixIndex.Of(book);

        var hit = Assert.Single(index.GlossaryHits);

        Assert.Equal(KeyOf(book, "{{quelle ((" + S1 + ")) S. 1"), hit.ParagraphKey);
        // The citation counts as one unit and the space as another: the word starts at 2.
        Assert.Equal((2, "Achtsamkeit".Length), (hit.Start, hit.Length));
        // The editor's offset conversion lands on the word in the Markdown.
        var offset = ManuscriptEditor.ContentOffset(book, hit.ParagraphKey, hit.Start)!.Value;
        var content = book.FindTextBlock(hit.ParagraphKey)!.Block.Content;
        Assert.StartsWith("**Achtsamkeit", content[offset..]);
    }

    [Fact]
    public void ReferenceCount_CountsTextAndAuthorUses()
    {
        var book = Load(
            "- # Eins\n" +
            $"\t- Von (({P1})) und {Cite(S1)} und noch einmal (({P1})).\n" +
            $"\t- Skip (({P1}))\n\t  status:: relevant\n",
            $"- # Menschen\n  anhang:: menschen\n\t- Eva\n\t  id:: {P1}\n\t  kuerzel:: E\n" +
            $"- # Quellen\n  anhang:: quellen\n\t- Buch\n\t  id:: {S1}\n\t  autoren:: (({P1}))\n");
        var index = AppendixIndex.Of(book);

        Assert.Equal(4, index.ReferenceCount(Guid.Parse(P1)));
        Assert.Equal(1, index.ReferenceCount(Guid.Parse(S1)));
        Assert.Equal(0, index.ReferenceCount(Guid.Parse(G1)));
        Assert.Empty(index.MissingTargets);
    }

    [Fact]
    public void MissingTargets_DeletedPersonAndSource()
    {
        var book = Load(
            "- # Eins\n" +
            $"\t- Weg {Cite(S3, "S. 2")} und (({Gone}))\n",
            $"- # Quellen\n  anhang:: quellen\n\t- Buch\n\t  id:: {S1}\n\t  autoren:: (({P1}))\n");
        var index = AppendixIndex.Of(book);

        Assert.Equal(
            [(KeyOf(book, "Weg"), Guid.Parse(S3)), (Appendix.Sources(book).Single().Key, Guid.Parse(P1))],
            index.MissingTargets);
        // A missing ((id)) in the text stays a cross reference (CrossRefIndex.Missing), not a missing appendix target.
        Assert.Single(CrossRefIndex.Of(book).Missing);
    }
}
