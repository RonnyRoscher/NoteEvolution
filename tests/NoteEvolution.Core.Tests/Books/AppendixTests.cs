using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;

namespace NoteEvolution.Core.Tests.Books;

public class AppendixTests
{
    private const string G1 = "0193a1c2-1b7e-7c1d-9a0f-2b3c4d5e6f70";
    private const string P1 = "0193a1c2-1b7e-7c1d-9a0f-2b3c4d5e6f71";
    private const string S1 = "0193a1c2-1b7e-7c1d-9a0f-2b3c4d5e6f72";

    private const string Body =
        "title:: Buch: Test\n" +
        "type:: book\n" +
        "\n" +
        "- # Eins\n" +
        "\t- Text eins\n";

    private const string Full =
        Body +
        "- # Glossar\n" +
        "  anhang:: glossar\n" +
        "\t- Achtsamkeit\n" +
        $"\t  id:: {G1}\n" +
        "\t  formen:: Achtsam, achtsames\n" +
        "\t  farbe:: blau\n" +
        "\t  Bewusstes Wahrnehmen.\n" +
        "\t  Zweite Zeile.\n" +
        "- # Menschen, die mich inspiriert haben\n" +
        "  anhang:: menschen\n" +
        "\t- Marshall B. Rosenberg\n" +
        $"\t  id:: {P1}\n" +
        "\t  kuerzel:: MBR\n" +
        "\t  themen:: Sprache, Empathie\n" +
        "\t  farbe:: rot\n" +
        "\t  Begründer der GFK.\n" +
        "- # Quellen\n" +
        "  anhang:: quellen\n" +
        "\t- Gewaltfreie Kommunikation\n" +
        $"\t  id:: {S1}\n" +
        "\t  art:: buch\n" +
        $"\t  autoren:: (({P1}))\n" +
        "\t  jahr:: 2001\n" +
        "\t  url:: https://example.org\n" +
        "\t  herausgeber:: Junfermann\n" +
        "\t  farbe:: grün\n";

    private static Book Load(string text) =>
        Book.Load(LogseqParser.Parse("Buch - Test.md", Encoding.UTF8.GetBytes(text)));

    private static string Serialize(Book book) => Encoding.UTF8.GetString(PageSerializer.Serialize(book.Page));

    private static Book Reload(Book book) => Book.Load(book.Page);

    [Fact]
    public void RoundTrip_EachKind_KeepsUnknownPropertiesAndId()
    {
        var book = Load(Full);
        Assert.Equal("Achtsamkeit", Appendix.Glossary(book).Single().Term);
        Assert.Equal(["Achtsam", "achtsames"], Appendix.Glossary(book).Single().Forms);
        Assert.Equal("Bewusstes Wahrnehmen.\nZweite Zeile.", Appendix.Glossary(book).Single().Explanation);
        var person = Appendix.People(book).Single();
        Assert.Equal(("Marshall B. Rosenberg", "MBR"), (person.Name, person.Abbreviation));
        Assert.Equal([Guid.Parse(P1)], Appendix.Sources(book).Single().AuthorIds);
        Assert.Equal(person.Key, Appendix.PersonById(book, Guid.Parse(P1))!.Key);
        Assert.Equal(SourceKind.Book, Appendix.SourceById(book, Guid.Parse(S1))!.Kind);
        Assert.Null(Appendix.PersonById(book, Guid.NewGuid()));

        Appendix.Save(book, Appendix.Glossary(book).Single());
        Appendix.Save(book, person);
        Appendix.Save(book, Appendix.Sources(book).Single());

        Assert.Equal(Full, Serialize(book));
        var again = Reload(book);
        Assert.Equal(["Sprache", "Empathie"], Appendix.People(again).Single().Topics);
        Assert.Equal("https://example.org", Appendix.Sources(again).Single().Url);
    }

