using System.Net;
using System.Text;
using Bunit;
using Microsoft.Data.Sqlite;
using NoteEvolution.AI.Model;
using NoteEvolution.AI.Tests;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>Switching the AI model from the settings: the shell closes the vault, activates the model and opens the vault again.</summary>
public class ModelSwitchTests : UiTestContext
{
    private const string BetaPath = "pages/Buch - Beta.md";
    private const string JournalPath = "journals/2026_03_01.md";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static readonly ModelInfo ModelA = TestModel("a");
    private static readonly ModelInfo ModelB = TestModel("b");

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

    private TestVault Vault()
    {
        var vault = TestVault.Create(
            ("pages/Buch - Alpha.md", "title:: Alpha\ntype:: book\n\n- # Eins\n\t- Erster Text\n"),
            (BetaPath, "title:: Beta\ntype:: book\n\n- # Zwei\n\t- Zweiter Text\n"),
            (JournalPath, "- Vertrauen wächst\n- Gedanke über Wolken\n"));
        _vaults.Add(vault);
        return vault;
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "The condition was not met in time.");
            await Task.Delay(10);
        }
    }

    /// <summary>Renders the shell with <paramref name="tv"/> open on its book Beta (not the first) and model a's index ready.</summary>
    private async Task<IRenderedComponent<Shell>> OpenShellAsync(TestVault tv)
    {
        UseAi(installed: true, () => new FakeEmbedder(), [ModelA, ModelB]);
        Settings.LastVault = tv.Root;
        Settings.RememberBook(tv.Root, Path.Combine(tv.Root, BetaPath));
        var cut = Render<Shell>();
        WaitForVaultOpened(cut);
        Assert.Equal("Beta", State.CurrentBook!.Title);
        var session = State.Session!;
        await Eventually(() => session.AiStatus.State == AiState.Ready);
        return cut;
    }

    /// <summary>Opens the model dialog from the settings, selects model b and starts the switch.</summary>
    private static void SwitchToB(IRenderedComponent<Shell> cut)
    {
        cut.Find(".ne-settings").Click();
        cut.Find(".ne-settings-ai-choose").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-ai-model-dialog")));
        cut.FindAll(".ne-ai-model-option input[type=radio]")[1].Change(true);
        cut.Find(".ne-ai-switch").Click();
    }

    /// <summary>Whether <paramref name="session"/> was disposed: its write operations throw then (with nothing to undo, an undo changes nothing).</summary>
    private static bool IsDisposed(VaultSession session)
    {
        try
        {
            session.TryUndo(out _);
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    /// <summary>The model the vault's vector cache was written for (its meta table).</summary>
    private static string? CachedModel(string vaultRoot)
    {
        var path = Path.Combine(vaultRoot, ".noteevolution", "vectors.db");
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        db.Open();
        using var read = db.CreateCommand();
        read.CommandText = "SELECT value FROM meta WHERE key = 'model'";
        return read.ExecuteScalar() as string;
    }

    [Fact]
    public async Task Switch_ActivatesNew_ReopensVault_ReindexesWithNewModel()
    {
        var tv = Vault();
        var cut = await OpenShellAsync(tv);
        var old = State.Session!;
        Assert.Equal("a", CachedModel(tv.Root));

        SwitchToB(cut);

        await Eventually(() => Ai!.Model.Id == "b");
        WaitForVaultOpened(cut, old);
        var session = State.Session!;
        Assert.True(IsDisposed(old));
        Assert.Equal("Beta", State.CurrentBook!.Title);
        Assert.Same(session.Vault.FindPageByPath(Path.Combine(tv.Root, BetaPath)), State.CurrentBook.Page);
        await Eventually(() => session.AiStatus.State == AiState.Ready);
        Assert.Equal("b", CachedModel(tv.Root));
        Assert.False(Directory.Exists(Store!.DirectoryOf(ModelA)));
        Assert.True(Store.IsInstalled(ModelB));
        Assert.Equal("b", Settings.AiModelId);
        Assert.Equal("b", UiSettings.Load(Platform.UserDataDirectory).AiModelId);
        Assert.Empty(cut.FindAll(".ne-ai-model-dialog"));
        Assert.Empty(cut.FindAll(".ne-settings-dialog"));
    }

    [Fact]
    public async Task Switch_FlushesEditorBeforeDisposingSession()
    {
        var tv = Vault();
        var cut = await OpenShellAsync(tv);
        var old = State.Session!;
        var flushes = new List<(VaultSession? Session, bool Disposed)>();
        State.FlushEditor = () =>
        {
            flushes.Add((State.Session, State.Session is { } s && IsDisposed(s)));
            return Task.CompletedTask;
        };

        SwitchToB(cut);

        await Eventually(() => Ai!.Model.Id == "b");
        WaitForVaultOpened(cut, old);

        // The editor's text is saved once into the old, still open session; opening the vault again finds no session
        // to save into (the editor of the old one is still registered until the shell renders without it).
        Assert.Equal((old, false), flushes[0]);
        Assert.Single(flushes, f => f.Session is not null);
        Assert.True(IsDisposed(old));

        // The new session opens its vector cache in the background; the vault folder is deleted only after that.
        var session = State.Session!;
        await Eventually(() => session.AiStatus.State == AiState.Ready);
    }

    [Fact]
    public async Task Switch_DownloadFails_OldModelStaysActive()
    {
        var tv = Vault();
        var cut = await OpenShellAsync(tv);
        var old = State.Session!;
        ModelServer.Status = HttpStatusCode.NotFound;

        SwitchToB(cut);

        cut.WaitForAssertion(() => Assert.Contains("404", cut.Find(".ne-ai-model-error").TextContent), Wait);
        Assert.Equal("a", Ai!.Model.Id);
        Assert.Same(old, State.Session);
        Assert.False(IsDisposed(old));
        Assert.Equal(AiState.Ready, old.AiStatus.State);
        Assert.False(Directory.Exists(Store!.DirectoryOf(ModelB)));
        Assert.True(Store.IsInstalled(ModelA));
        Assert.Null(Settings.AiModelId);
    }

    [Fact]
    public async Task Switch_ActivationFails_ErrorShown_VaultReopenedWithOldModel()
    {
        var tv = Vault();
        var cut = await OpenShellAsync(tv);
        var old = State.Session!;

        // The new model's files vanish between the end of its download and its activation.
        Ai!.ModelChanged += () =>
        {
            if (!Ai.IsDownloading && Directory.Exists(Store!.DirectoryOf(ModelB)))
            {
                Directory.Delete(Store.DirectoryOf(ModelB), recursive: true);
            }
        };

        SwitchToB(cut);

        // The dialog shows the error once the vault is open again.
        cut.WaitForAssertion(() => Assert.Contains("model b", cut.Find(".ne-ai-model-error").TextContent), Wait);
        WaitForVaultOpened(cut, old);
        Assert.Equal("a", Ai.Model.Id);
        Assert.True(Ai.ModelInstalled);
        Assert.Equal("Beta", State.CurrentBook!.Title);
        var session = State.Session!;
        await Eventually(() => session.AiStatus.State == AiState.Ready);
        Assert.Equal("a", CachedModel(tv.Root));
        Assert.Null(Settings.AiModelId);
    }

    [Fact]
    public async Task Switch_ConflictOpenedDuringDownload_NoSwitch()
    {
        var tv = Vault();
        var cut = await OpenShellAsync(tv);
        var old = State.Session!;
        ModelServer.Hang = true;

        SwitchToB(cut);

        await Eventually(() => ModelServer.Requests == 1);
        var path = Path.Combine(tv.Root, JournalPath);
        await cut.InvokeAsync(() =>
        {
            old.Vault.FindPageByPath(path)!.Roots[1].SetContent("Lokal geändert");
            File.WriteAllText(path, "- Vertrauen wächst\n- Extern geändert\n", new UTF8Encoding(false));
            old.HandleExternalChange(path);
        });
        Assert.True(old.HasAnyOpenConflict);

        ModelServer.Hang = false;

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-ai-switch-conflict")));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-ai-cancel")), Wait); // the download has ended
        Assert.Single(cut.FindAll(".ne-ai-switch-conflict"));
        Assert.Empty(cut.FindAll(".ne-ai-model-error"));
        Assert.Equal("a", Ai!.Model.Id);
        Assert.Same(old, State.Session);
        Assert.False(IsDisposed(old));
        Assert.Null(Settings.AiModelId);
    }

    [Fact]
    public async Task Switch_WithoutVault_ActivatesAndSaves()
    {
        UseAi(installed: true, () => new FakeEmbedder(), [ModelA, ModelB]);
        var cut = Render<Shell>();
        Assert.Null(State.Session);

        SwitchToB(cut);

        await Eventually(() => Ai!.Model.Id == "b");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-ai-model-dialog")));
        Assert.True(Ai!.ModelInstalled);
        Assert.False(Directory.Exists(Store!.DirectoryOf(ModelA)));
        Assert.Equal("b", Settings.AiModelId);
        Assert.Equal("b", UiSettings.Load(Platform.UserDataDirectory).AiModelId);
        Assert.Null(State.Session);
        Assert.Single(cut.FindAll(".ne-welcome"));
    }
}
