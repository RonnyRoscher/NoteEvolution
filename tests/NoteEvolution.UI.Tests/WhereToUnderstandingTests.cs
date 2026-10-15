using AngleSharp.Dom;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NoteEvolution.AI.Language;
using NoteEvolution.AI.Tests;
using NoteEvolution.AI.Understanding;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>"Wohin damit?" with the language model's topics and "Genauer prüfen" (spec 5.1, 5.2, 11.1).</summary>
public class WhereToUnderstandingTests : UiTestContext
{
    private const string DreamText = "Träume ordnen das Erlebte.";

    private const string SleepNote =
        "Wer zu wenig schläft, verliert zuerst die Geduld und dann die Fähigkeit, Wichtiges von Unwichtigem zu trennen; das merkt man selbst erst spät.";

    private const string WorkNote =
        "Konzentration entsteht nicht durch Willenskraft allein, sondern durch eine Umgebung, die Unterbrechungen fernhält und Pausen zulässt, jeden Tag neu.";

    private const string Reason = "Es geht um Schlaf und Träume.";

    private readonly List<TestVault> _vaults = [];
    private readonly FakeChatModel _chat = new();

    public WhereToUnderstandingTests()
    {
        // Summaries name their heading; the check answers with the first topic's first place.
        _chat.Answer = (call, n) => call.Options.Json
            ? "{\"thema\": 1, \"stelle\": \"1.1\", \"begruendung\": \"" + Reason + "\"}"
            : call.User.StartsWith("Überschrift: ", StringComparison.Ordinal)
                ? "Zusammenfassung von " + call.User.Split('\n')[0]["Überschrift: ".Length..] + "."
                : $"Kernaussage {n}.";
    }

    protected override void Dispose(bool disposing)
    {
        // The session (and its stores in the vault) is closed first, then the vault folders are deleted.
        base.Dispose(disposing);
        if (disposing)
        {
            _vaults.ForEach(v => v.Dispose());
        }
    }

    /// <summary>A book with 3 headings (one a sub-heading) and 2 notes long enough for a core statement: 5 items of background work.</summary>
    private TestVault Vault()
    {
        var vault = TestVault.Create(
            ("pages/Buch - Alpha.md",
                $"title:: Alpha\ntype:: book\n\n- # Schlaf\n\t- Über den Schlaf und seine Phasen.\n\t- ## Träume\n\t\t- {DreamText}\n\t\t- Wer schlecht schläft, träumt wirr.\n- # Arbeit\n\t- Konzentration braucht Ruhe.\n"),
            ("journals/2026_03_01.md", $"- {SleepNote}\n"),
            ("journals/2026_03_02.md", $"- {WorkNote}\n"));
        _vaults.Add(vault);
        return vault;
    }

    private string Text(string key, params object[] args) =>
        Services.GetRequiredService<IStringLocalizer<Strings>>()[key, args].Value;

    private string Duration(int seconds) =>
        Durations.Format(TimeSpan.FromSeconds(seconds), Services.GetRequiredService<IStringLocalizer<Strings>>());

