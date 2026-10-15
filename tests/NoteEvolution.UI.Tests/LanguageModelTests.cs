using Microsoft.Extensions.Logging.Abstractions;
using NoteEvolution.AI.Embeddings;
using NoteEvolution.AI.Language;
using NoteEvolution.AI.Tests;
using NoteEvolution.AI.Understanding;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Platform;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>The language model runtime and the vault session's background work with it (spec 4, 7, 8).</summary>
public class LanguageModelTests : UiTestContext
{
    private const string BookPath = "pages/Buch - Alpha.md";
    private const string OlderJournal = "journals/2026_03_01.md";
    private const string NewerJournal = "journals/2026_03_02.md";
    private const string Model = "qwen2.5:7b";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private const string OlderNote =
        "Wer zu wenig schläft, verliert zuerst die Geduld und dann die Fähigkeit, Wichtiges von Unwichtigem zu trennen; das merkt man selbst erst spät.";

    private const string NewerNote =
        "Konzentration entsteht nicht durch Willenskraft allein, sondern durch eine Umgebung, die Unterbrechungen fernhält und Pausen zulässt, jeden Tag neu.";

    private const string DreamText = "Träume ordnen das Erlebte.";

    private readonly List<TestVault> _vaults = [];
    private readonly FakeChatModel _chat = new();

    protected override void Dispose(bool disposing)
    {
        // The session (and its stores in the vault) is closed first, then the vault folders are deleted.
        base.Dispose(disposing);
        if (disposing)
        {
            _vaults.ForEach(v => v.Dispose());
        }
    }

    private TestVault Vault()
    {
        var vault = TestVault.Create(
            (BookPath, $"title:: Alpha\ntype:: book\n\n- # Schlaf\n\t- Über den Schlaf und seine Phasen.\n\t- ## Träume\n\t\t- {DreamText}\n- # Arbeit\n\t- Konzentration braucht Ruhe.\n"),
            ("pages/Buch - Leer.md", "title:: Leer\ntype:: book\n"),
            (OlderJournal, $"- {OlderNote}\n- kurz\n"),
            (NewerJournal, $"- {NewerNote}\n"));
        _vaults.Add(vault);
        return vault;
    }

