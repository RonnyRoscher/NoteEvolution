using NoteEvolution.AI.Tests;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>
/// The tests that keep the CPU busy for seconds (300 notes, the AI embedding, the language model's background work, the
/// [handled] assistant's places) run in a collection of their own, alone, so they neither slow down their neighbours nor
/// time out next to them.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ManyNotesCollection
{
    public const string Name = "Many notes";
}

/// <summary>The status dot and the coverage with 300 notes (spec 6.4, 7.2): their events come at most once a second.</summary>
[Collection(ManyNotesCollection.Name)]
public class ManyNotesUiTests : UiTestContext
{
    private const string Book =
        "title:: Alpha\ntype:: book\n\n- # Schlaf\n\t- Über den Schlaf und seine Phasen.\n\t- ## Träume\n\t\t- Träume ordnen das Erlebte.\n\t\t- Wer schlecht schläft, träumt wirr.\n- # Arbeit\n\t- Konzentration braucht Ruhe.\n";

    private readonly List<TestVault> _vaults = [];
    private readonly FakeChatModel _chat = new();

    public ManyNotesUiTests()
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

    private static async Task Eventually(Func<bool> condition, string because = "The condition was not met in time.")
    {
        var until = DateTime.UtcNow + RealTimeWait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, because);
            await Task.Delay(10);
        }
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

    // The coverage's 300-note test (spec 6.4) is here rather than with the other coverage tests in LanguageModelTests, so
    // it runs alone as well.
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