    [Fact]
    public void Add_CreatesTheSectionInOrder_GlossaryPeopleSources()
    {
        var book = Load(Body);
        var source = Appendix.Add(book, AppendixKind.Sources);
        var term = Appendix.Add(book, AppendixKind.Glossary);

        var fresh = Reload(book);
        Assert.Equal(["Eins", "Glossar", "Quellen"], fresh.Root.Children.Select(n => n.Title));
        Assert.Equal([AppendixKind.None, AppendixKind.Glossary, AppendixKind.Sources], fresh.Root.Children.Select(n => n.AppendixKind));

        var person = Appendix.Add(book, AppendixKind.People);
        fresh = Reload(book);
        Assert.Equal(
            ["Eins", "Glossar", "Menschen, die mich inspiriert haben", "Quellen"], fresh.Root.Children.Select(n => n.Title));
        Assert.Equal(person, Appendix.People(fresh).Single().Key);
        Assert.Equal(term, Appendix.Glossary(fresh).Single().Key);
        Assert.Equal(source, Appendix.Sources(fresh).Single().Key);
        Assert.All(new[] { person, term, source }, key => Assert.NotEqual(Guid.Empty, fresh.FindTextBlock(key)!.Block.Id));

        // A second entry joins the existing section and the entries carry no dates.
        Appendix.Add(book, AppendixKind.Glossary);
        Assert.Equal(2, Appendix.Glossary(Reload(book)).Count);
        Assert.DoesNotContain("created::", Serialize(book));
        Assert.DoesNotContain("updated::", Serialize(book));
        Assert.Contains("  anhang:: glossar\n", Serialize(book));
    }

    [Fact]
    public void EntryWithoutId_GetsOneOnSave()
    {
        var book = Load(Body + "- # Glossar\n  anhang:: glossar\n\t- Mut\n\t  Tapferkeit.\n");
        var entry = Appendix.Glossary(book).Single();
        Assert.Equal(Guid.Empty, entry.Id);

        Appendix.Save(book, entry with { Explanation = "Wagemut." });

        var saved = Appendix.Glossary(Reload(book)).Single();
        Assert.NotEqual(Guid.Empty, saved.Id);
        Assert.Equal(("Mut", "Wagemut."), (saved.Term, saved.Explanation));
        Assert.Equal(entry.Key, saved.Key);
    }

    [Fact]
    public void SuggestAbbreviation_Initials_ThenDigit()
    {
        var book = Load(Body);
        Assert.Equal("MBR", Appendix.SuggestAbbreviation(book, "Marshall B. Rosenberg"));

        var first = Appendix.Add(book, AppendixKind.People);
        Appendix.Save(book, Appendix.People(Reload(book)).Single() with { Name = "Marshall B. Rosenberg", Abbreviation = "MBR" });
        Assert.Equal("MBR2", Appendix.SuggestAbbreviation(Reload(book), "Maria B. Ruiz"));
        Assert.Equal("MBR", Appendix.SuggestAbbreviation(Reload(book), "Maria B. Ruiz", first));
        Assert.Equal("ÄÖ", Appendix.SuggestAbbreviation(Reload(book), "änne öz"));
    }

    [Fact]
    public void Validation_AbbreviationTakenIgnoringCase_InvalidCharacters_EmptyName()
    {
        var book = Load(Full);
        var other = Appendix.People(book).Single() with { Key = Guid.NewGuid() };

        Assert.Equal("AppendixAbbreviationTaken", Appendix.ValidationError(book, other with { Abbreviation = "mbr" }));
        Assert.Null(Appendix.ValidationError(book, Appendix.People(book).Single()));
        Assert.Equal("AppendixAbbreviationInvalid", Appendix.ValidationError(book, other with { Abbreviation = "A B" }));
        Assert.Equal("AppendixAbbreviationInvalid", Appendix.ValidationError(book, other with { Abbreviation = "ABCDEFG" }));
        Assert.Equal("AppendixAbbreviationInvalid", Appendix.ValidationError(book, other with { Abbreviation = "" }));
        Assert.Equal("AppendixAbbreviationInvalid", Appendix.ValidationError(book, other with { Abbreviation = "A_1" }));
        Assert.Null(Appendix.ValidationError(book, other with { Abbreviation = "A.-1" }));
        Assert.Equal("AppendixNameEmpty", Appendix.ValidationError(book, other with { Name = "  ", Abbreviation = "X" }));

        Assert.Equal("AppendixTermEmpty", Appendix.ValidationError(book, Appendix.Glossary(book).Single() with { Term = " " }));
        Assert.Null(Appendix.ValidationError(book, Appendix.Glossary(book).Single()));
        Assert.Equal("AppendixTitleEmpty", Appendix.ValidationError(book, Appendix.Sources(book).Single() with { Title = "" }));
        Assert.Null(Appendix.ValidationError(book, Appendix.Sources(book).Single()));
    }

