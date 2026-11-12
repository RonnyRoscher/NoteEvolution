using NoteEvolution.AI.Understanding;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>The record of where adopted notes went compared with the model's suggestion.</summary>
public sealed class PlacementOutcomesTests : IDisposable
{
    private readonly TestVault _vault = TestVault.Create(
        ("pages/Buch.md",
            "title:: Buch\ntype:: book\n\n" +
            "- Vorspann\n" +
            "- # A\n\t- Text A\n\t- ## A1\n\t\t- Text A1\n\t- ## A2\n\t\t- Text A2\n" +
            "- # B\n\t- Text B\n"));

    private readonly Book _book;

    public PlacementOutcomesTests() => _book = Book.Load(_vault.Open().Books.Single().Page);

    public void Dispose() => _vault.Dispose();

    private OutlineNode Node(string title) => All(_book.Root).Single(n => n.Title == title);

    private static IEnumerable<OutlineNode> All(OutlineNode node) => node.Children.SelectMany(child => All(child).Prepend(child));

    private Guid KeyOf(string content) => _book.Page.AllBlocks().Single(b => b.Content == content).Key;

    private static NotePlacement Suggests(OutlineNode node, PlacementFit fit = PlacementFit.Good, bool stale = false) =>
        new(new PlacementVerdict(node.Key, null, "Grund", Fit: fit), stale);

    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("A1", "A1", OutcomeRelation.SameSection)]
    [InlineData("A1", "A2", OutcomeRelation.SameChapter)]
    [InlineData("A1", "A", OutcomeRelation.SameChapter)]
    [InlineData("A", "A2", OutcomeRelation.SameChapter)]
    [InlineData("A1", "B", OutcomeRelation.OtherChapter)]
    public void Relate_BySectionAndChapter(string suggested, string chosen, OutcomeRelation expected) =>
        Assert.Equal(expected, PlacementOutcomes.Relate(Node(suggested), Node(chosen)));

    [Fact]
    public void Relate_ThePrologueIsAChapterOfItsOwn()
    {
        Assert.Equal(OutcomeRelation.SameSection, PlacementOutcomes.Relate(_book.Root, _book.Root));
        Assert.Equal(OutcomeRelation.OtherChapter, PlacementOutcomes.Relate(_book.Root, Node("A")));
    }

    [Fact]
    public void Of_ComparesTheSuggestedSectionWithTheElementsSection()
    {
        var outcome = PlacementOutcomes.Of(
            _book, Suggests(Node("A1"), PlacementFit.Partial, stale: true), KeyOf("Text A2"), "note:x#Notiz", fromRelevant: true, Now)!;

        Assert.Equal(
            new PlacementOutcome(Now, _book.LinkName, "note:x#Notiz", "A#0 › A1#0", PlacementFit.Partial, true, "A#0 › A2#0",
                OutcomeRelation.SameChapter, true),
            outcome);
    }

    [Fact]
    public void Of_AHeadingIsItsOwnSection()
    {
        var outcome = PlacementOutcomes.Of(_book, Suggests(Node("B")), Node("B").Key, "n", false, Now)!;

        Assert.Equal(OutcomeRelation.SameSection, outcome.Relation);
    }

    [Fact]
    public void Of_SuggestedSectionFoundByItsPath_WhenItsKeyIsFromAnotherSession()
    {
        var placement = new NotePlacement(new PlacementVerdict(Guid.NewGuid(), null, "Grund", SectionPath: "A#0 › A1#0"), false);

        Assert.Equal(OutcomeRelation.SameSection, PlacementOutcomes.Of(_book, placement, KeyOf("Text A1"), "n", false, Now)!.Relation);
    }

    [Fact]
    public void Of_NothingToCompare_IsNull()
    {
        var gone = new NotePlacement(new PlacementVerdict(Guid.NewGuid(), null, "Grund", SectionPath: "Weg#0"), false);

        Assert.Null(PlacementOutcomes.Of(_book, gone, KeyOf("Text A1"), "n", false, Now));
        Assert.Null(PlacementOutcomes.Of(_book, Suggests(Node("A")), Guid.NewGuid(), "n", false, Now));
    }

    [Fact]
    public void AppendAndRead_RoundTrip_DamagedLineSkipped_MissingFileEmpty()
    {
        Assert.Empty(PlacementOutcomes.Read(_vault.Root));
        var first = PlacementOutcomes.Of(_book, Suggests(Node("A1")), KeyOf("Text A1"), "note:a#Eins", false, Now)!;
        var second = PlacementOutcomes.Of(_book, Suggests(Node("A1"), PlacementFit.Poor), KeyOf("Text B"), "note:b#Zwei „ä“", true, Now.AddMinutes(1))!;

        PlacementOutcomes.Append(_vault.Root, first);
        File.AppendAllText(PlacementOutcomes.PathIn(_vault.Root), "{ kaputt\n");
        PlacementOutcomes.Append(_vault.Root, second);

        Assert.Equal([first, second], PlacementOutcomes.Read(_vault.Root));
        Assert.Contains("\"relation\":\"OtherChapter\"", File.ReadAllText(PlacementOutcomes.PathIn(_vault.Root)));
    }

    [Fact]
    public void Count_ByRelation_ForOneBook()
    {
        PlacementOutcome Outcome(OutcomeRelation relation, string book = "Buch") =>
            new(Now, book, "n", "A#0", PlacementFit.Good, false, "B#0", relation, false);

        var counts = PlacementOutcomes.Count(
            [
                Outcome(OutcomeRelation.SameSection), Outcome(OutcomeRelation.SameSection), Outcome(OutcomeRelation.SameChapter),
                Outcome(OutcomeRelation.OtherChapter), Outcome(OutcomeRelation.SameSection, "Anderes Buch"),
            ],
            "buch");

        Assert.Equal(new OutcomeCounts(2, 1, 1), counts);
        Assert.Equal(4, counts.Total);
    }
}
