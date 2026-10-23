using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Editor;
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

    /// <summary>Opens the vault, selects <paramref name="section"/> and renders the editor (it shows the whole book).</summary>
    private async Task<(VaultSession Session, IRenderedComponent<EditorPane> Cut)> RenderAsync(TestVault tv, string section = "Eins")
    {
        var session = await OpenSessionAsync(tv);
        State.CurrentSectionKey = Section(section).Key;
        var cut = Render<EditorPane>();
        cut.WaitForAssertion(() => Assert.NotEmpty(Editor.Documents));
        return (session, cut);
    }

    private static JsonNode Doc(string json) => JsonNode.Parse(json)!;

    private static JsonArray Blocks(JsonNode doc) => doc["content"]!.AsArray();

    /// <summary>The index in <see cref="Blocks"/> of the text block at <paramref name="index"/> (counting text blocks only).</summary>
    private static int At(JsonNode doc, int index) =>
        Blocks(doc).Select((n, i) => (Type: (string?)n!["type"], Index: i)).Where(n => n.Type == "textBlock").ElementAt(index).Index;

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
    public async Task Pane_ShowsWholeBookWithChips()
    {
        using var tv = Alpha();
        var (_, cut) = await RenderAsync(tv);

        var (json, showChips) = Editor.Documents.Single();
        Assert.False(showChips);
        Assert.Equal("Erster Text|Zweiter Text|Dritter Text", TextsOf(json));
        var chip = Blocks(Doc(json))[At(Doc(json), 0)]!["attrs"]!["sources"]![0]!;
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
        await cut.InvokeAsync(() => State.FlushEditor!()); // waits for the autosave's own sync to finish
        var (removed, added) = LineDiff.Changed(Encoding.UTF8.GetBytes(before), Encoding.UTF8.GetBytes(tv.Read(BookPath)));
        Assert.Single(removed);
        Assert.Equal(2, added.Count); // the text line and the block's new updated:: line
        Assert.Contains("\t- Zweiter Text, geändert\n\t  updated:: " + Stamp + "\n", tv.Read(BookPath));
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

        cut.WaitForAssertion(() => Assert.Equal("Erster Text|Zweiter Text, extern|Dritter Text", TextsOf(Editor.Json)));
    }

    private static Guid KeyOf(JsonNode doc, int index) => Guid.Parse((string)Blocks(doc)[At(doc, index)]!["attrs"]!["key"]!);

    private BlockDateLabel? LabelOf(Guid key) =>
        State.CurrentBook!.FindTextBlock(key) is { } text
            ? BlockDateText.LabelOf(text.Created, text.Updated, Services.GetRequiredService<IStringLocalizer<Strings>>())
            : null;

    [Fact]
    public async Task Pane_OwnSave_PushesTheChangedBlocksDates_DocumentKept()
    {
        using var tv = Alpha();
        var (_, cut) = await RenderAsync(tv);
        var doc = Doc(Editor.Json);
        var key = KeyOf(doc, 1);
        SetText(doc, 1, "Zweiter Text, geändert");

        await ChangeAsync(cut, doc);
        await cut.InvokeAsync(() => State.FlushEditor!());

        var pushed = Assert.Single(Editor.BlockDates);
        var label = Assert.Single(pushed);
        Assert.Equal(key, label.Key);
        Assert.Equal("geändert " + Clock.Now.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture), label.Value!.Text);
        Assert.Equal(LabelOf(key), label.Value);
        Assert.Single(Editor.Documents);

        // Another save in the same minute: the label is unchanged, nothing is pushed.
        SetText(doc, 1, "Zweiter Text, noch einmal");
        await ChangeAsync(cut, doc);
        await cut.InvokeAsync(() => State.FlushEditor!());

        Assert.Contains("\t- Zweiter Text, noch einmal\n", tv.Read(BookPath));
        Assert.Single(Editor.BlockDates);
    }

    [Fact]
    public async Task Pane_DatesPushFails_PushedAgainOnTheNextSync()
    {
        using var tv = Alpha();
        var (_, cut) = await RenderAsync(tv);
        var doc = Doc(Editor.Json);
        var key = KeyOf(doc, 1);
        SetText(doc, 1, "Zweiter Text, geändert");
        Editor.FailBlockDates = new InvalidOperationException("The editor is gone.");

        await ChangeAsync(cut, doc);
        await Assert.ThrowsAsync<InvalidOperationException>(() => cut.InvokeAsync(() => State.FlushEditor!()));

        // The next sync (the save's own state change may already bring one) pushes the label that was not shown.
        await cut.InvokeAsync(() => State.FlushEditor!());

        var label = Assert.Single(Assert.Single(Editor.BlockDates));
        Assert.Equal(key, label.Key);
        Assert.Equal(LabelOf(key), label.Value);
    }

    [Fact]
    public async Task Pane_OwnSave_NewBlock_GetsItsDates()
    {
        using var tv = Alpha();
        var (_, cut) = await RenderAsync(tv);
        var doc = Doc(Editor.Json);
        var typed = (JsonObject)Blocks(doc)[At(doc, 2)]!.DeepClone();
        var key = Guid.NewGuid();
        typed["attrs"]!["key"] = key.ToString("D");
        typed["attrs"]!["sources"] = new JsonArray();
        typed["content"]![0]!["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Neu getippt" });
        typed["content"]![0]!["attrs"]!["md"] = null;
        Blocks(doc).Add(typed);

        await ChangeAsync(cut, doc);
        await cut.InvokeAsync(() => State.FlushEditor!());

        Assert.Contains("\t- Neu getippt\n", tv.Read(BookPath));
        var label = Assert.Single(Assert.Single(Editor.BlockDates));
        Assert.Equal(key, label.Key);
        Assert.StartsWith("erstellt ", label.Value!.Text);
        Assert.Equal(LabelOf(key), label.Value);
        Assert.Single(Editor.Documents);
    }

    [Fact]
    public async Task Pane_SaveWithoutDateChange_PushesNoDates()
    {
        using var tv = Alpha();
        var (_, cut) = await RenderAsync(tv);
        var before = tv.Read(BookPath);
        var doc = Doc(Editor.Json);
        SetPara(doc, 1, 0, "Zweiter Text", ""); // a trailing hard break, which the file does not keep

        await ChangeAsync(cut, doc);
        await cut.InvokeAsync(() => State.FlushEditor!());
        await cut.InvokeAsync(() => State.FlushEditor!());

        Assert.Equal(before, tv.Read(BookPath));
        Assert.Empty(Editor.BlockDates);
        Assert.Single(Editor.Documents);
    }

    private const string StatusBook =
        "title:: Alpha\ntype:: book\n\n- # Eins\n" +
        "\t- Text\n\t  status:: formulierung\n" +
        "\t\t- Absatz eins\n\t\t  status:: bemerkung\n" +
        "\t\t- Absatz zwei\n";

    /// <summary>The <c>status</c> attributes of the first text block and its paragraphs (<c>-</c> for none).</summary>
    private static string StatusesOf(string json)
    {
        var block = Blocks(Doc(json)).First(n => (string?)n!["type"] == "textBlock")!;
        return string.Join("|", new[] { block }.Concat(block["content"]!.AsArray().Skip(1))
            .Select(n => (string?)n!["attrs"]!["status"] ?? "-"));
    }

    [Fact]
    public async Task Pane_ExternalStatusChange_ReloadsWithNewStatus()
    {
        using var tv = Alpha();
        var (_, cut) = await RenderAsync(tv);
        var path = Path.Combine(tv.Root, BookPath);

        File.WriteAllText(path, tv.Read(BookPath).Replace("\t- Erster Text\n", "\t- Erster Text\n\t  status:: fertig\n"));
        await cut.InvokeAsync(() => State.Session!.HandleExternalChange(path));

        cut.WaitForAssertion(() => Assert.Equal("fertig", StatusesOf(Editor.Json)));
    }

    [Fact]
    public async Task Pane_OwnSaveNormalized_WithStatuses_EditorNotReloaded()
    {
        using var tv = TestVault.Create((BookPath, StatusBook));
        var (_, cut) = await RenderAsync(tv);
        Assert.Equal("formulierung|bemerkung|-", StatusesOf(Editor.Json));
        var doc = Doc(Editor.Json);
        SetPara(doc, 0, 1, "Absatz eins, neu", ""); // a trailing hard break, which the file does not keep

        await ChangeAsync(cut, doc);
        await cut.InvokeAsync(() => State.FlushEditor!());
        await cut.InvokeAsync(() => State.FlushEditor!());

        Assert.Contains("\t\t- Absatz eins, neu\n\t\t  status:: bemerkung\n", tv.Read(BookPath));
        Assert.Single(Editor.Documents);
    }

    [Fact]
    public async Task Pane_StatusSetAfterOwnSave_ReloadsWithNewStatus()
    {
        using var tv = TestVault.Create((BookPath, StatusBook));
        var (session, cut) = await RenderAsync(tv);
        var doc = Doc(Editor.Json);
        var blockKey = Guid.Parse((string)Blocks(doc)[At(doc, 0)]!["attrs"]!["key"]!);
        var detailKey = Guid.Parse((string)Blocks(doc)[At(doc, 0)]!["content"]![2]!["attrs"]!["key"]!);
        SetPara(doc, 0, 2, "Absatz zwei, neu");
        await ChangeAsync(cut, doc);
        await cut.InvokeAsync(() => State.FlushEditor!());
        Assert.Single(Editor.Documents);

        // What a status command does after saving the editor: change the page, save it, take the book again.
        await cut.InvokeAsync(() =>
        {
            var book = session.Vault.FindBook("Buch - Alpha")!;
            ManuscriptEditor.SetStatus(book, BookElements.Find(book, blockKey)!, BlockStatus.Done);
            ManuscriptEditor.SetStatus(book, BookElements.Find(book, detailKey)!, BlockStatus.Relevant);
            Assert.True(session.TrySave(book.Page, out _));
            State.RefreshBook();
            State.Notify();
        });

        cut.WaitForAssertion(() => Assert.Equal("fertig|bemerkung|relevant", StatusesOf(Editor.Json)));
        Assert.Contains("Absatz zwei, neu", Editor.Json);
    }

    [Fact]
    public async Task Pane_LinkedBlockBackAfterAutosavedDelete_NoteHasUsageAgain()
    {
        using var tv = Alpha();
        var (session, cut) = await RenderAsync(tv);
        var original = Doc(Editor.Json);
        var deleted = original.DeepClone();
        Blocks(deleted).RemoveAt(At(deleted, 0));
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
        // The editor reloads through the state change it listens to (InvokeAsync), not within the drop call.
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(2, Editor.Documents.Count);
            Assert.Equal("Erster Text|Gedächtnis braucht Schlaf|Zweiter Text|Dritter Text", TextsOf(Editor.Json));
        });
    }

    [Fact]
    public async Task Pane_DropOnHeading_AdoptsAtThatSectionsStart()
    {
        using var tv = Alpha();
        var (session, cut) = await RenderAsync(tv);
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
        Assert.Contains("\t- Erster Text, neu\n\t  id:: " + FirstId + "\n\t  updated:: " + Stamp + "\n\t- Zweiter Text\n", book);
        Assert.DoesNotContain("source::", book);
        Assert.DoesNotContain("used-in::", tv.Read(NotesPath));
        // The new document comes through the state change (InvokeAsync), after the chip call has returned.
        cut.WaitForAssertion(() => Assert.Empty(Blocks(Doc(Editor.Json))[At(Doc(Editor.Json), 0)]!["attrs"]!["sources"]!.AsArray()));
    }

    [Fact]
    public async Task Pane_SplitFromJs_AddsUsageToNote()
    {
        using var tv = Alpha();
        var (_, cut) = await RenderAsync(tv);
        var doc = Doc(Editor.Json);
        var first = Blocks(doc)[At(doc, 0)]!;
        var split = first.DeepClone();
        var newKey = Guid.CreateVersion7().ToString("D");
        split["attrs"]!["key"] = newKey;
        split["attrs"]!["splitFrom"] = first["attrs"]!["key"]!.GetValue<string>();
        Blocks(doc).Insert(At(doc, 0) + 1, split);
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
        var second = Blocks(doc)[At(doc, 1)]!;
        var para = second["content"]![0]!.DeepClone();
        para["attrs"]!["key"] = Guid.CreateVersion7().ToString("D");
        para["attrs"]!["depth"] = 1;
        Blocks(doc)[At(doc, 0)]!["content"]!.AsArray().Add(para);
        Blocks(doc).RemoveAt(At(doc, 1));
        await ChangeAsync(cut, doc);
        await cut.InvokeAsync(() => State.FlushEditor!());

        Assert.Equal(
            "title:: Alpha\ntype:: book\n\n- # Eins\n\t- Vorher\n\t  updated:: " + Stamp + "\n\t\t- Erster Text\n", tv.Read(BookPath));
        Assert.DoesNotContain("used-in::", tv.Read(NotesPath));
        Assert.True(session.Undo.CanUndo);
    }

    [Fact]
    public async Task Pane_SectionSwitch_SavesPendingText_KeepsTheDocument()
    {
        using var tv = Alpha();
        var (_, cut) = await RenderAsync(tv);
        var doc = Doc(Editor.Json);
        SetText(doc, 1, "Zweiter Text, noch nicht gespeichert");
        await ChangeAsync(cut, doc);

        State.CurrentSectionKey = Section("Zwei").Key;
        await cut.InvokeAsync(State.Notify);

        cut.WaitForAssertion(() =>
            Assert.Contains(
                "\t- Zweiter Text, noch nicht gespeichert\n\t  updated:: " + Stamp + "\n- # Zwei\n\t- Dritter Text\n", tv.Read(BookPath)));
        await cut.InvokeAsync(() => State.FlushEditor!());
        Assert.Single(Editor.Documents);
    }

    [Fact]
    public async Task Pane_ShowsHeadingsAndChipToggle()
    {
        using var tv = Alpha();
        var (_, cut) = await RenderAsync(tv);

        cut.WaitForAssertion(() =>
        {
            var (json, showChips) = Editor.Documents[^1];
            Assert.False(showChips);
            Assert.Equal(["heading", "textBlock", "textBlock", "heading", "textBlock"], Blocks(Doc(json)).Select(n => (string?)n!["type"]));
        });

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
        cut.WaitForAssertion(() => Assert.Equal("Erster Text, lokal|Zweiter Text, extern|Dritter Text", TextsOf(Editor.Json)));
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
        Blocks(doc)[At(doc, 1)]!["attrs"]!["key"] = Blocks(doc)[At(doc, 0)]!["attrs"]!["key"]!.GetValue<string>(); // duplicate keys

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

    private const string HeadingId = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";

    private const string MissingId = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee";

    /// <summary>Alpha with references: to the heading "Zwei", to the text block "Erster Text" and to a missing block.</summary>
    private static TestVault WithRefs() => TestVault.Create(
        (BookPath,
            "title:: Alpha\ntype:: book\n\n" +
            "- # Eins\n" +
            "\t- Erster ((" + HeadingId + ")) Text, der so lang ist, dass er gekürzt wird\n" +
            "\t  id:: " + FirstId + "\n" +
            "\t- Siehe ((" + HeadingId + ")) und ((" + FirstId + ")) und ((" + MissingId + "))\n" +
            "- # Zwei\n" +
            "  id:: " + HeadingId + "\n" +
            "\t- Dritter Text\n"));

    /// <summary>The blockRef nodes of the shown document, in order.</summary>
    private List<JsonNode> RefNodes() =>
        [.. Blocks(Doc(Editor.Json)).Where(n => (string?)n!["type"] == "textBlock")
            .SelectMany(n => n!["content"]!.AsArray())
            .SelectMany(p => p!["content"]?.AsArray() ?? [])
            .Where(n => (string?)n!["type"] == "blockRef")
            .Select(n => n!)];

    [Fact]
    public async Task Pane_ShowsReferencesWithLabels()
    {
        using var tv = WithRefs();
        await RenderAsync(tv);

        var refs = RefNodes();
        Assert.Equal(
            [
                (HeadingId, "→ Zwei", "Zwei", false),
                (HeadingId, "→ Zwei", "Zwei", false),
                // A reference in the target's text shows as "→" in its label.
                (FirstId, "→ Eins: Erster → Text, der so lang ist, dass er…", "Eins › Erster → Text, der so lang ist, dass er gekürzt wird", false),
                (MissingId, Text("RefLabelMissing"), Text("RefTitleMissing"), true),
            ],
            refs.Select(n => ((string)n["attrs"]!["id"]!, (string)n["attrs"]!["label"]!, (string)n["attrs"]!["title"]!,
                (bool)n["attrs"]!["missing"]!)));
    }

    [Fact]
    public async Task Pane_ReferenceToANote_IsAForeignNode_FileUnchanged()
    {
        // Spec 2.1: a reference to a block outside the book (here a note) is no cross reference. It is still one unit
        // of the shown text (a foreign node showing the reference text), so the editor's offsets count it as one.
        using var tv = NoteRefVault();
        var (_, cut) = await RenderAsync(tv);

        Assert.Equal([NoteId, MissingId], RefNodes().Select(n => (string)n["attrs"]!["id"]!));
        var foreign = RefNodes()[0]["attrs"]!;
        Assert.Equal("((" + NoteId + "))", (string?)foreign["label"]);
        Assert.True((bool)foreign["foreign"]!);
        Assert.False(RefNodes()[1]["attrs"]!.AsObject().ContainsKey("foreign"));

        // Saving another block leaves the reference as it is; the label push gives the note its reference text as label.
        var doc = Doc(Editor.Json);
        SetText(doc, 1, "Zweiter Text, geändert");
        await ChangeAsync(cut, doc);
        await cut.InvokeAsync(() => State.FlushEditor!());

        var saved = tv.Read(BookPath);
        Assert.Contains("Zweiter Text, geändert", saved);
        Assert.Contains("\t- Erster Text ((" + NoteId + ")) hier und ((" + MissingId + "))\n", saved);
        Assert.All(Editor.RefLabels, pushed =>
            Assert.Equal(new RefLabel("((" + NoteId + "))", "", false, true), pushed[Guid.Parse(NoteId)]));
    }

    [Fact]
    public async Task AdoptAtCursor_AfterANoteReference_AtTheOffsetTheEditorShows()
    {
        using var tv = NoteRefVault();
        var (_, cut) = await RenderAsync(tv);
        var book = State.CurrentBook!;
        var element = book.Page.AllBlocks().Single(b => b.Content.StartsWith("Erster Text", StringComparison.Ordinal)).Key;
        State.Cursor = new CursorInfo(ElementKind.TextBlock, element, element, ShownOffsetAfter(Doc(Editor.Json), NoteId));
        var note = State.Session!.Notes.All().Single(n => n.Block.Content == "Notiz").Key;

        var message = await cut.InvokeAsync(() => NoteAdoption.AdoptAsync(State, note, AdoptVariant.AtCursor, "Ohne Titel", NullLogger.Instance));

        Assert.Null(message);
        Assert.Contains("\t- Erster Text ((" + NoteId + "))Notiz hier und ((" + MissingId + "))\n", tv.Read(BookPath));
    }

    [Fact]
    public async Task InsertReference_AfterANoteReference_AtTheOffsetTheEditorShows()
    {
        using var tv = NoteRefVault();
        var (_, cut) = await RenderAsync(tv);
        var book = State.CurrentBook!;
        var element = book.Page.AllBlocks().Single(b => b.Content.StartsWith("Erster Text", StringComparison.Ordinal)).Key;

        // The cursor right after the note reference, counted in the editor's document.
        State.Cursor = new CursorInfo(ElementKind.TextBlock, element, element, ShownOffsetAfter(Doc(Editor.Json), NoteId));
        var target = book.Page.AllBlocks().Single(b => b.Content == "Zweiter Text").Key;
        var message = await cut.InvokeAsync(() => ManuscriptCommands.InsertReferenceAsync(State, target, "Verweis", NullLogger.Instance));

        Assert.Null(message);
        var saved = tv.Read(BookPath);
        var id = System.Text.RegularExpressions.Regex.Match(saved, @"\t- Zweiter Text\n\t  id:: ([0-9a-f-]{36})").Groups[1].Value;
        Assert.Contains("\t- Erster Text ((" + NoteId + "))((" + id + ")) hier und ((" + MissingId + "))\n", saved);
    }

    /// <summary>Alpha with a text block referring to a note and to a missing block.</summary>
    private static TestVault NoteRefVault() => TestVault.Create(
        (BookPath,
            "title:: Alpha\ntype:: book\n\n" +
            "- # Eins\n" +
            "\t- Erster Text ((" + NoteId + ")) hier und ((" + MissingId + "))\n" +
            "\t- Zweiter Text\n"),
        (NotesPath, "- Notiz\n  id:: " + NoteId + "\n"));

    /// <summary>
    /// The offset right after the reference node to <paramref name="id"/> in its paragraph, as the editor counts it:
    /// a character per text character, one position per node.
    /// </summary>
    internal static int ShownOffsetAfter(JsonNode doc, string id)
    {
        var para = Blocks(doc).SelectMany(n => n!["content"]?.AsArray() ?? [])
            .Single(p => (p!["content"]?.AsArray() ?? []).Any(n => (string?)n!["attrs"]?["id"] == id))!;
        var offset = 0;
        foreach (var node in para["content"]!.AsArray())
        {
            offset += (string?)node!["text"] is { } text ? text.Length : 1;
            if ((string?)node["attrs"]?["id"] == id)
            {
                return offset;
            }
        }

        throw new InvalidOperationException("No reference node " + id);
    }

    /// <summary>The heading node with <paramref name="title"/> in an editor document.</summary>
    private static JsonNode HeadingNode(JsonNode doc, string title) =>
        Blocks(doc).Single(n => (string?)n!["type"] == "heading" && (string?)n["content"]?[0]?["text"] == title)!;

    [Fact]
    public async Task Pane_OwnSave_RenamedTarget_PushesTheNewLabel_DocumentKept()
    {
        using var tv = WithRefs();
        var (_, cut) = await RenderAsync(tv);
        var doc = Doc(Editor.Json);
        HeadingNode(doc, "Zwei")["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Zwei neu" });

        await ChangeAsync(cut, doc);
        await cut.InvokeAsync(() => State.FlushEditor!());

        Assert.Contains("- # Zwei neu\n", tv.Read(BookPath));
        var pushed = Assert.Single(Editor.RefLabels);
        Assert.Equal(new RefLabel("→ Zwei neu", "Zwei neu", false), pushed[Guid.Parse(HeadingId)]);
        Assert.True(pushed[Guid.Parse(MissingId)].Missing);
        Assert.Single(Editor.Documents);

        // Another save that changes no label and no reference pushes nothing.
        SetText(doc, 2, "Dritter Text, geändert");
        await ChangeAsync(cut, doc);
        await cut.InvokeAsync(() => State.FlushEditor!());

        Assert.Single(Editor.RefLabels);
    }

    [Fact]
    public async Task Pane_OwnSave_TypedReference_IsPushedToBecomeANode()
    {
        using var tv = WithRefs();
        var (_, cut) = await RenderAsync(tv);
        var doc = Doc(Editor.Json);
        SetText(doc, 2, "Dritter Text ((" + FirstId + "))");

        await ChangeAsync(cut, doc);
        await cut.InvokeAsync(() => State.FlushEditor!());

        Assert.Contains("\t- Dritter Text ((" + FirstId + "))\n", tv.Read(BookPath));
        var pushed = Assert.Single(Editor.RefLabels);
        Assert.StartsWith("→ Eins: Erster", pushed[Guid.Parse(FirstId)].Text);
        Assert.Single(Editor.Documents);
    }

    [Fact]
    public async Task Pane_DeletedTarget_ShowsTheReferenceAsMissing()
    {
        using var tv = WithRefs();
        var (_, cut) = await RenderAsync(tv);
        var doc = Doc(Editor.Json);
        Blocks(doc).RemoveAt(At(doc, 0));

        await ChangeAsync(cut, doc);
        await cut.InvokeAsync(() => State.FlushEditor!());

        Assert.DoesNotContain("Erster", tv.Read(BookPath));
        var pushed = Assert.Single(Editor.RefLabels);
        Assert.Equal(new RefLabel(Text("RefLabelMissing"), Text("RefTitleMissing"), true), pushed[Guid.Parse(FirstId)]);
        Assert.False(pushed[Guid.Parse(HeadingId)].Missing);
        Assert.Single(Editor.Documents);
    }

    [Fact]
    public async Task Pane_ReferenceClick_RevealsTheTarget()
    {
        using var tv = WithRefs();
        var (_, cut) = await RenderAsync(tv);
        var zwei = Section("Zwei").Key;
        var first = Section("Eins").TextBlocks.First().Key;

        await cut.InvokeAsync(() => Editor.Callbacks!.OnReferenceClicked(Guid.Parse(HeadingId)));

        cut.WaitForAssertion(() => Assert.Equal([zwei], Editor.Reveals));
        Assert.Equal(zwei, State.CurrentSectionKey);
        Assert.Equal(zwei, State.Cursor!.Key);

        await cut.InvokeAsync(() => Editor.Callbacks!.OnReferenceClicked(Guid.Parse(FirstId)));

        cut.WaitForAssertion(() => Assert.Equal([zwei, first], Editor.Reveals));
        Assert.Equal((first, 0), (State.Cursor!.Key, State.Cursor.Offset));

        // A missing target goes nowhere.
        await cut.InvokeAsync(() => Editor.Callbacks!.OnReferenceClicked(Guid.Parse(MissingId)));

        Assert.Equal([zwei, first], Editor.Reveals);
        Assert.Equal(first, State.Cursor!.Key);
    }

    [Fact]
    public async Task Pane_ChipClick_RevealsNote_CursorIsTracked_FlushUnregisteredOnDispose()
    {
        using var tv = Alpha();
        var (_, cut) = await RenderAsync(tv);
        var first = Section("Eins").TextBlocks.First().Key;
        var revealed = new List<Guid>();
        State.NoteRevealRequested += revealed.Add;

        await cut.InvokeAsync(() => Editor.Callbacks!.OnChipClicked(Guid.Parse(NoteId)));
        await cut.InvokeAsync(() => Editor.Callbacks!.OnCursorChanged(new CursorInfo(ElementKind.TextBlock, first, first, 0)));

        Assert.Equal([Guid.Parse(NoteId)], revealed);
        Assert.Equal(first, State.CursorTextBlockKey);

        await cut.InvokeAsync(() => Editor.Callbacks!.OnCursorChanged(new CursorInfo(ElementKind.Heading, Section("Zwei").Key, null, 0)));
        Assert.Null(State.CursorTextBlockKey);

        await cut.InvokeAsync(() => Editor.Callbacks!.OnCursorChanged(new CursorInfo(ElementKind.Detail, Guid.NewGuid(), first, 2)));
        Assert.Equal(first, State.CursorTextBlockKey);

        await DisposeComponentsAsync();

        Assert.Null(State.FlushEditor);
        Assert.Null(State.WarnUnsavedText);
        Assert.True(Editor.Disposed);
    }

    /// <summary>A cursor at offset 3 in the first text block of the current book with one of <paramref name="texts"/>.</summary>
    private CursorInfo CursorInText(params string[] texts)
    {
        var key = State.CurrentBook!.Page.AllBlocks().First(b => texts.Contains(b.Content)).Key;
        return new CursorInfo(ElementKind.TextBlock, key, key, 3);
    }

    [Fact]
    public async Task NewBook_CursorReportedOnLoad_NotTaken_AdoptGoesToEndOfBook()
    {
        const string betaPath = "pages/Buch - Beta.md";
        const string beta = "title:: Beta\ntype:: book\n\n- # Anfang\n\t- Beta Text\n- # Ende\n\t- Letzter Text\n";
        using var tv = TestVault.Create(
            (BookPath, "title:: Alpha\ntype:: book\n\n- # Eins\n\t- Erster Text\n"),
            (betaPath, beta),
            (NotesPath, "- Eine Notiz\n"));
        var session = await OpenSessionAsync(tv);
        State.CurrentBook = session.Vault.FindBook("Buch - Alpha");

        // The editor puts its selection somewhere in every new document and may report it.
        Editor.CursorAfterLoad = () => CursorInText("Erster Text", "Beta Text");

        // The first document (app start): the cursor is not invented.
        var cut = Render<EditorPane>();
        cut.WaitForAssertion(() => Assert.Single(Editor.Documents));
        Assert.Null(State.Cursor);

        // Another book.
        await cut.InvokeAsync(() =>
        {
            State.CurrentBook = session.Vault.FindBook("Buch - Beta");
            State.CurrentSectionKey = State.CurrentBook!.Root.Key;
            State.Cursor = null;
            State.Notify();
        });
        cut.WaitForAssertion(() => Assert.Equal(2, Editor.Documents.Count));
        Assert.Null(State.Cursor);
        Assert.Equal([false, false], Editor.KeepCursor);

        // Adopting at the cursor takes the no-cursor path: a new text block at the end of the book.
        var note = session.Notes.All().Single(n => n.Block.Content == "Eine Notiz").Key;
        Assert.Null(await cut.InvokeAsync(() => NoteAdoption.AdoptAsync(State, note, AdoptVariant.AtCursor, "(ohne Titel)", NullLogger.Instance)));

        var added = State.CurrentBook!.Page.AllBlocks().Single(b => b.Content == "Eine Notiz");
        Assert.Equal(beta + $"\t- Eine Notiz\n\t  id:: {added.Id}\n\t  source:: (({session.Notes.All().Single().Block.Id}))\n\t  status:: relevant\n\t  created:: {Stamp}\n\t  updated:: {Stamp}\n", tv.Read(betaPath));
    }

    [Fact]
    public async Task SameBookReload_CursorReportedOnLoad_Taken()
    {
        using var tv = Alpha();
        var (_, cut) = await RenderAsync(tv);
        var cursor = CursorInText("Zweiter Text");
        Editor.CursorAfterLoad = () => cursor;

        // Before the user placed a cursor, loading the same book again (showing the chips) invents none.
        cut.Find(".ne-editor-chips input").Change(true);

        cut.WaitForAssertion(() => Assert.Equal(2, Editor.Documents.Count));
        Assert.Null(State.Cursor);

        // Once the user placed one, the editor keeps its cursor on such a reload and reports it.
        await cut.InvokeAsync(() => Editor.Callbacks!.OnCursorChanged(CursorInText("Erster Text")));
        Editor.CursorPlaced = true;
        cut.Find(".ne-editor-chips input").Change(false);

        cut.WaitForAssertion(() => Assert.Equal(3, Editor.Documents.Count));
        Assert.Equal(cursor, State.Cursor);
        Assert.Equal(Section("Eins").Key, State.CurrentSectionKey);
        Assert.Equal([false, true, true], Editor.KeepCursor);
    }
}
