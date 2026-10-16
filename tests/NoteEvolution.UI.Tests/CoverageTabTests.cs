using Bunit;
using NoteEvolution.AI.Tests;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>The "Abdeckung" tab of the notes pane (spec 6.3).</summary>
public class CoverageTabTests : UiTestContext
{
    private const string Source = "11111111-1111-1111-1111-111111111111";

    private readonly FakeChatModel _chat = new();
    private readonly SemaphoreSlim _gate = new(0);
    private TestVault? _vault;

    protected override void Dispose(bool disposing)
    {
        // Release the held language model requests so that the session can close.
        _gate.Release(100);
        base.Dispose(disposing);
        if (disposing)
        {
            _vault?.Dispose();
        }
    }

    /// <summary>"Eins" (with the leaf "Eins-A", holding a used text block), the leaves "Zwei" and "Drei", and three journal notes.</summary>
    private TestVault Vault() => _vault = TestVault.Create(
        ("pages/Buch - Alpha.md",
            "title:: Alpha\ntype:: book\n\n- # Eins\n\t- ## Eins-A\n\t\t- Erster Text\n\t\t  id:: " + Source + "\n- # Zwei\n- # Drei\n"),
        ("journals/2026_03_01.md", "- Älteste Notiz.\n"),
        ("journals/2026_03_02.md", "- Mittlere Notiz.\n"),
        ("journals/2026_03_03.md", "- Neueste Notiz.\n"));

    /// <summary>With the language model: its worker runs (held at the gate), so <see cref="VaultSession.Understanding"/> is set.</summary>
    private async Task<VaultSession> OpenWithModelAsync()
    {
        _chat.Gate = _gate;
        var language = UseLanguageModel(_chat);
        await language.RefreshAsync(CancellationToken.None);
        return await OpenWithAiAsync(Vault());
    }

    private OutlineNode Node(string title) => State.CurrentBook!.Root.Children.SelectMany(Flatten).First(n => n.Title == title);

    private static IEnumerable<OutlineNode> Flatten(OutlineNode node) => new[] { node }.Concat(node.Children.SelectMany(Flatten));

    private static Guid NoteKey(VaultSession session, string text) => session.Notes.All().Single(n => n.Block.Content == text).Key;

    private static SectionCoverage Section(OutlineNode node, int used, int assigned) =>
        new(node.Key, used, assigned, 0, used, assigned, 0, true);

    private static Coverage CoverageOf(IReadOnlyList<Guid> homeless, IReadOnlyList<Guid> uncertain, int done, int total,
        params SectionCoverage[] sections) =>
        new(sections.ToDictionary(s => s.SectionKey), 0, homeless, uncertain, done, total);

    private IRenderedComponent<NotesPane> OpenTab(VaultSession session, Coverage coverage)
    {
        session.Coverage = coverage;
        var cut = Render<NotesPane>();
        cut.Find(".ne-tab-coverage").Click();
        return cut;
    }

    private static List<string> Paths(IRenderedComponent<NotesPane> cut) =>
        [.. cut.FindAll(".ne-coverage-path").Select(e => e.TextContent.Trim())];

    [Fact]
    public async Task Tab_ComesAfterJournal_AndJournalStaysTheDefault()
    {
        await OpenWithModelAsync();

        var cut = Render<NotesPane>();

        var tabs = cut.FindAll(".ne-tab").Select(t => t.ClassList.First(c => c.StartsWith("ne-tab-"))).ToList();
        Assert.Equal(["ne-tab-relevant", "ne-tab-search", "ne-tab-journal", "ne-tab-coverage"], tabs);
        Assert.Contains("active", cut.Find(".ne-tab-journal").ClassList);
        Assert.Empty(cut.FindAll(".ne-coverage"));
    }

    [Fact]
    public async Task Tab_ShowsTheMetrics()
    {
        var session = await OpenWithModelAsync();
        var coverage = CoverageOf([NoteKey(session, "Neueste Notiz.")], [NoteKey(session, "Mittlere Notiz."), NoteKey(session, "Älteste Notiz.")],
            84, 120, Section(Node("Eins-A"), 1, 0), Section(Node("Zwei"), 0, 0), Section(Node("Drei"), 0, 0));

        var cut = OpenTab(session, coverage);

        Assert.Contains("84/120", cut.Find(".ne-coverage-checked").TextContent);
        Assert.Equal("2", cut.Find(".ne-coverage-empty .ne-coverage-value").TextContent);
        Assert.Equal("1", cut.Find(".ne-coverage-homeless .ne-coverage-value").TextContent);
        Assert.Equal("2", cut.Find(".ne-coverage-uncertain .ne-coverage-value").TextContent);
    }

