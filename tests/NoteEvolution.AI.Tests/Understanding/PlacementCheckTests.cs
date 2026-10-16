using System.Text.Json;
using NoteEvolution.AI.Relevance;
using NoteEvolution.AI.Understanding;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;

namespace NoteEvolution.AI.Tests.Understanding;

public class PlacementCheckTests
{
    private const string BookBody =
        "- # Kapitel A\n\t- Erster Absatz in A.\n\t- Zweiter Absatz in A.\n" +
        "\t- ## Unter A1\n\t\t- Text unter A1.\n\t\t- Mehr unter A1.\n\t\t- Noch mehr unter A1.\n" +
        "\t- ## Unter A2\n\t\t- Text unter A2.\n" +
        "- # Kapitel B\n\t- ## Unter B\n\t\t- Text unter B.\n" +
        "- # Kapitel C\n\t- Text in C.\n" +
        "- # Kapitel D\n\t- Text in D.\n";

    private sealed record Setup(Book Book, OutlineNode A, OutlineNode A1, OutlineNode A2, OutlineNode B, OutlineNode UnterB, OutlineNode C)
    {
        public Guid Block(string text) => Blocks(Book.Root).Single(tb => tb.Text == text).Key;

        private static IEnumerable<TextBlock> Blocks(OutlineNode node) => node.TextBlocks.Concat(node.Children.SelectMany(Blocks));
    }

    private static Setup Load()
    {
        var tv = TestVault.Create(("pages/Buch.md", "title:: Buch\ntype:: book\n\n" + BookBody));
        var book = tv.Open().Books.Single();
        var a = book.Root.Children.Single(n => n.Title == "Kapitel A");
        var b = book.Root.Children.Single(n => n.Title == "Kapitel B");
        return new Setup(book, a, a.Children.First(), a.Children.Last(), b, b.Children.Single(),
            book.Root.Children.Single(n => n.Title == "Kapitel C"));
    }

    private static PlacementHit Hit(Setup s, string text, double score) =>
        new(s.Block(text), score, s.Book.FindTextBlock(s.Block(text))!.Section.Key);

    /// <summary>Topics A1 (best), C, A2, B, D; places of A, A1 and C.</summary>
    private static PlacementCheckPrompt Build(Setup s, string note = "Meine Notiz", Func<OutlineNode, string?>? summaryOf = null)
    {
        var topics = new[]
        {
            new TopicHit(s.A1.Key, 0.8, "A1 handelt von Eins. Und mehr."), new TopicHit(s.C.Key, 0.7, null),
            new TopicHit(s.A2.Key, 0.6, null), new TopicHit(s.B.Key, 0.5, "Alles über B."),
            new TopicHit(s.Book.Root.Children.Single(n => n.Title == "Kapitel D").Key, 0.4, null),
        };
        var hits = new[]
        {
            Hit(s, "Text unter A1.", 0.9), Hit(s, "Mehr unter A1.", 0.85), Hit(s, "Noch mehr unter A1.", 0.84),
            Hit(s, "Erster Absatz in A.", 0.8), Hit(s, "Text in C.", 0.7),
        };
        var places = hits.GroupBy(h => h.SectionKey).ToDictionary(g => g.Key, g => (IReadOnlyList<PlacementHit>)[.. g]);
        return PlacementCheck.Prompt(s.Book, note, topics, places, summaryOf);
    }

    [Fact]
    public void Prompt_SectionsOfTheThreeBestChapters_InBookOrder()
    {
        var s = Load();
        var prompt = Build(s);

        // The best chapters are A (own text, then its sub-headings), C (no sub-headings) and B (only its sub-heading); not D.
        Assert.Equal([s.A.Key, s.A1.Key, s.A2.Key, s.C.Key, s.UnterB.Key], prompt.Topics.Select(t => t.SectionKey));
        Assert.Equal([1, 2, 3, 4, 5], prompt.Topics.Select(t => t.Number));
        Assert.StartsWith("Du ordnest eine Notiz in ein Buch ein.", prompt.System);
        Assert.Contains("Meine Notiz", prompt.User);
        Assert.Contains("1. Kapitel A\n", prompt.User);
        Assert.Contains("2. Kapitel A › Unter A1 – A1 handelt von Eins.\n", prompt.User);
        Assert.Contains("3. Kapitel A › Unter A2\n", prompt.User);
        Assert.Contains("4. Kapitel C\n", prompt.User);
        Assert.Contains("5. Kapitel B › Unter B", prompt.User);
        Assert.DoesNotContain("Kapitel D", prompt.User);
    }

