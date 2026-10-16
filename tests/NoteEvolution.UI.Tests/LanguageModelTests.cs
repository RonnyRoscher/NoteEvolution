using Microsoft.Extensions.Logging;
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
        // Nothing changed, nothing asked again; the summaries are settled, so the feed brought the placement checks.
        Assert.Equal(5, _chat.Calls.Count(c => c.Options.Schema is null));

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
        AnswerPlacements();
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
        // The page-change feed found the summaries settled: 3 placement checks came with it.
        await Eventually(() => session.Understanding is { Pause: UnderstandingPause.None, Done: 8, Total: 8 });
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

    // ---- Precomputed placements ----

    private const string ShortNote = "kurz";

    /// <summary>Placement checks (they have a schema) get a verdict for the first place; everything else the default answer.</summary>
    private void AnswerPlacements() =>
        _chat.Answer = (call, n) => call.Options.Schema is not null
            ? "{\"begruendung\":\"Passt zum Abschnitt.\",\"stelle\":\"1.1\",\"passung\":\"gut\"}"
            : $"Antwort {n}.";

    /// <summary>The placement checks of the note (its text, after the journal date, ends with the content).</summary>
    private IReadOnlyList<ChatCall> PlacementCalls(string noteContent) =>
        [.. _chat.Calls.Where(c => c.Options.Schema is not null
            && c.User[..c.User.IndexOf("\n\nAbschnitte:", StringComparison.Ordinal)].EndsWith("\n" + noteContent, StringComparison.Ordinal))];

    /// <summary>The 5 items of <see cref="OpenReadyAsync"/>, then (after the debounce) one placement check per unused note.</summary>
    private async Task<VaultSession> OpenWithPlacementsAsync(TestVault vault, int total = 8)
    {
        AnswerPlacements();
        var session = await OpenReadyAsync(vault);
        Time.Advance(VaultSession.PageIndexDelay);
        await AllDone(session, total);
        return session;
    }

    [Fact]
    public async Task Session_Placements_AfterAllSummaries()
    {
        var tv = Vault();
        AnswerPlacements();
        var language = UseLanguageModel(_chat);
        await language.RefreshAsync(CancellationToken.None);
        using var gate = new SemaphoreSlim(0);
        _chat.Gate = gate;
        var session = await OpenWithAiAsync(tv);
        await Eventually(() => _chat.Calls.Count == 1);

        // A feed while a summary is still missing builds no placement checks.
        gate.Release();
        await Eventually(() => _chat.Calls.Count == 2);
        var feeds = session.FeedsApplied;
        Time.Advance(VaultSession.PageIndexDelay);
        await Eventually(() => session.FeedsApplied > feeds, "The page-change feed did not run.");
        gate.Release(100);
        await AllDone(session, 5);
        await Task.Delay(50);
        Assert.DoesNotContain(_chat.Calls, c => c.Options.Schema is not null);
        Assert.Null(session.Placement(NoteKey(session, NewerNote)));

        // The last summary scheduled the feed that builds them: one check per unused note, newest first, short ones too.
        var placementsChanged = 0;
        session.PlacementsChanged += () => placementsChanged++;
        Time.Advance(VaultSession.PageIndexDelay);
        await AllDone(session, 8);
        foreach (var content in new[] { NewerNote, OlderNote, ShortNote })
        {
            Assert.Single(PlacementCalls(content));
            Assert.False(Assert.IsType<NotePlacement>(session.Placement(NoteKey(session, content))).Stale);
        }

        Assert.True(_chat.Calls.Skip(5).First().User.Contains(NewerNote, StringComparison.Ordinal));
        Assert.True(placementsChanged > 0);
        await Task.Delay(50);
        Assert.Equal(8, _chat.Calls.Count);
    }

    [Fact]
    public async Task Session_Placements_FailedSubHeadingDoesNotBlock()
    {
        var tv = Vault();
        _chat.Failure = (call, _) => call.Options.Schema is null && call.User.Contains(DreamText, StringComparison.Ordinal)
            ? new InvalidOperationException("Kaputt.")
            : null;

        // "Träume" fails three times, so "Schlaf" is skipped as well; the book's summaries are settled all the same.
        var session = await OpenWithPlacementsAsync(tv);

        Assert.Equal(3, _chat.Calls.Count(c => c.User.Contains(DreamText, StringComparison.Ordinal) && c.Options.Schema is null));
        Assert.Single(PlacementCalls(NewerNote));
        Assert.NotNull(session.Placement(NoteKey(session, NewerNote)));
    }

    [Fact]
    public async Task Session_Placements_HeadingGivenUpWithoutAnyStored_SchedulesTheFeed()
    {
        // The book's only heading fails: no summary is ever stored, so only the given-up heading can schedule the feed.
        var tv = TestVault.Create(
            (BookPath, $"title:: Alpha\ntype:: book\n\n- # Träume\n\t- {DreamText}\n"),
            (NewerJournal, $"- {NewerNote}\n"));
        _vaults.Add(tv);
        AnswerPlacements();
        _chat.Failure = (call, _) => call.Options.Schema is null && call.User.Contains(DreamText, StringComparison.Ordinal)
            ? new InvalidOperationException("Kaputt.")
            : null;
        var language = UseLanguageModel(_chat);
        await language.RefreshAsync(CancellationToken.None);
        var session = await OpenWithAiAsync(tv);
        await AllDone(session, 2);
        Assert.Empty(PlacementCalls(NewerNote));

        Time.Advance(VaultSession.PageIndexDelay);
        await AllDone(session, 3);
        Assert.Single(PlacementCalls(NewerNote));
    }

    [Fact]
    public async Task Session_Placements_OnlyUnusedNotes()
    {
        var tv = TestVault.Create(
            (BookPath, $"title:: Alpha\ntype:: book\n\n- # Schlaf\n\t- Über den Schlaf und seine Phasen.\n\t- ## Träume\n\t\t- {DreamText}\n- # Arbeit\n\t- Konzentration braucht Ruhe.\n"),
            (OlderJournal, $"- {OlderNote}\n- kurz\n  used-in:: [[Buch - Alpha]]\n"),
            (NewerJournal, $"- {NewerNote}\n"));
        _vaults.Add(tv);

        var session = await OpenWithPlacementsAsync(tv, total: 7);
        await Task.Delay(50);

        // The used note gets no check (it would have been one of 3) and no placement.
        Assert.Equal(7, _chat.Calls.Count);
        Assert.Empty(PlacementCalls(ShortNote));
        Assert.Null(session.Placement(NoteKey(session, ShortNote)));
        Assert.NotNull(session.Placement(NoteKey(session, OlderNote)));
    }

    [Fact]
    public async Task Session_ManualCheck_StoredAndNotRecomputed()
    {
        var tv = Vault();
        AnswerPlacements();
        var session = await OpenReadyAsync(tv);
        using var gate = new SemaphoreSlim(0);
        _chat.Gate = gate;

        // The background asks for the newest note first; meanwhile the older one is checked by hand.
        Time.Advance(VaultSession.PageIndexDelay);
        await Eventually(() => _chat.Calls.Count == 6);
        var waiting = false;
        var check = session.CheckPlacementAsync(
            NoteKey(session, OlderNote), NoteText(session, OlderNote), CurrentBook(session), CancellationToken.None,
            waiting: () => waiting = true);
        await Eventually(() => waiting, "The check did not wait behind the running request.");
        await Task.Delay(50); // queued right after waiting is reported
        gate.Release();
        await Eventually(() => _chat.Calls.Count == 7);
        Assert.Single(PlacementCalls(OlderNote));
        gate.Release();
        var (verdict, failure) = await check;
        Assert.Null(failure);
        Assert.Equal("Passt zum Abschnitt.", verdict!.Reason);

        // Stored under the background's fingerprint: the background does not ask again.
        gate.Release(100);
        await AllDone(session, 8);
        await Task.Delay(50);
        Assert.Single(PlacementCalls(OlderNote));
        Assert.Equal(8, _chat.Calls.Count);
        Assert.Equal(verdict, session.Placement(NoteKey(session, OlderNote))?.Verdict);
        Assert.False(session.Placement(NoteKey(session, OlderNote))!.Stale);
    }

    [Fact]
    public async Task Session_NoteEdited_PlacementStaleThenCurrent()
    {
        var tv = Vault();
        var session = await OpenWithPlacementsAsync(tv);
        using var gate = new SemaphoreSlim(0);
        _chat.Gate = gate;

        // The edit keeps the note's first 80 characters, so its identity: the old placement shows as stale.
        const string edited = OlderNote + " Auch das.";
        Edit(session, OlderJournal, OlderNote, edited);
        Time.Advance(VaultSession.PageIndexDelay);
        var key = NoteKey(session, edited);
        await Eventually(() => session.Placement(key) is { Stale: true }, "The old placement was not shown as stale.");

        // The core statement, then the new check.
        gate.Release(100);
        await Eventually(() => session.Placement(key) is { Stale: false }, "The new placement did not arrive.");
        Assert.Single(PlacementCalls(edited));
    }

    [Fact]
    public async Task Session_PromptBuildThrows_LoggedOtherWorkContinues()
    {
        var tv = Vault();
        AnswerPlacements();
        var language = UseLanguageModel(_chat);
        await language.RefreshAsync(CancellationToken.None);
        var logger = new ListLogger();
        var session = await OpenWithAiAsync(tv, logger: logger);
        var newer = NoteKey(session, NewerNote);
        session.PlacementPromptBuilding = (_, key) =>
        {
            if (key == newer)
            {
                throw new InvalidOperationException("Topics failed.");
            }
        };
        await AllDone(session, 5);

        // The other notes are checked; the note whose prompt failed is logged and left out.
        Time.Advance(VaultSession.PageIndexDelay);
        await AllDone(session, 7);
        Assert.Empty(PlacementCalls(NewerNote));
        Assert.Single(PlacementCalls(OlderNote));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Exception?.Message == "Topics failed.");

        // Headings and core statements are still with the worker: a changed note is asked for again.
        Edit(session, NewerJournal, NewerNote, NewerNote + " Neu.");
        Time.Advance(VaultSession.PageIndexDelay);
        await Eventually(() => _chat.Calls.Any(c => c.Options.Schema is null && c.User.Contains(NewerNote + " Neu.", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Session_BookSwitchedWhileBuilding_OldResultDropped()
    {
        var tv = TestVault.Create(
            (BookPath, $"title:: Alpha\ntype:: book\n\n- # Schlaf\n\t- Über den Schlaf und seine Phasen.\n\t- ## Träume\n\t\t- {DreamText}\n- # Arbeit\n\t- Konzentration braucht Ruhe.\n"),
            ("pages/Buch - Beta.md", "title:: Beta\ntype:: book\n\n- # Erholung\n\t- Pausen geben Kraft für den Tag.\n"),
            (OlderJournal, $"- {OlderNote}\n- kurz\n"),
            (NewerJournal, $"- {NewerNote}\n"));
        _vaults.Add(tv);
        AnswerPlacements();
        var session = await OpenReadyAsync(tv);
        var alpha = State.CurrentBook!.LinkName;
        Assert.Equal("Buch - Alpha", alpha);
        await Eventually(() => session.Understanding?.Kinds?[UnderstandingKind.Heading] is { Done: 4, Total: 4 },
            "The other book's heading was not summarized.");

        // Alpha's checks are being built (held at the first note) when the book changes to Beta.
        using var building = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        session.PlacementPromptBuilding = (book, _) =>
        {
            if (book.LinkName == alpha)
            {
                building.Set();
                release.Wait(Wait);
            }
        };
        Time.Advance(VaultSession.PageIndexDelay);
        Assert.True(building.Wait(Wait), "Alpha's checks were not built.");
        State.CurrentBook = session.Vault.FindBook("Buch - Beta");
        await AllDone(session, 1 + 2 + 3);
        release.Set();
        await Task.Delay(100);

        // Only Beta's checks reached the worker.
        var checks = _chat.Calls.Where(c => c.Options.Schema is not null).ToList();
        Assert.Equal(3, checks.Count);
        Assert.All(checks, c => Assert.Contains("Erholung", c.User, StringComparison.Ordinal));
        Assert.All(checks, c => Assert.DoesNotContain("Schlaf", c.User, StringComparison.Ordinal));
        Assert.Equal(6, session.Understanding!.Total);
    }

    [Fact]
    public async Task Session_BookSwitched_OtherBooksPlacementSurvivesPrune()
    {
        var tv = Vault();
        var session = await OpenWithPlacementsAsync(tv);
        var key = NoteKey(session, NewerNote);
        var alpha = State.CurrentBook!;

        // The other book's work completes (and is pruned) without Alpha's placement checks.
        State.CurrentBook = session.Vault.FindBook("Buch - Leer");
        await Eventually(() => session.Understanding is { Done: 2, Total: 2 }, "The other book's work was not planned.");
        await Task.Delay(200);

        // Back to Alpha: its results are still there, nothing is asked again.
        State.CurrentBook = alpha;
        await Eventually(() => session.Placement(key) is { Stale: false }, "Alpha's placement was lost.");
        await AllDone(session, 8);
        await Task.Delay(50);
        Assert.Equal(8, _chat.Calls.Count);
    }

    [Fact]
    public async Task Session_HeadingEdited_StalePlacementSurvivesPrune()
    {
        var tv = Vault();
        var session = await OpenWithPlacementsAsync(tv);
        var key = NoteKey(session, NewerNote);
        using var gate = new SemaphoreSlim(0);
        _chat.Gate = gate;

        // The edited heading and its parent are summarized again; meanwhile the book waits and its plan completes (pruned).
        Edit(session, BookPath, DreamText, "Träume ordnen das Erlebte neu.");
        Time.Advance(VaultSession.PageIndexDelay);
        await Eventually(() => _chat.Calls.Count == 9);
        gate.Release();
        await Eventually(() => _chat.Calls.Count == 10);
        gate.Release();
        await Eventually(() => session.Understanding is { Done: 5, Total: 5 }, "The summaries were not done.");
        await Task.Delay(200);
        Assert.NotNull(session.Placement(key));

        // The new checks: the old result shows as stale until the new one is there.
        Time.Advance(VaultSession.PageIndexDelay);
        await Eventually(() => session.Placement(key) is { Stale: true }, "The old placement was not shown as stale.");
        gate.Release(100);
        await Eventually(() => session.Placement(key) is { Stale: false }, "The new placement did not arrive.");
    }

    [Fact]
    public async Task Session_SummarySettledWhileReadinessIsRead_FeedStillScheduled()
    {
        var tv = TestVault.Create(
            (BookPath, $"title:: Alpha\ntype:: book\n\n- # Schlaf\n\t- Über den Schlaf und seine Phasen.\n\t- ## Träume\n\t\t- {DreamText}\n- # Arbeit\n\t- Konzentration braucht Ruhe.\n"),
            ("pages/Buch - Beta.md", "title:: Beta\ntype:: book\n\n- # Erholung\n\t- Pausen geben Kraft für den Tag.\n"),
            (OlderJournal, $"- {OlderNote}\n- kurz\n"),
            (NewerJournal, $"- {NewerNote}\n"));
        _vaults.Add(tv);

        // Beta's heading (the other book's, after Alpha's work) is held at the model.
        using var betaHeld = new ManualResetEventSlim();
        _chat.Answer = (call, n) =>
        {
            if (call.Options.Schema is not null)
            {
                return "{\"begruendung\":\"Passt.\",\"stelle\":\"1.1\",\"passung\":\"gut\"}";
            }

            if (call.User.Contains("Pausen geben Kraft", StringComparison.Ordinal))
            {
                betaHeld.Wait(Wait);
            }

            return $"Antwort {n}.";
        };
        var session = await OpenReadyAsync(tv);
        var feeds = session.FeedsApplied;
        Time.Advance(VaultSession.PageIndexDelay);
        await Eventually(() => session.FeedsApplied > feeds, "Alpha's placement checks were not fed.");

        // Beta becomes current; its feed has read the store (not settled) when Beta's summary is stored.
        using var read = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        session.PlacementReadinessChecked = book =>
        {
            if (book.LinkName == "Buch - Beta")
            {
                read.Set();
                proceed.Wait(Wait);
            }
        };
        State.CurrentBook = session.Vault.FindBook("Buch - Beta");
        Assert.True(read.Wait(Wait), "Beta's readiness was not read.");
        betaHeld.Set();
        await Eventually(() => session.Understanding?.Kinds?[UnderstandingKind.Heading] is { Done: 4, Total: 4 },
            "Beta's heading was not summarized.");
        await Task.Delay(100); // its vector
        session.PlacementReadinessChecked = null;
        proceed.Set();

        // Applying the feed noticed the summary settled meanwhile: the debounced feed builds Beta's checks.
        await Eventually(() => session.FeedsApplied > feeds + 1, "Beta's feed was not applied.");
        Time.Advance(VaultSession.PageIndexDelay);
        await Eventually(() => _chat.Calls.Any(c => c.Options.Schema is not null && c.User.Contains("Erholung", StringComparison.Ordinal)),
            "Beta's placement checks were not built.");
    }

    [Fact]
    public async Task Session_SummaryWithoutVector_NotReadyUntilVectorSet()
    {
        var tv = Vault();
        AnswerPlacements();
        var language = UseLanguageModel(_chat);
        await language.RefreshAsync(CancellationToken.None);
        using var gate = new SemaphoreSlim(0);
        _chat.Gate = gate;
        var embedder = new FakeEmbedder();
        var session = await OpenWithAiAsync(tv, embedder);
        await Eventually(() => _chat.Calls.Count == 1);

        // The summaries are stored without their vectors (the embedder fails): the prompt could not use them.
        embedder.Throws = true;
        gate.Release(100);
        await AllDone(session, 5);
        await Task.Delay(50);

        // The embedder works again, but the summaries have no vectors yet: the feed builds no checks.
        embedder.Throws = false;
        var built = 0;
        session.PlacementPromptBuilding = (_, _) => Interlocked.Increment(ref built);
        var feeds = session.FeedsApplied;
        Time.Advance(VaultSession.PageIndexDelay);
        await Eventually(() => session.FeedsApplied > feeds, "The page-change feed did not run.");
        Assert.Equal(0, Volatile.Read(ref built));
        Assert.DoesNotContain(_chat.Calls, c => c.Options.Schema is not null);

        // The worker embeds them once its work changed (that feed), and the vectors schedule the next feed.
        await Task.Delay(100);
        Time.Advance(VaultSession.PageIndexDelay);
        await AllDone(session, 8);
        Assert.Single(PlacementCalls(NewerNote));
    }

    // ---- Coverage (spec 6.4) ----

    [Fact]
    public async Task Coverage_ComputedOnlyWhileWatched()
    {
        var tv = Vault();
        var session = await OpenWithPlacementsAsync(tv);
        var changed = 0;
        session.CoverageChanged += () => changed++;

        // Nobody watches: nothing is computed, however much time passes.
        Time.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.Equal(0, session.CoverageComputations);
        Assert.Null(session.Coverage);

        // Watching computes it a delay later: all three unused notes are placed and checked.
        var owner = new object();
        session.WatchCoverage(owner, visible: true);
        Assert.Equal(0, session.CoverageComputations);
        Time.Advance(VaultSession.CoverageDelay);
        await Eventually(() => session.Coverage is not null, "The coverage was not computed.");
        var coverage = session.Coverage!;
        Assert.Equal((3, 3), (coverage.Checked, coverage.Unused));
        Assert.Equal(3, coverage.Sections[Guid.Empty].TotalAssigned);
        await Eventually(() => !session.CoverageRunning, "The computation did not end.");
        Assert.Equal(1, changed);

        // No longer watched: a change computes nothing, until someone watches again.
        session.WatchCoverage(owner, visible: false);
        Edit(session, NewerJournal, NewerNote, NewerNote + " Neu.");
        Time.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.Equal(1, session.CoverageComputations);
        session.WatchCoverage(owner, visible: true);
        Time.Advance(VaultSession.CoverageDelay);
        await Eventually(() => session.CoverageComputations >= 2 && !session.CoverageRunning, "The coverage was not computed again.");
        Assert.NotSame(coverage, session.Coverage);
    }

    [Fact]
    public async Task Coverage_ComputeThrows_KeepsLastAndLogs()
    {
        var tv = Vault();
        AnswerPlacements();
        var language = UseLanguageModel(_chat);
        await language.RefreshAsync(CancellationToken.None);
        var logger = new ListLogger();
        var session = await OpenWithAiAsync(tv, logger: logger);
        await AllDone(session, 5);
        Time.Advance(VaultSession.PageIndexDelay);
        await AllDone(session, 8);
        session.WatchCoverage(this, visible: true);
        Time.Advance(VaultSession.CoverageDelay);
        await Eventually(() => session.Coverage is not null && !session.CoverageRunning, "The coverage was not computed.");
        var last = session.Coverage!;

        // The computation fails: logged, and the last coverage stays.
        session.CoverageComputing = () => throw new InvalidOperationException("Coverage failed.");
        Edit(session, NewerJournal, NewerNote, NewerNote + " Neu.");
        Time.Advance(VaultSession.CoverageDelay);
        await Eventually(() => logger.Entries.Any(e => e.Level == LogLevel.Error && e.Exception?.Message == "Coverage failed."),
            "The failure was not logged.");
        await Eventually(() => !session.CoverageRunning, "The computation did not end.");
        Assert.Same(last, session.Coverage);

        // The next change computes it again.
        session.CoverageComputing = null;
        Edit(session, NewerJournal, NewerNote + " Neu.", NewerNote);
        Time.Advance(VaultSession.CoverageDelay);
        await Eventually(() => !ReferenceEquals(last, session.Coverage), "The coverage was not computed again.");
    }

    /// <summary>Collects the session's log entries.</summary>
    private sealed class ListLogger : ILogger
    {
        public System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception), exception));
    }
}
