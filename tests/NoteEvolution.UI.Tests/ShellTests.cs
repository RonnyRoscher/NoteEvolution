using System.Text.Json.Nodes;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NoteEvolution.Core.Links;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

public class ShellTests : UiTestContext
{
    private static TestVault TwoBooks() => TestVault.Create(
        ("pages/Buch - Alpha.md", "title:: Alpha\ntype:: book\n\n- # Eins\n\t- Erster Text\n"),
        ("pages/Buch - Beta.md", "title:: Beta\ntype:: book\n\n- # Zwei\n"),
        ("journals/2026_03_01.md", "- Eine Notiz\n"));

    private string Text(string key) => Services.GetRequiredService<IStringLocalizer<Strings>>()[key].Value;

    /// <summary>The calls of the host page's splash screen, in order.</summary>
    private List<string> SplashCalls() =>
        [.. JSInterop.Invocations.Where(i => i.Identifier.StartsWith("neSplash.", StringComparison.Ordinal)).Select(i => i.Identifier)];

    [Fact]
    public void Startup_WithLastVault_SplashSaysOpening_EndsWhenTheVaultIsOpen()
    {
        using var tv = TwoBooks();
        Settings.LastVault = tv.Root;

        var cut = Render<Shell>();

        cut.WaitForAssertion(() => Assert.Equal(["neSplash.status", "neSplash.done"], SplashCalls()), TimeSpan.FromSeconds(10));
        Assert.NotNull(State.Session);
        Assert.Equal(Text("VaultOpening"), JSInterop.Invocations["neSplash.status"].Single().Arguments[0]);
    }

    [Fact]
    public void Startup_WithoutVault_SplashEnds_OnlyOnce()
    {
        var cut = Render<Shell>();

        cut.WaitForAssertion(() => Assert.Equal(["neSplash.done"], SplashCalls()), TimeSpan.FromSeconds(10));

        // Later renderings leave the splash screen alone.
        cut.Find(".ne-outline .ne-collapse").Click();
        Assert.Equal(["neSplash.done"], SplashCalls());
    }

    [Fact]
    public void Startup_LastVaultGone_SplashEndsWithoutOpening()
    {
        using var dir = new TempDir();
        Settings.LastVault = Path.Combine(dir.Path, "weg");

        var cut = Render<Shell>();

        cut.WaitForAssertion(() => Assert.Equal(["neSplash.done"], SplashCalls()), TimeSpan.FromSeconds(10));
        Assert.Null(State.Session);
        Assert.Single(cut.FindAll(".ne-welcome"));
    }

    [Fact]
    public void Shell_RendersThreeAreas_WithSavedWidths()
    {
        Settings.OutlineWidth = 300;
        Settings.NotesWidth = 420.5;

        var cut = Render<Shell>();

        Assert.Contains("width: 300px", cut.Find(".ne-outline").GetAttribute("style"));
        Assert.Contains("width: 420.5px", cut.Find(".ne-notes").GetAttribute("style"));
        Assert.Single(cut.FindAll(".ne-editor"));
        Assert.Equal(2, cut.FindAll(".ne-splitter").Count);
    }

    [Fact]
    public void Splitter_DragLeft_ResizesOutlineAndSavesWidth()
    {
        var cut = Render<Shell>();

        cut.FindAll(".ne-splitter")[0].PointerDown(new PointerEventArgs { ClientX = 260 });
        cut.Find(".ne-splitter-overlay").PointerMove(new PointerEventArgs { ClientX = 330 });
        cut.Find(".ne-splitter-overlay").PointerUp(new PointerEventArgs { ClientX = 330 });

        Assert.Contains("width: 330px", cut.Find(".ne-outline").GetAttribute("style"));
        Assert.Empty(cut.FindAll(".ne-splitter-overlay"));
        Assert.Equal(330, UiSettings.Load(Platform.UserDataDirectory).OutlineWidth);
    }

    [Fact]
    public void Splitter_DragRight_NarrowsNotes_ClampedToMinimum()
    {
        var cut = Render<Shell>();

        cut.FindAll(".ne-splitter")[1].PointerDown(new PointerEventArgs { ClientX = 1000 });
        cut.Find(".ne-splitter-overlay").PointerMove(new PointerEventArgs { ClientX = 950 });
        Assert.Contains("width: 430px", cut.Find(".ne-notes").GetAttribute("style"));

        cut.Find(".ne-splitter-overlay").PointerMove(new PointerEventArgs { ClientX = 2000 });
        cut.Find(".ne-splitter-overlay").PointerUp(new PointerEventArgs { ClientX = 2000 });

        Assert.Equal(Splitter.MinWidth, UiSettings.Load(Platform.UserDataDirectory).NotesWidth);
    }

