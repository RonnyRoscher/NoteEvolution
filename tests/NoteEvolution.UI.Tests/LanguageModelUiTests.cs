using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NoteEvolution.AI.Language;
using NoteEvolution.AI.Tests;
using NoteEvolution.AI.Understanding;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Components.Dialogs;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>The language model's settings, its setup dialog and its progress in the header (spec 4, 7, 11.1).</summary>
public class LanguageModelUiTests : UiTestContext
{
    private const string Model = "qwen2.5:7b";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

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

    private string Text(string key, params object[] args) =>
        Services.GetRequiredService<IStringLocalizer<Strings>>()[key, args].Value;

    /// <summary>A book with 3 headings and 2 notes long enough for a core statement: 5 items of background work.</summary>
    private TestVault Vault()
    {
        var vault = TestVault.Create(
            ("pages/Buch - Alpha.md", "title:: Alpha\ntype:: book\n\n- # Schlaf\n\t- Über den Schlaf und seine Phasen.\n\t- ## Träume\n\t\t- Träume ordnen das Erlebte.\n- # Arbeit\n\t- Konzentration braucht Ruhe.\n"),
            ("journals/2026_03_01.md",
                "- Wer zu wenig schläft, verliert zuerst die Geduld und dann die Fähigkeit, Wichtiges von Unwichtigem zu trennen; das merkt man selbst erst spät.\n"),
            ("journals/2026_03_02.md",
                "- Konzentration entsteht nicht durch Willenskraft allein, sondern durch eine Umgebung, die Unterbrechungen fernhält und Pausen zulässt, jeden Tag neu.\n"));
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

    /// <summary>A ready language model whose every request waits at <paramref name="gate"/>, and an open vault with the AI.</summary>
    private async Task<VaultSession> OpenGatedAsync(SemaphoreSlim gate)
    {
        _chat.Gate = gate;
        var language = UseLanguageModel(_chat);
        await language.RefreshAsync(CancellationToken.None);
        Assert.Equal(LanguageModelState.Ready, language.State);
        return await OpenWithAiAsync(Vault());
    }

    private IRenderedComponent<HeaderBar> RenderHeader() => Render<HeaderBar>();

    // ---- Settings ----

    [Fact]
    public async Task Settings_LanguageModelSection_SavesAddressModelEnabled()
    {
        _chat.Models = [new ChatModelInfo(Model, 4_700_000_000), new ChatModelInfo("llama3:8b", 4_000_000_000)];
        var language = UseLanguageModel(_chat);
        Settings.LanguageModelEnabled = false;
        await language.RefreshAsync(CancellationToken.None);
        var closed = 0;
        var cut = Render<SettingsDialog>(p => p.Add(c => c.OnClose, EventCallback.Factory.Create(this, () => closed++)));

        // The models installed in Ollama (and the configured one) are offered; the state says what the runtime found.
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".ne-settings-llm-model option").Count));
        Assert.Equal([Model, "llama3:8b"], cut.FindAll(".ne-settings-llm-model option").Select(o => o.GetAttribute("value")));
        Assert.False(cut.Find(".ne-settings-llm-enabled").HasAttribute("checked"));
        Assert.Equal("http://127.0.0.1:11434", cut.Find(".ne-settings-llm-address").GetAttribute("value"));
        Assert.Equal(Text("LlmStateOff"), cut.Find(".ne-settings-llm-state").TextContent);

        cut.Find(".ne-settings-llm-enabled").Change(true);
        cut.Find(".ne-settings-llm-address").Input("http://localhost:11434");
        cut.Find(".ne-settings-llm-model").Change("llama3:8b");
        Assert.Empty(cut.FindAll(".ne-settings-llm-remote"));
        cut.Find(".ne-settings-save").Click();

        Assert.Equal(1, closed);
        Assert.True(Settings.LanguageModelEnabled);
        Assert.Equal("http://localhost:11434", Settings.LanguageModelAddress);
        Assert.Equal("llama3:8b", Settings.LanguageModelName);
        var saved = UiSettings.Load(Platform.UserDataDirectory);
        Assert.True(saved.LanguageModelEnabled);
        Assert.Equal("http://localhost:11434", saved.LanguageModelAddress);
        Assert.Equal("llama3:8b", saved.LanguageModelName);

        // Saving asks the server again with the new values.
        await Eventually(() => language.State == LanguageModelState.Ready, "The runtime was not refreshed after saving.");
        var again = Render<SettingsDialog>();
        again.WaitForAssertion(() => Assert.Equal(Text("LlmStateReady", "0.5.0"), again.Find(".ne-settings-llm-state").TextContent));
        Assert.Empty(again.FindAll(".ne-settings-llm-pull"));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Settings_ReadyWithoutLocalAi_HintsThatItIsNeeded(bool? aiInstalled, bool hint)
    {
        if (aiInstalled is { } installed)
        {
            UseAi(installed, () => new FakeEmbedder());
        }

        var language = UseLanguageModel(_chat);
        await language.RefreshAsync(CancellationToken.None);

        // Without the local AI (embedding model) the understanding cannot be used: the section says so.
        var cut = Render<SettingsDialog>();

        Assert.Equal(Text("LlmStateReady", "0.5.0"), cut.Find(".ne-settings-llm-state").TextContent);
        Assert.Equal(hint ? [Text("LlmNeedsLocalAi")] : [], cut.FindAll(".ne-settings-llm-needs-ai").Select(e => e.TextContent));
        Assert.Equal("Braucht die lokale KI (Einstellungen › Lokale KI)", Text("LlmNeedsLocalAi"));
    }

    [Fact]
    public async Task Settings_ModelMissing_ShowsState_AndPullOpensTheDialog()
    {
        _chat.Models = [];
        var language = UseLanguageModel(_chat);
        await language.RefreshAsync(CancellationToken.None);

        var cut = Render<SettingsDialog>();

        Assert.Equal(Text("LlmStateModelMissing"), cut.Find(".ne-settings-llm-state").TextContent);
        cut.Find(".ne-settings-llm-pull").Click();
        Assert.Single(cut.FindAll(".ne-llm-dialog"));
        Assert.Single(cut.FindAll(".ne-llm-pull"));

        _chat.Version = null;
        await language.RefreshAsync(CancellationToken.None);
        cut.WaitForAssertion(() => Assert.Equal(Text("LlmStateUnreachable"), cut.Find(".ne-settings-llm-state").TextContent));
    }

    [Fact]
    public void Settings_RemoteAddress_NeedsConfirmation()
    {
        UseLanguageModel(_chat);
        var closed = 0;
        var cut = Render<SettingsDialog>(p => p.Add(c => c.OnClose, EventCallback.Factory.Create(this, () => closed++)));

        cut.Find(".ne-settings-llm-address").Input("http://192.168.1.20:11434");

        Assert.Equal(Text("LlmRemoteWarning"), cut.Find(".ne-settings-llm-remote").TextContent);
        cut.Find(".ne-settings-save").Click();
        Assert.Equal(0, closed);
        Assert.Equal(Text("LlmRemoteNotConfirmed"), cut.Find(".ne-dialog-error").TextContent);
        Assert.Equal("http://127.0.0.1:11434", Settings.LanguageModelAddress);
        Assert.Equal("http://127.0.0.1:11434", UiSettings.Load(Platform.UserDataDirectory).LanguageModelAddress);

        cut.Find(".ne-settings-llm-remote-confirm").Change(true);
        cut.Find(".ne-settings-save").Click();
        Assert.Equal(1, closed);
        Assert.Equal("http://192.168.1.20:11434", Settings.LanguageModelAddress);
        Assert.Equal("http://192.168.1.20:11434", UiSettings.Load(Platform.UserDataDirectory).LanguageModelAddress);
    }

    [Fact]
    public void Settings_InvalidAddress_IsRefused()
    {
        UseLanguageModel(_chat);
        var cut = Render<SettingsDialog>();

        cut.Find(".ne-settings-llm-address").Input("keine adresse");
        cut.Find(".ne-settings-save").Click();

        Assert.Equal(Text("LlmAddressInvalid"), cut.Find(".ne-dialog-error").TextContent);
        Assert.Equal("http://127.0.0.1:11434", Settings.LanguageModelAddress);
    }

    [Fact]
    public async Task Settings_Save_RefreshWinsOverOlderRetry()
    {
        // The old address accepts the connection but never answers; the new one is ready.
        var old = new FakeChatModel { VersionHangs = true };
        Settings.LanguageModelEnabled = true;
        var language = new LanguageModelRuntime(
            Settings, address => address.Port == 11500 ? _chat : old, Time, NullLogger<LanguageModelRuntime>.Instance);
        Services.AddSingleton(language);
        var retry = language.RefreshAsync(CancellationToken.None);
        await Eventually(() => old.VersionCalls == 1);

        var cut = Render<SettingsDialog>();
        cut.Find(".ne-settings-llm-address").Input("http://localhost:11500");
        cut.Find(".ne-settings-save").Click();
        await Eventually(() => language.State == LanguageModelState.Ready);

        // The older check ends (as unreachable) only now: its result is dropped.
        Time.Advance(LanguageModelRuntime.CheckTimeout);
        await retry;
        Assert.Equal(LanguageModelState.Ready, language.State);
        Assert.Same(_chat, language.Chat);
        Assert.Equal("0.5.0", language.Version);
    }

    // ---- Setup ----

    [Fact]
    public async Task Offer_ShownOnce_WhenReachableAndNotOffered()
    {
        var vault = Vault();
        UseLanguageModel(_chat);
        Settings.LanguageModelEnabled = false;
        _chat.Version = null; // not running when the app starts
        Settings.LastVault = vault.Root;
        Platform.FolderToPick = vault.Root;
        var cut = Render<Shell>();
        WaitForVaultOpened(cut);
        Assert.Empty(cut.FindAll(".ne-llm-dialog"));
        Assert.False(Settings.LanguageModelOffered);

        // Ollama started meanwhile: opening a vault asks it again, so the offer appears.
        _chat.Version = "0.5.0";
        var first = State.Session;
        await cut.Find(".ne-open-vault").ClickAsync(new MouseEventArgs());
        WaitForVaultOpened(cut, first);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-llm-dialog")));
        Assert.Contains(Text("LlmOfferTitle"), cut.Find(".ne-llm-dialog").GetAttribute("aria-label"));
        Assert.True(Settings.LanguageModelOffered);
        Assert.True(UiSettings.Load(Platform.UserDataDirectory).LanguageModelOffered);

        cut.Find(".ne-llm-not-now").Click();
        Assert.Empty(cut.FindAll(".ne-llm-dialog"));
        Assert.False(Settings.LanguageModelEnabled);

        // Once only.
        var second = State.Session;
        await cut.Find(".ne-open-vault").ClickAsync(new MouseEventArgs());
        WaitForVaultOpened(cut, second);
        await Task.Delay(100);
        Assert.Empty(cut.FindAll(".ne-llm-dialog"));
    }

    [Fact]
    public async Task Offer_Setup_EnablesAndOffersThePull()
    {
        _chat.Models = [];
        var language = UseLanguageModel(_chat);
        Settings.LanguageModelEnabled = false;
        await language.RefreshAsync(CancellationToken.None);
        var closed = 0;
        var cut = Render<LanguageModelDialog>(p => p
            .Add(c => c.Offer, true)
            .Add(c => c.OnClose, EventCallback.Factory.Create(this, () => closed++)));

        await cut.Find(".ne-llm-setup").ClickAsync(new MouseEventArgs());

        Assert.True(Settings.LanguageModelEnabled);
        Assert.True(UiSettings.Load(Platform.UserDataDirectory).LanguageModelEnabled);
        Assert.Equal(LanguageModelState.ModelMissing, language.State);
        cut.WaitForAssertion(() => Assert.Contains(Text("LlmPullConfirm", Model, ModelSizes.Gigabytes(4_700_000_000)), cut.Find(".ne-llm-confirm").TextContent));
        Assert.Single(cut.FindAll(".ne-llm-pull"));
        Assert.Equal(0, closed);

        // The pull ends with Ollama's success: the model is ready and the dialog closes.
        _chat.Models = [new ChatModelInfo(Model, 4_700_000_000)];
        await cut.Find(".ne-llm-pull").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.Equal(1, closed));
        Assert.Equal(LanguageModelState.Ready, language.State);
    }

    [Fact]
    public async Task Pull_ShowsProgressMbAndRemaining_Cancel()
    {
        _chat.Models = [];
        var language = UseLanguageModel(_chat);
        await language.RefreshAsync(CancellationToken.None);
        using var step = new SemaphoreSlim(0);
        var cancelled = false;
        _chat.Pull = async (_, progress, ct) =>
        {
            try
            {
                // Ollama reports each layer with its own Completed/Total.
                progress?.Report(new PullProgress("pulling manifest", 0, 0));
                progress?.Report(new PullProgress("pulling aaa", 0, 3_000_000_000));
                progress?.Report(new PullProgress("pulling bbb", 0, 1_000_000_000));
                await step.WaitAsync(ct);
                progress?.Report(new PullProgress("pulling aaa", 1_500_000_000, 3_000_000_000));
                progress?.Report(new PullProgress("pulling bbb", 500_000_000, 1_000_000_000));
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                throw;
            }
        };
        var closed = 0;
        var cut = Render<LanguageModelDialog>(p => p.Add(c => c.OnClose, EventCallback.Factory.Create(this, () => closed++)));

        cut.Find(".ne-llm-pull").Click();

        cut.WaitForAssertion(() => Assert.Equal(Text("LlmPullProgressUnknown", 0, 4000), cut.Find(".ne-llm-pull-status").TextContent));
        Assert.Equal(LanguageModelState.Pulling, language.State);
        Assert.Equal("4000000000", cut.Find(".ne-llm-pull-progress").GetAttribute("max"));

        // 2 GB in 20 s: 100 MB/s, so the other 2 GB take 20 s.
        Time.Advance(TimeSpan.FromSeconds(20));
        step.Release();
        cut.WaitForAssertion(() => Assert.Equal(Text("LlmPullProgress", 2000, 4000, "20 s"), cut.Find(".ne-llm-pull-status").TextContent));
        Assert.Equal("2000000000", cut.Find(".ne-llm-pull-progress").GetAttribute("value"));

        cut.Find(".ne-llm-cancel").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-llm-pull-progress")));
        Assert.True(cancelled);
        Assert.Empty(cut.FindAll(".ne-llm-failure"));
        Assert.Single(cut.FindAll(".ne-llm-pull"));
        Assert.Equal(0, closed);
        await Eventually(() => language.State == LanguageModelState.ModelMissing);
    }

    [Fact]
    public async Task Pull_EndsWithoutSuccess_ShowsFailure()
    {
        _chat.Models = [];
        var language = UseLanguageModel(_chat);
        await language.RefreshAsync(CancellationToken.None);
        _chat.Pull = (_, progress, _) =>
        {
            progress?.Report(new PullProgress("pulling aaa", 100, 1_000));
            return Task.CompletedTask; // the stream ended without "success"
        };
        var closed = 0;
        var cut = Render<LanguageModelDialog>(p => p.Add(c => c.OnClose, EventCallback.Factory.Create(this, () => closed++)));

        await cut.Find(".ne-llm-pull").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => Assert.Contains(Text("LlmPullFailed"), cut.Find(".ne-llm-failure").TextContent));
        Assert.Equal(0, closed);
        Assert.Equal(LanguageModelState.ModelMissing, language.State);
        await Assert.ThrowsAsync<ChatModelUnavailableException>(() => language.PullAsync(null, CancellationToken.None));
    }

    // ---- Header ----

    [Fact]
    public async Task Header_ShowsCountBarRemaining_ThenReady()
    {
        using var gate = new SemaphoreSlim(0);
        await OpenGatedAsync(gate);
        var cut = RenderHeader();

        cut.WaitForAssertion(() => Assert.Equal(
            Text("LlmHeaderProgress", 0, 5) + " · " + Text("LlmHeaderRemainingUnknown"), cut.Find(".ne-llm-text").TextContent));
        Assert.Equal("5", cut.Find(".ne-llm-progress").GetAttribute("max"));
        Assert.Equal("0", cut.Find(".ne-llm-progress").GetAttribute("value"));
        Assert.Single(cut.FindAll(".ne-ai"));

        // Three answers of 4:40 each; 2 items are open: 9:20 remain.
        for (var i = 1; i <= 3; i++)
        {
            var calls = i;
            await Eventually(() => _chat.Calls.Count == calls);
            Time.Advance(TimeSpan.FromSeconds(280));
            gate.Release();
        }

        await Eventually(() => _chat.Calls.Count == 4);
        cut.WaitForAssertion(() => Assert.Equal(
            Text("LlmHeaderProgress", 3, 5) + " · " + Text("LlmHeaderRemaining", "9:20"), cut.Find(".ne-llm-text").TextContent));
        Assert.Equal("3", cut.Find(".ne-llm-progress").GetAttribute("value"));

        gate.Release(2);
        cut.WaitForAssertion(() => Assert.Equal(Text("LlmHeaderReady"), cut.Find(".ne-llm-text").TextContent));
        Assert.Empty(cut.FindAll(".ne-llm-progress"));
    }

    [Fact]
    public async Task Header_PauseToggle_SavedAndForwarded()
    {
        using var gate = new SemaphoreSlim(0);
        var session = await OpenGatedAsync(gate);
        var cut = RenderHeader();
        await Eventually(() => _chat.Calls.Count == 1);
        Assert.Equal("⏸", cut.Find(".ne-llm-pause").TextContent);

        cut.Find(".ne-llm-pause").Click();

        Assert.True(Settings.LanguageModelPaused);
        Assert.True(UiSettings.Load(Platform.UserDataDirectory).LanguageModelPaused);
        Assert.Equal("▶", cut.Find(".ne-llm-pause").TextContent);

        // The running request finishes; no further one starts (without any refresh of the runtime).
        gate.Release();
        await Eventually(() => session.Understanding?.Pause == UnderstandingPause.User, "The pause did not reach the worker.");
        cut.WaitForAssertion(() => Assert.Equal(
            Text("LlmHeaderProgress", 1, 5) + " · " + Text("LlmPauseUser"), cut.Find(".ne-llm-text").TextContent));
        await Task.Delay(100);
        Assert.Single(_chat.Calls);

        cut.Find(".ne-llm-pause").Click();

        Assert.False(Settings.LanguageModelPaused);
        Assert.False(UiSettings.Load(Platform.UserDataDirectory).LanguageModelPaused);
        await Eventually(() => _chat.Calls.Count == 2, "The worker did not go on.");
        gate.Release(4);
        await Eventually(() => session.Understanding is { Done: 5, Total: 5 });
    }

    [Fact]
    public async Task Header_PauseReasons_Typing_Battery_Unreachable()
    {
        using var gate = new SemaphoreSlim(0);
        var session = await OpenGatedAsync(gate);
        var cut = RenderHeader();
        await Eventually(() => _chat.Calls.Count == 1);

        // Typing during a request: it finishes, then the work waits for the typing pause.
        session.NotifyTyping();
        gate.Release();
        cut.WaitForAssertion(() => Assert.Equal(
            Text("LlmHeaderProgress", 1, 5) + " · " + Text("LlmPauseTyping"), cut.Find(".ne-llm-text").TextContent));

        // Ollama goes away: the next request fails, the work pauses.
        _chat.Version = null;
        _chat.Failure = (_, _) => new ChatModelUnavailableException("gone");
        gate.Release();
        Time.Advance(UnderstandingWorker.TypingQuiet);

        // The worker may set its wait for the end of the typing pause only after that advance: time goes on until it ends.
        await Eventually(() =>
        {
            if (session.Understanding?.Pause == UnderstandingPause.Unreachable) return true;
            Time.Advance(TimeSpan.FromSeconds(1));
            return false;
        });
        cut.WaitForAssertion(() => Assert.Equal(
            Text("LlmHeaderProgress", 1, 5) + " · " + Text("LlmPauseUnreachable"), cut.Find(".ne-llm-text").TextContent));

        // The texts of the other reasons (battery end to end: LanguageModelTests.Worker_OnBattery_FromMonitor).
        var l = Services.GetRequiredService<IStringLocalizer<Strings>>();
        Assert.Equal(
            Text("LlmHeaderProgress", 1, 5) + " · " + Text("LlmPauseBattery"),
            HeaderBar.LanguageModelText(new UnderstandingProgress(1, 5, TimeSpan.FromMinutes(3), UnderstandingPause.Battery), l));
        Assert.Equal(
            Text("LlmHeaderProgress", 1, 5) + " · " + Text("LlmPauseModelMissing"),
            HeaderBar.LanguageModelText(new UnderstandingProgress(1, 5, null, UnderstandingPause.ModelMissing), l));
        Assert.Equal(Text("LlmHeaderReady"), HeaderBar.LanguageModelText(new UnderstandingProgress(5, 5, null, UnderstandingPause.User), l));
    }

    [Fact]
    public void Header_BeforeTheFirstPlan_Checking()
    {
        var l = Services.GetRequiredService<IStringLocalizer<Strings>>();

        // The worker has not looked at the work yet: neither a count nor "bereit".
        Assert.Equal(Text("LlmHeaderChecking"),
            HeaderBar.LanguageModelText(new UnderstandingProgress(0, 0, null, UnderstandingPause.None, Planned: false), l));
        Assert.Equal("Sprachmodell prüft …", Text("LlmHeaderChecking"));
        Assert.Equal(Text("LlmHeaderReady"),
            HeaderBar.LanguageModelText(new UnderstandingProgress(0, 0, null, UnderstandingPause.None), l));
    }

    [Fact]
    public async Task Header_WithoutWork_ShowsTheRuntimeState()
    {
        _chat.Version = null;
        var language = UseLanguageModel(_chat);
        await language.RefreshAsync(CancellationToken.None);
        var cut = RenderHeader();

        Assert.Equal(Text("LlmHeaderUnreachable"), cut.Find(".ne-llm-text").TextContent);

        _chat.Version = "0.5.0";
        _chat.Models = [];
        await language.RefreshAsync(CancellationToken.None);
        cut.WaitForAssertion(() => Assert.Equal(Text("LlmHeaderModelMissing"), cut.Find(".ne-llm-text").TextContent));

        Settings.LanguageModelEnabled = false;
        await language.RefreshAsync(CancellationToken.None);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-llm")));
    }

    // ---- Header breakdown ----

    private static void OpenBreakdown(IRenderedComponent<HeaderBar> cut)
    {
        cut.WaitForElement("button.ne-llm-text").Click();
        cut.WaitForElement(".ne-llm-breakdown");
    }

    [Fact]
    public async Task Breakdown_OpensWithRowsPerKind_ShowingCountBarAndRemaining()
    {
        using var gate = new SemaphoreSlim(0);
        var session = await OpenGatedAsync(gate);
        var cut = RenderHeader();
        var button = cut.WaitForElement(".ne-llm-text");
        Assert.Equal("button", button.TagName.ToLowerInvariant());
        Assert.Equal("false", button.GetAttribute("aria-expanded"));
        Assert.Empty(cut.FindAll(".ne-llm-breakdown"));

        button.Click();

        cut.WaitForElement(".ne-llm-breakdown");
        Assert.Equal("true", cut.Find(".ne-llm-text").GetAttribute("aria-expanded"));
        var kinds = session.Understanding!.Kinds!;
        var headings = cut.Find(".ne-llm-row-headings");
        Assert.Equal(Text("LlmBreakdownCount", kinds[UnderstandingKind.Heading].Done, kinds[UnderstandingKind.Heading].Total),
            headings.QuerySelector(".ne-llm-row-count")!.TextContent);
        Assert.Equal(kinds[UnderstandingKind.Heading].Total.ToString(), headings.QuerySelector("progress")!.GetAttribute("max"));
        cut.WaitForAssertion(() => Assert.Equal(Text("LlmBreakdownCount", 0, 2), cut.Find(".ne-llm-row-cores .ne-llm-row-count").TextContent));
        Assert.NotNull(cut.Find(".ne-llm-row-cores progress"));

        // Open work shows the estimate (unknown before the third answer); a skipped count only appears with failures.
        Assert.Equal(Text("LlmHeaderRemainingUnknown"), headings.QuerySelector(".ne-llm-row-remaining")!.TextContent);
        Assert.Empty(cut.FindAll(".ne-llm-row-failed"));
        Assert.Empty(cut.FindAll(".ne-llm-breakdown-pause"));

        // The embedding row counts the embedded top-level notes.
        // The status is put together after a throttle delay on the fake clock.
        await Eventually(() =>
        {
            if (session.EmbeddingProgress is { } e && e.Total > 0 && e.Done == e.Total) return true;
            Time.Advance(VaultSession.NoteStatusDelay);
            return false;
        }, "The notes were not all embedded.");
        cut.WaitForAssertion(() =>
        {
            var embedding = session.EmbeddingProgress!.Value;
            Assert.Equal(Text("LlmBreakdownCount", embedding.Done, embedding.Total), cut.Find(".ne-llm-row-embedding .ne-llm-row-count").TextContent);
        });

        gate.Release(5);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-llm-row-headings .ne-llm-row-remaining")));
    }

    [Fact]
    public async Task Breakdown_ShowsPlacementRow_WhenTheBookIsCurrent()
    {
        using var gate = new SemaphoreSlim(0);
        gate.Release(100);
        var session = await OpenGatedAsync(gate);
        var cut = RenderHeader();
        await Eventually(() => session.Understanding?.Kinds?.ContainsKey(UnderstandingKind.Placement) == true, "No placement progress.");

        OpenBreakdown(cut);

        Assert.Single(cut.FindAll(".ne-llm-row-placements"));
        Assert.Single(cut.FindAll(".ne-llm-row-cores"));
    }

    [Fact]
    public async Task Breakdown_Skipped_ShowsCount()
    {
        using var gate = new SemaphoreSlim(0);
        _chat.Failure = (call, _) => call.Options.Schema is null && call.User.Contains("Träume", StringComparison.Ordinal)
            ? new InvalidOperationException("Kaputt.")
            : null;
        gate.Release(100);
        var session = await OpenGatedAsync(gate);
        var cut = RenderHeader();
        await Eventually(() => session.Understanding?.Kinds?[UnderstandingKind.Heading].Failed > 0, "Nothing was given up.");
        var failed = session.Understanding!.Kinds![UnderstandingKind.Heading].Failed;

        OpenBreakdown(cut);

        cut.WaitForAssertion(() => Assert.Equal(
            Text("LlmBreakdownSkipped", failed), cut.Find(".ne-llm-row-headings .ne-llm-row-failed").TextContent));
    }

    [Fact]
    public async Task Breakdown_Paused_ShowsTheReason()
    {
        using var gate = new SemaphoreSlim(0);
        var session = await OpenGatedAsync(gate);
        var cut = RenderHeader();
        await Eventually(() => _chat.Calls.Count == 1);
        cut.WaitForElement(".ne-llm-pause").Click();
        gate.Release();
        await Eventually(() => session.Understanding?.Pause == UnderstandingPause.User, "The pause did not reach the worker.");

        OpenBreakdown(cut);

        cut.WaitForAssertion(() => Assert.Equal(
            Text("LlmBreakdownPaused", Text("LlmPauseUser")), cut.Find(".ne-llm-breakdown-pause").TextContent));
    }

    [Fact]
    public async Task Breakdown_EscapeAndOutsideClick_Close()
    {
        using var gate = new SemaphoreSlim(0);
        await OpenGatedAsync(gate);
        var cut = RenderHeader();

        OpenBreakdown(cut);
        cut.Find(".ne-llm-breakdown").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-llm-breakdown")));
        Assert.Equal("false", cut.Find(".ne-llm-text").GetAttribute("aria-expanded"));

        OpenBreakdown(cut);
        cut.Find(".ne-llm-breakdown").KeyDown(new KeyboardEventArgs { Key = "a" });
        Assert.Single(cut.FindAll(".ne-llm-breakdown"));
        cut.Find(".ne-llm-backdrop").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-llm-breakdown")));

        // The button toggles it, too.
        OpenBreakdown(cut);
        cut.Find(".ne-llm-text").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-llm-breakdown")));
    }

    [Fact]
    public async Task Breakdown_WithoutLocalAi_HasNoEmbeddingRow()
    {
        var language = UseLanguageModel(_chat);
        await language.RefreshAsync(CancellationToken.None);
        var session = await OpenSessionAsync(Vault());
        var cut = RenderHeader();

        Assert.Null(session.EmbeddingProgress);
        Assert.Empty(cut.FindAll(".ne-llm-row-embedding"));
    }
}
