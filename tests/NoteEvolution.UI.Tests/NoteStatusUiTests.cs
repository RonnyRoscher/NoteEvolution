using AngleSharp.Dom;
using Bunit;
using Bunit.Extensions.WaitForHelpers;
using NoteEvolution.AI.Language;
using NoteEvolution.AI.Tests;
using NoteEvolution.AI.Understanding;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>The status dot on the note card (spec 7.2).</summary>
public class NoteStatusUiTests : UiTestContext
{
    private const string SleepNote =
        "Wer zu wenig schläft, verliert zuerst die Geduld und dann die Fähigkeit, Wichtiges von Unwichtigem zu trennen; das merkt man selbst erst spät.";

    private const string WorkNote =
        "Konzentration entsteht nicht durch Willenskraft allein, sondern durch eine Umgebung, die Unterbrechungen fernhält und Pausen zulässt, jeden Tag neu.";

    private const string UsedNote = "Schon verwendet";

    private const string Book =
        "title:: Alpha\ntype:: book\n\n- # Schlaf\n\t- Über den Schlaf und seine Phasen.\n\t- ## Träume\n\t\t- Träume ordnen das Erlebte.\n\t\t- Wer schlecht schläft, träumt wirr.\n- # Arbeit\n\t- Konzentration braucht Ruhe.\n";

    private readonly List<TestVault> _vaults = [];
    private readonly FakeChatModel _chat = new();

