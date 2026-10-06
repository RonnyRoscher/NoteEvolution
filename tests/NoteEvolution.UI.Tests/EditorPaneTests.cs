using System.Text;
using System.Text.Json.Nodes;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

public class EditorPaneTests : UiTestContext
{
    internal const string BookPath = "pages/Buch - Alpha.md";

    internal const string NotesPath = "pages/Ideen.md";

    internal const string FirstId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";

    internal const string NoteId = "11111111-1111-4111-8111-111111111111";

    internal static TestVault Alpha() => TestVault.Create(
        (BookPath,
            "title:: Alpha\ntype:: book\n\n" +
            "- # Eins\n" +
            "\t- Erster Text\n" +
            "\t  id:: " + FirstId + "\n" +
            "\t  source:: ((" + NoteId + "))\n" +
            "\t- Zweiter Text\n" +
            "- # Zwei\n" +
            "\t- Dritter Text\n"),
        (NotesPath,
            "- Gedächtnis wird durch Wiederholung stabil\n" +
            "  id:: " + NoteId + "\n" +
            "  used-in:: [[Buch - Alpha]] ((" + FirstId + "))\n" +
            "- Gedächtnis braucht Schlaf\n"));

    private OutlineNode Section(string title) =>
        State.Session!.Vault.FindBook("Buch - Alpha")!.Root.Children.First(n => n.Title == title);

    /// <summary>Opens the vault, selects <paramref name="section"/> and renders the editor (section view unless told otherwise).</summary>
    private async Task<(VaultSession Session, IRenderedComponent<EditorPane> Cut)> RenderAsync(
        TestVault tv, string section = "Eins", ViewMode mode = ViewMode.Section)
    {
        var session = await OpenSessionAsync(tv);
        State.CurrentSectionKey = Section(section).Key;
        State.Mode = mode;
        var cut = Render<EditorPane>();
        cut.WaitForAssertion(() => Assert.NotEmpty(Editor.Documents));
        return (session, cut);
    }

    private static JsonNode Doc(string json) => JsonNode.Parse(json)!;

    private static JsonArray Blocks(JsonNode doc) => doc["content"]!.AsArray();

    /// <summary>Replaces the own text of the text block at <paramref name="index"/> (counting text blocks only).</summary>
    private static void SetText(JsonNode doc, int index, string text)
    {
        var block = Blocks(doc).Where(n => (string?)n!["type"] == "textBlock").ElementAt(index)!;
        block["content"]![0]!["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
    }

    /// <summary>
    /// Replaces the runs of paragraph <paramref name="para"/> (0 = the block's own text) of the text block at
    /// <paramref name="index"/>; the lines are joined with hard breaks.
    /// </summary>
    private static void SetPara(JsonNode doc, int index, int para, params string[] lines)
    {
        var block = Blocks(doc).Where(n => (string?)n!["type"] == "textBlock").ElementAt(index)!;
        var runs = new JsonArray();
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                runs.Add(new JsonObject { ["type"] = "hardBreak" });
            }

            if (lines[i].Length > 0)
            {
                runs.Add(new JsonObject { ["type"] = "text", ["text"] = lines[i] });
            }
        }

