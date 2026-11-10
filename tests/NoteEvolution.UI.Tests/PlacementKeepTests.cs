using NoteEvolution.AI.Relevance;
using NoteEvolution.AI.Understanding;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Model;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>When a stored placement is checked again after the book changed (spec 4).</summary>
public sealed class PlacementKeepTests : IDisposable
{
    private const string Note = "Meine Notiz über den Schlaf.";

    private const string BookBody =
        "- # Kapitel A\n\t- Text in A.\n" +
        "\t- ## Unter A1\n\t\t- Text unter A1.\n\t\t- Mehr unter A1.\n" +
        "\t- ## Unter A2\n\t\t- Text unter A2.\n" +
        "- # Kapitel B\n\t- Text in B.\n" +
        "- # Kapitel C\n\t- Text in C.\n" +
        "- # Kapitel D\n\t- Text in D.\n";

    private readonly TestVault _vault = TestVault.Create(("pages/Buch.md", "title:: Buch\ntype:: book\n\n" + BookBody));
    private readonly Page _page;

    public PlacementKeepTests() => _page = _vault.Open().Books.Single().Page;

    public void Dispose() => _vault.Dispose();

    /// <summary>The book as it is now; the keys stay across changes of the page.</summary>
    private Book Book() => Core.Books.Book.Load(_page);

    private static OutlineNode Node(Book book, string title) => All(book.Root).Single(n => n.Title == title);

    private static IEnumerable<OutlineNode> All(OutlineNode node) => node.Children.SelectMany(child => All(child).Prepend(child));

    private Block Block(string content) => _page.AllBlocks().Single(b => b.Content == content);

    /// <summary>The request for the note with topics (best first) by title, each with an optional summary.</summary>
    private static PlacementCheckPrompt Prompt(Book book, string[] topics, string note = Note,
        IReadOnlyDictionary<Guid, IReadOnlyList<PlacementHit>>? places = null, string? summary = null) =>
        PlacementCheck.Prompt(book, note,
            [.. topics.Select((title, i) => new TopicHit(Node(book, title).Key, 0.9 - i * 0.1, summary is null ? null : $"{summary} {title}."))],
            places ?? new Dictionary<Guid, IReadOnlyList<PlacementHit>>());

    /// <summary>The verdict as stored for <paramref name="prompt"/> and read back, with its path and basis.</summary>
    private static PlacementVerdict Stored(PlacementCheckPrompt prompt, Guid section, Guid? after = null) =>
        PlacementCheck.Deserialize(PlacementCheck.Serialize(new PlacementVerdict(section, after, "Passt."), prompt))!;

    private static readonly string[] Topics = ["Unter A1", "Kapitel B", "Unter A2", "Kapitel C", "Kapitel D"];

    /// <summary>The note's last real check: A1 chosen from A, A1, A2, B and C (chapters A, B, C).</summary>
    private PlacementVerdict Latest()
    {
        var book = Book();
        return Stored(Prompt(book, Topics), Node(book, "Unter A1").Key);
    }

    private void AddHeading(string chapter, string title)
    {
        var heading = Core.Model.Block.CreateDetached("## " + title);
        var parent = Block("# " + chapter);
        _page.InsertBlock(parent, parent.Children.Count, heading);
        _page.InsertBlock(heading, 0, Core.Model.Block.CreateDetached("Text unter " + title + "."));
    }

    [Fact]
    public void NewHeadingInOfferedChapter_WorseRank_Kept()
    {
        var latest = Latest();
        AddHeading("Kapitel A", "Unter A3");
        var book = Book();

        var prompt = Prompt(book, ["Unter A1", "Kapitel B", "Unter A3", "Unter A2", "Kapitel C"]);

        Assert.Contains("Kapitel A#0 › Unter A3#0", prompt.Basis!.Offered);
        Assert.Equal(RecheckReason.None, PlacementKeep.Decide(latest, prompt, book));
    }

