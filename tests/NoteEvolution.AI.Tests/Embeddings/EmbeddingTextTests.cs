using NoteEvolution.AI.Embeddings;
using NoteEvolution.AI.Model;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Vaults;
using NoteEvolution.TestSupport;

namespace NoteEvolution.AI.Tests.Embeddings;

public class EmbeddingTextTests
{
    private static NoteBlock FirstWhere(TestVault tv, Func<NoteBlock, bool> predicate) =>
        new NoteRepository(tv.Open()).All().First(predicate);

    private static Book OpenBook(TestVault tv) => tv.Open().Books.Single();

    private static OutlineNode Section(Book book, string title) =>
        Walk(book.Root).First(n => n.Title == title);

    private static IEnumerable<OutlineNode> Walk(OutlineNode node)
    {
        yield return node;
        foreach (var child in node.Children.SelectMany(Walk))
        {
            yield return child;
        }
    }

    [Fact]
    public void ForNote_JournalChild_DateParentOwnAndDescendants()
    {
        using var tv = TestVault.Create(("journals/2026_03_01.md",
            "- Reise planen\n  id:: 6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70\n  mehr zum Thema\n" +
            "\t- Zug nehmen\n  \t  used-in:: [[Buch]]\n\t\t- Tickets\n\t\t\t- Preis prüfen\n\t- Hotel\n"));
        var note = FirstWhere(tv, n => n.Block.Content == "Zug nehmen");

        Assert.Equal("passage: 2026-03-01\nReise planen\nZug nehmen\nTickets\nPreis prüfen", EmbeddingText.ForNote(note));
    }

    [Fact]
    public void ForNote_PageBlockWithoutDateOrParent()
    {
        using var tv = TestVault.Create(("pages/Idee.md", "- Idee A\n\t- Detail\n- Idee B\n"));
        var note = FirstWhere(tv, n => n.Block.Content == "Idee A");

        Assert.Equal("passage: Idee A\nDetail", EmbeddingText.ForNote(note));
    }

    [Fact]
    public void ForNote_ParentContributesOnlyItsFirstLine()
    {
        using var tv = TestVault.Create(("journals/2026_03_01.md", "- Eltern\n  zweite Zeile\n\t- Kind\n"));
        var note = FirstWhere(tv, n => n.Block.Content == "Kind");

        Assert.Equal("passage: 2026-03-01\nEltern\nKind", EmbeddingText.ForNote(note));
    }

    [Fact]
    public void ForHeadingPath_RootIsBookTitle_NestedJoinedWithSlash()
    {
        using var tv = TestVault.Create(("pages/Buch.md", Samples.SpecBook));
        var book = OpenBook(tv);

        Assert.Equal("query: Buch: LoveMagic", EmbeddingText.ForHeadingPath(book, book.Root));
        Assert.Equal("query: Liebe und Wahrheit", EmbeddingText.ForHeadingPath(book, Section(book, "Liebe und Wahrheit")));
        Assert.Equal("query: Liebe und Wahrheit / Vertrauen", EmbeddingText.ForHeadingPath(book, Section(book, "Vertrauen")));
    }

    [Fact]
    public void ForTextBlock_SkipsNoteParagraphs()
    {
        using var tv = TestVault.Create(("pages/Buch.md", Samples.SpecBook));
        var book = OpenBook(tv);
        var tb = Section(book, "Vertrauen").TextBlocks.Single();

        Assert.Equal(
            "query: Angst baut Widerstand auf, Vertrauen baut Schwung auf.\nGute Interpretationsvarianten zu sehen ist trainierbar.",
            EmbeddingText.ForTextBlock(tb));
    }

    [Fact]
    public void ForTextBlock_UnescapesStageOneEscapes()
    {
        using var tv = TestVault.Create(("pages/Buch.md",
            "type:: book\n\n- # Kapitel\n\t- Einleitung\n\t  \\- kein Punkt\n\t  fazit:\\: gut\n"));
        var tb = Section(OpenBook(tv), "Kapitel").TextBlocks.Single();

        Assert.Equal("query: Einleitung\n- kein Punkt\nfazit:: gut", EmbeddingText.ForTextBlock(tb));
    }

    [Fact]
    public void ForCursor_FirstBlockHasNoPredecessor()
    {
        using var tv = TestVault.Create(("pages/Buch.md",
            "type:: book\ntitle:: B\n\n- # Kapitel\n\t- eins\n\t- zwei\n\t- ## Unter\n\t\t- fremd\n\t- drei\n"));
        var book = OpenBook(tv);
        var blocks = Section(book, "Kapitel").TextBlocks.ToList();

        Assert.Equal("query: Kapitel\neins\nzwei", EmbeddingText.ForCursor(book, blocks[0]));
        Assert.Equal("query: Kapitel\neins\nzwei\ndrei", EmbeddingText.ForCursor(book, blocks[1]));
        Assert.Equal("query: Kapitel\nzwei\ndrei", EmbeddingText.ForCursor(book, blocks[2]));
    }

    [Fact]
    public void ForCursor_UsesNestedHeadingPath()
    {
        using var tv = TestVault.Create(("pages/Buch.md", Samples.SpecBook));
        var book = OpenBook(tv);
        var tb = Section(book, "Vertrauen").TextBlocks.Single();

        Assert.StartsWith("query: Liebe und Wahrheit / Vertrauen\nAngst baut Widerstand auf", EmbeddingText.ForCursor(book, tb));
    }

    [Fact]
    public void ForSearch_TrimsText() =>
        Assert.Equal("query: Vertrauen", EmbeddingText.ForSearch("  Vertrauen \n"));

    [Fact]
    public void ForModel_E5_KeepsMarkers()
    {
        Assert.Equal("query: a", EmbeddingText.ForModel("query: a", ModelCatalog.E5Small));
        Assert.Equal("passage: b", EmbeddingText.ForModel("passage: b", ModelCatalog.E5Small));
    }

    [Fact]
    public void ForModel_BgeM3_RemovesMarkers()
    {
        Assert.Equal("a", EmbeddingText.ForModel("query: a", ModelCatalog.BgeM3));
        Assert.Equal("b", EmbeddingText.ForModel("passage: b", ModelCatalog.BgeM3));
        Assert.Equal("ohne", EmbeddingText.ForModel("ohne", ModelCatalog.BgeM3));
        Assert.Equal("xquery: a", EmbeddingText.ForModel("xquery: a", ModelCatalog.BgeM3));
    }

    [Fact]
    public void Hash_DependsOnModelAndText()
    {
        var hash = EmbeddingText.Hash("m1", "text");

        Assert.Equal(64, hash.Length);
        Assert.Equal(hash.ToLowerInvariant(), hash);
        Assert.Equal(hash, EmbeddingText.Hash("m1", "text"));
        Assert.NotEqual(hash, EmbeddingText.Hash("m2", "text"));
        Assert.NotEqual(hash, EmbeddingText.Hash("m1", "text2"));
        Assert.Equal(
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("m1\ntext"u8.ToArray())), hash);
    }
}
