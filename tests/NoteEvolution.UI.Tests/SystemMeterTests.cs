using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Components.Dialogs;
using NoteEvolution.UI.Platform;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>The system meter in the header (spec 11.2).</summary>
public class SystemMeterTests : UiTestContext
{
    private readonly List<TestVault> _vaults = [];

    protected override void Dispose(bool disposing)
    {
        // The session is closed first, then the vault folders are deleted.
        base.Dispose(disposing);
        if (disposing)
        {
            _vaults.ForEach(v => v.Dispose());
        }
    }

    /// <summary>The resource text with the line breaks a page's attribute has (the parser makes them all <c>\n</c>).</summary>
    private string Text(string key, params object[] args) =>
        Services.GetRequiredService<IStringLocalizer<Strings>>()[key, args].Value.ReplaceLineEndings("\n");

    private static SystemSnapshot Snapshot(double? cpu, double? ramUsed, double? ramTotal, double? diskFree) =>
        new(cpu, ramUsed, ramTotal, diskFree, null, null, null, null, null);

    [Fact]
    public void SystemMeter_ShowsValues_GermanDecimal()
    {
        SystemMonitor.Snapshot = new SystemSnapshot(63.4, 9.79, 15.84, 112.3, 40.2, 4.86, 12.4, 0.6, false, 38_400_000);

        var cut = Render<SystemMeter>();

        var meter = cut.Find(".ne-sysmeter");
        Assert.Equal("CPU 63 % (App 12 % · Ollama 40 %) · RAM 9,8/15,8 GB (App 0,6 · Ollama 4,9 GB) · Frei 112 GB · Analyse 38 MB", meter.TextContent);
        Assert.False(meter.ClassList.Contains("ne-sysmeter-warn"));

        // The shares are in the line itself, no tooltip.
        Assert.Null(meter.GetAttribute("title"));
    }

    [Fact]
    public void SystemMeter_WithoutOllama_LeavesItsPartsOut()
    {
        SystemMonitor.Snapshot = new SystemSnapshot(63.4, 9.79, 15.84, 112.3, null, null, 12.4, 0.6, false, 38_400_000);

        var cut = Render<SystemMeter>();

        Assert.Equal("CPU 63 % (App 12 %) · RAM 9,8/15,8 GB (App 0,6 GB) · Frei 112 GB · Analyse 38 MB", cut.Find(".ne-sysmeter").TextContent);
    }

    [Fact]
    public void SystemMeter_Unknown_Dash()
    {
        var cut = Render<SystemMeter>();

        var meter = cut.Find(".ne-sysmeter");
        Assert.Equal("CPU – % (App – %) · RAM –/– GB (App – GB) · Frei – GB", meter.TextContent);
        Assert.False(meter.ClassList.Contains("ne-sysmeter-warn"));
        Assert.Null(meter.GetAttribute("title"));
    }

    [Fact]
    public void SystemMeter_AnalysisSize_FormattedAndOptional()
    {
        string TextFor(long? bytes)
        {
            SystemMonitor.Snapshot = new SystemSnapshot(10, 4, 16, 100, null, null, 1, 0.5, null, bytes);
            return Render<SystemMeter>().Find(".ne-sysmeter").TextContent;
        }

        Assert.EndsWith("· Analyse 0 MB", TextFor(0), StringComparison.Ordinal);
        Assert.EndsWith("· Analyse 38 MB", TextFor(38_400_000), StringComparison.Ordinal);
        Assert.EndsWith("· Analyse 120 MB", TextFor(123_000_000), StringComparison.Ordinal);
        Assert.EndsWith("· Analyse 1,5 GB", TextFor(1_540_000_000), StringComparison.Ordinal);

        // Without a vault there is no size.
        Assert.DoesNotContain("Analyse", TextFor(null), StringComparison.Ordinal);
    }

    [Fact]
    public void SystemMeter_Warn_Cpu90_Ram1Gb()
    {
        bool Warns(SystemSnapshot snapshot)
        {
            SystemMonitor.Snapshot = snapshot;
            return Render<SystemMeter>().Find(".ne-sysmeter").ClassList.Contains("ne-sysmeter-warn");
        }

        Assert.False(Warns(Snapshot(90, 8, 16, 100)));
        Assert.True(Warns(Snapshot(90.1, 8, 16, 100)));
        Assert.False(Warns(Snapshot(10, 15, 16, 100)));
        Assert.True(Warns(Snapshot(10, 15.1, 16, 100)));

        // Unknown values give no warning.
        Assert.False(Warns(Snapshot(null, 15.5, null, null)));
        Assert.False(Warns(Snapshot(null, null, 16, null)));
    }

    [Fact]
    public async Task SystemMeter_RefreshesEvery2s()
    {
        var tv = TestVault.Create(("pages/Buch - Alpha.md", "title:: Alpha\ntype:: book\n\n- # Eins\n"));
        _vaults.Add(tv);
        var session = await OpenSessionAsync(tv);
        SystemMonitor.Snapshot = Snapshot(10, 4, 16, 100);
        var cut = Render<SystemMeter>();
        Assert.StartsWith("CPU 10 %", cut.Find(".ne-sysmeter").TextContent, StringComparison.Ordinal);
        Assert.Equal(session.Vault.Root, SystemMonitor.LastVaultRoot);
        var reads = SystemMonitor.Reads;

        SystemMonitor.Snapshot = Snapshot(20, 4, 16, 100);
        Time.Advance(SystemMeter.Interval - TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);
        Assert.Equal(reads, SystemMonitor.Reads);
        Assert.StartsWith("CPU 10 %", cut.Find(".ne-sysmeter").TextContent, StringComparison.Ordinal);

        Time.Advance(TimeSpan.FromMilliseconds(1));
        cut.WaitForAssertion(() => Assert.StartsWith("CPU 20 %", cut.Find(".ne-sysmeter").TextContent, StringComparison.Ordinal));

        SystemMonitor.Snapshot = Snapshot(30, 4, 16, 100);
        Time.Advance(SystemMeter.Interval);
        cut.WaitForAssertion(() => Assert.StartsWith("CPU 30 %", cut.Find(".ne-sysmeter").TextContent, StringComparison.Ordinal));
        Assert.Equal(reads + 2, SystemMonitor.Reads);

        // Gone with the component: no more readings.
        await DisposeComponentsAsync();
        Time.Advance(SystemMeter.Interval * 3);
        await Task.Delay(50);
        Assert.Equal(reads + 2, SystemMonitor.Reads);
    }

    [Fact]
    public void SystemMeter_Hidden_WhenSettingOff()
    {
        // In the header by default.
        var header = Render<HeaderBar>();
        Assert.Single(header.FindAll(".ne-sysmeter"));

        // Switched off in the settings: gone from the header at once, and saved.
        var settings = Render<SettingsDialog>();
        Assert.True(settings.Find(".ne-settings-sysmeter").HasAttribute("checked"));
        settings.Find(".ne-settings-sysmeter").Change(false);
        settings.Find(".ne-settings-save").Click();

        Assert.False(Settings.SystemMeterVisible);
        Assert.False(UiSettings.Load(Platform.UserDataDirectory).SystemMeterVisible);
        header.WaitForAssertion(() => Assert.Empty(header.FindAll(".ne-sysmeter")));

        // Hidden when the setting is off from the start.
        Assert.Empty(Render<SystemMeter>().FindAll(".ne-sysmeter"));
    }
}