    [Fact]
    public void NewHeadingRankedBeforeChosen_NewCandidate()
    {
        var latest = Latest();
        AddHeading("Kapitel A", "Unter A3");
        var book = Book();

        var prompt = Prompt(book, ["Unter A3", "Unter A1", "Kapitel B", "Unter A2", "Kapitel C"]);

        Assert.Equal(RecheckReason.NewCandidate, PlacementKeep.Decide(latest, prompt, book));
    }

    [Fact]
    public void NewChapterInTopThree_NewCandidate()
    {
        var latest = Latest();
        var book = Book();

        // D comes third, ranked after the chosen section: as a new chapter among the best three it counts all the same.
        var prompt = Prompt(book, ["Unter A1", "Kapitel B", "Kapitel D", "Kapitel C"]);

        Assert.Equal(["Kapitel A#0", "Kapitel B#0", "Kapitel D#0"], prompt.Basis!.TopChapters);
        Assert.Equal(RecheckReason.NewCandidate, PlacementKeep.Decide(latest, prompt, book));
    }

    [Fact]
    public void RenamedSectionFoundByKey_Kept()
    {
        var latest = Latest();
        Block("# Kapitel A").SetContent("# Kapitel Alpha");
        Block("## Unter A1").SetContent("## Unter Eins");
        var book = Book();

        var prompt = Prompt(book, ["Unter Eins", "Kapitel B", "Unter A2", "Kapitel C", "Kapitel D"]);

        Assert.Equal("Kapitel Alpha#0 › Unter Eins#0", prompt.Basis!.Offered[1]);
        Assert.Equal(RecheckReason.None, PlacementKeep.Decide(latest, prompt, book));
    }

    [Fact]
    public void OtherSummary_Kept()
    {
        var book = Book();
        var latest = Stored(Prompt(book, Topics, summary: "Alt über"), Node(book, "Unter A1").Key);

        var prompt = Prompt(book, Topics, summary: "Ganz neu über");

        Assert.Contains("Ganz neu über Unter A1", prompt.User);
        Assert.Equal(RecheckReason.None, PlacementKeep.Decide(latest, prompt, book));
    }

    [Fact]
    public void PlaceGone_Kept()
    {
        var book = Book();
        var a1 = Node(book, "Unter A1");
        var place = a1.TextBlocks.First();
        var places = new Dictionary<Guid, IReadOnlyList<PlacementHit>> { [a1.Key] = [new PlacementHit(place.Key, 0.9, a1.Key)] };
        var latest = Stored(Prompt(book, Topics, places: places), a1.Key, place.Key);
        Assert.Equal("Text unter A1.", latest.AfterText);

        _page.RemoveBlock(Block("Text unter A1."));
        book = Book();

        Assert.Equal(RecheckReason.None, PlacementKeep.Decide(latest, Prompt(book, Topics), book));
    }

    [Fact]
    public void ChosenSectionDeleted_SectionGone()
    {
        var latest = Latest();
        _page.RemoveBlock(Block("## Unter A1"));
        var book = Book();

        var prompt = Prompt(book, ["Kapitel B", "Unter A2", "Kapitel C", "Kapitel D"]);

        Assert.Equal(RecheckReason.SectionGone, PlacementKeep.Decide(latest, prompt, book));
    }

    [Fact]
    public void HalfOfferedGone_Rebuilt()
    {
        var latest = Latest();
        _page.RemoveBlock(Block("## Unter A2"));
        _page.RemoveBlock(Block("# Kapitel B"));
        _page.RemoveBlock(Block("# Kapitel C"));
        var book = Book();

        // A and A1 of the five are still offered, and nothing new.
        var prompt = Prompt(book, ["Unter A1"]);

        Assert.Equal(["Kapitel A#0", "Kapitel A#0 › Unter A1#0"], prompt.Basis!.Offered);
        Assert.Equal(RecheckReason.Rebuilt, PlacementKeep.Decide(latest, prompt, book));
    }

