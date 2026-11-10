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

    /// <summary>The verdict as an earlier session stored it: its keys are not in this session's book.</summary>
    private static PlacementVerdict Earlier(PlacementVerdict verdict) => PlacementCheck.Deserialize(PlacementCheck.Serialize(verdict with
    {
        SectionKey = Guid.NewGuid(), AfterTextBlockKey = verdict.AfterTextBlockKey is null ? null : Guid.NewGuid(),
        EmbeddingSectionKey = verdict.EmbeddingSectionKey is null ? null : Guid.NewGuid(),
        Basis = verdict.Basis! with { OfferedKeys = null, TopChapterKeys = null },
    }))!;

    [Fact]
    public void RenamedChapterNotChosen_InTopThree_Kept()
    {
        var latest = Latest();
        Block("# Kapitel B").SetContent("# Kapitel Beta");
        var book = Book();

        var prompt = Prompt(book, ["Unter A1", "Kapitel Beta", "Unter A2", "Kapitel C", "Kapitel D"]);

        Assert.Contains("Kapitel Beta#0", prompt.Basis!.TopChapters);
        Assert.Equal(RecheckReason.None, PlacementKeep.Decide(latest, prompt, book));
    }

    [Fact]
    public void RenamedSiblingRankedBeforeChosen_Kept()
    {
        var book = Book();
        var latest = Stored(Prompt(book, ["Unter A2", "Unter A1", "Kapitel B", "Kapitel C"]), Node(book, "Unter A1").Key);
        Block("## Unter A2").SetContent("## Unter Zwei");
        book = Book();

        Assert.Equal(RecheckReason.None,
            PlacementKeep.Decide(latest, Prompt(book, ["Unter Zwei", "Unter A1", "Kapitel B", "Kapitel C"]), book));
    }

    [Fact]
    public void RenamedChapter_ResultFromEarlierSession_KeptByTheNamesSeenInThisSession()
    {
        var names = new SectionNames();
        var before = Book();
        var latest = Earlier(Latest());
        names.Remember(before); // the build before the rename
        Block("# Kapitel B").SetContent("# Kapitel Beta");
        var book = Book();
        var prompt = Prompt(book, ["Unter A1", "Kapitel Beta", "Unter A2", "Kapitel C", "Kapitel D"]);

        Assert.Equal(RecheckReason.None, PlacementKeep.Decide(latest, prompt, book, names));

        // Without a book of this session from before the rename, its old name is not found.
        Assert.Equal(RecheckReason.NewCandidate, PlacementKeep.Decide(latest, prompt, book, new SectionNames()));
    }

    [Fact]
    public void RenamedChosenSection_RewrittenToTodaysNames_KeptAfterRestart()
    {
        var names = new SectionNames();
        var latest = Latest();
        names.Remember(Book());
        Assert.Null(PlacementKeep.Renamed(latest, Book(), names)); // nothing renamed: nothing to write
        Block("# Kapitel A").SetContent("# Kapitel Alpha");
        Block("## Unter A1").SetContent("## Unter Eins");
        var book = Book();
        var prompt = Prompt(book, ["Unter Eins", "Kapitel B", "Unter A2", "Kapitel C", "Kapitel D"]);
        Assert.Equal(RecheckReason.None, PlacementKeep.Decide(latest, prompt, book, names));

        var renamed = PlacementKeep.Renamed(latest, book, names)!;

        Assert.Equal("Kapitel Alpha#0 › Unter Eins#0", renamed.SectionPath);
        Assert.Equal(prompt.Basis!.Offered.Order(), renamed.Basis!.Offered.Order());
        Assert.Equal(["Kapitel Alpha#0", "Kapitel B#0", "Kapitel C#0"], renamed.Basis.TopChapters);
        Assert.Contains("Kapitel Alpha#0 › Unter Eins#0", renamed.Basis.Ranking);
        Assert.Equal(latest.Basis!.NoteHash, renamed.Basis.NoteHash);

        // After a restart the keys are new: the rewritten paths find the section and keep the result.
        var restarted = Earlier(renamed);
        Assert.Same(Node(book, "Unter Eins"), SectionPath.Find(book, restarted.SectionPath!));
        Assert.Equal(RecheckReason.None, PlacementKeep.Decide(restarted, prompt, book, new SectionNames()));
    }

    [Fact]
    public void ChosenSectionDeleted_SameTitledSiblingLeft_SectionGone()
    {
        AddHeading("Kapitel A", "Unter A1"); // "Unter A1#1"
        var names = new SectionNames();
        var before = Book();
        var chosen = All(before.Root).First(n => n.Title == "Unter A1");
        var latest = Stored(Prompt(before, ["Unter A2", "Kapitel B", "Kapitel C"]), chosen.Key);
        Assert.Equal("Kapitel A#0 › Unter A1#0", latest.SectionPath);
        var earlier = Earlier(latest);
        names.Remember(before);

        _page.RemoveBlock(_page.AllBlocks().First(b => b.Content == "## Unter A1"));
        var book = Book();
        var prompt = Prompt(book, ["Unter A2", "Kapitel B", "Kapitel C"]);

        // The sibling now has the path "Unter A1#0", but it is another section.
        Assert.Equal(RecheckReason.SectionGone, PlacementKeep.Decide(latest, prompt, book, names));
        Assert.Equal(RecheckReason.SectionGone, PlacementKeep.Decide(earlier, prompt, book, names));

        // The next build, before the recheck is done: the path now belongs to the sibling in this session's names.
        names.Remember(book);
        Assert.Equal(RecheckReason.SectionGone, PlacementKeep.Decide(latest, prompt, book, names));
        Assert.Equal(RecheckReason.SectionGone, PlacementKeep.Decide(earlier, prompt, book, names));
        Assert.Null(PlacementKeep.Renamed(latest, book, names));
    }

    /// <summary>The block (with its subtree) read again with new keys at the same place, as after a change of its own lines outside the app.</summary>
    private void NewKeys(string content)
    {
        var old = Block(content);
        var parent = old.Parent;
        var index = parent is null ? _page.AllBlocks().Where(b => b.Parent is null).ToList().IndexOf(old) : parent.Children.ToList().IndexOf(old);
        var copy = old.CloneDetached(withoutProperties: false);
        _page.RemoveBlock(old);
        _page.InsertBlock(parent, index, copy);
    }

    [Fact]
    public void ChosenHeadingReadAgainWithNewKey_SameTitle_Kept()
    {
        var names = new SectionNames();
        var before = Book();
        var latest = Latest();
        var earlier = Earlier(latest);
        names.Remember(before);
        NewKeys("## Unter A1");
        var book = Book();
        Assert.NotEqual(Node(before, "Unter A1").Key, Node(book, "Unter A1").Key);
        var prompt = Prompt(book, Topics);

        Assert.Equal(RecheckReason.None, PlacementKeep.Decide(latest, prompt, book, names));
        Assert.Equal(RecheckReason.None, PlacementKeep.Decide(earlier, prompt, book, names));

        // Also once this session's builds have seen the new key.
        names.Remember(book);
        Assert.Equal(RecheckReason.None, PlacementKeep.Decide(latest, prompt, book, names));
    }

    [Fact]
    public void TopChapterReadAgainWithNewKey_Kept()
    {
        var names = new SectionNames();
        var latest = Latest();
        names.Remember(Book());
        NewKeys("# Kapitel B");
        var book = Book();

        Assert.Equal(RecheckReason.None, PlacementKeep.Decide(latest, Prompt(book, Topics), book, names));
    }

    [Fact]
    public void NoKey_IsNotTheRoot()
    {
        var names = new SectionNames();
        var book = Book();
        names.Remember(book);

        Assert.Null(names.Find(book, Guid.Empty, "Gibt es nicht#0"));
        Assert.Same(book.Root, names.Find(book, Guid.Empty, ""));
        Assert.Same(Node(book, "Kapitel B"), names.Find(book, Guid.Empty, "Kapitel B#0"));
    }

    [Fact]
    public void GoneSectionWithoutStoredKey_RenamedBasisDoesNotPointAtTheRoot()
    {
        // A basis without keys (an earlier session's) offered "Kapitel C", which is gone; "Kapitel A" was renamed.
        var names = new SectionNames();
        var latest = Earlier(Latest());
        names.Remember(Book());
        _page.RemoveBlock(Block("# Kapitel C"));
        Block("# Kapitel A").SetContent("# Kapitel Alpha");
        var book = Book();

        var renamed = PlacementKeep.Renamed(latest, book, names)!;
        var gone = renamed.Basis!.Offered.ToList().IndexOf("Kapitel C#0");
        Assert.True(gone >= 0);
        Assert.Equal(Guid.Empty, renamed.Basis.OfferedKeys![gone]);

        // Read back in the next build: the gone entry stays gone, it does not become the root ("").
        var again = PlacementKeep.Renamed(renamed, book, names);
        Assert.True(again is null || !again.Basis!.Offered.Contains(""));
        Assert.DoesNotContain("", renamed.Basis.Offered);
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
