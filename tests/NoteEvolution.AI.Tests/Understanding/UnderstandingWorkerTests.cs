using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NoteEvolution.AI.Embeddings;
using NoteEvolution.AI.Language;
using NoteEvolution.AI.Understanding;
using NoteEvolution.TestSupport;

namespace NoteEvolution.AI.Tests.Understanding;

public sealed class UnderstandingWorkerTests
{
    private const string Model = "qwen2.5:7b";
    private const string Embed = "embed-1";

    private static UnderstandingItem Heading(string title, params UnderstandingItem[] children) =>
        new(UnderstandingKind.Heading, "fp-" + title, "id-" + title, title, "Text von " + title,
            [.. children.Select(c => c.Fingerprint)], Guid.NewGuid());

    private static UnderstandingItem Note(string title, string? fingerprint = null, string? identity = null) =>
        new(UnderstandingKind.Note, fingerprint ?? "fp-" + title, identity ?? "id-" + title, title, "Notiz " + title, [],
            Guid.NewGuid());

    /// <summary>What a recorded call was about: the heading title, the note title, or the whole user text.</summary>
    private static string Subject(ChatCall call)
    {
        var lines = call.User.Split('\n');
        if (lines[0].StartsWith("Überschrift: ", StringComparison.Ordinal)) return lines[0]["Überschrift: ".Length..];
        if (lines[0] == "Notiz:") return lines[1]["Notiz ".Length..];
        return call.User;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TempDir _dir = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Lock _gate = new();
        private readonly List<string> _embedded = [];
        private readonly List<UnderstandingProgress> _reports = [];
        private Task? _run;

        public Fixture()
        {
            Store = UnderstandingStore.Open(_dir.Path);
            Worker = new UnderstandingWorker(Chat, Model, Store, EmbedText, Embed, Time, () => OnBattery,
                NullLogger.Instance);
            Worker.ProgressChanged += () =>
            {
                lock (_gate) _reports.Add(Worker.Progress);
            };
        }

        public FakeChatModel Chat { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public UnderstandingStore Store { get; }
        public UnderstandingWorker Worker { get; }
        public bool OnBattery { get; set; }

        public IReadOnlyList<string> Embedded
        {
            get { lock (_gate) return [.. _embedded]; }
        }

        public IReadOnlyList<UnderstandingProgress> Reports
        {
            get { lock (_gate) return [.. _reports]; }
        }

        public Task Run => _run ?? throw new InvalidOperationException("not started");

        public void Start() => _run = Task.Run(() => Worker.RunAsync(_stop.Token));

        public IReadOnlyList<string> Subjects => [.. Chat.Calls.Select(Subject)];

        private float[] EmbedText(string text)
        {
            lock (_gate) _embedded.Add(text);
            return [text.Length, 1f];
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            Worker.Dispose();
            if (_run is not null) await _run.WaitAsync(TimeSpan.FromSeconds(10));
            Store.Dispose();
            _dir.Dispose();
            _stop.Dispose();
        }
    }

    private static async Task Until(Func<bool> condition, string what = "condition")
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(5);
        }
    }

    /// <summary>Gives the worker loop real time to (not) act before a negative assertion.</summary>
    private static Task Quiet() => Task.Delay(200);

    [Fact]
    public async Task Worker_SummarizesBottomUp_ParentGetsChildSummaries()
    {
        await using var f = new Fixture();
        var u = Heading("U");
        var a = Heading("A", u);
        var b = Heading("B");
        f.Worker.SetWork([u, a, b], null, [], []);

        f.Start();
        await Until(() => f.Store.Get(b.Fingerprint) is not null);

        Assert.Equal(["U", "A", "B"], f.Subjects);
        Assert.Contains("- U: Antwort 1.", f.Chat.Calls[1].User);
        Assert.Contains("Text von A", f.Chat.Calls[1].User);
        Assert.All(f.Chat.Calls, c => Assert.Equal(Model, c.Model));
        Assert.All(f.Chat.Calls, c => Assert.Equal(UnderstandingPrompts.SummaryTokens, c.Options.MaxTokens));

        var stored = f.Store.Get(a.Fingerprint)!;
        Assert.Equal("Antwort 2.", stored.Text);
        Assert.Equal(a.Identity, stored.Identity);
        Assert.Equal(Model, stored.ChatModel);
        Assert.Equal(Embed, stored.EmbedModel);
        Assert.Contains(EmbeddingText.QueryPrefix + "Antwort 2.", f.Embedded);
        Assert.Empty(f.Store.WithoutVector(Embed));
    }

    [Fact]
    public async Task Worker_OrderCurrentSectionThenBookThenNotesNewestThenOtherBooks()
    {
        await using var f = new Fixture();
        var a1 = Heading("A1");
        var a = Heading("A", a1);
        var b1 = Heading("B1");
        var b = Heading("B", b1);
        var other = Heading("O");
        f.Worker.SetWork([a1, a, b1, b], b1.Key, [Note("Neu"), Note("Alt")], [other]);

        f.Start();
        await Until(() => f.Chat.Calls.Count == 7);
        await Quiet();

        Assert.Equal(["B1", "B", "A1", "A", "Neu", "Alt", "O"], f.Subjects);
        Assert.Equal(UnderstandingPrompts.CoreTokens, f.Chat.Calls[4].Options.MaxTokens);
        Assert.Contains(EmbeddingText.PassagePrefix + "Antwort 5.", f.Embedded);
    }

    [Fact]
    public async Task Worker_CurrentSectionsChapter_BeforeEarlierChapters()
    {
        await using var f = new Fixture();
        var a1 = Heading("A1");
        var a = Heading("A", a1);
        var b1 = Heading("B1");
        var b2x = Heading("B2x");
        var b2 = Heading("B2", b2x);
        var b = Heading("B", b1, b2);
        f.Worker.SetWork([a1, a, b1, b2x, b2, b], b2.Key, [], []);

        f.Start();
        await Until(() => f.Chat.Calls.Count == 6);

        // the section's subtree and path first, then the rest of its chapter (its sibling B1), then the chapter itself
        Assert.Equal(["B2x", "B2", "B1", "B", "A1", "A"], f.Subjects);
    }

    [Fact]
    public async Task Worker_ChangedText_FirstInItsGroup()
    {
        await using var f = new Fixture();
        f.Store.Put(new UnderstandingEntry(UnderstandingKind.Note, "fp-old", "id-Geändert", "alt", Model, null, null));
        f.Worker.SetWork([], null, [Note("Neu"), Note("Geändert")], []);

        f.Start();
        await Until(() => f.Chat.Calls.Count == 2);

        Assert.Equal(["Geändert", "Neu"], f.Subjects);
    }

    [Fact]
    public async Task Worker_OneRequestAtATime()
    {
        await using var f = new Fixture();
        f.Chat.Gate = new SemaphoreSlim(0);
        f.Worker.SetWork([], null, [Note("Eins"), Note("Zwei")], []);

        f.Start();
        await Until(() => f.Chat.Calls.Count == 1);
        await Quiet();
        Assert.Single(f.Chat.Calls);

        f.Chat.Gate.Release();
        await Until(() => f.Chat.Calls.Count == 2);
        Assert.NotNull(f.Store.Get("fp-Eins"));
        f.Chat.Gate.Release();
        await Until(() => f.Store.Get("fp-Zwei") is not null);

        Assert.Equal(1, f.Chat.MaxRunning);
    }

    [Fact]
    public async Task Worker_Typing_WaitsFor10sQuiet()
    {
        await using var f = new Fixture();
        f.Worker.SetWork([], null, [Note("Eins")], []);
        f.Worker.NotifyTyping();

        f.Start();
        await Until(() => f.Worker.Progress.Pause == UnderstandingPause.Typing);
        f.Time.Advance(TimeSpan.FromSeconds(9));
        await Quiet();
        Assert.Empty(f.Chat.Calls);

        f.Worker.NotifyTyping(); // quiet again only at 19 s
        f.Time.Advance(TimeSpan.FromSeconds(5));
        await Quiet();
        Assert.Empty(f.Chat.Calls);
        Assert.Equal(UnderstandingPause.Typing, f.Worker.Progress.Pause);

        f.Time.Advance(TimeSpan.FromSeconds(5));
        await Until(() => f.Store.Get("fp-Eins") is not null);
        Assert.Single(f.Chat.Calls);
    }

    [Fact]
    public async Task Worker_PausedSwitch_StopsAfterCurrent()
    {
        await using var f = new Fixture();
        f.Chat.Gate = new SemaphoreSlim(0);
        f.Worker.SetWork([], null, [Note("Eins"), Note("Zwei")], []);

        f.Start();
        await Until(() => f.Chat.Calls.Count == 1);
        f.Worker.Paused = true;
        Assert.True(f.Worker.Paused);
        f.Chat.Gate.Release();

        await Until(() => f.Worker.Progress.Pause == UnderstandingPause.User);
        Assert.NotNull(f.Store.Get("fp-Eins")); // the running request finished and was stored
        f.Time.Advance(TimeSpan.FromMinutes(10));
        await Quiet();
        Assert.Single(f.Chat.Calls);

        f.Worker.Paused = false;
        await Until(() => f.Chat.Calls.Count == 2);
        f.Chat.Gate.Release();
        await Until(() => f.Store.Get("fp-Zwei") is not null);
    }

    [Fact]
    public async Task Worker_OnBattery_PausesAndReports()
    {
        await using var f = new Fixture();
        f.OnBattery = true;
        f.Worker.SetWork([], null, [Note("Eins")], []);

        f.Start();
        await Until(() => f.Reports.Any(r => r.Pause == UnderstandingPause.Battery));
        Assert.Equal(UnderstandingPause.Battery, f.Worker.Progress.Pause);
        await Quiet();
        Assert.Empty(f.Chat.Calls);

        f.OnBattery = false;
        f.Time.Advance(UnderstandingWorker.BatteryCheck);
        await Until(() => f.Store.Get("fp-Eins") is not null);
        await Until(() => f.Worker.Progress.Pause == UnderstandingPause.None);
    }

    [Fact]
    public async Task Worker_Unreachable_PausesAndRetriesEvery60s()
    {
        await using var f = new Fixture();
        f.Chat.Failure = (_, n) =>
        {
            if (n > 1) return null;
            f.Chat.Version = null; // the server went away during the request
            return new ChatModelUnavailableException("connection refused");
        };
        f.Worker.SetWork([], null, [Note("Eins")], []);

        f.Start();
        await Until(() => f.Worker.Progress.Pause == UnderstandingPause.Unreachable);
        Assert.Equal(0, f.Store.Attempts("fp-Eins")); // not the note's fault
        var checks = f.Chat.VersionCalls;

        f.Time.Advance(TimeSpan.FromSeconds(59));
        await Quiet();
        Assert.Equal(checks, f.Chat.VersionCalls);

        f.Time.Advance(TimeSpan.FromSeconds(1));
        await Until(() => f.Chat.VersionCalls == checks + 1);
        await Quiet();
        Assert.Single(f.Chat.Calls);
        Assert.Equal(UnderstandingPause.Unreachable, f.Worker.Progress.Pause);

        f.Chat.Version = "0.5.0";
        f.Time.Advance(TimeSpan.FromSeconds(60));
        await Until(() => f.Store.Get("fp-Eins") is not null);
        Assert.Equal(2, f.Chat.Calls.Count);
        await Until(() => f.Worker.Progress.Pause == UnderstandingPause.None);
    }

    [Fact]
    public async Task Worker_VersionNeverAnswers_TimesOutAndRetriesAfter60s()
    {
        await using var f = new Fixture();
        f.Chat.VersionHangs = true; // accepts the connection, never answers
        f.Worker.SetWork([], null, [Note("Eins")], []);

        f.Start();
        await Until(() => f.Chat.VersionCalls == 1, "the first check");
        f.Time.Advance(UnderstandingWorker.CheckTimeout - TimeSpan.FromSeconds(1));
        await Quiet();
        Assert.NotEqual(UnderstandingPause.Unreachable, f.Worker.Progress.Pause);

        f.Time.Advance(TimeSpan.FromSeconds(1));
        await Until(() => f.Worker.Progress.Pause == UnderstandingPause.Unreachable, "the unreachable pause");
        Assert.Empty(f.Chat.Calls);

        f.Time.Advance(UnderstandingWorker.RetryInterval - TimeSpan.FromSeconds(1));
        await Quiet();
        Assert.Equal(1, f.Chat.VersionCalls);

        f.Chat.VersionHangs = false;
        f.Time.Advance(TimeSpan.FromSeconds(1));
        await Until(() => f.Store.Get("fp-Eins") is not null, "the note after the retry");
        Assert.Equal(2, f.Chat.VersionCalls);
    }

    [Fact]
    public async Task Worker_ModelMissing_Reported()
    {
        await using var f = new Fixture();
        f.Chat.Models = [new("llama3:8b", 1)];
        f.Worker.SetWork([], null, [Note("Eins")], []);

        f.Start();
        await Until(() => f.Worker.Progress.Pause == UnderstandingPause.ModelMissing);
        Assert.Contains(f.Reports, r => r.Pause == UnderstandingPause.ModelMissing);
        await Quiet();
        Assert.Empty(f.Chat.Calls);

        f.Chat.Models = [new(Model, 1)];
        f.Time.Advance(UnderstandingWorker.RetryInterval);
        await Until(() => f.Store.Get("fp-Eins") is not null);
    }

    [Fact]
    public async Task Worker_Failure_RetriedAtMost3Times()
    {
        await using var f = new Fixture();
        f.Chat.Failure = (call, _) => Subject(call) == "Kaputt" ? new ChatModelUnavailableException("HTTP 500") : null;
        f.Worker.SetWork([], null, [Note("Kaputt"), Note("Gut")], []);

        f.Start();
        await Until(() => f.Store.Attempts("fp-Kaputt") == 3);
        await Until(() => f.Worker.Progress is { Done: 2, Total: 2 });
        await Quiet();

        Assert.Equal(["Kaputt", "Gut", "Kaputt", "Kaputt"], f.Subjects); // a failed entry is retried later
        Assert.Null(f.Store.Get("fp-Kaputt"));
        Assert.NotNull(f.Store.Get("fp-Gut"));
    }

    [Fact]
    public async Task Worker_ServerErrorOnEveryRequest_PausesWithoutSkippingItems()
    {
        await using var f = new Fixture();
        var broken = true;
        f.Chat.Failure = (_, _) => broken ? new ChatModelUnavailableException("HTTP 500: model runner crashed") : null;
        string[] fingerprints = ["fp-A", "fp-B", "fp-C", "fp-D"];
        f.Worker.SetWork([], null, [Note("A"), Note("B"), Note("C"), Note("D")], []);

        f.Start();
        await Until(() => f.Worker.Progress.Pause == UnderstandingPause.Unreachable);
        Assert.Equal(3, f.Chat.Calls.Count); // three different items in a row: a server problem
        Assert.All(fingerprints, fp => Assert.Equal(0, f.Store.Attempts(fp)));
        Assert.Equal(0, f.Worker.Progress.Done);

        f.Time.Advance(UnderstandingWorker.RetryInterval); // one more try, failing again: paused at once, nothing charged
        await Until(() => f.Chat.Calls.Count == 4);
        await Until(() => f.Worker.Progress.Pause == UnderstandingPause.Unreachable);
        await Quiet();
        Assert.Equal(4, f.Chat.Calls.Count);
        Assert.All(fingerprints, fp => Assert.Equal(0, f.Store.Attempts(fp)));

        broken = false;
        f.Time.Advance(UnderstandingWorker.RetryInterval);
        await Until(() => fingerprints.All(fp => f.Store.Get(fp) is not null));
        await Until(() => f.Worker.Progress is { Done: 4, Total: 4, Pause: UnderstandingPause.None });
    }

    [Fact]
    public async Task Worker_SingleItemFailing_AmongSuccesses_StillSkippedAfter3()
    {
        await using var f = new Fixture();
        f.Chat.Failure = (call, _) => Subject(call) == "Kaputt" ? new ChatModelUnavailableException("HTTP 500") : null;
        f.Worker.SetWork([], null, [Note("Kaputt"), Note("G1"), Note("G2"), Note("G3")], []);

        f.Start();
        await Until(() => f.Store.Attempts("fp-Kaputt") == 3);
        await Until(() => f.Worker.Progress is { Done: 4, Total: 4 });
        await Quiet();

        Assert.Equal(["Kaputt", "G1", "G2", "G3", "Kaputt", "Kaputt"], f.Subjects);
        Assert.DoesNotContain(f.Reports, r => r.Pause == UnderstandingPause.Unreachable);
        Assert.Null(f.Store.Get("fp-Kaputt"));
    }

    [Fact]
    public async Task Worker_EmptyAnswer_CountsAsFailure()
    {
        await using var f = new Fixture();
        f.Chat.Answer = (_, n) => n == 1 ? "**  **" : "Antwort.";
        f.Worker.SetWork([], null, [Note("Eins")], []);

        f.Start();
        await Until(() => f.Store.Get("fp-Eins") is not null);

        Assert.Equal(2, f.Chat.Calls.Count);
        Assert.Equal("Antwort.", f.Store.Get("fp-Eins")!.Text);
    }

    [Fact]
    public async Task Worker_Timeout5Min_CountsAsFailure()
    {
        await using var f = new Fixture();
        f.Chat.Gate = new SemaphoreSlim(0);
        f.Worker.SetWork([], null, [Note("Eins")], []);

        f.Start();
        await Until(() => f.Chat.Calls.Count == 1);
        f.Time.Advance(TimeSpan.FromSeconds(299));
        await Quiet();
        Assert.Equal(0, f.Store.Attempts("fp-Eins"));
        Assert.False(f.Chat.Calls[0].Cancelled);

        f.Time.Advance(TimeSpan.FromSeconds(1));
        await Until(() => f.Chat.Calls.Count == 2);
        Assert.True(f.Chat.Calls[0].Cancelled);
        Assert.Equal(1, f.Store.Attempts("fp-Eins"));

        f.Chat.Gate.Release();
        await Until(() => f.Store.Get("fp-Eins") is not null);
        Assert.Equal("Antwort 2.", f.Store.Get("fp-Eins")!.Text);
    }

    [Fact]
    public async Task Worker_TextChangedDuringRequest_StoresUnderOldFingerprint_Requeues()
    {
        await using var f = new Fixture();
        f.Chat.Gate = new SemaphoreSlim(0);
        f.Worker.SetWork([], null, [Note("Alt", "fp-1", "id-N")], []);

        f.Start();
        await Until(() => f.Chat.Calls.Count == 1);
        f.Worker.SetWork([], null, [Note("Neu", "fp-2", "id-N")], []);
        f.Chat.Gate.Release();

        await Until(() => f.Chat.Calls.Count == 2);
        Assert.Equal("Antwort 1.", f.Store.Get("fp-1")!.Text);
        Assert.Null(f.Store.Get("fp-2"));
        Assert.Equal("Neu", Subject(f.Chat.Calls[1]));

        f.Chat.Gate.Release();
        await Until(() => f.Store.Get("fp-2") is not null);
        Assert.Equal("Antwort 2.", f.Store.Get("fp-2")!.Text);

        // All done: the old fingerprint is no longer live and is pruned.
        await Until(() => f.Store.Get("fp-1") is null, "prune");
    }

    [Fact]
    public async Task Worker_Disposed_CancelsRunningRequest_StoresNothing()
    {
        await using var f = new Fixture();
        f.Chat.Gate = new SemaphoreSlim(0);
        f.Worker.SetWork([], null, [Note("Eins")], []);

        f.Start();
        await Until(() => f.Chat.Calls.Count == 1);
        f.Worker.Dispose();

        await f.Run.WaitAsync(TimeSpan.FromSeconds(10)); // ends without throwing
        Assert.True(f.Chat.Calls[0].Cancelled);
        Assert.Null(f.Store.Get("fp-Eins"));
        Assert.Equal(0, f.Store.Attempts("fp-Eins"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            f.Worker.RunNextAsync("s", "u", new ChatOptions(10), CancellationToken.None));
    }

    [Fact]
    public async Task Worker_ExistingCurrentEntries_NotRequested_OnlyMissingVectorsEmbedded()
    {
        await using var f = new Fixture();
        var h = Heading("H");
        f.Store.Put(new UnderstandingEntry(UnderstandingKind.Heading, h.Fingerprint, h.Identity, "Thema H", Model,
            "old-embed", [9f]));
        f.Store.Put(new UnderstandingEntry(UnderstandingKind.Note, "fp-A", "id-A", "Kern A", Model, Embed, [1f]));
        f.Store.Put(new UnderstandingEntry(UnderstandingKind.Note, "fp-B", "id-B", "Kern B", Model, null, null));
        f.Worker.SetWork([h], null, [Note("A"), Note("B")], []);

        f.Start();
        await Until(() => f.Store.WithoutVector(Embed).Count == 0);
        await Until(() => f.Worker.Progress is { Done: 3, Total: 3 });
        await Quiet();

        Assert.Empty(f.Chat.Calls);
        Assert.Equal([EmbeddingText.PassagePrefix + "Kern B", EmbeddingText.QueryPrefix + "Thema H"], f.Embedded.Order());
        Assert.Equal(Embed, f.Store.Get(h.Fingerprint)!.EmbedModel);
        Assert.Equal("Thema H", f.Store.Get(h.Fingerprint)!.Text);
        Assert.Equal([1f], f.Store.Get("fp-A")!.Vector!);
    }

    [Fact]
    public async Task Worker_BeforeFirstWork_ProgressNotPlanned()
    {
        await using var f = new Fixture();
        Assert.False(f.Worker.Progress.Planned);

        // Running without work yet: nothing is known, so nothing may look done.
        f.Start();
        await Quiet();
        Assert.False(f.Worker.Progress.Planned);

        f.Worker.SetWork([], null, [Note("A")], []);
        await Until(() => f.Worker.Progress is { Planned: true, Done: 1, Total: 1 });
    }

    [Fact]
    public async Task Worker_CompletedPlan_PrunesStoreWithLiveItems()
    {
        await using var f = new Fixture();
        var h = Heading("H");
        f.Store.Put(new UnderstandingEntry(UnderstandingKind.Heading, "old-H", h.Identity, "Thema alt", Model, Embed, [1f]));
        f.Store.Put(new UnderstandingEntry(UnderstandingKind.Note, "fp-Gone", "id-Gone", "Kern weg", Model, Embed, [1f]));

        // Without work the store is left alone (an empty snapshot is not "everything is gone").
        f.Start();
        await Quiet();
        Assert.NotNull(f.Store.Get("fp-Gone"));

        f.Worker.SetWork([h], null, [Note("A")], []);
        await Until(() => f.Worker.Progress is { Done: 2, Total: 2 });
        await Until(() => f.Store.Get("fp-Gone") is null, "prune");

        // The entries of the live items stay; the old entry of a live heading is replaced by its current one.
        Assert.NotNull(f.Store.Get(h.Fingerprint));
        Assert.NotNull(f.Store.Get("fp-A"));
        Assert.Null(f.Store.Get("old-H"));
    }

    [Fact]
    public async Task Worker_Remaining_FromLast10_NullBeforeThird()
    {
        await using var f = new Fixture();
        f.Chat.Gate = new SemaphoreSlim(0);
        var notes = Enumerable.Range(1, 13).Select(i => Note("N" + i)).ToList();
        f.Worker.SetWork([], null, notes, []);

        f.Start();
        for (var i = 1; i <= 11; i++)
        {
            await Until(() => f.Chat.Calls.Count == i, "call " + i);
            f.Time.Advance(TimeSpan.FromSeconds(i == 1 ? 100 : 10));
            f.Chat.Gate.Release();
            await Until(() => f.Worker.Progress.Done == i, "done " + i);

            var progress = f.Worker.Progress;
            Assert.Equal(13, progress.Total);
            if (i < 3) Assert.Null(progress.Remaining);
            if (i == 3) Assert.Equal(TimeSpan.FromSeconds(40 * 10), progress.Remaining); // (100 + 10 + 10) / 3 × 10 open
        }

        // the first (100 s) answer has left the window of the last 10: 10 s × 2 open
        Assert.Equal(TimeSpan.FromSeconds(20), f.Worker.Progress.Remaining);
    }

    [Fact]
    public async Task RunNext_RunsAfterCurrentRequest_BeforeQueue()
    {
        await using var f = new Fixture();
        f.Chat.Gate = new SemaphoreSlim(0);
        f.Worker.SetWork([], null, [Note("Eins"), Note("Zwei")], []);

        f.Start();
        await Until(() => f.Chat.Calls.Count == 1);
        var check = f.Worker.RunNextAsync("System", "Frage", new ChatOptions(UnderstandingPrompts.CheckTokens, Json: true),
            CancellationToken.None);
        await Quiet();
        Assert.Single(f.Chat.Calls); // the running request is not interrupted
        Assert.False(check.IsCompleted);

        f.Chat.Gate.Release();
        await Until(() => f.Chat.Calls.Count == 2);
        Assert.Equal("Frage", f.Chat.Calls[1].User);
        Assert.Equal("System", f.Chat.Calls[1].System);
        Assert.True(f.Chat.Calls[1].Options.Json);

        f.Chat.Gate.Release();
        Assert.Equal("Antwort 2.", await check.WaitAsync(TimeSpan.FromSeconds(10)));
        await Until(() => f.Chat.Calls.Count == 3);
        Assert.Equal("Zwei", Subject(f.Chat.Calls[2]));
        f.Chat.Gate.Release();
        await Until(() => f.Store.Get("fp-Zwei") is not null);
    }
    [Fact]
    public async Task RunNext_BehindRunningRequest_ReportsWaitingThenStarted()
    {
        await using var f = new Fixture();
        f.Chat.Gate = new SemaphoreSlim(0);
        f.Worker.SetWork([], null, [Note("Eins")], []);
        f.Start();
        await Until(() => f.Chat.Calls.Count == 1);
        Assert.True(f.Worker.Busy);

        var events = new List<string>();
        var check = f.Worker.RunNextAsync("System", "Frage", new ChatOptions(10), CancellationToken.None,
            waiting: () => { lock (events) events.Add("waiting"); },
            started: () => { lock (events) events.Add("started"); });
        await Quiet();
        lock (events) Assert.Equal(["waiting"], events);

        // The background request ends; the check's own request starts.
        f.Chat.Gate.Release();
        await Until(() => f.Chat.Calls.Count == 2);
        lock (events) Assert.Equal(["waiting", "started"], events);
        Assert.True(f.Worker.Busy);
        f.Chat.Gate.Release();
        Assert.Equal("Antwort 2.", await check.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task RunNext_Idle_StartsWithoutWaiting()
    {
        await using var f = new Fixture();
        f.Start();
        await Quiet();
        Assert.False(f.Worker.Busy);

        var events = new List<string>();
        var answer = await f.Worker.RunNextAsync("System", "Frage", new ChatOptions(10), CancellationToken.None,
            waiting: () => { lock (events) events.Add("waiting"); },
            started: () => { lock (events) events.Add("started"); }).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("Antwort 1.", answer);
        lock (events) Assert.Equal(["started"], events);
        Assert.False(f.Worker.Busy);
    }

    private const string PlacementSystem = "System Zuordnung";
    private const string Verdict = """{"begruendung":"passt","stelle":"1.1","passung":"gut"}""";

    private static UnderstandingItem Placement(string title, Guid? noteKey = null, string? identity = null)
    {
        var prompt = new PlacementCheckPrompt(PlacementSystem, "Prüfe " + title,
            [new CheckTopic(1, Guid.NewGuid(), [Guid.NewGuid()])], "{\"schema\":\"" + title + "\"}");
        return UnderstandingItems.ForPlacement(noteKey ?? Guid.NewGuid(), identity ?? "id-" + title, "Buch", prompt, Model);
    }

    private static bool IsPlacement(ChatCall call) => call.System == PlacementSystem;

    /// <summary>A placement call gets a valid verdict, every other call the default answer.</summary>
    private static void AnswerPlacements(Fixture f) =>
        f.Chat.Answer = (call, n) => IsPlacement(call) ? Verdict : $"Antwort {n}.";

    [Fact]
    public async Task RunNext_Answered_StoredBeforeTheNextPlan_NotAskedAgain()
    {
        await using var f = new Fixture();
        f.Chat.Gate = new SemaphoreSlim(0);
        AnswerPlacements(f);
        var first = Placement("Eins");
        var second = Placement("Zwei");
        f.Worker.SetWork([], null, [], [], [first, second]);
        f.Start();
        await Until(() => f.Chat.Calls.Count == 1);

        // The second check is asked by hand behind the first; its answer is stored before the worker plans on.
        var check = f.Worker.RunNextAsync(second.Check!.System, second.Check.User, PlacementCheck.Options(second.Check),
            CancellationToken.None,
            answered: answer => f.Store.Put(new UnderstandingEntry(UnderstandingKind.Placement, second.Fingerprint,
                second.Identity, answer, Model, null, null)));
        f.Chat.Gate.Release(10);
        Assert.Equal(Verdict, await check.WaitAsync(TimeSpan.FromSeconds(10)));

        await Until(() => f.Worker.Progress is { Done: 2, Total: 2 }, "both checks done");
        await Quiet();
        Assert.Equal(2, f.Chat.Calls.Count);
    }

    [Fact]
    public async Task Placements_LastGroup_AfterOtherBooks()
    {
        await using var f = new Fixture();
        AnswerPlacements(f);
        var p = Placement("P");
        f.Worker.SetWork([Heading("H")], null, [Note("N")], [Heading("O")], [p]);

        f.Start();
        await Until(() => f.Store.Get(p.Fingerprint) is not null);

        Assert.Equal(["H", "N", "O", p.Check!.User], f.Subjects);
        var call = f.Chat.Calls[3];
        Assert.Equal(p.Check.System, call.System);
        Assert.Equal(p.Check.User, call.User);
        Assert.Equal(p.Check.Schema, call.Options.Schema);
        Assert.True(call.Options.Json);
    }

    [Fact]
    public async Task Placement_StoredAsJson_NoVector()
    {
        await using var f = new Fixture();
        AnswerPlacements(f);
        var p = Placement("P");
        f.Worker.SetWork([], null, [], [], [p]);

        f.Start();
        await Until(() => f.Store.Get(p.Fingerprint) is not null);
        await Until(() => f.Worker.Progress is { Done: 1, Total: 1 });
        await Quiet();

        var entry = f.Store.Get(p.Fingerprint)!;
        Assert.Equal(UnderstandingKind.Placement, entry.Kind);
        Assert.Equal(p.Identity, entry.Identity);
        Assert.Equal(PlacementCheck.Parse(Verdict, p.Check!), PlacementCheck.Deserialize(entry.Text));
        Assert.Null(entry.Vector);
        Assert.Empty(f.Embedded);
    }

    [Fact]
    public async Task Placement_Unusable_IsFailedAttempt()
    {
        await using var f = new Fixture();
        f.Chat.Gate = new SemaphoreSlim(0);
        f.Chat.Answer = (_, _) => "{}";
        var p = Placement("P");
        f.Worker.SetWork([], null, [], [], [p]);

        f.Start();
        await Until(() => f.Chat.Calls.Count == 1);
        f.Chat.Gate.Release();
        await Until(() => f.Store.Attempts(p.Fingerprint) == 1);
        Assert.Null(f.Store.Get(p.Fingerprint));

        f.Chat.Gate.Release(2);
        await Until(() => f.Store.Attempts(p.Fingerprint) == 3);
        await Until(() => f.Worker.Progress.Kinds?[UnderstandingKind.Placement] is { Done: 1, Total: 1, Failed: 1 });
        await Quiet();
        Assert.Equal(3, f.Chat.Calls.Count);
    }

    [Fact]
    public async Task Progress_Kinds_CountEachKind()
    {
        await using var f = new Fixture();
        f.Chat.Gate = new SemaphoreSlim(0);
        f.Worker.SetWork([Heading("H1"), Heading("H2")], null, [Note("N")], [Heading("O")], [Placement("P1"), Placement("P2")]);

        f.Start();
        await Until(() => f.Chat.Calls.Count == 1);

        var progress = f.Worker.Progress;
        Assert.Equal(new KindProgress(0, 3, 0, null), progress.Kinds![UnderstandingKind.Heading]);
        Assert.Equal(new KindProgress(0, 1, 0, null), progress.Kinds[UnderstandingKind.Note]);
        Assert.Equal(new KindProgress(0, 2, 0, null), progress.Kinds[UnderstandingKind.Placement]);
        Assert.Equal(5, progress.Total); // the other book's heading is not counted overall
        Assert.Equal(0, progress.Done);
    }

    [Fact]
    public async Task Progress_Remaining_PerKindMean()
    {
        await using var f = new Fixture();
        f.Chat.Gate = new SemaphoreSlim(0);
        AnswerPlacements(f);
        List<UnderstandingItem> notes = [Note("N1"), Note("N2"), Note("N3")];
        var placements = Enumerable.Range(1, 5).Select(i => Placement("P" + i)).ToList();
        f.Worker.SetWork([], null, notes, [], placements);

        f.Start();
        for (var i = 1; i <= 6; i++)
        {
            await Until(() => f.Chat.Calls.Count == i, "call " + i);
            if (i == 6)
            {
                // A new note arrives while the third placement runs: it is queued before the remaining placements.
                f.Worker.SetWork([], null, [.. notes, Note("N4")], [], placements);
            }

            f.Time.Advance(TimeSpan.FromSeconds(i <= 3 ? 10 : 30));
            f.Chat.Gate.Release();
        }

        await Until(() => f.Chat.Calls.Count == 7);
        Assert.Equal("N4", Subject(f.Chat.Calls[6]));
        var progress = f.Worker.Progress;
        Assert.Equal(new KindProgress(3, 5, 0, TimeSpan.FromSeconds(60)), progress.Kinds![UnderstandingKind.Placement]);
        Assert.Equal(new KindProgress(3, 4, 0, TimeSpan.FromSeconds(10)), progress.Kinds[UnderstandingKind.Note]);
        Assert.Equal(TimeSpan.FromSeconds(70), progress.Remaining);
        f.Chat.Gate.Release(3);
    }

    [Fact]
    public async Task Progress_Current_WhileRequestRuns()
    {
        await using var f = new Fixture();
        f.Chat.Gate = new SemaphoreSlim(0);
        AnswerPlacements(f);
        var noteKey = Guid.NewGuid();
        var p = Placement("P", noteKey);
        f.Worker.SetWork([], null, [], [], [p]);

        f.Start();
        await Until(() => f.Chat.Calls.Count == 1);
        Assert.Equal(new UnderstandingCurrent(UnderstandingKind.Placement, noteKey), f.Worker.Progress.Current);

        f.Chat.Gate.Release();
        await Until(() => f.Store.Get(p.Fingerprint) is not null);
        await Until(() => f.Worker.Progress.Current is null, "current cleared");

        var manual = new UnderstandingCurrent(UnderstandingKind.Placement, Guid.NewGuid());
        var check = f.Worker.RunNextAsync("System", "Frage", new ChatOptions(10), CancellationToken.None, current: manual);
        await Until(() => f.Chat.Calls.Count == 2);
        Assert.Equal(manual, f.Worker.Progress.Current);

        f.Chat.Gate.Release();
        await check.WaitAsync(TimeSpan.FromSeconds(10));
        await Until(() => f.Worker.Progress.Current is null, "current cleared after the manual check");
    }

    [Fact]
    public async Task Placement_NoteEditedDuringRequest_StoredUnderOldFingerprint()
    {
        await using var f = new Fixture();
        f.Chat.Gate = new SemaphoreSlim(0);
        AnswerPlacements(f);
        var noteKey = Guid.NewGuid();
        var old = Placement("Alt", noteKey, "id-N");
        var edited = Placement("Neu", noteKey, "id-N");
        f.Worker.SetWork([], null, [], [], [old]);

        f.Start();
        await Until(() => f.Chat.Calls.Count == 1);
        f.Worker.SetWork([], null, [], [], [edited]);
        f.Chat.Gate.Release();

        await Until(() => f.Chat.Calls.Count == 2);
        Assert.NotNull(f.Store.Get(old.Fingerprint));
        Assert.Null(f.Store.Get(edited.Fingerprint));
        Assert.Equal(edited.Check!.User, f.Chat.Calls[1].User);

        f.Chat.Gate.Release();
        await Until(() => f.Store.Get(edited.Fingerprint) is not null);
        Assert.False(f.Run.IsFaulted);
    }

    [Fact]
    public async Task Prune_KeepsLivePlacements()
    {
        await using var f = new Fixture();
        var live = Placement("Live");
        f.Store.Put(new UnderstandingEntry(UnderstandingKind.Placement, live.Fingerprint, live.Identity, "{}", Model, null, null));
        f.Store.Put(new UnderstandingEntry(UnderstandingKind.Placement, "fp-gone", "placement|Buch|id-weg", "{}", Model, null, null));
        f.Worker.SetWork([], null, [Note("A")], [], [live]);

        f.Start();
        await Until(() => f.Worker.Progress is { Done: 2, Total: 2 });
        await Until(() => f.Store.Get("fp-gone") is null, "prune");

        Assert.NotNull(f.Store.Get(live.Fingerprint));
        Assert.NotNull(f.Store.Get("fp-A"));
    }
}