    [Fact]
    public void Splitter_DragNotesLeft_NoFixedMaximum_EditorKeeps200Px()
    {
        Settings.OutlineWidth = 260;
        Settings.NotesWidth = 380;
        var cut = Render<Shell>();

        // Beyond the former 800 px: the notes pane widens until the editor would get narrower than 200 px, i.e. until
        // the pointer is 260 + 200 px from the left.
        cut.FindAll(".ne-splitter")[1].PointerDown(new PointerEventArgs { ClientX = 1000 });
        cut.Find(".ne-splitter-overlay").PointerMove(new PointerEventArgs { ClientX = 500 });
        Assert.Contains("width: 880px", cut.Find(".ne-notes").GetAttribute("style"));

        cut.Find(".ne-splitter-overlay").PointerMove(new PointerEventArgs { ClientX = 100 });
        cut.Find(".ne-splitter-overlay").PointerUp(new PointerEventArgs { ClientX = 100 });

        Assert.Contains("width: 920px", cut.Find(".ne-notes").GetAttribute("style"));
        Assert.Equal(920, UiSettings.Load(Platform.UserDataDirectory).NotesWidth);
    }

    [Fact]
    public void Splitter_DragNotesLeft_CollapsedOutline_LeavesOnlyTheEditorsMinimum()
    {
        Settings.OutlineCollapsed = true;
        Settings.NotesWidth = 380;
        var cut = Render<Shell>();

        cut.FindAll(".ne-splitter")[0].PointerDown(new PointerEventArgs { ClientX = 1000 });
        cut.Find(".ne-splitter-overlay").PointerUp(new PointerEventArgs { ClientX = 0 });

        // The collapsed outline is 32 px wide: the pointer may go to 32 + 200 px.
        Assert.Contains("width: 1148px", cut.Find(".ne-notes").GetAttribute("style"));
    }

    [Fact]
    public void Splitter_NotesShownNarrowerThanStored_DragStartsFromWhatIsShown()
    {
        // The pane was widened to 920 px, then the window got narrower and the pane shows only 530 px.
        Settings.NotesWidth = 920;
        JSInterop.Setup<double>("neLayout.paneWidth", _ => true).SetResult(530);
        var cut = Render<Shell>();

        cut.FindAll(".ne-splitter")[1].PointerDown(new PointerEventArgs { ClientX = 470 });
        cut.Find(".ne-splitter-overlay").PointerUp(new PointerEventArgs { ClientX = 460 });

        // 10 px to the left widen what is shown by 10 px (not the stored 920).
        Assert.Contains("width: 540px", cut.Find(".ne-notes").GetAttribute("style"));
    }

    [Fact]
    public void Splitter_DragOutlineRight_StillAtMost800()
    {
        var cut = Render<Shell>();

        cut.FindAll(".ne-splitter")[0].PointerDown(new PointerEventArgs { ClientX = 260 });
        cut.Find(".ne-splitter-overlay").PointerUp(new PointerEventArgs { ClientX = 2000 });

        Assert.Contains("width: 800px", cut.Find(".ne-outline").GetAttribute("style"));
    }

    [Fact]
    public void CollapseOutline_HidesPaneAndSplitter_AndIsSaved()
    {
        var cut = Render<Shell>();

        cut.Find(".ne-outline .ne-collapse").Click();

        Assert.Contains("collapsed", cut.Find(".ne-outline").ClassList);
        Assert.Single(cut.FindAll(".ne-splitter"));
        Assert.True(UiSettings.Load(Platform.UserDataDirectory).OutlineCollapsed);

        cut.Find(".ne-outline .ne-collapse").Click();

        Assert.DoesNotContain("collapsed", cut.Find(".ne-outline").ClassList);
        Assert.False(UiSettings.Load(Platform.UserDataDirectory).OutlineCollapsed);
    }

    [Fact]
    public void Theme_AndTextSettings_AreSetOnTheShell()
    {
        Settings.FontSizePt = 14;
        Settings.LineWidthCh = 80;
        var cut = Render<Shell>();

        Assert.False(cut.Find(".ne-shell").HasAttribute("data-theme"));
        Assert.Contains("--font-size: 14pt", cut.Find(".ne-shell").GetAttribute("style"));
        Assert.Contains("--line-width: 80ch", cut.Find(".ne-shell").GetAttribute("style"));

        Settings.Theme = ThemeChoice.Dark;
        cut.Render();

        Assert.Equal("dark", cut.Find(".ne-shell").GetAttribute("data-theme"));
    }