        block["content"]![para]!["content"] = runs;
    }

    private static string TextsOf(string json) =>
        string.Join("|", Blocks(Doc(json)).Where(n => (string?)n!["type"] == "textBlock")
            .Select(n => string.Concat((n!["content"]![0]!["content"]?.AsArray() ?? []).Select(t => (string?)t!["text"]))));

    private Task ChangeAsync(IRenderedComponent<EditorPane> cut, JsonNode doc) =>
        cut.InvokeAsync(() => Editor.Callbacks!.OnDocumentChanged(doc.ToJsonString()));

    private string Text(string key) => Services.GetRequiredService<IStringLocalizer<Strings>>()[key].Value;

    [Fact]
    public async Task Pane_ShowsSectionWithChips()
    {
        using var tv = Alpha();
        var (_, cut) = await RenderAsync(tv);

        var (json, manuscript, showChips) = Editor.Documents.Single();
        Assert.False(manuscript);
        Assert.False(showChips);
        Assert.Equal("Erster Text|Zweiter Text", TextsOf(json));
        var chip = Blocks(Doc(json))[0]!["attrs"]!["sources"]![0]!;
        Assert.Equal(NoteId, (string?)chip["id"]);
        Assert.StartsWith("Ideen – Gedächtnis wird", (string?)chip["label"]);
        Assert.Null(cut.Find(".ne-editor-host").GetAttribute("inert"));
        Assert.NotNull(State.FlushEditor);
    }

    [Fact]
    public async Task Pane_EditThenDebounce_SavesOnceAfter1000ms()
    {
        using var tv = Alpha();
        var (_, cut) = await RenderAsync(tv);
        var before = tv.Read(BookPath);
        var doc = Doc(Editor.Json);

        SetText(doc, 1, "Zweiter Tex");
        await ChangeAsync(cut, doc);
        Time.Advance(TimeSpan.FromMilliseconds(500));
        SetText(doc, 1, "Zweiter Text, geändert");
        await ChangeAsync(cut, doc);
        Time.Advance(TimeSpan.FromMilliseconds(999));

        // 1499 ms after the first change: neither change written yet.
        Assert.Equal(before, tv.Read(BookPath));

        Time.Advance(TimeSpan.FromMilliseconds(1));

        cut.WaitForAssertion(() => Assert.Contains("\t- Zweiter Text, geändert\n", tv.Read(BookPath)));
        var (removed, added) = LineDiff.Changed(Encoding.UTF8.GetBytes(before), Encoding.UTF8.GetBytes(tv.Read(BookPath)));
        Assert.Single(removed);
        Assert.Single(added);
        Assert.Single(Editor.Documents); // the editor is not reloaded after its own save
        Assert.False(State.CurrentBook!.Page.IsDirty);
        Assert.Equal("Zweiter Text, geändert", State.CurrentBook.FindNode(Section("Eins").Key)!.TextBlocks.Last().Text);
    }

    [Fact]
    public async Task Pane_TypedTrailingSpaces_SavedAsTyped_EditorNotReloaded()
    {
        using var tv = TestVault.Create(
            (BookPath, "title:: Alpha\ntype:: book\n\n- # Eins\n\t- Text\n\t\t- Absatz eins\n\t\t- Absatz zwei #notiz\n"));
        var (_, cut) = await RenderAsync(tv);
        var doc = Doc(Editor.Json);
        SetPara(doc, 0, 1, "Absatz eins ");
        SetPara(doc, 0, 2, "Absatz zwei ");

        await ChangeAsync(cut, doc);
        Time.Advance(TimeSpan.FromMilliseconds(1000));

        cut.WaitForAssertion(() => Assert.Contains("\t\t- Absatz eins \n\t\t- Absatz zwei  #notiz\n", tv.Read(BookPath)));
        await cut.InvokeAsync(() => State.FlushEditor!()); // waits for the autosave's own sync to finish
        Assert.Single(Editor.Documents);
    }

    [Fact]
    public async Task Pane_OwnSaveNormalizesText_EditorNotReloaded_LaterChangeStillShown()
    {
        using var tv = Alpha();
        var (_, cut) = await RenderAsync(tv);
        var doc = Doc(Editor.Json);
        SetPara(doc, 1, 0, "Zweiter Text", ""); // a trailing hard break, which the file does not keep

        await ChangeAsync(cut, doc);
        await cut.InvokeAsync(() => State.FlushEditor!());
        await cut.InvokeAsync(() => State.FlushEditor!());

        Assert.Single(Editor.Documents);

        // A change from elsewhere is still shown.
        var path = Path.Combine(tv.Root, BookPath);
        File.WriteAllText(path, tv.Read(BookPath).Replace("\t- Zweiter Text\n", "\t- Zweiter Text, extern\n"));
        await cut.InvokeAsync(() => State.Session!.HandleExternalChange(path));

        cut.WaitForAssertion(() => Assert.Equal("Erster Text|Zweiter Text, extern", TextsOf(Editor.Json)));
    }

    [Fact]
    public async Task Pane_LinkedBlockBackAfterAutosavedDelete_NoteHasUsageAgain()
    {
        using var tv = Alpha();
        var (session, cut) = await RenderAsync(tv);
        var original = Doc(Editor.Json);
        var deleted = original.DeepClone();
        Blocks(deleted).RemoveAt(0);
        await ChangeAsync(cut, deleted);
        await cut.InvokeAsync(() => State.FlushEditor!());
        Assert.DoesNotContain("used-in::", tv.Read(NotesPath));

        // Ctrl+Z in the editor: the block is back with its old key and sources.
        await ChangeAsync(cut, original);
        await cut.InvokeAsync(() => State.FlushEditor!());

        var block = Section("Eins").TextBlocks.First();
        Assert.Equal("Erster Text", block.Text);
        Assert.Equal([Guid.Parse(NoteId)], block.Sources);
        var newId = block.Block.Id!.Value;
        Assert.NotEqual(Guid.Parse(FirstId), newId);
        Assert.Contains("used-in:: [[Buch - Alpha]] ((" + newId.ToString("D") + "))\n", tv.Read(NotesPath));
        Assert.Contains("\t- Erster Text\n\t  id:: " + newId.ToString("D") + "\n\t  source:: ((" + NoteId + "))\n", tv.Read(BookPath));
        Assert.True(session.Undo.CanUndo);
    }

    [Fact]
    public async Task Pane_DropNote_AdoptsAfterBlock_ReloadsDocument()
    {
        using var tv = Alpha();
        var (session, cut) = await RenderAsync(tv);
        var note = session.Notes.All().Single(n => n.Block.Content == "Gedächtnis braucht Schlaf");
        var first = Section("Eins").TextBlocks.First().Key;

        await cut.InvokeAsync(() => Editor.Callbacks!.OnNoteDropped(note.Key, first));

        Assert.Equal(["Erster Text", "Gedächtnis braucht Schlaf", "Zweiter Text"], Section("Eins").TextBlocks.Select(t => t.Text));
        Assert.Contains("used-in:: [[Buch - Alpha]]", tv.Read(NotesPath).Split("- Gedächtnis braucht Schlaf")[1]);
        Assert.Equal(2, Editor.Documents.Count);
        Assert.Equal("Erster Text|Gedächtnis braucht Schlaf|Zweiter Text", TextsOf(Editor.Json));
    }

    [Fact]
    public async Task Pane_DropOnHeading_AdoptsAtThatSectionsStart()
    {
        using var tv = Alpha();
        var (session, cut) = await RenderAsync(tv, mode: ViewMode.Manuscript);
        State.CurrentSectionKey = Guid.Empty;
        await cut.InvokeAsync(State.Notify);
        var note = session.Notes.All().Single(n => n.Block.Content == "Gedächtnis braucht Schlaf");

        await cut.InvokeAsync(() => Editor.Callbacks!.OnNoteDropped(note.Key, Section("Zwei").Key));

        Assert.Equal(["Gedächtnis braucht Schlaf", "Dritter Text"], Section("Zwei").TextBlocks.Select(t => t.Text));
    }

    [Fact]
    public async Task Pane_ChipRemove_FlushesThenRemovesSource()
    {
        using var tv = Alpha();
        var (_, cut) = await RenderAsync(tv);
        var doc = Doc(Editor.Json);
        SetText(doc, 0, "Erster Text, neu");
        await ChangeAsync(cut, doc);

        await cut.InvokeAsync(() => Editor.Callbacks!.OnChipRemoved(Section("Eins").TextBlocks.First().Key, Guid.Parse(NoteId)));

        var book = tv.Read(BookPath);
        Assert.Contains("\t- Erster Text, neu\n\t  id:: " + FirstId + "\n\t- Zweiter Text\n", book);
        Assert.DoesNotContain("source::", book);
        Assert.DoesNotContain("used-in::", tv.Read(NotesPath));
        Assert.Empty(Blocks(Doc(Editor.Json))[0]!["attrs"]!["sources"]!.AsArray());
    }

    [Fact]
    public async Task Pane_SplitFromJs_AddsUsageToNote()
    {
        using var tv = Alpha();
        var (_, cut) = await RenderAsync(tv);
        var doc = Doc(Editor.Json);
        var first = Blocks(doc)[0]!;
        var split = first.DeepClone();
        var newKey = Guid.CreateVersion7().ToString("D");
        split["attrs"]!["key"] = newKey;
        split["attrs"]!["splitFrom"] = first["attrs"]!["key"]!.GetValue<string>();
        Blocks(doc).Insert(1, split);
        SetText(doc, 0, "Erster");
        SetText(doc, 1, "Text");
        await ChangeAsync(cut, doc);

        await cut.InvokeAsync(() => State.FlushEditor!());

        var section = Section("Eins");
        Assert.Equal(["Erster", "Text", "Zweiter Text"], section.TextBlocks.Select(t => t.Text));
        var newId = section.TextBlocks.ElementAt(1).Block.Id!.Value;
        Assert.Equal([Guid.Parse(NoteId)], section.TextBlocks.ElementAt(1).Sources);
        var notes = tv.Read(NotesPath);
        Assert.Contains("((" + FirstId + "))", notes);
        Assert.Contains("((" + newId.ToString("D") + "))", notes);
    }

    [Fact]
    public async Task Pane_JoinedBlocks_DeleteTheSecondAndRemoveItsUsage()
    {
        using var tv = TestVault.Create(
            (BookPath, "title:: Alpha\ntype:: book\n\n- # Eins\n\t- Vorher\n\t- Erster Text\n\t  id:: " + FirstId + "\n\t  source:: ((" + NoteId + "))\n"),
            (NotesPath, "- Gedächtnis\n  id:: " + NoteId + "\n  used-in:: [[Buch - Alpha]] ((" + FirstId + "))\n"));
        var (session, cut) = await RenderAsync(tv);
        var doc = Doc(Editor.Json);

        // Backspace at the start of the second block: its paragraph moves into the first (R29).
        var second = Blocks(doc)[1]!;
        var para = second["content"]![0]!.DeepClone();
        para["attrs"]!["key"] = Guid.CreateVersion7().ToString("D");
        para["attrs"]!["depth"] = 1;
        Blocks(doc)[0]!["content"]!.AsArray().Add(para);
        Blocks(doc).RemoveAt(1);
        await ChangeAsync(cut, doc);
        await cut.InvokeAsync(() => State.FlushEditor!());

        Assert.Equal("title:: Alpha\ntype:: book\n\n- # Eins\n\t- Vorher\n\t\t- Erster Text\n", tv.Read(BookPath));
        Assert.DoesNotContain("used-in::", tv.Read(NotesPath));
        Assert.True(session.Undo.CanUndo);
    }

    [Fact]
    public async Task Pane_SectionSwitch_SavesPendingTextToTheOldSection()
    {
        using var tv = Alpha();
        var (_, cut) = await RenderAsync(tv);
        var doc = Doc(Editor.Json);
        SetText(doc, 1, "Zweiter Text, noch nicht gespeichert");
        await ChangeAsync(cut, doc);

        State.CurrentSectionKey = Section("Zwei").Key;
        await cut.InvokeAsync(State.Notify);

        Assert.Contains("\t- Zweiter Text, noch nicht gespeichert\n- # Zwei\n\t- Dritter Text\n", tv.Read(BookPath));
        Assert.Equal("Dritter Text", TextsOf(Editor.Json));
    }

    [Fact]
    public async Task Pane_ManuscriptView_ShowsHeadingsAndChipToggle()
    {
        using var tv = Alpha();
        var (_, cut) = await RenderAsync(tv, mode: ViewMode.Manuscript);
        State.CurrentSectionKey = Guid.Empty;
        await cut.InvokeAsync(State.Notify);

        var (json, manuscript, showChips) = Editor.Documents[^1];
        Assert.True(manuscript);
        Assert.False(showChips);
        Assert.Equal(["heading", "textBlock", "textBlock", "heading", "textBlock"], Blocks(Doc(json)).Select(n => (string?)n!["type"]));

        cut.Find(".ne-editor-chips input").Change(true);

        cut.WaitForAssertion(() => Assert.True(Editor.Documents[^1].ShowChips));
    }

    [Fact]
    public async Task Pane_ExternalChangeWithPendingText_MergesBoth()
    {
        using var tv = Alpha();
        var (session, cut) = await RenderAsync(tv);
        var doc = Doc(Editor.Json);
        SetText(doc, 0, "Erster Text, lokal");
        await ChangeAsync(cut, doc);
        var path = Path.Combine(tv.Root, BookPath);
        File.WriteAllText(path, tv.Read(BookPath).Replace("\t- Zweiter Text\n", "\t- Zweiter Text, extern\n"));

        await cut.InvokeAsync(() => session.HandleExternalChange(path));

        var book = tv.Read(BookPath);
        Assert.Contains("\t- Erster Text, lokal\n", book);
        Assert.Contains("\t- Zweiter Text, extern\n", book);
        cut.WaitForAssertion(() => Assert.Equal("Erster Text, lokal|Zweiter Text, extern", TextsOf(Editor.Json)));
    }

    [Fact]
    public async Task Pane_ExternalDeleteWithPendingText_OpensConflict_FileNotRecreated()
    {
        using var tv = Alpha();
        var (session, cut) = await RenderAsync(tv);
        var doc = Doc(Editor.Json);
        SetText(doc, 0, "Erster Text, lokal");
        await ChangeAsync(cut, doc);
        var path = Path.Combine(tv.Root, BookPath);
        File.Delete(path);

        await cut.InvokeAsync(() => session.HandleExternalChange(path));
        await cut.InvokeAsync(() => State.FlushEditor!());

        Assert.True(session.HasOpenConflict(path));
        Assert.False(File.Exists(path));
        // The local page with the text stays in the vault for the conflict's resolution.
        var local = Book.Load(session.Vault.FindBook("Buch - Alpha")!.Page);
        Assert.Equal("Erster Text, lokal", local.FindNode(Section("Eins").Key)!.TextBlocks.First().Text);
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".ne-editor-host").GetAttribute("inert")));
        Assert.Equal(Text("EditorConflict"), cut.Find(".ne-editor-hint").TextContent);
    }

    [Fact]
    public async Task Pane_ExternalChangeMakesFileUnparseable_WithPendingText_OpensConflict_FileUntouched()
    {
        using var tv = Alpha();
        var (session, cut) = await RenderAsync(tv);
        var doc = Doc(Editor.Json);
        SetText(doc, 0, "Erster Text, lokal");
        await ChangeAsync(cut, doc);
        var path = Path.Combine(tv.Root, BookPath);
        var broken = tv.Read(BookPath) + "\t- Code\n\t  ```\n\t  offen\n";
        File.WriteAllText(path, broken);

        await cut.InvokeAsync(() => session.HandleExternalChange(path));
        await cut.InvokeAsync(() => State.FlushEditor!());

        Assert.True(session.HasOpenConflict(path));
        Assert.Equal(broken, tv.Read(BookPath));
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".ne-editor-host").GetAttribute("inert")));
    }

    [Fact]
    public async Task Pane_Dispose_SavesPendingText()
    {
        using var tv = Alpha();
        var (_, cut) = await RenderAsync(tv);
        var doc = Doc(Editor.Json);
        SetText(doc, 1, "Zweiter Text, kurz vor dem Schließen");
        await ChangeAsync(cut, doc);

        await DisposeComponentsAsync();

        Assert.Contains("\t- Zweiter Text, kurz vor dem Schließen\n", tv.Read(BookPath));
    }

    [Fact]
    public async Task Pane_TextThatCannotBeSaved_WarnsBeforeTheWindowCloses()
    {
        using var tv = Alpha();
        var (_, cut) = await RenderAsync(tv);
        var warned = true;
        await cut.InvokeAsync(() => warned = State.WarnUnsavedText!());
        Assert.False(warned);
        var path = Path.Combine(tv.Root, BookPath);
        var doc = Doc(Editor.Json);
        SetText(doc, 1, "Zweiter Text, nicht speicherbar");
        await ChangeAsync(cut, doc);

        // A read-only file cannot be replaced: the write fails after its retries and the text stays pending.
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            await cut.InvokeAsync(() => State.FlushEditor!());
            await cut.InvokeAsync(() => warned = State.WarnUnsavedText!());
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Assert.True(warned);
        cut.WaitForAssertion(() => Assert.Equal(Text("EditorCloseUnsaved"), cut.Find(".ne-editor-error").TextContent));
        Assert.DoesNotContain("nicht speicherbar", tv.Read(BookPath));
    }

    [Fact]
    public async Task Pane_InvalidDocument_ShowsErrorAndReloads()
    {
        using var tv = Alpha();
        var (_, cut) = await RenderAsync(tv);
        var before = tv.Read(BookPath);
        var doc = Doc(Editor.Json);
        Blocks(doc)[1]!["attrs"]!["key"] = Blocks(doc)[0]!["attrs"]!["key"]!.GetValue<string>(); // duplicate keys

        await ChangeAsync(cut, doc);
        await cut.InvokeAsync(() => State.FlushEditor!());

        Assert.Equal(before, tv.Read(BookPath));
        Assert.Equal(Text("EditorSaveInvalid"), cut.Find(".ne-editor-error").TextContent);
        Assert.Equal(2, Editor.Documents.Count);
    }

    [Fact]
    public async Task Pane_ReadOnlyBook_NotEditableWithHint()
    {
        using var tv = TestVault.Create(
            (BookPath, "title:: Alpha\ntype:: book\n\n- # Eins\n\t- Text\n\t  ```\n\t  offener Codeblock\n"));
        var session = await OpenSessionAsync(tv);
        Assert.True(State.CurrentBook!.Page.IsReadOnly);

        var cut = Render<EditorPane>();

        cut.WaitForAssertion(() => Assert.NotEmpty(Editor.Documents));
        Assert.NotNull(cut.Find(".ne-editor-host").GetAttribute("inert"));
        Assert.Equal(Text("EditorReadOnly"), cut.Find(".ne-editor-hint").TextContent);
    }

    [Fact]
    public async Task Pane_ChipClick_FocusesNote_CursorIsTracked_FlushUnregisteredOnDispose()
    {
        using var tv = Alpha();
        var (session, cut) = await RenderAsync(tv);
        var first = Section("Eins").TextBlocks.First().Key;

        await cut.InvokeAsync(() => Editor.Callbacks!.OnChipClicked(Guid.Parse(NoteId)));
        await cut.InvokeAsync(() => Editor.Callbacks!.OnCursorBlockChanged(first));

        Assert.Equal(session.Notes.All().Single(n => n.Block.Id == Guid.Parse(NoteId)).Key, State.FocusedNoteKey);
        Assert.Equal(first, State.CursorTextBlockKey);

        await DisposeComponentsAsync();

        Assert.Null(State.FlushEditor);
        Assert.Null(State.WarnUnsavedText);
        Assert.True(Editor.Disposed);
    }
}
