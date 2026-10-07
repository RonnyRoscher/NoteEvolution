using System.Collections.Concurrent;
using System.Net;
using System.Resources;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NoteEvolution.AI.Embeddings;
using NoteEvolution.AI.Relevance;
using NoteEvolution.AI.Search;
using NoteEvolution.AI.Tests;
using NoteEvolution.Core.Vaults;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Components.Dialogs;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

public class AiTests : UiTestContext
{
    private const string JournalPath = "journals/2026_03_01.md";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly NoteFilter All = new(false, null, null);

    private readonly LogCapture _logs = new();
    private readonly List<TestVault> _vaults = [];

    public AiTests()
    {
        Services.AddSingleton<ILoggerProvider>(_logs);
    }

    protected override void Dispose(bool disposing)
    {
        // The session (and its vector cache in the vault) is closed first, then the vault folders are deleted.
        base.Dispose(disposing);
        if (disposing)
        {
            _vaults.ForEach(v => v.Dispose());
        }
    }

    private TestVault Vault(string firstNote = "Vertrauen wächst langsam")
    {
        var vault = TestVault.Create(
            ("pages/Buch - Alpha.md", "title:: Alpha\ntype:: book\n\n- # Eins\n\t- Erster Text\n"),
            (JournalPath, $"- {firstNote}\n- Gedanke über Wolken\n"),
            ("journals/2026_03_02.md", "- Regen am Morgen\n"));
        _vaults.Add(vault);
        return vault;
    }

    private string Text(string key, params object[] args) =>
        Services.GetRequiredService<IStringLocalizer<Strings>>()[key, args].Value;