    private static async Task Eventually(Func<bool> condition, string because = "The condition was not met in time.")
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, because);
            await Task.Delay(10);
        }
    }

    /// <summary>A ready language model and an open vault whose background work is done (or, with <paramref name="gate"/>, runs).</summary>
    private async Task<VaultSession> OpenReadyAsync(SemaphoreSlim? gate = null)
    {
        _chat.Gate = gate;
        var language = UseLanguageModel(_chat);
        await language.RefreshAsync(CancellationToken.None);
        Assert.Equal(LanguageModelState.Ready, language.State);
        var session = await OpenWithAiAsync(Vault());
        if (gate is null)
        {
            await Eventually(() => session.Understanding is { Total: 5, Done: 5 }, "The background work did not finish.");
        }

        return session;
    }

    private IRenderedComponent<NoteCard> OpenList(VaultSession session)
    {
        var note = session.Notes.All().Single(n => n.Block.Content == SleepNote);
        var cut = Render<NoteCard>(p => p.Add(c => c.Note, note));
        cut.Find(".ne-note-whereto").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".ne-note-whereto-item")));
        return cut;
    }

    private static string? Topic(IElement item) => item.QuerySelector(".ne-note-whereto-topic")?.TextContent.Trim();

    private static string? Place(IElement item) => item.QuerySelector(".ne-note-whereto-place")?.TextContent.Trim();

    private TextBlock TextBlock(string text) => Blocks(State.CurrentBook!.Root).Single(tb => tb.Text == text);

    private static IEnumerable<TextBlock> Blocks(OutlineNode node) => node.TextBlocks.Concat(node.Children.SelectMany(Blocks));

    /// <summary>The text of the first place the check's prompt offered (its answer "1.1").</summary>
    private string FirstOfferedPlace()
    {
        var user = _chat.Calls.Last(c => c.Options.Json).User;
        var line = user.Split('\n').Single(l => l.TrimStart().StartsWith("1.1 nach: ", StringComparison.Ordinal));
        return line.TrimStart()["1.1 nach: ".Length..];
    }

    [Fact]
    public async Task WhereTo_ShowsTopicAndPlace_TooltipSummary()
    {
        var session = await OpenReadyAsync();

        var cut = OpenList(session);

        var items = cut.FindAll(".ne-note-whereto-item");
        var dream = items.Single(item => Place(item) == Text("NoteWhereToAfter", DreamText));
        Assert.Equal("Schlaf › Träume", Topic(dream));
        Assert.Equal("Zusammenfassung von Träume.", dream.GetAttribute("title"));
        Assert.Empty(cut.FindAll(".ne-note-whereto-path"));
        Assert.All(items, item => Assert.NotNull(Place(item)));
        Assert.Equal("nach ‚Träume ordnen das Erlebte.‘", Text("NoteWhereToAfter", DreamText));
    }

    [Fact]
    public async Task WhereTo_WithoutUnderstanding_AsToday()
    {
        await OpenWithAiAsync(Vault());

        var cut = OpenList(State.Session!);

        var items = cut.FindAll(".ne-note-whereto-item");
        Assert.All(items, item =>
        {
            Assert.Null(Topic(item));
            Assert.Null(Place(item));
            Assert.NotNull(item.QuerySelector(".ne-note-whereto-excerpt"));
            Assert.Equal(Text("NoteWhereToItemTitle"), item.GetAttribute("title"));
        });
        Assert.Equal("Schlaf / Träume",
            items.Single(item => item.QuerySelector(".ne-note-whereto-excerpt")!.TextContent.Trim() == DreamText)
                .QuerySelector(".ne-note-whereto-path")!.TextContent.Trim());
        Assert.Empty(cut.FindAll(".ne-note-whereto-check"));
    }

    [Fact]
    public async Task Check_Hidden_WhenModelNotReady()
    {
        _chat.Version = null;
        var language = UseLanguageModel(_chat);
        await language.RefreshAsync(CancellationToken.None);
        Assert.Equal(LanguageModelState.Unreachable, language.State);
        await OpenWithAiAsync(Vault());

        var cut = OpenList(State.Session!);

        Assert.Empty(cut.FindAll(".ne-note-whereto-check"));

        // The model becomes ready: the button appears below the open list.
        _chat.Version = "0.5.0";
        await language.RefreshAsync(CancellationToken.None);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-note-whereto-results > .ne-note-whereto-check")));
        Assert.Equal(Text("CheckButton"), cut.Find(".ne-note-whereto-check").TextContent.Trim());
    }

    [Fact]
    public async Task Check_Hidden_WhileTheWorkerFindsTheServerGone()
    {
        using var gate = new SemaphoreSlim(0);
        var session = await OpenReadyAsync(gate);
        await Eventually(() => _chat.Calls.Count == 1);
        var cut = OpenList(session);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-note-whereto-check")));

        // Ollama stops while the runtime still says ready: the worker pauses, the check is not offered.
        _chat.Version = null;
        _chat.Failure = (_, _) => new ChatModelUnavailableException("gone");
        gate.Release(100);
        await Eventually(() => session.Understanding?.Pause == UnderstandingPause.Unreachable);
        Assert.Equal(LanguageModelState.Ready, LanguageModel!.State);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-note-whereto-check")));

        // Back after the worker's next check: offered again.
        _chat.Version = "0.5.0";
        _chat.Failure = (_, _) => null;
        Time.Advance(UnderstandingWorker.RetryInterval);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-note-whereto-check")));
    }

    [Fact]
    public async Task Check_ShowsElapsedOfExpected_ThenRecommendationFirst()
    {
        var session = await OpenReadyAsync();
        var cut = OpenList(session);
        var count = cut.FindAll(".ne-note-whereto-item").Count;
        using var gate = new SemaphoreSlim(0);
        _chat.Gate = gate;

        cut.Find(".ne-note-whereto-check").Click();

        // Before the first check 90 s are expected; the elapsed time ticks every second.
        cut.WaitForAssertion(() => Assert.Equal(Text("CheckRunning", Duration(0), Duration(90)), cut.Find(".ne-note-whereto-checking-text").TextContent.Trim()));
        await Eventually(() => _chat.Calls.Any(c => c.Options.Json));
        Assert.Empty(cut.FindAll(".ne-note-whereto-check"));
        Time.Advance(TimeSpan.FromSeconds(42));
        cut.WaitForAssertion(() => Assert.Equal(Text("CheckRunning", Duration(42), Duration(90)), cut.Find(".ne-note-whereto-checking-text").TextContent.Trim()));
        var progress = cut.Find(".ne-note-whereto-checking progress");
        Assert.Equal("42", progress.GetAttribute("value"));
        Assert.Equal("90", progress.GetAttribute("max"));
        Assert.Empty(cut.FindAll(".ne-note-whereto-check-longer"));

        gate.Release();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-note-whereto-recommended")));
        Assert.Empty(cut.FindAll(".ne-note-whereto-checking"));
        var entries = cut.FindAll(".ne-note-whereto-list > li");
        Assert.Equal(count + 1, entries.Count);
        var recommended = entries[0].QuerySelector(".ne-note-whereto-recommended")!;
        Assert.Equal(Text("CheckRecommended"), recommended.QuerySelector(".ne-note-whereto-recommended-label")!.TextContent.Trim());
        Assert.Equal(Reason, recommended.QuerySelector(".ne-note-whereto-reason")!.TextContent.Trim());
        Assert.Equal(Text("NoteWhereToAfter", FirstOfferedPlace()), Place(recommended));
        Assert.NotNull(Topic(recommended));

        // The next check expects the mean of the checks so far.
        cut.Find(".ne-note-whereto-check").Click();
        cut.WaitForAssertion(() => Assert.Equal(Text("CheckRunning", Duration(0), Duration(42)), cut.Find(".ne-note-whereto-checking-text").TextContent.Trim()));
        gate.Release();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-note-whereto-checking")));
        Assert.Single(cut.FindAll(".ne-note-whereto-recommended"));
    }

    [Fact]
    public async Task Check_LongerThanExpected_Indeterminate()
    {
        var session = await OpenReadyAsync();
        var cut = OpenList(session);
        using var gate = new SemaphoreSlim(0);
        _chat.Gate = gate;
        cut.Find(".ne-note-whereto-check").Click();
        await Eventually(() => _chat.Calls.Any(c => c.Options.Json));

        Time.Advance(TimeSpan.FromSeconds(91));

        cut.WaitForAssertion(() => Assert.Equal(Text("CheckLonger"), cut.Find(".ne-note-whereto-check-longer").TextContent.Trim()));
        Assert.Equal(Text("CheckRunning", Duration(91), Duration(90)), cut.Find(".ne-note-whereto-checking-text").TextContent.Trim());
        var progress = cut.Find(".ne-note-whereto-checking progress");
        Assert.False(progress.HasAttribute("value"));
        Assert.Equal("dauert länger als erwartet", Text("CheckLonger"));
        gate.Release();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-note-whereto-recommended")));
    }

    [Fact]
    public async Task Check_Unusable_ShowsHint_ListStays()
    {
        var session = await OpenReadyAsync();
        var cut = OpenList(session);
        var count = cut.FindAll(".ne-note-whereto-item").Count;
        _chat.Answer = (_, _) => "Das passt wohl zu Schlaf.";

        cut.Find(".ne-note-whereto-check").Click();

        cut.WaitForAssertion(() => Assert.Equal(Text("CheckUnusable"), cut.Find(".ne-note-whereto-check-failed").TextContent.Trim()));
        Assert.Equal("Das Sprachmodell hat keine verwertbare Antwort gegeben", Text("CheckUnusable"));
        Assert.Equal(count, cut.FindAll(".ne-note-whereto-item").Count);
        Assert.Empty(cut.FindAll(".ne-note-whereto-recommended"));
        Assert.Single(cut.FindAll(".ne-note-whereto-check"));
    }

    [Fact]
    public async Task Check_Cancel_StopsAndKeepsList()
    {
        var session = await OpenReadyAsync();
        var cut = OpenList(session);
        var count = cut.FindAll(".ne-note-whereto-item").Count;
        using var gate = new SemaphoreSlim(0);
        _chat.Gate = gate;
        cut.Find(".ne-note-whereto-check").Click();
        await Eventually(() => _chat.Calls.Any(c => c.Options.Json));

        cut.Find(".ne-note-whereto-check-cancel").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-note-whereto-checking")));
        await Eventually(() => _chat.Calls.Last(c => c.Options.Json).Cancelled, "The request was not cancelled.");
        Assert.Equal(count, cut.FindAll(".ne-note-whereto-item").Count);
        Assert.Single(cut.FindAll(".ne-note-whereto-check"));
        Assert.Empty(cut.FindAll(".ne-note-whereto-check-failed"));
        Assert.Empty(cut.FindAll(".ne-note-whereto-recommended"));
    }

    [Fact]
    public async Task Check_WaitsBehindBackgroundRequest_ShowsWaiting()
    {
        using var gate = new SemaphoreSlim(0);
        var session = await OpenReadyAsync(gate);
        await Eventually(() => _chat.Calls.Count == 1, "No background request started.");
        var cut = OpenList(session);

        cut.Find(".ne-note-whereto-check").Click();

        cut.WaitForAssertion(() => Assert.Equal(Text("CheckWaiting"), cut.Find(".ne-note-whereto-checking-text").TextContent.Trim()));
        Assert.Equal("Wartet auf das Sprachmodell …", Text("CheckWaiting"));

        // The background request ends: the check's own request runs next.
        gate.Release();
        await Eventually(() => _chat.Calls.Count == 2);
        Assert.True(_chat.Calls[1].Options.Json);
        cut.WaitForAssertion(() => Assert.Equal(Text("CheckRunning", Duration(0), Duration(90)), cut.Find(".ne-note-whereto-checking-text").TextContent.Trim()));
        gate.Release();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-note-whereto-recommended")));
        gate.Release(10);
    }

    [Fact]
    public async Task Check_RecommendationClick_JumpsToPlace()
    {
        var session = await OpenReadyAsync();
        var cut = OpenList(session);
        cut.Find(".ne-note-whereto-check").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-note-whereto-recommended")));
        var target = TextBlock(FirstOfferedPlace());

        cut.Find(".ne-note-whereto-recommended").Click();

        cut.WaitForAssertion(() => Assert.Equal(target.Key, State.PendingReveal));
        Assert.Single(cut.FindAll(".ne-note-whereto-recommended"));
    }
}
