using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NoteEvolution.AI.Embeddings;
using NoteEvolution.AI.Language;
using NoteEvolution.AI.Search;
using NoteEvolution.AI.Tests;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Vaults;
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
        cut.Find(".ne-tab-relevant").Click();

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
            cut.Find(".ne-tab-relevant").Click();
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
        cut.Find(".ne-tab-relevant").Click();
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
        cut.Find(".ne-tab-relevant").Click();
        cut.WaitForAssertion(() => Assert.Equal(30, Texts(cut).Count));
        var path = Path.Combine(tv.Root, "pages", "Ideen.md");
        File.WriteAllText(path, "- Schlaf Träume ganz neu\n");

        session.HandleExternalChange(path);
        Time.Advance(VaultSession.PageIndexDelay);

        // The page's notes get new keys and are embedded again; the list follows once they are in the index.
        AdvanceUntil(cut, () => Assert.Equal(["Schlaf Träume ganz neu"], Texts(cut)), Debounce);
    }

    [Fact]
    public async Task Relevant_CursorInTextBlock_UsesCursorBlock()
    {
        var tv = Vault();
        await OpenReadyAsync(tv, new FakeEmbedder());
        State.CurrentSectionKey = State.CurrentBook!.Root.Key;
        SetCursorInTextBlock(State.CurrentBook.Root.Children.ElementAt(1).TextBlocks.First().Key);
        var cut = Render<NotesPane>();
        cut.Find(".ne-tab-relevant").Click();

        cut.WaitForAssertion(() => Assert.Contains(Music, Texts(cut)[0]));

        Act(cut, () =>
        {
            SetCursorInTextBlock(State.CurrentBook.Root.Children.ElementAt(2).TextBlocks.First().Key);
            State.Notify();
        });
        Time.Advance(Debounce);

        cut.WaitForAssertion(() => Assert.Contains(Soup, Texts(cut)[0]));
    }

    /// <summary>The "Wohin damit?" items of the card at <paramref name="index"/> of the list.</summary>
    private static List<AngleSharp.Dom.IElement> WhereToItems(IRenderedComponent<NotesPane> cut, int index) =>
        [.. cut.FindAll(".ne-relevant-list .ne-note-card")[index].QuerySelectorAll(".ne-note-whereto-item")];

    /// <summary>
    /// Advances the time past the debounce several times and gives the background work time to run, so a computation
    /// the tab would start has had its chance to show.
    /// </summary>
    private async Task LetTimePass()
    {
        for (var i = 0; i < 10; i++)
        {
            Time.Advance(Debounce);
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Relevant_FrozenWhileWhereToOpen_AfterJumpAndAdopt_RecomputesAfterClose()
    {
        var tv = Vault();
        var session = await OpenReadyAsync(tv, new FakeEmbedder());
        var cut = Render<NotesPane>();
        cut.Find(".ne-tab-relevant").Click();
        Act(cut, () => cut.Find(".ne-hide-used").Change(true));
        Time.Advance(Debounce);
        cut.WaitForAssertion(() => Assert.DoesNotContain(UsedSleep, Texts(cut)));
        cut.WaitForAssertion(() => Assert.Equal(30, Texts(cut).Count));
        var shown = Texts(cut);
        Assert.Contains(Sleep, shown[0]);
        const int index = 2;
        var note = session.Notes.All().Single(n => n.Block.Content == shown[index]);
        cut.FindAll(".ne-relevant-list .ne-note-card")[index].QuerySelector(".ne-note-whereto")!.Click();
        cut.WaitForAssertion(() => Assert.Equal(3, WhereToItems(cut, index).Count));
        var music = State.CurrentBook!.Root.Children.ElementAt(1).TextBlocks.Single();

        // The jump to the music text block moves the cursor, adopting the note changes the notes: the list stays as it is.
        WhereToItems(cut, index).Single(item => item.TextContent.Contains("Musik", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.Equal(music.Key, State.PendingReveal));
        cut.FindAll(".ne-relevant-list .ne-note-card")[index].QuerySelector(".ne-note-adopt-more")!.Click();
        cut.Find(".ne-adopt-menu .ne-adopt-after").Click();
        cut.WaitForAssertion(() => Assert.True(note.IsUsed));
        await LetTimePass();

        Assert.Equal(shown, Texts(cut));
        Assert.Equal(3, WhereToItems(cut, index).Count);
        Assert.NotNull(cut.FindAll(".ne-relevant-list .ne-note-card")[index].QuerySelector(".ne-note-used"));

        // Closing the list lets the tab compute once with the current state, which leaves out the used note.
        cut.FindAll(".ne-relevant-list .ne-note-card")[index].QuerySelector(".ne-note-whereto")!.Click();

        AdvanceUntil(cut, () => Assert.DoesNotContain(note.Block.Content, Texts(cut)), Debounce);
        Assert.Equal(30, Texts(cut).Count);
        Assert.Empty(State.WhereToOpenNotes);
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

        // The model is missing: the journal is shown first, the Relevant tab explains and offers the download.
        Assert.Equal(AiState.ModelMissing, session.AiStatus.State);
        Assert.Equal([Text("NotesTabJournal")], ActiveTabs(cut));
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
            AdvanceUntil(cut, () => Assert.Equal(30, Texts(cut).Count), Debounce);
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

        cut.Find(".ne-tab-relevant").Click();

        Assert.Equal([Text("NotesTabRelevant")], ActiveTabs(cut));
        Assert.Equal(Text("NotesRelevantNoBook"), cut.Find(".ne-relevant-hint").TextContent.Trim());
    }

    [Fact]
    public async Task Tabs_Order_RelevantSearchJournalCoverage_JournalIsTheDefaultAlsoWithTheAi()
    {
        var tv = Vault();
        using var gate = new ManualResetEventSlim();
        try
        {
            var session = await OpenSessionAsync(
                tv, UseAi(installed: true, () => new FakeEmbedder { OnEmbed = (_, _) => gate.Wait(Wait) }));
            Select(0);
            Assert.Equal(AiState.Indexing, session.AiStatus.State);
            var cut = Render<NotesPane>();

            // The model is installed and the notes are being indexed: the start tab is Journal all the same.
            Assert.Equal([Text("NotesTabRelevant"), Text("NotesTabSearch"), Text("NotesTabJournal"), Text("NotesTabCoverage")], Tabs(cut));
            Assert.Equal([Text("NotesTabJournal")], ActiveTabs(cut));

            // Once the AI is ready the tab stays where it is, and so does a choice of the user.
            gate.Set();
            await Eventually(() => session.AiStatus.State == AiState.Ready);
            cut.Render();
            Assert.Equal([Text("NotesTabJournal")], ActiveTabs(cut));
            cut.Find(".ne-tab-relevant").Click();
            cut.WaitForAssertion(() => Assert.Equal([Text("NotesTabRelevant")], ActiveTabs(cut)));
            cut.Render();
            Assert.Equal([Text("NotesTabRelevant")], ActiveTabs(cut));
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public async Task Tabs_JournalIsTheDefault_AlsoWhenAiIsReady()
    {
        var tv = Vault();
        await OpenReadyAsync(tv, new FakeEmbedder());

        var cut = Render<NotesPane>();

        Assert.Equal([Text("NotesTabJournal")], ActiveTabs(cut));
    }

    [Fact]
    public async Task Tabs_ModelMissing_JournalIsTheDefault()
    {
        var tv = Vault();
        await OpenSessionAsync(tv, UseAi(installed: false, () => new FakeEmbedder()));
        Select(0);

        var cut = Render<NotesPane>();

        Assert.Equal([Text("NotesTabJournal")], ActiveTabs(cut));
    }

    [Fact]
    public async Task Tabs_WithoutAi_JournalIsTheDefault()
    {
        var tv = Vault();
        await OpenSessionAsync(tv);

        var cut = Render<NotesPane>();

        Assert.Equal([Text("NotesTabRelevant"), Text("NotesTabSearch"), Text("NotesTabJournal"), Text("NotesTabCoverage")], Tabs(cut));
        Assert.Equal([Text("NotesTabJournal")], ActiveTabs(cut));
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

/// <summary>The "Relevant" tab's two groups: the notes the model placed into the section, then the similar ones (spec 5).</summary>
public class RelevantTabPlacedTests : UiTestContext
{
    private const string SleepNote =
        "Wer zu wenig schläft, verliert zuerst die Geduld und dann die Fähigkeit, Wichtiges von Unwichtigem zu trennen; das merkt man selbst erst spät.";

    private const string DreamNote =
        "Träume ordnen das Erlebte der Nacht und sortieren, was am Tag wichtig war, in das Gedächtnis ein, still und ohne Anstrengung.";

    private const string OtherNote = "Suppe kochen mit Gemüse braucht Zeit und eine gute Brühe, die lange ziehen darf, bevor man sie salzt.";

    private const string Reason = "Es geht um Schlaf und Träume.";

    private readonly List<TestVault> _vaults = [];
    private readonly FakeChatModel _chat = new();

    public RelevantTabPlacedTests()
    {
        // Summaries name their heading; a check answers with the first place, except for the soup note (no usable answer).
        _chat.Answer = (call, n) => call.Options.Json
            ? call.User.Contains("Suppe kochen", StringComparison.Ordinal)
                ? "kein JSON"
                : "{\"begruendung\": \"" + Reason + "\", \"stelle\": \"" + PlaceOf(call.User) + "\"}"
            : call.User.StartsWith("Überschrift: ", StringComparison.Ordinal)
                ? "Zusammenfassung von " + call.User.Split('\n')[0]["Überschrift: ".Length..] + "."
                : $"Kernaussage {n}.";
    }

    /// <summary>The number of the offered place after the dream paragraph (in the section Träume), or "1.1" if it is not offered.</summary>
    private static string PlaceOf(string prompt) =>
        prompt.Split('\n').Select(l => l.TrimStart()).FirstOrDefault(l => l.Contains(" nach: Träume ordnen das Erlebte.", StringComparison.Ordinal))
            ?.Split(' ')[0] ?? "1.1";

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _vaults.ForEach(v => v.Dispose());
        }
    }

    private string Text(string key, params object[] args) =>
        Services.GetRequiredService<IStringLocalizer<Strings>>()[key, args].Value;

    private static async Task Eventually(Func<bool> condition, string because)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, because);
            await Task.Delay(10);
        }
    }

    /// <summary>A book with the sections Schlaf (with the sub-section Träume) and Arbeit, and three journal notes.</summary>
    private TestVault Vault()
    {
        var vault = TestVault.Create(
            ("pages/Buch - Alpha.md",
                "title:: Alpha\ntype:: book\n\n- # Schlaf\n\t- Über den Schlaf und seine Phasen.\n\t- ## Träume\n\t\t- Träume ordnen das Erlebte.\n\t\t- Wer schlecht schläft, träumt wirr.\n- # Arbeit\n\t- Konzentration braucht Ruhe.\n"),
            ("journals/2026_03_01.md", $"- {SleepNote}\n"),
            ("journals/2026_03_02.md", $"- {DreamNote}\n"),
            ("journals/2026_03_03.md", $"- {OtherNote}\n"));
        _vaults.Add(vault);
        return vault;
    }

    private static NoteBlock Note(VaultSession session, string text) => session.Notes.All().Single(n => n.Block.Content == text);

    /// <summary>Opens the vault with the language model, lets the background place the sleep and dream notes and selects the section <paramref name="title"/>.</summary>
    private async Task<VaultSession> OpenPlacedAsync(string title = "Schlaf")
    {
        var language = UseLanguageModel(_chat);
        await language.RefreshAsync(CancellationToken.None);
        Assert.Equal(LanguageModelState.Ready, language.State);
        var session = await OpenWithAiAsync(Vault());
        await Eventually(() => session.Understanding is { Total: 5, Done: 5 }, "The background work did not finish.");
        Time.Advance(VaultSession.PageIndexDelay);
        foreach (var text in new[] { SleepNote, DreamNote })
        {
            var key = Note(session, text).Key;
            await Eventually(() => session.Placement(key) is not null, "The placement was not stored.");
        }

        Select(title);
        return session;
    }

    private void Select(string title)
    {
        State.CurrentSectionKey = State.CurrentBook!.Root.Children.Single(n => n.Title == title).Key;
        State.Notify();
    }

    private IRenderedComponent<NotesPane> OpenTab()
    {
        var cut = Render<NotesPane>();
        cut.Find(".ne-tab-relevant").Click();
        return cut;
    }

    private static List<string> PlacedTexts(IRenderedComponent<NotesPane> cut) =>
        [.. cut.FindAll(".ne-relevant-placed-item .ne-note-card > .ne-note-body > .ne-note-text").Select(e => e.TextContent.Trim())];

    private static List<string> SimilarTexts(IRenderedComponent<NotesPane> cut) =>
        [.. cut.FindAll(".ne-relevant-list > .ne-note-card > .ne-note-body > .ne-note-text").Select(e => e.TextContent.Trim())];

    [Fact]
    public async Task Relevant_PlacedHere_FirstGroup_NewestFirst()
    {
        var session = await OpenPlacedAsync();
        var dreams = State.CurrentBook!.Root.Children.Single(n => n.Title == "Schlaf").Children.Single();
        Assert.Equal(dreams.Key, session.Placement(Note(session, SleepNote).Key)!.Verdict.SectionKey);

        // Both notes sit in the sub-section Träume of the current section Schlaf.
        var cut = OpenTab();

        cut.WaitForAssertion(() => Assert.Equal([DreamNote, SleepNote], PlacedTexts(cut)));
        Assert.Equal("Hierher eingeordnet", Text("NotesRelevantPlaced"));
        Assert.Equal(Text("NotesRelevantPlaced"), cut.Find(".ne-relevant-group-placed").TextContent.Trim());
    }

    [Fact]
    public async Task Relevant_Similar_SecondGroup_WithoutDuplicates()
    {
        await OpenPlacedAsync();

        var cut = OpenTab();

        cut.WaitForAssertion(() => Assert.Equal([OtherNote], SimilarTexts(cut)));
        Assert.Equal("Weitere ähnliche", Text("NotesRelevantSimilar"));
        Assert.Equal(Text("NotesRelevantSimilar"), cut.Find(".ne-relevant-group-similar").TextContent.Trim());
        Assert.Equal(2, PlacedTexts(cut).Count);
        Assert.Equal(3, cut.FindAll(".ne-relevant-list .ne-note-card").Count);
        var order = cut.FindAll(".ne-relevant-list > h4, .ne-relevant-list > .ne-relevant-placed-item, .ne-relevant-list > .ne-note-card")
            .Select(e => e.ClassList.Contains("ne-relevant-group-placed") ? "P" : e.ClassList.Contains("ne-relevant-group-similar") ? "S" : "c");
        Assert.Equal("PccSc", string.Concat(order));
    }

    [Fact]
    public async Task Relevant_NoPlaced_NoGroupHeadings()
    {
        await OpenPlacedAsync("Arbeit");

        var cut = OpenTab();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".ne-relevant-list .ne-note-card").Count));
        Assert.Empty(cut.FindAll(".ne-relevant-group-heading"));
        Assert.Empty(cut.FindAll(".ne-relevant-placed-item"));
    }

    [Fact]
    public async Task Relevant_PlacedStale_Marked()
    {
        var session = await OpenPlacedAsync();
        var cut = OpenTab();
        cut.WaitForAssertion(() => Assert.Equal(2, PlacedTexts(cut).Count));
        Assert.Empty(cut.FindAll(".ne-relevant-stale"));

        // The edit keeps the note's first 80 characters, so its identity: the stored placement is stale, the new check waits.
        using var gate = new SemaphoreSlim(0);
        _chat.Gate = gate;
        const string edited = SleepNote + " Auch das.";
        var page = session.Vault.FindPageByPath(Path.Combine(_vaults[^1].Root, "journals", "2026_03_01.md"))!;
        page.AllBlocks().Single(b => b.Content == SleepNote).SetContent(edited);
        Assert.True(session.TrySave(page, out var error), error?.Message);
        Time.Advance(VaultSession.PageIndexDelay);
        await Eventually(() => session.Placement(Note(session, edited).Key) is { Stale: true }, "The placement was not shown as stale.");

        // The page change recomputes the list after the debounce, which the placements' change does not restart.
        AdvanceUntil(cut, () => Assert.Equal("veraltet", cut.Find(".ne-relevant-stale").TextContent.Trim()), RelevantTab.Debounce);
        Assert.Single(cut.FindAll(".ne-relevant-stale"));
        gate.Release(100);
    }

    [Fact]
    public async Task Relevant_PlacementStoredDuringDebounce_DoesNotRestartIt()
    {
        var session = await OpenPlacedAsync();
        var cut = OpenTab();
        cut.WaitForAssertion(() => Assert.Equal(2, PlacedTexts(cut).Count));

        // Arbeit has no placed notes; its list is due one debounce after the switch (once the pane rendered it).
        var rendered = cut.RenderCount;
        Select("Arbeit");
        cut.WaitForState(() => cut.RenderCount > rendered);
        Time.Advance(RelevantTab.Debounce - TimeSpan.FromMilliseconds(500));

        // A placement stored meanwhile ("Genauer prüfen" again) changes the placements but not the search's schedule.
        var sleep = Note(session, SleepNote);
        var (verdict, _) = await session.CheckPlacementAsync(sleep.Key, EmbeddingText.ForNote(sleep), State.CurrentBook!, CancellationToken.None);
        Assert.NotNull(verdict);
        cut.WaitForAssertion(() => Assert.Empty(PlacedTexts(cut)));
        Time.Advance(TimeSpan.FromMilliseconds(500));

        cut.WaitForAssertion(() => Assert.Equal(3, SimilarTexts(cut).Count));
        Assert.Empty(cut.FindAll(".ne-relevant-group-heading"));
    }

    [Fact]
    public async Task Relevant_PlacedTooltip_ReasonAndFit()
    {
        await OpenPlacedAsync();

        var cut = OpenTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".ne-relevant-placed-item")));
        Assert.All(cut.FindAll(".ne-relevant-placed-item"),
            item => Assert.Equal($"{Reason} ({Text("NoteFitPartial")})", item.GetAttribute("title")));
        Assert.Equal("Passung teilweise", Text("NoteFitPartial"));
    }
}