    private static async Task Eventually(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "The condition was not met in time.");
            await Task.Delay(10);
        }
    }

    // ---- Opening ----

    [Fact]
    public void Open_WithModel_IndexesInBackground_HeaderShowsProgressThenLocal()
    {
        var tv = Vault();
        using var gate = new ManualResetEventSlim();
        var embedder = new FakeEmbedder { OnEmbed = (_, _) => gate.Wait(Wait) };
        UseAi(installed: true, () => embedder);
        Settings.LastVault = tv.Root;

        try
        {
            var cut = Render<Shell>();

            // The vault is open while the embedder still waits: indexing does not hold up the opening.
            WaitForVaultOpened(cut);
            var total = State.Session!.Notes.All().Count();
            cut.WaitForAssertion(() => Assert.Equal(Text("AiIndexing", 0, total), cut.Find(".ne-ai").TextContent));
            Assert.Empty(cut.FindAll(".ne-ai-model-dialog"));

            gate.Set();

            cut.WaitForAssertion(() => Assert.Equal(Text("AiLocal"), cut.Find(".ne-ai").TextContent));
            Assert.Equal(new AiStatus(AiState.Ready, total, total, null), State.Session.AiStatus);
            Assert.IsType<HybridSearchService>(State.Session.Search);
            Assert.NotNull(State.Session.Semantic);
            Assert.True(State.Session.Relevance.IsAvailable);
            Assert.False(cut.Find(".ne-ai").HasAttribute("title"));
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public async Task Open_ModelMissing_ShowsDialog_NotNowRemembered()
    {
        var tv = Vault();
        var loads = 0;
        UseAi(installed: false, () =>
        {
            loads++;
            return new FakeEmbedder();
        });
        Platform.FolderToPick = tv.Root;
        var cut = Render<Shell>();

        await cut.Find(".ne-open-vault").ClickAsync(new MouseEventArgs());

        WaitForVaultOpened(cut);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-ai-model-dialog")));
        Assert.Equal(Text("AiModelMissing"), cut.Find(".ne-ai").TextContent);
        Assert.Equal(AiState.ModelMissing, State.Session!.AiStatus.State);
        Assert.IsType<FullTextSearchService>(State.Session.Search);
        Assert.Null(State.Session.Semantic);
        Assert.False(State.Session.Relevance.IsAvailable);
        var book = State.CurrentBook!;
        Assert.Empty(State.Session.Relevance.Relevant(new TopicRequest(book, book.Root.Key, null, false), _ => true));
        var someNote = State.Session.Notes.All().First();
        Assert.Empty(State.Session.Relevance.WhereTo(someNote.Key, "passage: x", book));
        Assert.Empty(State.Session.Relevance.Placements(someNote.Key, "passage: x", book));

        cut.Find(".ne-ai-not-now").Click();

        Assert.Empty(cut.FindAll(".ne-ai-model-dialog"));
        Assert.True(Settings.AiModelDeclined);
        Assert.True(UiSettings.Load(Platform.UserDataDirectory).AiModelDeclined);

        // Opened again, the dialog stays away; the settings still offer the download.
        var first = State.Session;
        await cut.Find(".ne-open-vault").ClickAsync(new MouseEventArgs());
        WaitForVaultOpened(cut, first);
        Assert.Empty(cut.FindAll(".ne-ai-model-dialog"));

        cut.Find(".ne-settings").Click();
        Assert.Contains(Text("SettingsAiMissing"), cut.Find(".ne-settings-ai").TextContent);
        cut.Find(".ne-settings-ai-download").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-ai-model-dialog")));
        Assert.Equal(0, loads);
        Assert.Equal(0, ModelServer.Requests);
    }

    [Fact]
    public void Open_ModelMissing_WithLinkCheckReport_DialogFollowsTheLinkCheck()
    {
        var tv = TestVault.Create(
            ("pages/Buch - Alpha.md",
                "title:: Alpha\ntype:: book\n\n- # Eins\n\t- Erster Text\n" +
                $"\t  id:: {Guid.NewGuid()}\n\t  source:: (({Guid.NewGuid()}))\n"),
            (JournalPath, "- Eine Notiz\n"));
        _vaults.Add(tv);
        UseAi(installed: false, () => new FakeEmbedder());
        Settings.LastVault = tv.Root;
        var cut = Render<Shell>();
        WaitForVaultOpened(cut);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-linkcheck-dialog")));
        Assert.Empty(cut.FindAll(".ne-ai-model-dialog"));

        cut.Find(".ne-linkcheck-close").Click();

        Assert.Empty(cut.FindAll(".ne-linkcheck-dialog"));
        Assert.Single(cut.FindAll(".ne-ai-model-dialog"));
    }

    // ---- Download ----

    [Fact]
    public void Download_Completes_StartsIndexing()
    {
        var tv = Vault();
        var embedder = new FakeEmbedder();
        var ai = UseAi(installed: false, () => embedder);
        Settings.LastVault = tv.Root;
        var cut = Render<Shell>();
        WaitForVaultOpened(cut);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-ai-model-dialog")));

        cut.Find(".ne-ai-download").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-ai-model-dialog")));
        Assert.True(ai.ModelInstalled);
        cut.WaitForAssertion(() => Assert.Equal(Text("AiLocal"), cut.Find(".ne-ai").TextContent));
        Assert.Contains(embedder.Texts, t => t.Contains("Gedanke über Wolken", StringComparison.Ordinal));
        Assert.IsType<HybridSearchService>(State.Session!.Search);
        Assert.True(File.Exists(Path.Combine(tv.Root, ".noteevolution", "vectors.db")));
        Assert.False(UiSettings.Load(Platform.UserDataDirectory).AiModelDeclined);
    }

    [Fact]
    public void Download_Cancelled_ShowsProgressThenModelMissing()
    {
        var tv = Vault();
        var ai = UseAi(installed: false, () => new FakeEmbedder());
        ModelServer.Hang = true;
        Settings.LastVault = tv.Root;
        var cut = Render<Shell>();
        WaitForVaultOpened(cut);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-ai-model-dialog")));

        cut.Find(".ne-ai-download").Click();

        cut.WaitForAssertion(() => Assert.Equal(Text("AiDownloading"), cut.Find(".ne-ai").TextContent));
        Assert.Single(cut.FindAll(".ne-ai-progress"));
        Assert.Empty(cut.FindAll(".ne-ai-download"));

        cut.Find(".ne-ai-cancel").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-ai-download")));
        Assert.Empty(cut.FindAll(".ne-ai-progress"));
        Assert.Empty(cut.FindAll(".ne-ai-model-error"));
        cut.WaitForAssertion(() => Assert.Equal(Text("AiModelMissing"), cut.Find(".ne-ai").TextContent));
        Assert.False(ai.ModelInstalled);
    }

    [Fact]
    public void Download_Fails_ShowsError_AppStillUsable()
    {
        var tv = Vault();
        var loads = 0;
        var ai = UseAi(installed: false, () =>
        {
            loads++;
            return new FakeEmbedder();
        });
        ModelServer.Status = HttpStatusCode.InternalServerError;
        Settings.LastVault = tv.Root;
        var cut = Render<Shell>();
        WaitForVaultOpened(cut);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-ai-model-dialog")));

        cut.Find(".ne-ai-download").Click();

        cut.WaitForAssertion(() => Assert.Contains("500", cut.Find(".ne-ai-model-error").TextContent));
        Assert.Contains(Text("AiModelDownloadFailed"), cut.Find(".ne-ai-model-dialog").TextContent);
        Assert.Single(cut.FindAll(".ne-ai-download")); // a retry is possible
        cut.WaitForAssertion(() => Assert.Equal(Text("AiModelMissing"), cut.Find(".ne-ai").TextContent));
        Assert.False(ai.ModelInstalled);

        cut.Find(".ne-ai-not-now").Click();

        // The app goes on without the AI: full-text search, the editor, the notes.
        Assert.Empty(cut.FindAll(".ne-ai-model-dialog"));
        Assert.Single(cut.FindAll(".ne-editor-pane"));
        var session = State.Session!;
        Assert.IsType<FullTextSearchService>(session.Search);
        var hit = Assert.Single(session.Search.Search(new SearchQuery("Wolken", All)));
        Assert.Contains("Wolken", session.Notes.Get(hit.NoteBlockKey)!.Block.Content);
        Assert.Equal(0, loads);
        Assert.Null(session.Semantic);
    }

    // ---- Failures of the model ----

    [Fact]
    public void EmbedderFails_HeaderShowsError_SearchFallsBackToFullText()
    {
        var tv = Vault();
        UseAi(installed: true, () => new FakeEmbedder { Throws = true });
        Settings.LastVault = tv.Root;
        var cut = Render<Shell>();
        WaitForVaultOpened(cut);

        cut.WaitForAssertion(() => Assert.Equal(Text("AiFailed"), cut.Find(".ne-ai").TextContent));
        Assert.Equal(FakeEmbedder.FailureMessage, cut.Find(".ne-ai").GetAttribute("title"));
        var session = State.Session!;
        Assert.Equal(AiState.Failed, session.AiStatus.State);
        Assert.False(session.Relevance.IsAvailable);

        var hit = Assert.Single(session.Search.Search(new SearchQuery("Wolken", All)));
        Assert.Contains("Wolken", session.Notes.Get(hit.NoteBlockKey)!.Block.Content);
        Assert.Empty(cut.FindAll(".ne-ai-model-dialog"));
    }

    [Fact]
    public async Task EmbedderLoadFails_HeaderShowsError_LoadNotRetried()
    {
        var tv = Vault();
        var loads = 0;
        var ai = UseAi(installed: true, () =>
        {
            loads++;
            throw new InvalidOperationException("onnx broken");
        });
        Platform.FolderToPick = tv.Root;
        var cut = Render<Shell>();
        await cut.Find(".ne-open-vault").ClickAsync(new MouseEventArgs());
        WaitForVaultOpened(cut);

        cut.WaitForAssertion(() => Assert.Equal(Text("AiFailed"), cut.Find(".ne-ai").TextContent));
        Assert.Equal("onnx broken", cut.Find(".ne-ai").GetAttribute("title"));
        Assert.Null(ai.Embedder);
        Assert.IsType<FullTextSearchService>(State.Session!.Search);
        Assert.Single(State.Session.Search.Search(new SearchQuery("Wolken", All)));

        var first = State.Session;
        await cut.Find(".ne-open-vault").ClickAsync(new MouseEventArgs());
        WaitForVaultOpened(cut, first);

        cut.WaitForAssertion(() => Assert.Equal(Text("AiFailed"), cut.Find(".ne-ai").TextContent));
        Assert.Equal(1, loads);
        Assert.Empty(cut.FindAll(".ne-ai-model-dialog"));
    }

    // ---- Vault switch ----

    [Fact]
    public async Task VaultSwitchDuringIndexing_OldIndexingCancelled_NoException()
    {
        var first = Vault();
        var second = Vault("Ganz andere Notiz");
        using var entered = new ManualResetEventSlim();
        var blocked = CancellationToken.None;
        var embedder = new FakeEmbedder
        {
            OnEmbed = (call, ct) =>
            {
                if (call != 1)
                {
                    return;
                }

                blocked = ct;
                entered.Set();
                ct.WaitHandle.WaitOne(Wait);
                ct.ThrowIfCancellationRequested();
            },
        };
        UseAi(installed: true, () => embedder);
        Settings.LastVault = first.Root;
        var cut = Render<Shell>();
        WaitForVaultOpened(cut);
        Assert.True(entered.Wait(Wait));
        var old = State.Session!;
        Platform.FolderToPick = second.Root;

        await cut.Find(".ne-open-vault").ClickAsync(new MouseEventArgs());

        WaitForVaultOpened(cut, old);
        Assert.True(blocked.IsCancellationRequested);
        cut.WaitForAssertion(() => Assert.Equal(Text("AiLocal"), cut.Find(".ne-ai").TextContent));
        Assert.Contains(embedder.Texts, t => t.Contains("Ganz andere Notiz", StringComparison.Ordinal));
        await Task.Delay(100); // late events of the old session would surface now
        Assert.DoesNotContain(_logs.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Equal(AiState.Ready, State.Session!.AiStatus.State);
    }

    // ---- Keeping the index current ----

    [Fact]
    public async Task PageReplaced_ReindexesThatPage()
    {
        var tv = Vault();
        var embedder = new FakeEmbedder();
        var ai = UseAi(installed: true, () => embedder);
        var session = await OpenSessionAsync(tv, ai);
        await Eventually(() => session.AiStatus.State == AiState.Ready);
        var before = embedder.Texts.Count;
        var page = session.Vault.FindPageByPath(Path.Combine(tv.Root, JournalPath))!;

        // Two saves within the debounce time: the page is indexed once, with its last text.
        page.Roots[0].SetContent("Vertrauen wächst schnell");
        Assert.True(session.TrySave(page, out _));
        Time.Advance(TimeSpan.FromMilliseconds(1000));
        page = session.Vault.FindPageByPath(page.FilePath)!;
        page.Roots[0].SetContent("Vertrauen wächst sehr schnell");
        Assert.True(session.TrySave(page, out _));
        Time.Advance(TimeSpan.FromMilliseconds(1999));
        await Task.Delay(50);
        Assert.Equal(before, embedder.Texts.Count);

        Time.Advance(TimeSpan.FromMilliseconds(1));

        var note = session.Notes.All().Single(n => n.Block.Content == "Vertrauen wächst sehr schnell");
        var expected = FakeEmbedder.VectorFor(EmbeddingText.ForNote(note));
        await Eventually(() => session.Semantic!.VectorOf(note.Key) is { } v && v.SequenceEqual(expected));
        var added = embedder.Texts.Skip(before).ToList();
        Assert.Equal([EmbeddingText.ForNote(note)], added); // the page's other note and the other pages come from the cache
        Assert.DoesNotContain(embedder.Texts, t => t.Contains("wächst schnell", StringComparison.Ordinal));
        Assert.Equal(AiState.Ready, session.AiStatus.State);
    }

    [Fact]
    public async Task ExternalChange_ReindexesThatPage_RemovedPageLeavesIndex()
    {
        var tv = Vault();
        var embedder = new FakeEmbedder();
        var ai = UseAi(installed: true, () => embedder);
        var session = await OpenSessionAsync(tv, ai);
        await Eventually(() => session.AiStatus.State == AiState.Ready);
        var journal = Path.Combine(tv.Root, JournalPath);
        var rain = session.Notes.All().Single(n => n.Block.Content == "Regen am Morgen");

        File.WriteAllText(journal, "- Von außen geschrieben\n");
        session.HandleExternalChange(journal);
        var other = Path.Combine(tv.Root, "journals", "2026_03_02.md");
        File.Delete(other);
        session.HandleExternalChange(other);
        var note = session.Notes.All().Single(n => n.Block.Content == "Von außen geschrieben");
        Assert.Null(session.Semantic!.VectorOf(note.Key));

        // The vault watcher reports the same changes again whenever it sees them, which restarts the debounce; so the
        // clock moves on until the index has caught up.
        await Eventually(() =>
        {
            Time.Advance(VaultSession.PageIndexDelay);
            return session.Semantic.VectorOf(note.Key) is not null && session.Semantic.VectorOf(rain.Key) is null;
        });
        Assert.Equal(AiState.Ready, session.AiStatus.State);
    }

    // ---- Localization ----

    [Fact]
    public void NoGermanLiterals_InNewComponents()
    {
        Services.AddSingleton<IStringLocalizer<Strings>>(new KeyLocalizer());
        UseAi(installed: false, () => new FakeEmbedder());
        var resources = new ResourceManager(typeof(Strings));

        var header = Render<HeaderBar>();
        Assert.Equal("AiModelMissing", header.Find(".ne-ai").TextContent);

        var settings = Render<SettingsDialog>();
        AssertKeys(settings, resources);
        Assert.Single(settings.FindAll(".ne-settings-ai-download"));

        var dialog = Render<AiModelDialog>();
        AssertKeys(dialog, resources);

        ModelServer.Hang = true;
        dialog.Find(".ne-ai-download").Click();
        dialog.WaitForAssertion(() => Assert.Single(dialog.FindAll(".ne-ai-progress")));
        AssertKeys(dialog, resources);
        header.WaitForAssertion(() => Assert.Equal("AiDownloading", header.Find(".ne-ai").TextContent));

        dialog.Find(".ne-ai-cancel").Click();
        dialog.WaitForAssertion(() => Assert.Single(dialog.FindAll(".ne-ai-download")));
        ModelServer.Hang = false;
        ModelServer.Status = HttpStatusCode.NotFound;
        dialog.Find(".ne-ai-download").Click();
        dialog.WaitForAssertion(() => Assert.Single(dialog.FindAll(".ne-ai-model-error")));
        AssertKeys(dialog, resources);
    }

    private static void AssertKeys<T>(IRenderedComponent<T> cut, ResourceManager resources)
        where T : IComponent
    {
        foreach (var text in UiTexts(cut.Find(".ne-modal")))
        {
            Assert.True(
                !text.Any(char.IsLetter) || resources.GetString(text, System.Globalization.CultureInfo.InvariantCulture) is not null,
                $"'{text}' is neither a resource key nor free of letters");
        }
    }

    /// <summary>Texts of the element and its descendants; the error detail (an exception message) is data.</summary>
    private static IEnumerable<string> UiTexts(IElement root)
    {
        foreach (var element in root.QuerySelectorAll("*").Prepend(root))
        {
            if (element.Closest(".ne-ai-model-error, .ne-settings-folders") is not null)
            {
                continue;
            }

            foreach (var name in new[] { "title", "aria-label", "placeholder" })
            {
                if (element.GetAttribute(name) is { Length: > 0 } value)
                {
                    yield return value;
                }
            }

            foreach (var text in element.ChildNodes.OfType<IText>().Select(t => t.Data.Trim()).Where(t => t.Length > 0))
            {
                yield return text;
            }
        }
    }

    private sealed class KeyLocalizer : IStringLocalizer<Strings>
    {
        public LocalizedString this[string name] => new(name, name);

        public LocalizedString this[string name, params object[] arguments] => new(name, name);

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    /// <summary>Collects every log entry of the app's loggers.</summary>
    private sealed class LogCapture : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, string Category, string Message, Exception? Exception)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(LogCapture owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                owner.Entries.Enqueue((logLevel, category, formatter(state, exception), exception));
        }
    }
}
