using Bunit;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>The coverage bars next to the headings of the outline (spec 6.2).</summary>
public class CoverageOutlineTests : UiTestContext
{
    private const string Source = "11111111-1111-1111-1111-111111111111";

    private TestVault? _vault;

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _vault?.Dispose();
        }
    }

    /// <summary>Three top-level headings, one with a sub-heading; one journal note is used under "Eins".</summary>
    private async Task<VaultSession> OpenAsync()
    {
        _vault = TestVault.Create(
            ("pages/Buch - Alpha.md",
                "title:: Alpha\ntype:: book\n\n- # Eins\n\t- Erster Text\n\t  id:: " + Source + "\n\t- ## Eins-A\n- # Zwei\n- # Drei\n"),
            ("journals/2026_03_01.md", "- Eine Notiz, die verwendet wird.\n  used-in:: [[Buch - Alpha]] ((" + Source + "))\n"));
        return await OpenSessionAsync(_vault);
    }

    private OutlineNode Node(string title) => State.CurrentBook!.Root.Children.SelectMany(Flatten).First(n => n.Title == title);

    private static IEnumerable<OutlineNode> Flatten(OutlineNode node) => new[] { node }.Concat(node.Children.SelectMany(Flatten));

    private static SectionCoverage Section(OutlineNode node, int used, int assigned, int uncertain = 0, bool leaf = true) =>
        new(node.Key, used, assigned, uncertain, used, assigned, uncertain, leaf);

    private Coverage CoverageOf(bool complete, params SectionCoverage[] sections) =>
        new(sections.ToDictionary(s => s.SectionKey), 0, [], [], Checked: 0, Unused: complete ? 0 : 1, Settled: 0,
            State.CurrentBook!.LinkName);

    private AngleSharp.Dom.IElement Row(IRenderedComponent<OutlinePane> cut, string title) =>
        cut.FindAll(".ne-outline-row").First(r => r.QuerySelector(".ne-outline-title")?.TextContent == title);

    [Fact]
    public async Task Toggle_ShowsAndHidesBars_AndSavesTheSetting()
    {
        var session = await OpenAsync();
        session.Coverage = CoverageOf(true, Section(Node("Eins"), 3, 2));
        var cut = Render<OutlinePane>();
        Assert.Empty(cut.FindAll(".ne-outline-coverage"));
        Assert.Equal("false", cut.Find(".ne-outline-coverage-toggle").GetAttribute("aria-pressed"));

        cut.Find(".ne-outline-coverage-toggle").Click();

        Assert.Single(cut.FindAll(".ne-outline-coverage"));
        Assert.Equal("true", cut.Find(".ne-outline-coverage-toggle").GetAttribute("aria-pressed"));
        Assert.True(Settings.ShowCoverage);
        Assert.True(UiSettings.Load(Platform.UserDataDirectory).ShowCoverage);

        cut.Find(".ne-outline-coverage-toggle").Click();

        Assert.Empty(cut.FindAll(".ne-outline-coverage"));
        Assert.False(UiSettings.Load(Platform.UserDataDirectory).ShowCoverage);
    }

    [Fact]
    public async Task Bar_WidthsRelativeToLargestSibling_CountAndTooltip()
    {
        var session = await OpenAsync();
        Settings.ShowCoverage = true;
        session.Coverage = CoverageOf(true,
            Section(Node("Eins"), 3, 2, uncertain: 1),
            Section(Node("Zwei"), 2, 0),
            Section(Node("Drei"), 0, 1));
        var cut = Render<OutlinePane>();

        var eins = Row(cut, "Eins").QuerySelector(".ne-outline-coverage")!;
        Assert.Equal("width: 60%", eins.QuerySelector(".ne-outline-coverage-used")!.GetAttribute("style")!.TrimEnd(';'));
        Assert.Equal("width: 40%", eins.QuerySelector(".ne-outline-coverage-assigned")!.GetAttribute("style")!.TrimEnd(';'));
        Assert.Equal("5", eins.QuerySelector(".ne-outline-coverage-count")!.TextContent);
        Assert.Equal("3 übernommen · 2 zugeordnet (1 unsicher)", eins.GetAttribute("title"));

        var zwei = Row(cut, "Zwei").QuerySelector(".ne-outline-coverage")!;
        Assert.Equal("width: 40%", zwei.QuerySelector(".ne-outline-coverage-used")!.GetAttribute("style")!.TrimEnd(';'));
        Assert.Equal("2 übernommen", zwei.GetAttribute("title"));
        Assert.Equal(zwei.GetAttribute("title"), zwei.GetAttribute("aria-label"));
    }

    [Fact]
    public async Task EmptyLeaf_ShowsRedZero_OnlyWhenComplete()
    {
        var session = await OpenAsync();
        Settings.ShowCoverage = true;
        session.Coverage = CoverageOf(true, Section(Node("Zwei"), 0, 0));
        var cut = Render<OutlinePane>();
        Assert.Equal("0", Row(cut, "Zwei").QuerySelector(".ne-outline-coverage-empty")!.TextContent);

        session.Coverage = CoverageOf(false, Section(Node("Zwei"), 0, 0));
        cut.Render();
        Assert.Empty(cut.FindAll(".ne-outline-coverage-empty"));
        Assert.Empty(cut.FindAll(".ne-outline-coverage"));
    }

    [Fact]
    public async Task OtherBooksCoverage_NotShown()
    {
        var session = await OpenAsync();
        Settings.ShowCoverage = true;

        // Left from the book shown before a switch, until this book's is computed.
        session.Coverage = CoverageOf(true, Section(Node("Zwei"), 1, 1)) with { BookLinkName = "Buch - Beta" };
        var cut = Render<OutlinePane>();

        Assert.Empty(cut.FindAll(".ne-outline-coverage"));
    }

    [Fact]
    public async Task Click_SelectsSection_AndRequestsRelevantTab()
    {
        var session = await OpenAsync();
        Settings.ShowCoverage = true;
        session.Coverage = CoverageOf(true, Section(Node("Zwei"), 1, 1));
        var requested = new List<NotesTab>();
        State.NotesTabRequested += requested.Add;
        var cut = Render<OutlinePane>();

        Row(cut, "Zwei").QuerySelector(".ne-outline-coverage")!.Click();

        Assert.Equal(Node("Zwei").Key, State.CurrentSectionKey);
        Assert.Equal([NotesTab.Relevant], requested);
    }

    [Fact]
    public async Task WithoutLanguageModel_ShowsOnlyTheUsedPart()
    {
        var session = await OpenAsync();
        var cut = Render<OutlinePane>();

        cut.Find(".ne-outline-coverage-toggle").Click();
        Time.Advance(VaultSession.CoverageDelay);
        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(session.Coverage);
            var eins = Row(cut, "Eins").QuerySelector(".ne-outline-coverage")!;
            Assert.Equal("1 übernommen", eins.GetAttribute("title"));
            Assert.Equal("width: 100%", eins.QuerySelector(".ne-outline-coverage-used")!.GetAttribute("style")!.TrimEnd(';'));
            Assert.Equal("width: 0%", eins.QuerySelector(".ne-outline-coverage-assigned")!.GetAttribute("style")!.TrimEnd(';'));
        });
    }
}
