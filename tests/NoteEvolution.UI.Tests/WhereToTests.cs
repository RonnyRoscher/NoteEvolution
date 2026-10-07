using Bunit;
using NoteEvolution.AI.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NoteEvolution.Core.Vaults;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;
using static NoteEvolution.UI.Tests.NotesTestData;

namespace NoteEvolution.UI.Tests;

/// <summary>The "Wohin damit?" button of a note card.</summary>
public class WhereToTests : UiTestContext
{
    private const string NoteText = "Schlaf und Träume im Alltag";

    private readonly List<TestVault> _vaults = [];

    protected override void Dispose(bool disposing)
    {
        // The session (and its vector cache in the vault) is closed first, then the vault folders are deleted.
        base.Dispose(disposing);
        if (disposing)
        {
            _vaults.ForEach(v => v.Dispose());
        }
    }

    /// <summary>A book with seven sections, each with one text block, and one note.</summary>
    private TestVault Vault()
    {
        var vault = TestVault.Create(
            (AlphaPath,
                "title:: Alpha\ntype:: book\n\n" +
                "- # Schlafen\n\t- Schlaf und Träume im Alltag\n" +
                "- # Musizieren\n\t- Musik Gitarre Klavier spielen\n" +
                "- # Kochen\n\t- Suppe kochen mit Gemüse\n" +
                "- # Reisen\n\t- Zug und Flugzeug nach Rom\n" +
                "- # Garten\n\t- Rosen und Tomaten pflanzen\n" +
                "- # Sport\n\t- Laufen und Schwimmen im See\n" +
                "- # Lesen\n\t- Romane und Gedichte am Abend\n"),
            (NotesPath, $"- {NoteText}\n"));
        _vaults.Add(vault);
        return vault;
    }

    private string Text(string key) => Services.GetRequiredService<IStringLocalizer<Strings>>()[key].Value;

    private IRenderedComponent<NoteCard> RenderCard(VaultSession session) =>
        Render<NoteCard>(p => p.Add(c => c.Note, Note(session, NoteText)));

    [Fact]
    public async Task WhereTo_ShowsFiveSections_ClickNavigates()
    {
        var tv = Vault();
        var session = await OpenWithAiAsync(tv);
        var book = State.CurrentBook!;
        var flushed = 0;
        State.FlushEditor = () =>
        {
            flushed++;
            return Task.CompletedTask;
        };
        State.CursorTextBlockKey = book.Root.Children.First().TextBlocks.First().Key;
        var cut = RenderCard(session);

        cut.Find(".ne-note-whereto").Click();

        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".ne-note-whereto-item").Count));
        var items = cut.FindAll(".ne-note-whereto-item");
        var percents = items
            .Select(i => int.Parse(i.QuerySelector(".ne-note-whereto-percent")!.TextContent.Replace("%", "").Trim(), System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        Assert.Equal(100, percents[0]);
        Assert.Equal(percents.OrderByDescending(p => p), percents);
        var titles = items.Select(i => i.QuerySelector(".ne-note-whereto-title")!.TextContent.Trim()).ToList();
        Assert.Equal(titles.Count, titles.Distinct().Count());
        Assert.All(titles, title => Assert.Contains(book.Root.Children, s => s.Title == title));

        var target = book.Root.Children.Single(s => s.Title == titles[1]);
        items[1].Click();

        cut.WaitForAssertion(() => Assert.Equal(target.Key, State.CurrentSectionKey));
        Assert.Equal(1, flushed);
        Assert.Null(State.CursorTextBlockKey);
        Assert.Same(book, State.CurrentBook);
    }

    [Fact]
    public async Task WhereTo_NoHits_ShowsHint()
    {
        // The embedder fails after the indexing (the sections need embedding): the AI answers with nothing.
        var tv = Vault();
        var embedder = new FakeEmbedder();
        var session = await OpenWithAiAsync(tv, embedder);
        embedder.Throws = true;
        var cut = RenderCard(session);

        cut.Find(".ne-note-whereto").Click();

        cut.WaitForAssertion(() => Assert.Equal(Text("NoteWhereToNone"), cut.Find(".ne-note-whereto-none").TextContent.Trim()));
        Assert.Empty(cut.FindAll(".ne-note-whereto-item"));
    }

    [Fact]
    public async Task WhereTo_HiddenWithoutAi()
    {
        var tv = Vault();
        var session = await OpenSessionAsync(tv);

        var off = RenderCard(session);

        Assert.Empty(off.FindAll(".ne-note-whereto"));
    }

    [Fact]
    public async Task WhereTo_HiddenWithoutBook()
    {
        var tv = TestVault.Create((NotesPath, $"- {NoteText}\n"));
        _vaults.Add(tv);
        var session = await OpenWithAiAsync(tv);
        Assert.Null(State.CurrentBook);

        var cut = RenderCard(session);

        Assert.Empty(cut.FindAll(".ne-note-whereto"));
    }

    [Fact]
    public async Task WhereTo_AiModelMissing_Hidden()
    {
        var tv = Vault();
        var session = await OpenSessionAsync(tv, UseAi(installed: false, () => new FakeEmbedder()));

        var cut = RenderCard(session);

        Assert.Empty(cut.FindAll(".ne-note-whereto"));
    }
}