    [Fact]
    public void Prompt_BestPlaces_AtMostTwoPerSection()
    {
        var s = Load();
        var prompt = Build(s);

        Assert.Equal([s.Block("Erster Absatz in A.")], prompt.Topics[0].TextBlockKeys);
        Assert.Equal([s.Block("Text unter A1."), s.Block("Mehr unter A1.")], prompt.Topics[1].TextBlockKeys);
        Assert.Empty(prompt.Topics[2].TextBlockKeys);
        Assert.Equal([s.Block("Text in C.")], prompt.Topics[3].TextBlockKeys);
        Assert.Contains("   1.1 nach: Erster Absatz in A.", prompt.User);
        Assert.Contains("   2.1 nach: Text unter A1.", prompt.User);
        Assert.Contains("   2.2 nach: Mehr unter A1.", prompt.User);
        Assert.Contains("   4.1 nach: Text in C.", prompt.User);
        Assert.DoesNotContain("Noch mehr unter A1.", prompt.User);
    }

    [Fact]
    public void Prompt_SummaryOfEverySection_FromUnderstanding()
    {
        var s = Load();
        var prompt = Build(s, summaryOf: node => node.Key == s.A2.Key ? "Zwei, z. B. Schlaf. Rest." : null);

        Assert.Contains("3. Kapitel A › Unter A2 – Zwei, z. B. Schlaf.\n", prompt.User);
        Assert.Contains("2. Kapitel A › Unter A1 – A1 handelt von Eins.\n", prompt.User);
        Assert.Equal("Zwei, z. B. Schlaf. Rest.", prompt.Topics[2].Summary);
    }

    [Fact]
    public void Prompt_Schema_LimitsStelleToTheOfferedPlaces()
    {
        var s = Load();
        var prompt = Build(s);

        using var schema = JsonDocument.Parse(prompt.Schema);
        var properties = schema.RootElement.GetProperty("properties");
        Assert.Equal(["begruendung", "stelle"], properties.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["1.0", "1.1", "2.0", "2.1", "2.2", "3.0", "4.0", "4.1", "5.0"],
            properties.GetProperty("stelle").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(PlacementCheck.Options(prompt).Schema, prompt.Schema);
        Assert.True(PlacementCheck.Options(prompt).Json);
    }

    [Fact]
    public void Prompt_TopicsNotInBook_LeftOut()
    {
        var s = Load();
        var prompt = PlacementCheck.Prompt(s.Book, "x", [new TopicHit(Guid.NewGuid(), 0.9, null), new TopicHit(s.UnterB.Key, 0.5, null)],
            new Dictionary<Guid, IReadOnlyList<PlacementHit>>());

        Assert.Equal([s.UnterB.Key], prompt.Topics.Select(t => t.SectionKey));
    }

    [Fact]
    public void Parse_Place_GivesSectionBlockAndSummary()
    {
        var s = Load();
        var prompt = Build(s);

        var verdict = PlacementCheck.Parse("{\"begruendung\":\"Passt dazu.\",\"stelle\":\"2.2\"}", prompt);

        Assert.Equal(new PlacementVerdict(s.A1.Key, s.Block("Mehr unter A1."), "Passt dazu.", "A1 handelt von Eins. Und mehr."), verdict);
    }

    [Theory]
    [InlineData("\"3.0\"")]
    [InlineData("\"3\"")]
    [InlineData("3")]
    public void Parse_NewPlaceAtEnd_NullAfterKey(string stelle)
    {
        var s = Load();
        var verdict = PlacementCheck.Parse("{\"begruendung\":\"Neu.\",\"stelle\":" + stelle + "}", Build(s));

        Assert.Equal(new PlacementVerdict(s.A2.Key, null, "Neu."), verdict);
    }

    [Fact]
    public void Parse_NumberWithPlace()
    {
        var s = Load();

        Assert.Equal(s.Block("Text in C."), PlacementCheck.Parse("{\"stelle\":4.1}", Build(s))!.AfterTextBlockKey);
    }

    [Theory]
    [InlineData("kein json")]
    [InlineData("{\"stelle\":")]
    [InlineData("{\"begruendung\":\"x\"}")]
    [InlineData("{\"stelle\":\"6.0\"}")]
    [InlineData("{\"stelle\":\"3.1\"}")]
    [InlineData("{\"stelle\":\"1.1.1\"}")]
    [InlineData("{\"stelle\":\"eins\"}")]
    public void Parse_Invalid_Null(string answer)
    {
        Assert.Null(PlacementCheck.Parse(answer, Build(Load())));
    }

    [Fact]
    public void Parse_ReasonTrimmedTo300()
    {
        var verdict = PlacementCheck.Parse("{\"begruendung\":\"" + new string('x', 500) + "\",\"stelle\":\"1.0\"}", Build(Load()));

        Assert.Equal(300, verdict!.Reason.Length);
    }

    [Fact]
    public void Prompt_NoteOver4000_Capped()
    {
        var note = "  " + new string('a', 3000) + new string('b', 2000) + "  ";
        var prompt = Build(Load(), note);

        Assert.Contains(new string('a', 3000) + new string('b', 1000) + "\n", prompt.User);
        Assert.DoesNotContain(new string('b', 1001), prompt.User);
    }
}