    [Fact]
    public void ExactlyHalfOffered_Kept()
    {
        // The basis offered A, A1, A2 and B; A and A1 are still offered: half, not less.
        var book = Book();
        var latest = Stored(Prompt(book, ["Unter A1", "Kapitel B"]), Node(book, "Unter A1").Key);
        Assert.Equal(4, latest.Basis!.Offered.Count);
        _page.RemoveBlock(Block("## Unter A2"));
        _page.RemoveBlock(Block("# Kapitel B"));
        book = Book();

        var prompt = Prompt(book, ["Unter A1"]);

        Assert.Equal(2, prompt.Basis!.Offered.Count);
        Assert.Equal(RecheckReason.None, PlacementKeep.Decide(latest, prompt, book));
    }

    [Fact]
    public void ChosenSectionNotRanked_AnyNewRankedSectionComesBefore()
    {
        // "Kapitel A" itself is offered (it has own text) but is no topic of the ranking: it counts as last.
        var book = Book();
        var latest = Stored(Prompt(book, Topics), Node(book, "Kapitel A").Key);
        Assert.DoesNotContain("Kapitel A#0", latest.Basis!.Ranking);
        Assert.Equal(RecheckReason.None, PlacementKeep.Decide(latest, Prompt(book, Topics), book));

        AddHeading("Kapitel A", "Unter A3");
        book = Book();
        var prompt = Prompt(book, ["Unter A1", "Kapitel B", "Unter A2", "Kapitel C", "Kapitel D", "Unter A3"]);

        Assert.Equal(RecheckReason.NewCandidate, PlacementKeep.Decide(latest, prompt, book));
    }

    [Fact]
    public void OtherRequestForm_Model()
    {
        var latest = Latest();
        var book = Book();
        var prompt = Prompt(book, Topics);

        Assert.Equal(RecheckReason.None, PlacementKeep.Decide(latest, prompt, book));
        Assert.Equal(RecheckReason.Model, PlacementKeep.Decide(latest with { Basis = latest.Basis! with { Request = "anders" } }, prompt, book));
        Assert.Equal(RecheckReason.Model, PlacementKeep.Decide(latest with { Basis = latest.Basis! with { Request = null } }, prompt, book));
    }

    [Fact]
    public void NoteTextChanged_NoteText()
    {
        var latest = Latest();
        var book = Book();

        Assert.Equal(RecheckReason.NoteText, PlacementKeep.Decide(latest, Prompt(book, Topics, note: Note + " Und mehr."), book));
    }

    [Fact]
    public void NoBasis_NoBasis()
    {
        var book = Book();
        var a1 = Node(book, "Unter A1");
        var old = PlacementCheck.Deserialize($"{{\"section\":\"{a1.Key}\",\"reason\":\"Passt.\"}}")!;

        Assert.Equal(RecheckReason.NoBasis, PlacementKeep.Decide(old, Prompt(book, Topics), book));
    }

    [Fact]
    public void SmallChangesAddUp_ComparedWithBasisNotPrevious()
    {
        var latest = Latest();

        // Each step alone keeps the placement: A2 goes, then chapter C.
        _page.RemoveBlock(Block("## Unter A2"));
        var book = Book();
        Assert.Equal(RecheckReason.None, PlacementKeep.Decide(latest, Prompt(book, ["Unter A1", "Kapitel B", "Kapitel C"]), book));

        _page.RemoveBlock(Block("# Kapitel C"));
        book = Book();
        var previous = Prompt(book, ["Unter A1", "Kapitel B"]);
        Assert.Equal(RecheckReason.None, PlacementKeep.Decide(latest, previous, book));

        // Then chapter B: against the previous request (A, A1, B) two of three would still be offered, against the
        // basis of the last real check (A, A1, A2, B, C) only two of five.
        _page.RemoveBlock(Block("# Kapitel B"));
        book = Book();
        var prompt = Prompt(book, ["Unter A1"]);
        Assert.Equal(RecheckReason.None, PlacementKeep.Decide(Stored(previous, Node(book, "Unter A1").Key), prompt, book));
        Assert.Equal(RecheckReason.Rebuilt, PlacementKeep.Decide(latest, prompt, book));
    }
}
