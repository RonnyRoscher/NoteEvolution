using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;

namespace NoteEvolution.Core.Tests.Books;

/// <summary>
/// Entries written in Logseq without an <c>id::</c> are usable: a glossary term is hit, a person or source is inserted
/// (and gets its id then), a person is found as an author by name (and gets its id then).
/// </summary>
public class AppendixWithoutIdTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 30, 0, TimeSpan.Zero);

    private const string Body =
        "title:: Buch: Test\n" +
        "type:: book\n" +
        "\n" +
        "- # Eins\n" +
        "\t- Ein Satz über Achtsamkeit.\n";

    private const string AppendixText =
        "- # Glossar\n" +
        "  anhang:: glossar\n" +
        "\t- Achtsamkeit\n" +
        "\t  Bewusstes Wahrnehmen.\n" +
        "- # Menschen, die mich inspiriert haben\n" +
        "  anhang:: menschen\n" +
        "\t- Anna Berg\n" +
        "\t  kuerzel:: AB\n" +
        "- # Quellen\n" +
        "  anhang:: quellen\n" +
        "\t- Achtsam leben\n" +
        "\t  art:: buch\n";

    private static Book Load(string text) =>
        Book.Load(LogseqParser.Parse("Buch - Test.md", Encoding.UTF8.GetBytes(text)));

    private static string Serialize(Book book) => Encoding.UTF8.GetString(PageSerializer.Serialize(book.Page));

    private static Guid SentenceKey(Book book) => book.ContentChildren.Single().TextBlocks.Single().Key;

    [Fact]
    public void GlossaryHit_EntryWithoutId_IsKeyedByItsBlock()
    {
        var book = Load(Body + AppendixText);
        var entry = Appendix.Glossary(book).Single();
        Assert.Equal(Guid.Empty, entry.Id);

        var hit = Assert.Single(AppendixIndex.Of(book).GlossaryHits);

        Assert.Equal(new GlossaryHit(entry.Key, SentenceKey(book), "Ein Satz über ".Length, "Achtsamkeit".Length), hit);
        // Reading changes nothing in the file.
        Assert.Equal(Body + AppendixText, Serialize(book));
    }

    [Fact]
    public void InsertAppendixRef_PersonWithoutId_GetsItsIdWithTheReference()
    {
        var book = Load(Body + AppendixText);
        var person = Appendix.People(book).Single();
        Assert.Equal(Guid.Empty, person.Id);

        ManuscriptEditor.InsertAppendixRef(book, SentenceKey(book), "Ein Satz".Length, AppendixKind.People, person.Key, null, Now);

        var reloaded = Book.Load(book.Page);
        var id = Appendix.People(reloaded).Single().Id;
        Assert.NotEqual(Guid.Empty, id);
        var text = Serialize(book);
        Assert.Contains($"\t- Ein Satz(({id:D})) über Achtsamkeit.\n", text);
        Assert.Contains($"\t- Anna Berg\n\t  kuerzel:: AB\n\t  id:: {id:D}\n", text);
        Assert.Equal(1, AppendixIndex.Of(reloaded).ReferenceCount(id));
    }

    [Fact]
    public void InsertAppendixRef_SourceWithoutId_CitationWithLocator()
    {
        var book = Load(Body + AppendixText);
        var source = Appendix.Sources(book).Single();

        ManuscriptEditor.InsertAppendixRef(book, SentenceKey(book), 0, AppendixKind.Sources, source.Key, "S. 12", Now);

        var id = Appendix.Sources(Book.Load(book.Page)).Single().Id;
        Assert.NotEqual(Guid.Empty, id);
        Assert.Contains("\t- {{quelle ((" + id.ToString("D") + ")) S. 12}}Ein Satz über Achtsamkeit.\n", Serialize(book));
    }

    [Fact]
    public void InsertAppendixRef_EntryWithId_KeepsIt_WrongKindOrKeyRefused()
    {
        const string withId = "0193a1c2-1b7e-7c1d-9a0f-2b3c4d5e6f71";
        var text = Body + AppendixText.Replace("\t  kuerzel:: AB\n", $"\t  kuerzel:: AB\n\t  id:: {withId}\n");
        var book = Load(text);
        var person = Appendix.People(book).Single();

        Assert.Throws<ArgumentException>(() =>
            ManuscriptEditor.InsertAppendixRef(book, SentenceKey(book), 0, AppendixKind.Sources, person.Key, null, Now));
        Assert.Throws<ArgumentException>(() =>
            ManuscriptEditor.InsertAppendixRef(book, SentenceKey(book), 0, AppendixKind.People, Guid.NewGuid(), null, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ManuscriptEditor.InsertAppendixRef(book, SentenceKey(book), 0, AppendixKind.Glossary, person.Key, null, Now));
        Assert.Equal(text, Serialize(book));

        ManuscriptEditor.InsertAppendixRef(book, SentenceKey(book), 0, AppendixKind.People, person.Key, null, Now);

        var after = Serialize(book);
        Assert.Contains($"\t- (({withId}))Ein Satz über Achtsamkeit.\n", after);
        Assert.EndsWith(text[text.IndexOf("- # Glossar", StringComparison.Ordinal)..], after);
    }

    [Fact]
    public void EnsureAuthors_PersonWithoutId_MatchedByNameAndGetsItsId()
    {
        var book = Load(Body + AppendixText);

        var ids = Appendix.EnsureAuthors(book, ["anna berg"]);

        var people = Appendix.People(Book.Load(book.Page));
        var anna = Assert.Single(people);
        Assert.NotEqual(Guid.Empty, anna.Id);
        Assert.Equal([anna.Id], ids);
    }
}