    [Fact]
    public async Task OpenVault_PickedFolder_ShowsBooks_SavesLastVault_AndLogseqHintOnce()
    {
        using var tv = TwoBooks();
        Platform.FolderToPick = tv.Root;
        var cut = Render<Shell>();

        await cut.Find(".ne-open-vault").ClickAsync(new MouseEventArgs());

        WaitForVaultOpened(cut);
        Assert.Equal("Alpha", State.CurrentBook!.Title);
        Assert.Equal(State.CurrentBook.Root.Key, State.CurrentSectionKey);
        cut.WaitForAssertion(() => Assert.Equal(["Alpha", "Beta"], cut.FindAll(".ne-book-select .ne-book-option").Select(o => o.TextContent)));
        Assert.Equal(tv.Root, UiSettings.Load(Platform.UserDataDirectory).LastVault);

        cut.Find(".ne-hint-ok").Click();

        Assert.Empty(cut.FindAll(".ne-hint"));
        Assert.True(UiSettings.Load(Platform.UserDataDirectory).LogseqHintShown);
    }

    [Fact]
    public void Startup_OpensLastVault_NoHintWhenAlreadyShown()
    {
        using var tv = TwoBooks();
        Settings.LastVault = tv.Root;
        Settings.LogseqHintShown = true;

        var cut = Render<Shell>();

        WaitForVaultOpened(cut);
        Assert.Equal("Alpha", State.CurrentBook!.Title);
        Assert.Empty(cut.FindAll(".ne-hint"));
    }