    public NoteStatusUiTests()
    {
        // Placement checks (they have a schema) get a good fit at the first place; everything else a plain answer.
        _chat.Answer = (call, n) => call.Options.Schema is not null
            ? "{\"begruendung\":\"Passt zum Abschnitt.\",\"stelle\":\"1.1\",\"passung\":\"gut\"}"
            : $"Antwort {n}.";
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _vaults.ForEach(v => v.Dispose());
        }
    }

    /// <summary>A book with 3 headings, 2 notes long enough for a core statement and a used note: 5 items of background work.</summary>
    private TestVault Vault()
    {
        var vault = TestVault.Create(
            ("pages/Buch - Alpha.md", Book),
            ("journals/2026_02_01.md", $"- {UsedNote}\n  used-in:: [[Buch - Alpha]]\n"),
            ("journals/2026_03_01.md", $"- {SleepNote}\n"),
            ("journals/2026_03_02.md", $"- {WorkNote}\n"));
        _vaults.Add(vault);
        return vault;
    }

    private static async Task Eventually(Func<bool> condition, string because = "The condition was not met in time.")
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, because);
            await Task.Delay(10);
        }
    }

    private async Task<VaultSession> OpenReadyAsync(TestVault vault, SemaphoreSlim? gate = null)
    {
        var language = UseLanguageModel(_chat);
        await language.RefreshAsync(CancellationToken.None);
        Assert.Equal(LanguageModelState.Ready, language.State);
        var session = await OpenWithAiAsync(vault);
        await Eventually(() => session.Understanding is { Total: 5, Done: 5 }, "The background work did not finish.");
        _chat.Gate = gate;
        return session;
    }

    private IRenderedComponent<NoteCard> Card(VaultSession session, string content) =>
        Render<NoteCard>(p => p.Add(c => c.Note, session.Notes.All().Single(n => n.Block.Content.StartsWith(content, StringComparison.Ordinal))));

    /// <summary>
    /// Advances the fake time a second at a time until <paramref name="assertion"/> holds: the status put together in the
    /// background may finish only after an advance, and the next one is then due a second later.
    /// </summary>
    private void AdvanceUntil(IRenderedComponent<NoteCard> cut, Action assertion)
    {
        for (var i = 0; i < 15; i++)
        {
            Time.Advance(TimeSpan.FromSeconds(1));
            try
            {
                cut.WaitForAssertion(assertion, TimeSpan.FromMilliseconds(500));
                return;
            }
            catch (WaitForFailedException)
            {
                // Not yet: another second.
            }
        }

        assertion();
    }

    private static IElement Dot(IRenderedComponent<NoteCard> cut) => cut.Find(".ne-note-head > .ne-note-dot");

    [Fact]
    public async Task UnusedNote_PendingDot_WithTooltipAndAriaLabel()
    {
        var session = await OpenReadyAsync(Vault());
        var cut = Card(session, SleepNote);

        // The status is put together at most once a second.
        Time.Advance(TimeSpan.FromSeconds(1));

        cut.WaitForAssertion(() => Assert.Contains("ne-note-dot-pending", Dot(cut).ClassList));
        var dot = Dot(cut);
        Assert.Equal("Embedding fertig · Kernaussage fertig · Zuordnung ausstehend", dot.GetAttribute("title"));
        Assert.Equal(dot.GetAttribute("title"), dot.GetAttribute("aria-label"));
    }

    [Fact]
    public async Task PlacementStored_CurrentDot()
    {
        var session = await OpenReadyAsync(Vault());
        var cut = Card(session, SleepNote);

        // The placement checks are built after the debounce; then the status is put together again.
        Time.Advance(VaultSession.PageIndexDelay);
        await Eventually(() => session.Understanding is { Total: 7, Done: 7 }, "The placement checks did not finish.");

        AdvanceUntil(cut, () => Assert.Contains("ne-note-dot-current", Dot(cut).ClassList));
        Assert.Equal("Embedding fertig · Kernaussage fertig · Zuordnung aktuell · Passung gut", Dot(cut).GetAttribute("title"));
    }

    [Fact]
    public async Task RequestRunning_RunningDot_ThenCurrent()
    {
        using var gate = new SemaphoreSlim(0);
        var session = await OpenReadyAsync(Vault(), gate);
        var calls = _chat.Calls.Count;
        Time.Advance(VaultSession.PageIndexDelay);
        await Eventually(() => _chat.Calls.Count > calls, "No placement check started.");
        var running = _chat.Calls[calls].User.Contains(WorkNote, StringComparison.Ordinal) ? WorkNote : SleepNote;
        var cut = Card(session, running);

        AdvanceUntil(cut, () => Assert.Contains("ne-note-dot-running", Dot(cut).ClassList));
        Assert.StartsWith("Wird gerade berechnet: Zuordnung", Dot(cut).GetAttribute("title"));

        gate.Release(100);
        await Eventually(() => session.Understanding is { Total: 7, Done: 7 }, "The placement checks did not finish.");
        AdvanceUntil(cut, () => Assert.Contains("ne-note-dot-current", Dot(cut).ClassList));
    }

    [Fact]
    public async Task UsedNote_NoDot()
    {
        var session = await OpenReadyAsync(Vault());
        var used = Card(session, UsedNote);
        var unused = Card(session, WorkNote);

        Time.Advance(TimeSpan.FromSeconds(1));

        unused.WaitForAssertion(() => Assert.Single(unused.FindAll(".ne-note-dot")));
        Assert.Single(used.FindAll(".ne-note-used"));
        Assert.Empty(used.FindAll(".ne-note-dot"));
    }

    [Fact]
    public async Task WithoutLanguageModel_NoDot()
    {
        var session = await OpenWithAiAsync(Vault());
        var cut = Card(session, SleepNote);

        Time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50);

        Assert.Null(session.NoteStatus(session.Notes.All().Single(n => n.Block.Content == SleepNote).Key));
        Assert.Empty(cut.FindAll(".ne-note-dot"));
    }

    [Fact]
    public async Task IndexStatusChange_ReadsOnlyEmbeddings_NoStoreReads()
    {
        var session = await OpenReadyAsync(Vault());
        var key = session.Notes.All().Single(n => n.Block.Content == SleepNote).Key;
        Time.Advance(VaultSession.PageIndexDelay);
        await Eventually(() => session.Understanding is { Total: 7, Done: 7 }, "The placement checks did not finish.");
        Time.Advance(TimeSpan.FromSeconds(1));
        await Eventually(() => session.NoteStatus(key) is { Placement: PlacementState.Current } && !session.NoteStatusBuilding,
            "The status was not put together.");
        await Task.Delay(50);
        var reads = session.NoteStatusReads;
        var builds = session.NoteStatusBuilds;
        var raised = 0;
        session.NoteStatusRaising = () => Interlocked.Increment(ref raised);

        // The notes are embedded again (the index reports its status per batch): their vectors stay, nothing is read.
        await session.Semantic!.RebuildAsync(session.Notes, CancellationToken.None);
        Time.Advance(TimeSpan.FromSeconds(1));
        await Eventually(() => !session.NoteStatusBuilding, "Putting the status together did not end.");
        await Task.Delay(50);

        Assert.True(session.NoteStatusBuilds > builds, "The index status change put nothing together.");
        Assert.Equal(reads, session.NoteStatusReads);
        Assert.Equal(0, Volatile.Read(ref raised));
        Assert.True(session.NoteStatus(key)!.Embedded);
    }

    [Fact]
    public async Task NoteStatusChanged_With300Notes_AtMostOncePerSecond()
    {
        var notes = string.Concat(Enumerable.Range(1, 300).Select(i => $"- Notiz Nummer {i} über Schlaf und Arbeit.\n"));
        var vault = TestVault.Create(("pages/Buch - Alpha.md", Book), ("journals/2026_03_01.md", notes));
        _vaults.Add(vault);
        var language = UseLanguageModel(_chat);
        await language.RefreshAsync(CancellationToken.None);
        var session = await OpenWithAiAsync(vault);
        var raised = new List<DateTimeOffset>();
        // Stamped where the build dispatches the event, so the dispatcher adds no delay.
        session.NoteStatusRaising = () =>
        {
            lock (raised)
            {
                raised.Add(Time.GetUtcNow());
            }
        };

        // After the summaries the 300 placement checks are built; then fake time runs in small steps while they are let
        // through one at a time (each one is a change), until a few seconds of changes were seen.
        await Eventually(() => session.Understanding is { Total: 3, Done: 3 }, "The summaries did not finish.");
        using var gate = new SemaphoreSlim(0);
        _chat.Gate = gate;
        Time.Advance(VaultSession.PageIndexDelay);
        await Eventually(() => session.Understanding is { Total: 303 }, "The placement checks were not built.");

        int Count()
        {
            lock (raised)
            {
                return raised.Count;
            }
        }

        var until = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while ((Count() < 4 || session.Understanding is not { Done: >= 13 }) && DateTime.UtcNow < until)
        {
            // The fake time stands still while the status is put together, so each change is raised at the time it is due.
            gate.Release();
            Time.Advance(TimeSpan.FromMilliseconds(100));
            await Eventually(() => !session.NoteStatusBuilding, "Putting the status together did not end.");
            await Task.Delay(10);
        }

        gate.Release(1000);
        DateTimeOffset[] times;
        lock (raised)
        {
            times = [.. raised];
        }

        Assert.True(times.Length >= 4, $"Only {times.Length} status changes were raised.");
        Assert.All(times.Zip(times.Skip(1)), pair => Assert.True(pair.Second - pair.First >= TimeSpan.FromSeconds(1),
            $"Raised at {pair.First:HH:mm:ss.fff} and again at {pair.Second:HH:mm:ss.fff}."));
        Assert.True(session.Understanding is { Done: >= 13 }, $"The placement checks did not run: {session.Understanding}.");
        Assert.NotNull(session.NoteStatus(session.Notes.All().First().Key));
    }

    // The coverage's 300-note test (spec 6.4) is here rather than with the other coverage tests in LanguageModelTests: the
    // tests of one class run one after another, and the two 300-note tests time out when they run at the same time.
    [Fact]
    public async Task Coverage_RecomputedAfterPlacementStored_Debounced1s()
    {
        var notes = string.Concat(Enumerable.Range(1, 300).Select(i => $"- Notiz Nummer {i} über Schlaf und Arbeit.\n"));
        var vault = TestVault.Create(("pages/Buch - Alpha.md", Book), ("journals/2026_03_01.md", notes));
        _vaults.Add(vault);
        var language = UseLanguageModel(_chat);
        await language.RefreshAsync(CancellationToken.None);
        var session = await OpenWithAiAsync(vault);
        var started = new List<DateTimeOffset>();
        session.CoverageComputing = () =>
        {
            lock (started)
            {
                started.Add(Time.GetUtcNow());
            }
        };
        session.WatchCoverage(this, visible: true);

        // After the summaries the 300 placement checks are built and let through one at a time (each one is stored and
        // raises PlacementsChanged) while fake time runs in small steps.
        await Eventually(() => session.Understanding is { Total: 3, Done: 3 }, "The summaries did not finish.");
        using var gate = new SemaphoreSlim(0);
        _chat.Gate = gate;
        Time.Advance(VaultSession.PageIndexDelay);
        await Eventually(() => session.Understanding is { Total: 303 }, "The placement checks were not built.");

        int Count()
        {
            lock (started)
            {
                return started.Count;
            }
        }

        var until = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while ((Count() < 4 || session.Understanding is not { Done: >= 13 }) && DateTime.UtcNow < until)
        {
            gate.Release();
            Time.Advance(TimeSpan.FromMilliseconds(100));
            await Eventually(() => !session.CoverageRunning, "The computation did not end.");
            await Task.Delay(10);
        }

        gate.Release(1000);
        DateTimeOffset[] times;
        lock (started)
        {
            times = [.. started];
        }

        Assert.True(times.Length >= 4, $"Only {times.Length} computations ran.");
        Assert.All(times.Zip(times.Skip(1)), pair => Assert.True(pair.Second - pair.First >= TimeSpan.FromSeconds(1),
            $"Computed at {pair.First:HH:mm:ss.fff} and again at {pair.Second:HH:mm:ss.fff}."));
        Assert.True(session.Coverage is { Unused: 300, Checked: > 0 }, $"The coverage did not count the checks: {session.Coverage}.");
    }
}