    private static async Task Eventually(Func<bool> condition, string because = "The condition was not met in time.")
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, because);
            await Task.Delay(10);
        }
    }

    /// <summary>A ready language model and an open vault whose 3 headings of the current book and 2 long notes are all done.</summary>
    private async Task<VaultSession> OpenReadyAsync(TestVault vault)
    {
        var language = UseLanguageModel(_chat);
        await language.RefreshAsync(CancellationToken.None);
        Assert.Equal(LanguageModelState.Ready, language.State);
        var session = await OpenWithAiAsync(vault);
        await AllDone(session, 5);
        return session;
    }

    private static Task AllDone(VaultSession session, int total) =>
        Eventually(() => session.Understanding is { } p && p.Total == total && p.Done == total, "The background work did not finish.");

    private Book CurrentBook(VaultSession session) => session.Vault.FindBook(State.CurrentBook!.LinkName)!;

    private static string NoteText(VaultSession session, string content) =>
        EmbeddingText.ForNote(session.Notes.All().Single(n => n.Block.Content == content));

    private static Guid NoteKey(VaultSession session, string content) =>
        session.Notes.All().Single(n => n.Block.Content == content).Key;

    private IReadOnlyList<AI.Relevance.TopicHit> Topics(VaultSession session) =>
        session.Relevance.Topics(NoteKey(session, NewerNote), NoteText(session, NewerNote), CurrentBook(session));

    private void Edit(VaultSession session, string path, string from, string to)
    {
        var page = session.Vault.FindPageByPath(Path.Combine(_vaults[^1].Root, path))!;
        page.AllBlocks().Single(b => b.Content == from).SetContent(to);
        Assert.True(session.TrySave(page, out var error), error?.Message);
    }

    // ---- Runtime ----

    [Fact]
    public void Runtime_ExpectedCheckDuration_MeanOfLastFive_90sBeforeFirst()
    {
        var runtime = new LanguageModelRuntime(Settings, _ => _chat, Time, NullLogger<LanguageModelRuntime>.Instance);
        Assert.Equal(TimeSpan.FromSeconds(90), runtime.ExpectedCheckDuration);

        runtime.RecordCheck(TimeSpan.FromSeconds(100)); // pushed out by the five below
        foreach (var seconds in new[] { 10, 20, 30, 40, 50 })
        {
            runtime.RecordCheck(TimeSpan.FromSeconds(seconds));
        }

        Assert.Equal(TimeSpan.FromSeconds(30), runtime.ExpectedCheckDuration);
    }

    [Fact]
    public async Task Runtime_States_OffUnreachableModelMissingReady()
    {
        var runtime = new LanguageModelRuntime(Settings, _ => _chat, Time, NullLogger<LanguageModelRuntime>.Instance);
        var states = new List<LanguageModelState>();
        runtime.Changed += () => states.Add(runtime.State);

        // Switched off: the version is read (for the first-setup offer), nothing else.
        await runtime.RefreshAsync(CancellationToken.None);
        Assert.Equal(LanguageModelState.Off, runtime.State);
        Assert.Equal("0.5.0", runtime.Version);
        Assert.Null(runtime.Chat);

        Settings.LanguageModelEnabled = true;
        _chat.Version = null;
        await runtime.RefreshAsync(CancellationToken.None);
        Assert.Equal(LanguageModelState.Unreachable, runtime.State);
        Assert.Null(runtime.Version);
        Assert.Null(runtime.Chat);

        // Asked again after 60 s: the server is there, the model is not.
        _chat.Version = "0.5.0";
        _chat.Models = [new ChatModelInfo("llama3:8b", 4_000_000_000)];
        Time.Advance(LanguageModelRuntime.RetryInterval - TimeSpan.FromSeconds(1));
        await Task.Delay(50);
        Assert.Equal(LanguageModelState.Unreachable, runtime.State);
        Time.Advance(TimeSpan.FromSeconds(1));
        await Eventually(() => runtime.State == LanguageModelState.ModelMissing);
        Assert.Null(runtime.Chat);

        // Pulling, then ready (the server has the model afterwards, with its implicit tag).
        states.Clear();
        _chat.Models = [new ChatModelInfo(Model + ":latest", 4_700_000_000)];
        await runtime.PullAsync(null, CancellationToken.None);
        Assert.Equal(LanguageModelState.Pulling, states[0]);
        Assert.Equal(LanguageModelState.Ready, runtime.State);
        Assert.Same(_chat, runtime.Chat);

        Settings.LanguageModelEnabled = false;
        await runtime.RefreshAsync(CancellationToken.None);
        Assert.Equal(LanguageModelState.Off, runtime.State);
        Assert.Null(runtime.Chat);
    }

    [Theory]
    [InlineData("http://localhost:11434", true)]
    [InlineData("http://LOCALHOST:11434", true)]
    [InlineData("http://127.0.0.1:11434", true)]
    [InlineData("http://[::1]:11434", true)]
    [InlineData("http://127.0.0.2:11434", false)]
    [InlineData("http://192.168.1.20:11434", false)]
    [InlineData("http://localhost.example.com:11434", false)]
    [InlineData("http://127.0.0.1.nip.io:11434", false)]
    [InlineData("https://ollama.example.com", false)]
    public void Runtime_IsLocal_OnlyLoopback(string address, bool local) =>
        Assert.Equal(local, LanguageModelRuntime.IsLocal(new Uri(address)));

    // ---- Session ----

    [Fact]
    public async Task Session_WithReadyModel_StartsWorker_FeedsBookAndNotes()
    {
        var tv = Vault();

        var session = await OpenReadyAsync(tv);

        var calls = _chat.Calls;
        Assert.All(calls, c => Assert.Equal(Model, c.Model));
        Assert.Equal(1, _chat.MaxRunning);

        // The current book's headings (children first), then the long notes newest first; the short note gets nothing.
        var headings = calls.Take(3).Select(c => c.User).ToList();
        Assert.Contains(DreamText, headings[0], StringComparison.Ordinal);
        Assert.Contains(headings, h => h.Contains("Konzentration braucht Ruhe", StringComparison.Ordinal));
        Assert.Contains(NewerNote, calls[3].User, StringComparison.Ordinal);
        Assert.Contains(OlderNote, calls[4].User, StringComparison.Ordinal);
        Assert.Equal(5, calls.Count);

        // "Wohin damit?" uses the summaries now.
        Assert.Contains(Topics(session), t => t.Summary is not null);
    }

    [Fact]
    public async Task Session_PageChanged_RequeuesItsItems()
    {
        var tv = Vault();
        var session = await OpenReadyAsync(tv);
        var before = _chat.Calls.Count;

        Edit(session, BookPath, DreamText, "Träume ordnen das Erlebte neu.");
        Edit(session, OlderJournal, OlderNote, OlderNote + " Auch das.");
        Time.Advance(VaultSession.PageIndexDelay - TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);
        Assert.Equal(before, _chat.Calls.Count);
        Time.Advance(TimeSpan.FromMilliseconds(1));

        // The changed heading and its parent, and the changed note; nothing else.
        await Eventually(() => _chat.Calls.Count == before + 3 && session.Understanding is { Done: 5, Total: 5 });
        var added = _chat.Calls.Skip(before).Select(c => c.User).ToList();
        Assert.Contains("Träume ordnen das Erlebte neu.", added[0], StringComparison.Ordinal);
        Assert.Contains(added, u => u.Contains("Schlaf", StringComparison.Ordinal) && !u.Contains("Erlebte neu", StringComparison.Ordinal));
        Assert.Contains(added, u => u.Contains("Auch das.", StringComparison.Ordinal));
        await Task.Delay(50);
        Assert.Equal(before + 3, _chat.Calls.Count);
    }

    [Fact]
    public async Task Session_RapidFocusChanges_FedOnceAfterTheyStop()
    {
        var tv = Vault();
        var session = await OpenReadyAsync(tv);
        var chapters = CurrentBook(session).Root.Children.Select(c => c.Key).ToList();
        await Task.Delay(50);
        var feeds = session.FeedsApplied;

        // The cursor wanders through the chapters: nothing is computed while it moves.
        State.CurrentSectionKey = chapters[0];
        Time.Advance(VaultSession.FocusFeedDelay / 2);
        State.CurrentSectionKey = chapters[1];
        Time.Advance(VaultSession.FocusFeedDelay / 2);
        State.CurrentSectionKey = chapters[0];
        Time.Advance(VaultSession.FocusFeedDelay - TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);
        Assert.Equal(feeds, session.FeedsApplied);

        // It rests: one feed.
        Time.Advance(TimeSpan.FromMilliseconds(1));
        await Eventually(() => session.FeedsApplied == feeds + 1, "The focus was not fed.");
        await Task.Delay(50);
        Assert.Equal(feeds + 1, session.FeedsApplied);
        Assert.Equal(5, _chat.Calls.Count); // nothing changed, nothing asked

        // Another book is fed at once (its headings make the count).
        State.CurrentBook = session.Vault.Books.Single(b => b.LinkName != State.CurrentBook!.LinkName);
        await Eventually(() => session.FeedsApplied == feeds + 2, "The other book was not fed.");
        await Eventually(() => session.Understanding is { Done: 2, Total: 2 }, "The other book's (empty) work was not planned.");
    }

    [Fact]
    public async Task Session_Typing_ForwardedToWorker()
    {
        var tv = Vault();
        var language = UseLanguageModel(_chat);
        await language.RefreshAsync(CancellationToken.None);
        using var gate = new SemaphoreSlim(0);
        _chat.Gate = gate;
        var session = await OpenWithAiAsync(tv);
        await Eventually(() => _chat.Calls.Count == 1);

        // Typing while a request runs: it is not cancelled, but the next one waits for 10 s without input.
        session.NotifyTyping();
        gate.Release(100);
        await Eventually(() => session.Understanding?.Pause == UnderstandingPause.Typing);
        await Task.Delay(50);
        Assert.Single(_chat.Calls);
        Assert.False(_chat.Calls[0].Cancelled);

        Time.Advance(UnderstandingWorker.TypingQuiet);
        await AllDone(session, 5);
    }

    [Fact]
    public async Task Worker_OnBattery_FromMonitor()
    {
        var tv = Vault();
        SystemMonitor.Snapshot = new SystemSnapshot(10, 4, 16, 100, null, null, null, null, OnBattery: true);
        var language = UseLanguageModel(_chat);
        await language.RefreshAsync(CancellationToken.None);
        var session = await OpenWithAiAsync(tv);

        // On battery the work waits and says why; nothing is asked.
        await Eventually(() => session.Understanding?.Pause == UnderstandingPause.Battery);
        await Task.Delay(50);
        Assert.Empty(_chat.Calls);

        // On the power supply again: the worker looks again after its battery interval and the session reads anew.
        SystemMonitor.Snapshot = SystemMonitor.Snapshot with { OnBattery = false };
        Time.Advance(UnderstandingWorker.BatteryCheck);
        await AllDone(session, 5);

        // The session keeps the battery state for a while instead of reading it before every request.
        Assert.Equal(2, SystemMonitor.Reads);
    }

    [Fact]
    public async Task Session_OllamaGoesAway_FallsBackAndRetriesAfter60s()
    {
        var tv = Vault();
        var session = await OpenReadyAsync(tv);
        Assert.Contains(Topics(session), t => t.Summary is not null);

        // Ollama stops; a changed note needs a request.
        _chat.Version = null;
        _chat.Failure = (_, _) => new ChatModelUnavailableException("connection refused");
        Edit(session, OlderJournal, OlderNote, OlderNote + " Später.");
        Time.Advance(VaultSession.PageIndexDelay);
        await Eventually(() => session.Understanding?.Pause == UnderstandingPause.Unreachable);

        // Everything works as without the language model; the header can say why (the pause).
        Assert.All(Topics(session), t => Assert.Null(t.Summary));
        Assert.NotEmpty(session.Relevance.Placements(NoteKey(session, NewerNote), NoteText(session, NewerNote), CurrentBook(session)));

        // Asked again only after 60 s.
        var checks = _chat.VersionCalls;
        Time.Advance(UnderstandingWorker.RetryInterval - TimeSpan.FromSeconds(1));
        await Task.Delay(50);
        Assert.Equal(checks, _chat.VersionCalls);

        _chat.Version = "0.5.0";
        _chat.Failure = (_, _) => null;
        Time.Advance(TimeSpan.FromSeconds(1));
        await Eventually(() => session.Understanding is { Pause: UnderstandingPause.None, Done: 5, Total: 5 });
        Assert.True(_chat.VersionCalls > checks);
        Assert.Contains(Topics(session), t => t.Summary is not null);
    }

    [Fact]
    public async Task Session_Dispose_StopsWorker()
    {
        var tv = Vault();
        var language = UseLanguageModel(_chat);
        await language.RefreshAsync(CancellationToken.None);
        using var gate = new SemaphoreSlim(0);
        _chat.Gate = gate;
        var session = await OpenWithAiAsync(tv);
        await Eventually(() => _chat.Calls.Count == 1);

        session.Dispose();

        await Eventually(() => _chat.Calls[0].Cancelled, "The running request was not cancelled.");
        Assert.Null(session.Understanding);
        gate.Release(100);
        await Task.Delay(100);
        Assert.Single(_chat.Calls);
        Assert.Equal(
            (null, PlacementCheckFailure.Unavailable),
            await session.CheckPlacementAsync(Guid.NewGuid(), "passage: Notiz", CurrentBook(session), CancellationToken.None));

        // Nothing of the cancelled request was stored.
        using var store = UnderstandingStore.Open(tv.Root);
        var vault = tv.Open();
        var items = vault.Books.SelectMany(b => UnderstandingItems.ForBook(b, Model)).Select(i => i.Fingerprint);
        Assert.All(items, fingerprint => Assert.Null(store.Get(fingerprint)));
    }

    [Fact]
    public async Task Session_NoFileWritten()
    {
        var tv = Vault();
        static Dictionary<string, byte[]> Files(string root) => Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(root, f).StartsWith(".noteevolution", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => f, File.ReadAllBytes);
        var before = Files(tv.Root);

        var session = await OpenReadyAsync(tv);
        Assert.Equal(PlacementCheckFailure.Unusable,
            (await session.CheckPlacementAsync(NoteKey(session, NewerNote), NoteText(session, NewerNote), CurrentBook(session), CancellationToken.None)).Failure);
        session.Dispose();

        Assert.True(File.Exists(Path.Combine(tv.Root, ".noteevolution", "understanding.db")));
        var after = Files(tv.Root);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        Assert.All(before, file => Assert.Equal(file.Value, after[file.Key]));
    }

    // ---- "Genauer prüfen" ----

    [Fact]
    public async Task Session_CheckPlacement_NextRequest_GivesVerdict()
    {
        var tv = Vault();
        var session = await OpenReadyAsync(tv);
        var before = _chat.Calls.Count;
        _chat.Answer = (_, _) => "{\"begruendung\": \"Es geht um Konzentration.\", \"stelle\": \"1.1\"}";

        var (verdict, failure) = await session.CheckPlacementAsync(
            NoteKey(session, NewerNote), NoteText(session, NewerNote), CurrentBook(session), CancellationToken.None);

        Assert.Null(failure);
        Assert.NotNull(verdict);
        Assert.Equal("Es geht um Konzentration.", verdict.Reason);
        Assert.NotNull(verdict.AfterTextBlockKey);
        var call = Assert.Single(_chat.Calls.Skip(before));
        Assert.Equal(UnderstandingPrompts.CheckTokens, call.Options.MaxTokens);
        Assert.True(call.Options.Json);
        Assert.Contains("\"stelle\"", call.Options.Schema, StringComparison.Ordinal);
        Assert.Contains(NewerNote, call.User, StringComparison.Ordinal);
        Assert.DoesNotContain(EmbeddingText.PassagePrefix, call.User, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_CheckPlacement_NoTopics_UnusableWithoutRequest()
    {
        var tv = Vault();
        var session = await OpenReadyAsync(tv);
        var before = _chat.Calls.Count;

        var (verdict, failure) = await session.CheckPlacementAsync(
            NoteKey(session, NewerNote), NoteText(session, NewerNote), session.Vault.FindBook("Buch - Leer")!, CancellationToken.None);

        Assert.Null(verdict);
        Assert.Equal(PlacementCheckFailure.Unusable, failure);
        Assert.Equal(before, _chat.Calls.Count);
    }

    [Fact]
    public async Task Session_CheckPlacement_SearchFails_NoAnswerWithoutRequest()
    {
        var tv = Vault();
        var session = await OpenReadyAsync(tv);
        var before = _chat.Calls.Count;

        // A missing book stands in for any unexpected failure while topics and places are searched.
        var (verdict, failure) = await session.CheckPlacementAsync(
            NoteKey(session, NewerNote), NoteText(session, NewerNote), null!, CancellationToken.None);

        Assert.Null(verdict);
        Assert.Equal(PlacementCheckFailure.NoAnswer, failure);
        Assert.Equal(before, _chat.Calls.Count);
    }

    [Fact]
    public async Task Session_CheckPlacement_CancelAndTimeout()
    {
        var tv = Vault();
        var session = await OpenReadyAsync(tv);
        using var gate = new SemaphoreSlim(0);
        _chat.Gate = gate;
        var key = NoteKey(session, NewerNote);
        var text = NoteText(session, NewerNote);

        using var cts = new CancellationTokenSource();
        var cancelled = session.CheckPlacementAsync(key, text, CurrentBook(session), cts.Token);
        await Eventually(() => _chat.Calls.Count == 6);
        cts.Cancel();
        Assert.Equal((null, PlacementCheckFailure.Cancelled), await cancelled);

        var timedOut = session.CheckPlacementAsync(key, text, CurrentBook(session), CancellationToken.None);
        await Eventually(() => _chat.Calls.Count == 7);
        Time.Advance(UnderstandingWorker.RequestTimeout);
        Assert.Equal((null, PlacementCheckFailure.NoAnswer), await timedOut);
    }
}
