using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NoteEvolution.AI.Relevance;
using NoteEvolution.AI.Search;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Editor;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>„Quellen suchen“ in the section bar: notes that may be where a text block came from, linked as a source on a click.</summary>
public class SourceFinderTests : UiTestContext
{
    private const string BookPath = "pages/Buch - Alpha.md";
    private const string Linked = "11111111-1111-1111-1111-111111111111";

    private const string BookText =
        "title:: Alpha\ntype:: book\n\n" +
        "- # Eins\n" +
        "\t- Erster Text\n" +
        "\t  source:: ((" + Linked + "))\n" +
        "- # Zwei\n";

    private readonly TestVault _vault = TestVault.Create(
        (BookPath, BookText),
        ("journals/2026_03_01.md", "- Schon Quelle\n  id:: " + Linked + "\n  used-in:: [[Buch - Alpha]]\n"),
        ("journals/2026_03_02.md", "- Frei\n\t- Unterpunkt eins\n\t\t- Tiefer\n\t\t  zweite Zeile\n"),
        ("journals/2026_03_03.md", "- Anderswo\n  used-in:: [[Buch - Beta]]\n"));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _vault.Dispose();
        }
    }

    /// <summary>Answers <see cref="Sources"/> with every allowed note (best first in the order given), and records the calls.</summary>
    private sealed class StubRelevance(IReadOnlyList<Guid> order) : IRelevanceService
    {
        public List<Guid> Asked { get; } = [];

        public bool IsAvailable => true;

        public IReadOnlyList<SearchHit> Relevant(TopicRequest topic, Func<Guid, bool> include, int limit = 30) => [];

        public IReadOnlyList<SectionHit> WhereTo(Guid noteKey, string noteText, Book book, int limit = 5) => [];

        public IReadOnlyList<PlacementHit> Placements(Guid noteKey, string noteText, Book book, int limit = 3) => [];

        public IReadOnlyList<TopicHit> Topics(Guid noteKey, string noteText, Book book, int limit = 3) => [];

        public IReadOnlyList<SearchHit> Sources(Book book, Guid textBlockKey, Func<Guid, bool> include, int limit = 5)
        {
            lock (Asked) Asked.Add(textBlockKey);
            return [.. order.Where(include).Take(limit).Select((key, i) => new SearchHit(key, 0.9 - i * 0.3))];
        }
    }

    private Book Book => State.CurrentBook!;

    private string Text(string key, params object[] args) => Services.GetRequiredService<IStringLocalizer<Strings>>()[key, args].Value;

    private Guid KeyOf(string content) => Book.Page.AllBlocks().Single(b => b.Content == content).Key;

    private Guid NoteKey(VaultSession session, string content) => session.Notes.All().Single(n => n.Block.Content == content).Key;

    private async Task<(IRenderedComponent<EditorPane> Cut, VaultSession Session, StubRelevance Relevance)> RenderAsync()
    {
        var session = await OpenSessionAsync(_vault);
        var relevance = new StubRelevance([NoteKey(session, "Schon Quelle"), NoteKey(session, "Anderswo"), NoteKey(session, "Frei")]);
        session.WrapRelevance = _ => relevance;
        var cut = Render<EditorPane>();
        cut.WaitForAssertion(() => Assert.NotEmpty(Editor.Documents));
        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionBoxMoved(120.5, true));
        return (cut, session, relevance);
    }

    private Task MoveToAsync(IRenderedComponent<EditorPane> cut, string content)
    {
        var element = BookElements.Find(Book, KeyOf(content))!;
        var cursor = new CursorInfo(element.Kind, element.Key, BookElements.TextBlockOf(Book, element)?.Key, 0);
        return cut.InvokeAsync(() => Editor.Callbacks!.OnCursorChanged(cursor));
    }

    [Fact]
    public async Task FindSources_OffersSimilarNotes_UsedOnesMarked_PresentSourcesLeftOut()
    {
        var (cut, _, relevance) = await RenderAsync();
        await MoveToAsync(cut, "Erster Text");

        cut.Find(".ne-sec-find-sources").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".ne-source-candidate").Count));
        // The whole note with its sub-points, not just its first line.
        Assert.Equal(["Anderswo", "Frei\n– Unterpunkt eins\n  – Tiefer\n    zweite Zeile"],
            cut.FindAll(".ne-source-text").Select(e => e.TextContent.Trim()));
        Assert.Equal(["100 %", "67 %"], cut.FindAll(".ne-source-percent").Select(e => e.TextContent.Trim()));
        Assert.Equal(["03.03.2026", "02.03.2026"], cut.FindAll(".ne-source-date").Select(e => e.TextContent.Trim()));
        var used = cut.Find(".ne-source-used");
        Assert.Equal(Text("SourceFinderUsedIn", "Buch - Beta"), used.GetAttribute("title"));
        Assert.Single(cut.FindAll(".ne-source-used"));
        Assert.Equal([KeyOf("Erster Text")], relevance.Asked);
    }

    [Fact]
    public async Task FindSources_Link_SetsSourceAndUsedIn_AndTheNoteLeavesTheList()
    {
        var (cut, session, _) = await RenderAsync();
        await MoveToAsync(cut, "Erster Text");
        cut.Find(".ne-sec-find-sources").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".ne-source-candidate").Count));

        cut.FindAll(".ne-source-link")[1].Click();

        cut.WaitForAssertion(() => Assert.Equal(["Anderswo"], cut.FindAll(".ne-source-text").Select(e => e.TextContent.Trim())));
        var note = session.Notes.Get(NoteKey(session, "Frei"))!;
        var noteId = note.Block.Id!.Value;
        var blockId = Book.FindTextBlock(KeyOf("Erster Text"))!.Block.Id!.Value;
        Assert.Equal([Guid.Parse(Linked), noteId], Book.FindTextBlock(KeyOf("Erster Text"))!.Sources);
        var journal = _vault.Read("journals/2026_03_02.md");
        Assert.Contains("used-in:: [[Buch - Alpha]] ((" + blockId.ToString("D") + "))", journal);
        Assert.Contains("source:: ((" + Linked + ")), ((" + noteId.ToString("D") + "))", _vault.Read(BookPath));
    }

    [Fact]
    public async Task FindSources_NotOnHeading_NorWithoutLocalAi()
    {
        var (cut, session, _) = await RenderAsync();
        await MoveToAsync(cut, "# Zwei");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-sec-find-sources")));

        session.WrapRelevance = null;
        await MoveToAsync(cut, "Erster Text");
        cut.Render();

        Assert.Empty(cut.FindAll(".ne-sec-find-sources"));
    }
}