    [Fact]
    public async Task Tab_SectionList_ShowsLeavesWithPath_SortedAndSelectable()
    {
        var session = await OpenWithModelAsync();
        var coverage = CoverageOf([], [], 0, 0,
            Section(Node("Eins-A"), 1, 1), Section(Node("Zwei"), 0, 3), Section(Node("Drei"), 0, 0));
        var cut = OpenTab(session, coverage);

        Assert.Equal(["Eins › Eins-A", "Zwei", "Drei"], Paths(cut));
        Assert.Equal(["2", "3", "0"], cut.FindAll(".ne-coverage-count").Select(e => e.TextContent.Trim()).ToList());
        Assert.Equal(3, cut.FindAll(".ne-coverage-bar").Count);

        cut.Find(".ne-coverage-sort").Change("fewest");

        Assert.Equal(["Drei", "Eins › Eins-A", "Zwei"], Paths(cut));

        cut.Find(".ne-coverage-sort").Change("book");

        Assert.Equal(["Eins › Eins-A", "Zwei", "Drei"], Paths(cut));

        cut.FindAll(".ne-coverage-section")[1].Click();

        Assert.Equal(Node("Zwei").Key, State.CurrentSectionKey);
    }

    [Fact]
    public async Task Tab_Homeless_ShowsNoteCardsAndUncertainIsCollapsed()
    {
        var session = await OpenWithModelAsync();
        var newest = NoteKey(session, "Neueste Notiz.");
        var oldest = NoteKey(session, "Älteste Notiz.");
        var middle = NoteKey(session, "Mittlere Notiz.");
        var coverage = CoverageOf([newest, oldest], [middle], 3, 3, Section(Node("Zwei"), 0, 0));

        var cut = OpenTab(session, coverage);

        var homeless = cut.FindAll(".ne-coverage-homeless-list .ne-note-card .ne-note-text").Select(e => e.TextContent.Trim()).ToList();
        Assert.Equal(["Neueste Notiz.", "Älteste Notiz."], homeless);
        var uncertain = cut.Find("details.ne-coverage-uncertain-list");
        Assert.False(uncertain.HasAttribute("open"));
        Assert.Contains("Mittlere Notiz.", uncertain.TextContent);
    }

    [Fact]
    public async Task Tab_WatchesTheCoverageOnlyWhileActive()
    {
        var session = await OpenWithModelAsync();
        var cut = Render<NotesPane>();
        session.SetFocus(State.CurrentBook, State.CurrentSectionKey);
        var before = session.CoverageComputations;

        cut.Find(".ne-tab-coverage").Click();
        Time.Advance(VaultSession.CoverageDelay);
        cut.WaitForAssertion(() => Assert.True(session.CoverageComputations > before));
        cut.WaitForAssertion(() => Assert.False(session.CoverageRunning));

        cut.Find(".ne-tab-journal").Click();
        var after = session.CoverageComputations;
        session.Coverage = null;
        Time.Advance(VaultSession.CoverageDelay);

        Assert.Equal(after, session.CoverageComputations);
    }

    [Fact]
    public async Task Tab_WithoutLanguageModel_ShowsTheHintAndUsedCountsOnly()
    {
        var session = await OpenSessionAsync(Vault());
        var coverage = CoverageOf([], [], 0, 3, Section(Node("Eins-A"), 2, 4), Section(Node("Zwei"), 0, 0));

        var cut = OpenTab(session, coverage);

        Assert.Equal("Die Abdeckung braucht das Sprachmodell (Einstellungen › Sprachmodell)", cut.Find(".ne-coverage-hint").TextContent.Trim());
        Assert.Equal(["2", "0", "0"], cut.FindAll(".ne-coverage-count").Select(e => e.TextContent.Trim()).ToList());
        Assert.All(cut.FindAll(".ne-coverage-assigned"), e => Assert.Contains("width: 0%", e.GetAttribute("style")));
        Assert.Empty(cut.FindAll(".ne-coverage-checked"));
        Assert.Empty(cut.FindAll(".ne-coverage-homeless-list"));
    }
}