    [Fact]
    public async Task OpenVault_Fails_ShowsErrorAndKeepsNoSession()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "datei"), "x");
        Platform.FolderToPick = Path.Combine(dir.Path, "datei");
        var cut = Render<Shell>();

        await cut.Find(".ne-open-vault").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-error")));
        Assert.Null(State.Session);
    }

    [Fact]
    public async Task OpenVault_AnotherVault_SavesTheEditorsPendingTextFirst()
    {
        const string bookPath = "pages/Buch - Alpha.md";
        using var first = TestVault.Create((bookPath, "title:: Alpha\ntype:: book\n\n- Vorspann\n- # Eins\n"));
        using var second = TwoBooks();
        Platform.FolderToPick = first.Root;
        var cut = Render<Shell>();
        await cut.Find(".ne-open-vault").ClickAsync(new MouseEventArgs());
        WaitForVaultOpened(cut);
        cut.WaitForAssertion(() => Assert.Contains("Vorspann", Editor.Json));
        var doc = JsonNode.Parse(Editor.Json)!;
        doc["content"]![0]!["content"]![0]!["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Vorspann, gerade getippt" });
        await cut.InvokeAsync(() => Editor.Callbacks!.OnDocumentChanged(doc.ToJsonString()));
        var firstSession = State.Session;
        var editorFlush = State.FlushEditor!;
        var flushedWhileShown = new List<bool>();
        State.FlushEditor = async () =>
        {
            // The editor is flushed while it still shows the first vault.
            flushedWhileShown.Add(ReferenceEquals(State.Session, firstSession) && cut.FindAll(".ne-editor-pane").Count == 1);
            await editorFlush();
        };

        Platform.FolderToPick = second.Root;
        await cut.Find(".ne-open-vault").ClickAsync(new MouseEventArgs());

        WaitForVaultOpened(cut, firstSession);
        Assert.Equal(second.Root, State.Session!.Vault.Root);
        Assert.Equal([true], flushedWhileShown);
        Assert.Equal(
            "title:: Alpha\ntype:: book\n\n- Vorspann, gerade getippt\n  updated:: " + Stamp + "\n- # Eins\n", first.Read(bookPath));
    }

    [Fact]
    public async Task HeaderBar_ShowsBooks_SelectionSetsCurrentBook()
    {
        using var tv = TwoBooks();
        var session = await OpenSessionAsync(tv);
        var changed = 0;
        State.Changed += () => changed++;

        var cut = Render<HeaderBar>();

        Assert.Equal(["Alpha", "Beta"], cut.FindAll(".ne-book-select .ne-book-option").Select(o => o.TextContent));
        var beta = session.Vault.Books.Single(b => b.Title == "Beta");

        cut.Find(".ne-book-select").Change(beta.LinkName);

        Assert.Same(beta, State.CurrentBook);
        Assert.Equal(beta.Root.Key, State.CurrentSectionKey);
        Assert.True(changed > 0);
    }

    [Fact]
    public void HeaderBar_ShowsAiOff()
    {
        var cut = Render<HeaderBar>();

        Assert.Equal("KI: aus", cut.Find(".ne-ai").TextContent.Trim());
    }

    [Fact]
    public async Task HeaderBar_ReadOnlyPages_WarningListsFileNameAndParseError()
    {
        using var tv = TwoBooks();
        File.WriteAllBytes(Path.Combine(tv.Root, "pages", "Kaputt.md"), [0x2D, 0x20, 0xFF, 0xFE, 0x0A]);
        var session = await OpenSessionAsync(tv);
        var broken = session.Vault.Pages.Single(p => p.IsReadOnly);

        var cut = Render<HeaderBar>();
        Assert.Empty(cut.FindAll(".ne-readonly-list"));

        cut.Find(".ne-readonly-toggle").Click();

        var item = Assert.Single(cut.FindAll(".ne-readonly-list li"));
        Assert.Equal("Kaputt.md", item.QuerySelector(".ne-readonly-file")!.TextContent);
        Assert.Equal(broken.ParseError, item.QuerySelector(".ne-readonly-error")!.TextContent);
    }

    [Fact]
    public async Task HeaderBar_NoReadOnlyPages_NoWarning()
    {
        using var tv = TwoBooks();
        await OpenSessionAsync(tv);

        var cut = Render<HeaderBar>();

        Assert.Empty(cut.FindAll(".ne-readonly-toggle"));
    }

    [Fact]
    public async Task HeaderBar_Undo_TooltipIsNextDescription_ClickUndoes()
    {
        using var tv = TwoBooks();
        var session = await OpenSessionAsync(tv);
        var cut = Render<HeaderBar>();
        Assert.True(cut.Find(".ne-undo").HasAttribute("disabled"));

        var action = new RecordingUndo("Übernehmen");
        session.Undo.Push(action);

        cut.WaitForAssertion(() => Assert.Equal("Übernehmen", cut.Find(".ne-undo").GetAttribute("title")));
        Assert.False(cut.Find(".ne-undo").HasAttribute("disabled"));

        cut.Find(".ne-undo").Click();

        Assert.True(action.Undone);
        cut.WaitForAssertion(() => Assert.True(cut.Find(".ne-undo").HasAttribute("disabled")));
    }

    [Fact]
    public async Task HeaderBar_Undo_FlushesTheEditorFirst()
    {
        using var tv = TwoBooks();
        var session = await OpenSessionAsync(tv);
        var action = new RecordingUndo("Löschen");
        session.Undo.Push(action);
        bool? undoneBeforeFlush = null;
        State.FlushEditor = () =>
        {
            undoneBeforeFlush = action.Undone;
            return Task.CompletedTask;
        };
        var cut = Render<HeaderBar>();

        cut.Find(".ne-undo").Click();

        Assert.False(undoneBeforeFlush);
        Assert.True(action.Undone);
    }

    [Fact]
    public async Task HeaderBar_Undo_DisabledWhileConflictOpen()
    {
        using var tv = TwoBooks();
        var session = await OpenSessionAsync(tv);
        var journal = Path.Combine(tv.Root, "journals", "2026_03_01.md");
        var page = session.Vault.FindPageByPath(journal)!;
        page.Roots[0].SetContent("Lokal geändert");
        var action = new RecordingUndo("Übernehmen");
        session.Undo.Push(action);
        Core.Storage.ExternalChangeOutcome.Conflict? conflict = null;
        session.ConflictDetected += c => conflict = c;
        var cut = Render<HeaderBar>();
        Assert.False(cut.Find(".ne-undo").HasAttribute("disabled"));

        File.Delete(journal);
        await cut.InvokeAsync(() => session.HandleExternalChange(journal));

        cut.WaitForAssertion(() => Assert.True(cut.Find(".ne-undo").HasAttribute("disabled")));
        Assert.False(File.Exists(journal));

        await cut.InvokeAsync(() => session.ResolveConflict(
            conflict!, conflict!.Conflicts.ToDictionary(c => c.Local.Key, _ => Core.Storage.ConflictChoice.Theirs)));

        cut.WaitForAssertion(() => Assert.False(cut.Find(".ne-undo").HasAttribute("disabled")));
        Assert.False(action.Undone);
    }

    [Fact]
    public async Task HeaderBar_Undo_Refused_ShowsTheFailure_UntilTheNextAction()
    {
        using var tv = TwoBooks();
        var session = await OpenSessionAsync(tv);
        session.Undo.Push(new RefusingUndo());
        var cut = Render<HeaderBar>();
        Assert.Empty(cut.FindAll(".ne-undo-error"));

        cut.Find(".ne-undo").Click();

        cut.WaitForAssertion(() => Assert.Equal(
            "Die Aktion konnte nicht rückgängig gemacht werden.", cut.Find(".ne-undo-error").TextContent));

        session.Undo.Push(new RecordingUndo("Übernehmen"));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-undo-error")));
    }

    private sealed class RefusingUndo : IUndoAction
    {
        public string Description => "Löschen";

        public void Undo() => throw new InvalidOperationException("Der gelöschte Textblock ist schon wieder im Buch.");
    }

    private sealed class RecordingUndo(string description) : IUndoAction
    {
        public bool Undone { get; private set; }

        public string Description => description;

        public void Undo() => Undone = true;
    }
}