    [Fact]
    public void EnsureAuthors_KnownByName_NewOnesCreated()
    {
        var book = Load(Full);

        var ids = Appendix.EnsureAuthors(book, ["marshall b. rosenberg", "Maria B. Ruiz", "MARIA B. RUIZ", "  "]);

        Assert.Equal(Guid.Parse(P1), ids[0]);
        Assert.Equal(ids[1], ids[2]);
        Assert.Equal(3, ids.Count);
        var people = Appendix.People(Reload(book));
        Assert.Equal(2, people.Count);
        var created = people[1];
        Assert.Equal((ids[1], "Maria B. Ruiz", "MBR2"), (created.Id, created.Name, created.Abbreviation));
    }

    [Fact]
    public void Save_EmptyFieldRemovesItsProperty()
    {
        var book = Load(Full);

        Appendix.Save(book, Appendix.Glossary(book).Single() with { Forms = [], Explanation = "" });
        Appendix.Save(book, Appendix.People(book).Single() with { Topics = [], Description = "" });
        Appendix.Save(book, Appendix.Sources(book).Single() with { AuthorIds = [], Year = null, Url = " ", Publisher = null });

        var text = Serialize(book);
        Assert.DoesNotContain("formen::", text);
        Assert.DoesNotContain("themen::", text);
        Assert.DoesNotContain("autoren::", text);
        Assert.DoesNotContain("jahr::", text);
        Assert.DoesNotContain("url::", text);
        Assert.DoesNotContain("herausgeber::", text);
        Assert.DoesNotContain("Bewusstes", text);
        Assert.DoesNotContain("Begründer", text);
        Assert.Contains("farbe:: blau", text);
        Assert.Contains("farbe:: rot", text);
        Assert.Contains("farbe:: grün", text);
        Assert.Contains($"id:: {G1}", text);
        Assert.Contains("kuerzel:: MBR", text);
    }

    [Fact]
    public void Save_WritesListsAndEscapesPropertyShapedText()
    {
        var book = Load(Body);
        var key = Appendix.Add(book, AppendixKind.Glossary);
        var id = Appendix.Glossary(Reload(book)).Single().Id;

        Appendix.Save(book, new GlossaryEntry(key, id, "Term", ["a", "b"], "Zeile eins\nregel:: nein\n- kein Aufzählungspunkt"));

        var entry = Appendix.Glossary(Reload(book)).Single();
        Assert.Contains("formen:: a, b", Serialize(book));
        Assert.Equal("Zeile eins\nregel:: nein\n- kein Aufzählungspunkt", entry.Explanation);
        Assert.Equal(id, entry.Id);
    }

    [Fact]
    public void Delete_RemovesTheEntryBlock_AndRejectsOtherBlocks()
    {
        var book = Load(Full);
        var key = Appendix.Glossary(book).Single().Key;

        Appendix.Delete(book, key);

        Assert.Empty(Appendix.Glossary(Reload(book)));
        Assert.Contains("anhang:: glossar", Serialize(book));
        var textKey = Reload(book).Root.Children.First().TextBlocks.Single().Key;
        Assert.Throws<ArgumentException>(() => Appendix.Delete(book, textKey));
    }
}
