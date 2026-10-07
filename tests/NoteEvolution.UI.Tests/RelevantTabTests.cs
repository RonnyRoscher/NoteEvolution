using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NoteEvolution.AI.Search;
using NoteEvolution.AI.Tests;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

public class RelevantTabTests : UiTestContext
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(1500);

    private const string Sleep = "Schlaf Träume Nummer";
    private const string Music = "Musik Gitarre Klavier Nummer";
    private const string Soup = "Suppe Gemüse kochen Nummer";
    private const string UsedSleep = "Schlaf Träume benutzt";

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

    /// <summary>A book with three sections (sleep, music, cooking) and notes about each; one sleep note is used.</summary>
    private TestVault Vault()
    {
        var notes = string.Concat(
            Enumerable.Range(1, 15).Select(i => $"- {Sleep} {i}\n")
            .Concat(Enumerable.Range(1, 15).Select(i => $"- {Music} {i}\n"))
            .Concat(Enumerable.Range(1, 10).Select(i => $"- {Soup} {i}\n"))) +
            $"- {UsedSleep}\n  used-in:: [[Buch - Alpha]]\n";
        var vault = TestVault.Create(
            ("pages/Buch - Alpha.md",
                "title:: Alpha\ntype:: book\n\n" +
                "- # Schlafen\n\t- Schlaf und Träume im Alltag\n" +
                "- # Musizieren\n\t- Musik Gitarre Klavier spielen\n" +
                "- # Kochen\n\t- Suppe kochen mit Gemüse\n"),
            ("pages/Ideen.md", notes));
        _vaults.Add(vault);
        return vault;
    }

    private string Text(string key, params object[] args) =>
        Services.GetRequiredService<IStringLocalizer<Strings>>()[key, args].Value;

    private static async Task Eventually(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "The condition was not met in time.");
            await Task.Delay(10);
        }
    }

    /// <summary>Opens the vault with a working embedder, waits until the notes are indexed and selects the book's <paramref name="section"/>-th section.</summary>
    private async Task<VaultSession> OpenReadyAsync(TestVault vault, FakeEmbedder embedder, int section = 0)
    {
        var session = await OpenSessionAsync(vault, UseAi(installed: true, () => embedder));
        await Eventually(() => session.AiStatus.State == AiState.Ready);
        Select(section);
        return session;
    }

    private void Select(int section)
    {
        State.CurrentSectionKey = State.CurrentBook!.Root.Children.ElementAt(section).Key;
        State.Notify();
    }

    /// <summary>Runs <paramref name="action"/> and waits until the pane has rendered because of it (events reach the components asynchronously).</summary>
    private static void Act(IRenderedComponent<NotesPane> cut, Action action)
    {
        var rendered = cut.RenderCount;
        action();
        cut.WaitForState(() => cut.RenderCount > rendered);
    }

    private static List<string> Texts(IRenderedComponent<NotesPane> cut) =>
        [.. cut.FindAll(".ne-relevant-list .ne-note-card > .ne-note-body > .ne-note-text").Select(e => e.TextContent.Trim())];

    private static List<string> Tabs(IRenderedComponent<NotesPane> cut) =>
        [.. cut.FindAll(".ne-tab").Select(e => e.TextContent.Trim())];

    private static List<string> ActiveTabs(IRenderedComponent<NotesPane> cut) =>
        [.. cut.FindAll(".ne-tab.active").Select(e => e.TextContent.Trim())];

    [Fact]
    public async Task Relevant_ShowsTop30ForSection_AfterDebounce()
    {
        var tv = Vault();
        await OpenReadyAsync(tv, new FakeEmbedder());
        var cut = Render<NotesPane>();

        cut.WaitForAssertion(() => Assert.Equal(30, Texts(cut).Count));
        var texts = Texts(cut);
        Assert.Contains(Sleep, texts[0]);
        Assert.Equal(16, texts.Count(t => t.StartsWith("Schlaf", StringComparison.Ordinal)));
        var scores = cut.FindAll(".ne-relevant-list .ne-note-score")
            .Select(e => int.Parse(e.TextContent.Replace("%", "").Trim(), System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        Assert.Equal(30, scores.Count);
        Assert.Equal(100, scores[0]);
        Assert.Equal(scores.OrderByDescending(s => s), scores);

        // A new section is picked up only 1500 ms after the change.
        Act(cut, () => Select(1));
        Time.Advance(Debounce - TimeSpan.FromMilliseconds(1));
        Assert.Contains(Sleep, Texts(cut)[0]);

        Time.Advance(TimeSpan.FromMilliseconds(1));

        cut.WaitForAssertion(() => Assert.Contains(Music, Texts(cut)[0]));
        Assert.Equal(30, Texts(cut).Count);
    }

    [Fact]
    public async Task Relevant_RapidSectionChanges_OnlyLatestApplied()
    {
        var tv = Vault();
        using var gate = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var embedder = new FakeEmbedder();
        embedder.OnEmbed = (call, _) =>
        {
            // The first topic (sleep) is slow: its embedding waits until the test lets it go.
            if (gate.IsSet || !embedder.Batches[call - 1].Any(t => t.Contains("Schlafen", StringComparison.Ordinal)))
            {
                return;
            }

            entered.Set();
            gate.Wait(Wait);
        };

        try
        {
            await OpenReadyAsync(tv, embedder, section: 1);
            Select(0);
            var before = embedder.Texts.Count;
            var cut = Render<NotesPane>();
            Assert.True(entered.Wait(Wait)); // the request for the first section is running

            // Two changes within the debounce time: neither starts before 1500 ms after the last one.
            Act(cut, () => Select(1));
            Time.Advance(TimeSpan.FromMilliseconds(1000));
            Act(cut, () => Select(2));
            Time.Advance(TimeSpan.FromMilliseconds(1499));
            Assert.DoesNotContain(embedder.Texts.Skip(before), t => t.Contains("Musizieren", StringComparison.Ordinal));
            Assert.DoesNotContain(embedder.Texts.Skip(before), t => t.Contains("Kochen", StringComparison.Ordinal));

            Time.Advance(TimeSpan.FromMilliseconds(1));

            cut.WaitForAssertion(() => Assert.Contains(Soup, Texts(cut)[0]));
            Assert.DoesNotContain(embedder.Texts.Skip(before), t => t.Contains("Musizieren", StringComparison.Ordinal));

            // The slow first request ends last; its result must not replace the newer one.
            gate.Set();
            await Task.Delay(300);
            cut.Render();
            Assert.Contains(Soup, Texts(cut)[0]);
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public async Task Relevant_HideUsedFilter_Applied()
    {
        var tv = Vault();
        await OpenReadyAsync(tv, new FakeEmbedder());
        var cut = Render<NotesPane>();
        cut.WaitForAssertion(() => Assert.Contains(UsedSleep, Texts(cut)));

        Act(cut, () => cut.Find(".ne-hide-used").Change(true));
        Time.Advance(Debounce);

        cut.WaitForAssertion(() => Assert.DoesNotContain(UsedSleep, Texts(cut)));
        Assert.Equal(30, Texts(cut).Count);
        Assert.Contains(Sleep, Texts(cut)[0]);

        Act(cut, () => cut.Find(".ne-hide-used").Change(false));
        Time.Advance(Debounce);

        cut.WaitForAssertion(() => Assert.Contains(UsedSleep, Texts(cut)));
    }

    [Fact]
    public async Task Relevant_NotesChanged_RecomputedAfterDebounce()
    {
        var tv = Vault();
        var session = await OpenReadyAsync(tv, new FakeEmbedder());
        var cut = Render<NotesPane>();
        cut.WaitForAssertion(() => Assert.Equal(30, Texts(cut).Count));
        var path = Path.Combine(tv.Root, "pages", "Ideen.md");
        File.WriteAllText(path, "- Schlaf Träume ganz neu\n");

        session.HandleExternalChange(path);
        Time.Advance(VaultSession.PageIndexDelay);

        // The page's notes get new keys and are embedded again; the list follows once they are in the index.
        cut.WaitForState(
            () =>
            {
                Time.Advance(Debounce);
                return Texts(cut).SequenceEqual(["Schlaf Träume ganz neu"]);
            },
            Wait);
    }

    [Fact]
    public async Task Relevant_ManuscriptMode_UsesCursorBlock()
    {
        var tv = Vault();
        await OpenReadyAsync(tv, new FakeEmbedder());
        State.Mode = ViewMode.Manuscript;
        State.CurrentSectionKey = State.CurrentBook!.Root.Key;
        State.CursorTextBlockKey = State.CurrentBook.Root.Children.ElementAt(1).TextBlocks.First().Key;
        var cut = Render<NotesPane>();

        cut.WaitForAssertion(() => Assert.Contains(Music, Texts(cut)[0]));

        Act(cut, () =>
        {
            State.CursorTextBlockKey = State.CurrentBook.Root.Children.ElementAt(2).TextBlocks.First().Key;
            State.Notify();
        });
        Time.Advance(Debounce);

        cut.WaitForAssertion(() => Assert.Contains(Soup, Texts(cut)[0]));
    }

    [Fact]
    public async Task Relevant_AiUnavailable_ShowsHint()
    {
        var tv = Vault();
        var session = await OpenSessionAsync(tv, UseAi(installed: false, () => new FakeEmbedder()));
        Select(0);
        var requested = 0;
        State.AiModelDialogRequested += () => requested++;
        var cut = Render<NotesPane>();

        // The model is missing: the Search tab is shown first, the Relevant tab explains and offers the download.
        Assert.Equal(AiState.ModelMissing, session.AiStatus.State);
        Assert.Equal([Text("NotesTabSearch")], ActiveTabs(cut));
        cut.Find(".ne-tab-relevant").Click();
        Assert.Equal(Text("NotesRelevantModelMissing"), cut.Find(".ne-relevant-hint").TextContent.Trim());
        Assert.Empty(cut.FindAll(".ne-relevant-list .ne-note-card"));
        Assert.Equal(Text("NotesRelevantDownload"), cut.Find(".ne-relevant-download").TextContent.Trim());

        cut.Find(".ne-relevant-download").Click();

        Assert.Equal(1, requested);
    }

    [Fact]
    public async Task Relevant_AiOff_ShowsHint()
    {
        var tv = Vault();
        await OpenSessionAsync(tv);
        var cut = Render<NotesPane>();

        cut.Find(".ne-tab-relevant").Click();

        Assert.Equal(Text("NotesRelevantOff"), cut.Find(".ne-relevant-hint").TextContent.Trim());
        Assert.Empty(cut.FindAll(".ne-relevant-download"));
    }

    [Fact]
    public async Task Relevant_AiFailed_ShowsErrorHint()
    {
        var tv = Vault();
        var session = await OpenSessionAsync(tv, UseAi(installed: true, () => new FakeEmbedder { Throws = true }));
        await Eventually(() => session.AiStatus.State == AiState.Failed);
        var cut = Render<NotesPane>();

        cut.Find(".ne-tab-relevant").Click();

        Assert.Equal(Text("NotesRelevantFailed"), cut.Find(".ne-relevant-hint").TextContent.Trim());
        Assert.Equal(FakeEmbedder.FailureMessage, cut.Find(".ne-relevant-hint").GetAttribute("title"));
    }

    [Fact]
    public async Task Relevant_IndexingWithoutVectors_ShowsPreparingThenResults()
    {
        var tv = Vault();
        using var gate = new ManualResetEventSlim();
        try
        {
            var session = await OpenSessionAsync(
                tv, UseAi(installed: true, () => new FakeEmbedder { OnEmbed = (_, _) => gate.Wait(Wait) }));
            Select(0);
            var cut = Render<NotesPane>();
            cut.Find(".ne-tab-relevant").Click();

            Assert.Equal(AiState.Indexing, session.AiStatus.State);
            Assert.Equal(Text("NotesRelevantPreparing"), cut.Find(".ne-relevant-hint").TextContent.Trim());

            gate.Set();

            // The AI becoming available recomputes the list (after the debounce).
            await Eventually(() => session.AiStatus.State == AiState.Ready);
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-relevant-hint")));
            cut.WaitForState(
                () =>
                {
                    Time.Advance(Debounce);
                    return Texts(cut).Count == 30;
                },
                Wait);
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public async Task Relevant_NoBook_ShowsHint()
    {
        var tv = TestVault.Create(("journals/2026_03_01.md", "- Eine Notiz\n- Noch eine\n"));
        _vaults.Add(tv);
        var session = await OpenSessionAsync(tv, UseAi(installed: true, () => new FakeEmbedder()));
        await Eventually(() => session.AiStatus.State == AiState.Ready);
        Assert.Null(State.CurrentBook);

        var cut = Render<NotesPane>();

        Assert.Equal([Text("NotesTabRelevant")], ActiveTabs(cut));
        Assert.Equal(Text("NotesRelevantNoBook"), cut.Find(".ne-relevant-hint").TextContent.Trim());
    }

    [Fact]
    public async Task Tabs_Order_RelevantSearchJournal_DefaultDependsOnAi()
    {
        var tv = Vault();
        using var gate = new ManualResetEventSlim();
        try
        {
            var session = await OpenSessionAsync(
                tv, UseAi(installed: true, () => new FakeEmbedder { OnEmbed = (_, _) => gate.Wait(Wait) }));
            Select(0);
            var cut = Render<NotesPane>();

            // The AI is not ready yet: Search comes first, and stays when the AI becomes available later.
            Assert.Equal([Text("NotesTabRelevant"), Text("NotesTabSearch"), Text("NotesTabJournal")], Tabs(cut));
            Assert.Equal([Text("NotesTabSearch")], ActiveTabs(cut));
            gate.Set();
            await Eventually(() => session.AiStatus.State == AiState.Ready);
            cut.Render();
            Assert.Equal([Text("NotesTabSearch")], ActiveTabs(cut));

            // Rendered once the AI is available: Relevant is the default.
            var again = Render<NotesPane>();
            Assert.Equal([Text("NotesTabRelevant"), Text("NotesTabSearch"), Text("NotesTabJournal")], Tabs(again));
            Assert.Equal([Text("NotesTabRelevant")], ActiveTabs(again));
            again.Find(".ne-tab-journal").Click();
            again.WaitForAssertion(() => Assert.Equal([Text("NotesTabJournal")], ActiveTabs(again)));
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public async Task Tabs_WithoutAi_SearchIsTheDefault()
    {
        var tv = Vault();
        await OpenSessionAsync(tv);

        var cut = Render<NotesPane>();

        Assert.Equal([Text("NotesTabRelevant"), Text("NotesTabSearch"), Text("NotesTabJournal")], Tabs(cut));
        Assert.Equal([Text("NotesTabSearch")], ActiveTabs(cut));
    }

    [Fact]
    public async Task Search_WithAi_UsesHybrid()
    {
        var tv = Vault();
        var session = await OpenReadyAsync(tv, new FakeEmbedder());
        var cut = Render<NotesPane>();
        cut.Find(".ne-tab-search").Click();

        cut.WaitForElement(".ne-search-input").Input("Gitarre");
        Time.Advance(TimeSpan.FromMilliseconds(300));

        // Full text alone finds the 15 music notes; the hybrid search adds the notes that only fit by meaning.
        Assert.IsType<HybridSearchService>(session.Search);
        cut.WaitForState(() => cut.FindAll(".ne-search-list .ne-note-card").Count > 0);
        var texts = cut.FindAll(".ne-search-list .ne-note-card > .ne-note-body > .ne-note-text")
            .Select(e => e.TextContent.Trim())
            .ToList();
        Assert.Contains(Music, texts[0]);
        Assert.True(texts.Count > 15);
        Assert.Contains(texts, t => t.StartsWith("Schlaf", StringComparison.Ordinal) || t.StartsWith("Suppe", StringComparison.Ordinal));
    }
}
