using NoteEvolution.AI.Relevance;
using NoteEvolution.AI.Understanding;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;

namespace NoteEvolution.AI.Tests.Understanding;

public class PlacementCheckTests
{
    private const string BookBody =
        "- # Kapitel A\n\t- Erster Absatz in A.\n\t- Zweiter Absatz in A.\n" +
        "\t- ## Unter A\n\t\t- Text unter A.\n" +
        "- # Kapitel B\n\t- Text in B.\n";

    private sealed record Setup(Book Book, Guid A, Guid UnterA, Guid B, IReadOnlyList<Guid> ABlocks, Guid BBlock);

    private static Setup Load()
    {
        var tv = TestVault.Create(("pages/Buch.md", "title:: Buch\ntype:: book\n\n" + BookBody));
        var book = tv.Open().Books.Single();
        var a = book.Root.Children.Single(n => n.Title == "Kapitel A");
        var b = book.Root.Children.Single(n => n.Title == "Kapitel B");
        var setup = new Setup(book, a.Key, a.Children.Single().Key, b.Key, a.TextBlocks.Select(t => t.Key).ToList(),
            b.TextBlocks.Single().Key);
        return setup;
    }

    private static PlacementCheckPrompt Build(Setup s, string note = "Meine Notiz")
    {
        var topics = new[] { new TopicHit(s.A, 0.8, "Alles über A."), new TopicHit(s.UnterA, 0.7, null), new TopicHit(s.B, 0.5, "Alles über B.") };
        var places = new Dictionary<Guid, IReadOnlyList<PlacementHit>>
        {
            [s.A] = [new PlacementHit(s.ABlocks[0], 0.9, s.A), new PlacementHit(s.ABlocks[1], 0.8, s.A)],
            [s.B] = [new PlacementHit(s.BBlock, 0.6, s.B)],
        };
        return PlacementCheck.Prompt(s.Book, note, topics, places);
    }

    [Fact]
    public void Prompt_NumbersTopicsAndPlaces_WithSummariesAndStarts()
    {
        var s = Load();
        var prompt = Build(s);

        Assert.Equal(3, prompt.Topics.Count);
        Assert.Equal([1, 2, 3], prompt.Topics.Select(t => t.Number));
        Assert.Equal(s.ABlocks, prompt.Topics[0].TextBlockKeys);
        Assert.Empty(prompt.Topics[1].TextBlockKeys);
        Assert.Equal([s.BBlock], prompt.Topics[2].TextBlockKeys);
        Assert.StartsWith("Du ordnest eine Notiz in ein Buch ein.", prompt.System);
        Assert.Contains("Meine Notiz", prompt.User);
        Assert.Contains("1. Kapitel A", prompt.User);
        Assert.Contains("Alles über A.", prompt.User);
        Assert.Contains("2. Kapitel A › Unter A", prompt.User);
        Assert.Contains("3. Kapitel B", prompt.User);
        Assert.Contains("1.1 nach: Erster Absatz in A.", prompt.User);
        Assert.Contains("1.2 nach: Zweiter Absatz in A.", prompt.User);
        Assert.Contains("3.1 nach: Text in B.", prompt.User);
        Assert.Contains("0 = neue Stelle am Ende des Themas", prompt.User);
    }

    [Fact]
    public void Parse_ValidAnswer()
    {
        var s = Load();
        var prompt = Build(s);

        var verdict = PlacementCheck.Parse("{\"thema\":1,\"stelle\":\"1.2\",\"begruendung\":\"Passt dazu.\"}", prompt);

        Assert.Equal(new PlacementVerdict(s.A, s.ABlocks[1], "Passt dazu."), verdict);
        var asStrings = PlacementCheck.Parse("{\"thema\":\"3\",\"stelle\":\"3.1\",\"begruendung\":\"B.\"}", prompt);
        Assert.Equal(new PlacementVerdict(s.B, s.BBlock, "B."), asStrings);
    }

    [Fact]
    public void Parse_NewPlace0_NullAfterKey()
    {
        var s = Load();
        var prompt = Build(s);

        var verdict = PlacementCheck.Parse("{\"thema\":2,\"stelle\":\"0\",\"begruendung\":\"Neu.\"}", prompt);

        Assert.Equal(new PlacementVerdict(s.UnterA, null, "Neu."), verdict);
    }

    [Fact]
    public void Parse_InvalidJson_Null()
    {
        var prompt = Build(Load());

        Assert.Null(PlacementCheck.Parse("kein json", prompt));
        Assert.Null(PlacementCheck.Parse("{\"thema\":", prompt));
        Assert.Null(PlacementCheck.Parse("{\"thema\":1,\"stelle\":\"1.9\",\"begruendung\":\"x\"}", prompt));
    }

    [Fact]
    public void Parse_TopicNotInList_Null()
    {
        var prompt = Build(Load());

        Assert.Null(PlacementCheck.Parse("{\"thema\":4,\"stelle\":\"0\",\"begruendung\":\"x\"}", prompt));
        Assert.Null(PlacementCheck.Parse("{\"thema\":1,\"stelle\":\"3.1\",\"begruendung\":\"x\"}", prompt));
    }

    [Fact]
    public void Parse_ReasonTrimmedTo300()
    {
        var prompt = Build(Load());

        var verdict = PlacementCheck.Parse("{\"thema\":1,\"stelle\":\"0\",\"begruendung\":\"" + new string('x', 500) + "\"}", prompt);

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

    [Fact]
    public void Parse_PlaceNotInTopic_Null()
    {
        Assert.Null(PlacementCheck.Parse("{\"thema\":1,\"stelle\":\"1.9\",\"begruendung\":\"x\"}", Build(Load())));
    }

    [Fact]
    public void Parse_StelleOfOtherTopic_Null()
    {
        Assert.Null(PlacementCheck.Parse("{\"thema\":1,\"stelle\":\"2.1\",\"begruendung\":\"x\"}", Build(Load())));
    }

    [Fact]
    public void Parse_N0_NewPlace()
    {
        var s = Load();
        var verdict = PlacementCheck.Parse("{\"thema\":1,\"stelle\":\"1.0\",\"begruendung\":\"x\"}", Build(s));

        Assert.Equal(new PlacementVerdict(s.A, null, "x"), verdict);
    }
}
