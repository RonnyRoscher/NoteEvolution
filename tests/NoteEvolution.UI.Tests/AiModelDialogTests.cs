using System.Text;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.DependencyInjection;
using NoteEvolution.AI.Model;
using NoteEvolution.AI.Tests;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components.Dialogs;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

public class AiModelDialogTests : UiTestContext
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static readonly ModelInfo ModelA = TestModel("a") with { DisplayName = "Modell A" };
    private static readonly ModelInfo ModelB = TestModel("b") with { DisplayName = "Modell B" };

    private readonly List<TestVault> _vaults = [];
    private int _closed;
    private readonly List<ModelInfo> _switched = [];

    protected override void Dispose(bool disposing)
    {
        // The session (and its vector cache in the vault) is closed first, then the vault folders are deleted.
        base.Dispose(disposing);
        if (disposing)
        {
            _vaults.ForEach(v => v.Dispose());
        }
    }

    private string Text(string key, params object[] args) =>
        Services.GetRequiredService<IStringLocalizer<Strings>>()[key, args].Value;

    private AiRuntime UseCatalog(bool installed) =>
        UseAi(installed, () => new FakeEmbedder(), [ModelA, ModelB]);

    private IRenderedComponent<AiModelDialog> RenderDialog() =>
        Render<AiModelDialog>(p => p
            .Add(c => c.OnClose, EventCallback.Factory.Create(this, () => _closed++))
            .Add(c => c.OnSwitch, EventCallback.Factory.Create<ModelInfo>(this, m => _switched.Add(m))));

    private static void Select(IRenderedComponent<AiModelDialog> cut, int index) =>
        cut.FindAll(".ne-ai-model-option input[type=radio]")[index].Change(true);

    private static async Task Eventually(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "The condition was not met in time.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public void Lists_AllModels_WithSizes_ActiveMarked()
    {
        UseCatalog(installed: true);

        var cut = RenderDialog();

        var options = cut.FindAll(".ne-ai-model-option");
        Assert.Equal(2, options.Count);
        Assert.Contains(ModelA.DisplayName, options[0].QuerySelector(".ne-ai-model-name")!.TextContent);
        var sizes = Text("AiModelOption", ModelSizes.Megabytes(ModelA.TotalSize), ModelSizes.Gigabytes(ModelA.MemoryEstimate));
        Assert.Contains(sizes, options[0].TextContent);
        Assert.Contains(ModelSizes.Gigabytes(ModelB.MemoryEstimate), options[1].TextContent);
        Assert.NotNull(options[0].QuerySelector(".ne-ai-model-active"));
        Assert.Equal(Text("AiModelActive"), options[0].QuerySelector(".ne-ai-model-active")!.TextContent);
        Assert.Null(options[1].QuerySelector(".ne-ai-model-active"));
        Assert.True(options[0].QuerySelector("input[type=radio]")!.HasAttribute("checked"));
        Assert.False(options[1].QuerySelector("input[type=radio]")!.HasAttribute("checked"));
    }

    [Fact]
    public void NothingInstalled_NoModelMarkedActive()
    {
        UseCatalog(installed: false);

        var cut = RenderDialog();

        Assert.Empty(cut.FindAll(".ne-ai-model-active"));
        Assert.True(cut.FindAll(".ne-ai-model-option input[type=radio]")[0].HasAttribute("checked"));
    }

    [Fact]
    public async Task FirstLoad_DownloadsSelected_ActivatesIt_SavesSetting_Closes()
    {
        var ai = UseCatalog(installed: false);
        var cut = RenderDialog();
        Select(cut, 1);

        cut.Find(".ne-ai-download").Click();

        await Eventually(() => ai.Model.Id == "b" && ai.ModelInstalled);
        cut.WaitForAssertion(() => Assert.Equal(1, _closed));
        Assert.Equal("b", Settings.AiModelId);
        Assert.Equal("b", UiSettings.Load(Platform.UserDataDirectory).AiModelId);
        Assert.Empty(_switched);
    }

    [Fact]
    public void Installed_SwitchDisabledForActive_EnabledForOther()
    {
        UseCatalog(installed: true);
        var cut = RenderDialog();

        Assert.True(cut.Find(".ne-ai-switch").HasAttribute("disabled"));
        Assert.Single(cut.FindAll(".ne-ai-switch-hint"));
        Assert.Empty(cut.FindAll(".ne-ai-download"));
        Assert.Empty(cut.FindAll(".ne-ai-not-now"));
        Assert.Empty(cut.FindAll(".ne-ai-switch-conflict"));

        Select(cut, 1);

        Assert.False(cut.Find(".ne-ai-switch").HasAttribute("disabled"));

        cut.Find(".ne-ai-close").Click();
        Assert.Equal(1, _closed);
    }

    [Fact]
    public async Task Installed_Switch_DownloadsThenRaisesOnSwitch()
    {
        var ai = UseCatalog(installed: true);
        var cut = RenderDialog();
        Select(cut, 1);

        cut.Find(".ne-ai-switch").Click();

        await Eventually(() => _switched.Count == 1);
        Assert.Equal("b", _switched[0].Id);
        Assert.Equal("a", ai.Model.Id); // the switch itself is the owner's job
        Assert.Null(Settings.AiModelId);
        Assert.Equal(0, _closed);
    }

    [Fact]
    public async Task Installed_SwitchHandlerThrows_ShowsError()
    {
        UseCatalog(installed: true);
        var cut = Render<AiModelDialog>(p => p
            .Add(c => c.OnSwitch, EventCallback.Factory.Create<ModelInfo>(this, (Action<ModelInfo>)(_ => throw new InvalidOperationException("switch broke")))));
        Select(cut, 1);

        cut.Find(".ne-ai-switch").Click();

        await Task.Yield();
        cut.WaitForAssertion(() => Assert.Contains("switch broke", cut.Find(".ne-ai-model-error").TextContent));
    }

    [Fact]
    public void Downloading_SelectionLocked_CancelShown()
    {
        UseCatalog(installed: false);
        ModelServer.Hang = true;
        var cut = RenderDialog();
        Select(cut, 1);

        cut.Find(".ne-ai-download").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-ai-cancel")));
        Assert.All(cut.FindAll(".ne-ai-model-option input[type=radio]"), r => Assert.True(r.HasAttribute("disabled")));
        Assert.Empty(cut.FindAll(".ne-ai-download"));

        cut.Find(".ne-ai-cancel").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-ai-download")));
        Assert.All(cut.FindAll(".ne-ai-model-option input[type=radio]"), r => Assert.False(r.HasAttribute("disabled")));
    }

    [Fact]
    public async Task Installed_OpenConflict_SwitchDisabled_HintShown()
    {
        var tv = TestVault.Create(
            ("pages/Buch - Alpha.md", "title:: Alpha\ntype:: book\n\n- # Eins\n\t- Erster Text\n"),
            ("journals/2026_03_01.md", "- Vertrauen wächst\n- Gedanke über Wolken\n"));
        _vaults.Add(tv);
        var ai = UseCatalog(installed: true);
        var session = await OpenSessionAsync(tv, ai);
        var path = Path.Combine(tv.Root, "journals", "2026_03_01.md");
        session.Vault.FindPageByPath(path)!.Roots[1].SetContent("Lokal geändert");
        File.WriteAllText(path, "- Vertrauen wächst\n- Extern geändert\n", new UTF8Encoding(false));
        session.HandleExternalChange(path);
        Assert.True(session.HasAnyOpenConflict);

        var cut = RenderDialog();
        Select(cut, 1);

        Assert.True(cut.Find(".ne-ai-switch").HasAttribute("disabled"));
        Assert.Equal(Text("AiModelSwitchConflict"), cut.Find(".ne-ai-switch-conflict").TextContent);
    }
}
