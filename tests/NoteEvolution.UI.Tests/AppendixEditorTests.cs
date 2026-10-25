using System.Text.Json.Nodes;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Editor;
using NoteEvolution.UI.Resources;

namespace NoteEvolution.UI.Tests;

/// <summary>
/// People, source citations and glossary marks in the editor (appendix spec 3): the insert buttons of the section bar,
/// the nodes of the editor document, the labels and marks pushed to the editor, clicks and the locator field.
/// </summary>
public class AppendixEditorTests : UiTestContext
{
    private const string BookPath = "pages/Buch - Alpha.md";

    private const string AnnaId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1";

    private const string SourceId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb1";

    private const string GlossaryId = "cccccccc-cccc-4ccc-8ccc-ccccccccccc1";

    private const string Head = "title:: Alpha\ntype:: book\n\n";

    private const string Text = "- # Eins\n\t- Achtsamkeit ist ein Wort\n\t- Noch einmal Achtsamkeit\n";

    private const string AppendixText =
        "- # Glossar\n  anhang:: glossar\n" +
        $"\t- Achtsamkeit\n\t  id:: {GlossaryId}\n\t  Bewusstes Wahrnehmen.\n" +
        "- # Menschen, die mich inspiriert haben\n  anhang:: menschen\n" +
        $"\t- Anna Berg\n\t  kuerzel:: AB\n\t  themen:: Achtsamkeit, Stille\n\t  id:: {AnnaId}\n" +
        "- # Quellen\n  anhang:: quellen\n" +
        $"\t- Achtsam leben\n\t  art:: buch\n\t  autoren:: (({AnnaId}))\n\t  jahr:: 2020\n\t  id:: {SourceId}\n";

    private const string BookText = Head + Text + AppendixText;

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

    private Book Book => State.CurrentBook!;

    private string L(string key) => Services.GetRequiredService<IStringLocalizer<Strings>>()[key].Value;

    private Guid KeyOf(string content) => Book.Page.AllBlocks().Single(b => b.Content == content).Key;

    private async Task<TestVault> OpenAsync(string bookText = BookText)
    {
        var vault = TestVault.Create((BookPath, bookText));
        _vaults.Add(vault);
        await OpenSessionAsync(vault);
        return vault;
    }

    /// <summary>Renders the editor and lets it report a visible marking box.</summary>
    private async Task<IRenderedComponent<EditorPane>> RenderAsync()
    {
        var cut = Render<EditorPane>();
        cut.WaitForAssertion(() => Assert.NotEmpty(Editor.Documents));
        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionBoxMoved(120.5, true));
        return cut;
    }

    /// <summary>Moves the cursor (as the editor reports it) into the element whose block has <paramref name="content"/>.</summary>
    private Task MoveToAsync<T>(IRenderedComponent<T> cut, string content, int offset = 0)
        where T : IComponent
    {
        var element = BookElements.Find(Book, KeyOf(content))!;
        var cursor = new CursorInfo(element.Kind, element.Key, BookElements.TextBlockOf(Book, element)?.Key, offset);
        return cut.InvokeAsync(() => Editor.Callbacks!.OnCursorChanged(cursor));
    }

    /// <summary>The inline nodes of the own text of the text block with <paramref name="key"/> in the shown document.</summary>
    private JsonArray InlineOf(Guid key) =>
        JsonNode.Parse(Editor.Json)!["content"]!.AsArray()
            .Single(n => (string?)n!["attrs"]?["key"] == key.ToString("D"))!["content"]![0]!["content"]!.AsArray();

    /// <summary>The text a paragraph shows: text as it is, a line break as "\n", every other inline node as one character.</summary>
    private static string ShownText(JsonNode para) =>
        string.Concat((para["content"]?.AsArray() ?? []).Select(n => (string?)n!["type"] switch
        {
            "text" => (string?)n["text"],
            "hardBreak" => "\n",
            _ => "￼",
        }));

    [Fact]
    public async Task InsertPerson_Picker_WritesBlockRef_ShowsAbbreviation()
    {
        var vault = await OpenAsync();
        var cut = await RenderAsync();
        var session = State.Session!;

        await MoveToAsync(cut, "Achtsamkeit ist ein Wort", 11);
        cut.WaitForAssertion(() => Assert.False(cut.Find(".ne-sec-person").HasAttribute("disabled")));
        Assert.Equal(L("SectionInsertPerson"), cut.Find(".ne-sec-person").TextContent.Trim());
        await cut.InvokeAsync(() => cut.Find(".ne-sec-person").Click());
        cut.WaitForAssertion(() => Assert.Equal(
            ["Anna Berg (AB)", L("AppendixPickerNew")], cut.FindAll(".ne-ref-option").Select(o => o.TextContent.Trim())));
        await cut.InvokeAsync(() => cut.Find(".ne-ref-query").Input("ab"));
        await cut.InvokeAsync(() => cut.Find(".ne-ref-query").KeyDown("Enter"));

        Assert.Equal(
            BookText.Replace("\t- Achtsamkeit ist ein Wort\n", $"\t- Achtsamkeit(({AnnaId})) ist ein Wort\n\t  updated:: {Stamp}\n"),
            vault.Read(BookPath));
        Assert.Equal(L("UndoPersonRef"), session.Undo.NextDescription);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-appendix-picker")));

        // The editor shows the person as its abbreviation, with name and topics as tooltip; the cursor goes after it.
        var key = KeyOf($"Achtsamkeit(({AnnaId})) ist ein Wort");
        cut.WaitForAssertion(() => Assert.Contains(InlineOf(key), n => (string?)n!["type"] == "personRef"));
        var person = InlineOf(key).Single(n => (string?)n!["type"] == "personRef")!["attrs"]!;
        Assert.Equal(("AB", "Anna Berg — Achtsamkeit, Stille", false, AnnaId), ((string?)person["label"], (string?)person["title"], (bool)person["missing"]!, (string?)person["id"]));
        Assert.Equal((key, 12), (State.Cursor!.Key, State.Cursor.Offset));
    }

    [Fact]
    public async Task InsertSource_WithLocator_WritesMacro_ShowsNumber()
    {
        var vault = await OpenAsync();
        var cut = await RenderAsync();

        await MoveToAsync(cut, "Noch einmal Achtsamkeit", 4);
        await cut.InvokeAsync(() => cut.Find(".ne-sec-insert-source").Click());
        cut.WaitForAssertion(() => Assert.Equal(
            ["Achtsam leben – Anna Berg", L("AppendixPickerNew")], cut.FindAll(".ne-ref-option").Select(o => o.TextContent.Trim())));
        await cut.InvokeAsync(() => cut.Find(".ne-appendix-locator").Input(" S. 12 "));
        await cut.InvokeAsync(() => cut.FindAll(".ne-ref-option")[0].Click());

        Assert.Equal(
            BookText.Replace("\t- Noch einmal Achtsamkeit\n", $"\t- Noch{{{{quelle (({SourceId})) S. 12}}}} einmal Achtsamkeit\n\t  updated:: {Stamp}\n"),
            vault.Read(BookPath));
        Assert.Equal(L("UndoSourceRef"), State.Session!.Undo.NextDescription);

        // The source is the first one cited: "[1, S. 12]", the full reference as tooltip.
        var key = KeyOf($"Noch{{{{quelle (({SourceId})) S. 12}}}} einmal Achtsamkeit");
        cut.WaitForAssertion(() => Assert.Contains(InlineOf(key), n => (string?)n!["type"] == "sourceCite"));
        var cite = InlineOf(key).Single(n => (string?)n!["type"] == "sourceCite")!["attrs"]!;
        Assert.Equal(("[1, S. 12]", "Anna Berg: Achtsam leben. 2020", "S. 12"), ((string?)cite["label"], (string?)cite["title"], (string?)cite["locator"]));
    }

    [Fact]
    public async Task NewFromPicker_CreatesEntryThenInserts()
    {
        var vault = await OpenAsync();
        var cut = Render<Shell>();
        cut.WaitForAssertion(() => Assert.NotEmpty(Editor.Documents));
        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionBoxMoved(120.5, true));

        await MoveToAsync(cut, "Achtsamkeit ist ein Wort", 4);
        await cut.InvokeAsync(() => cut.Find(".ne-sec-person").Click());
        await cut.InvokeAsync(() => cut.Find(".ne-appendix-picker-new").Click());

        // The window "Menschen" opens with a new entry; once it is saved, the person goes in where the cursor was.
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".ne-appendix-dialog .ne-appendix-name")));
        Assert.Empty(cut.FindAll(".ne-appendix-picker"));
        await cut.InvokeAsync(() => cut.Find(".ne-appendix-name").Change("Carl Dorn"));
        await cut.InvokeAsync(() => cut.Find(".ne-modal-close").Click());

        cut.WaitForAssertion(() => Assert.Contains(Appendix.People(State.CurrentBook!), p => p.Name == "Carl Dorn"));
        var carl = Appendix.People(State.CurrentBook!).Single(p => p.Name == "Carl Dorn");
        Assert.Equal("CD", carl.Abbreviation);
        cut.WaitForAssertion(() => Assert.Contains($"\t- Acht(({carl.Id:D}))samkeit ist ein Wort\n", vault.Read(BookPath)));
        Assert.Empty(cut.FindAll(".ne-appendix-dialog"));
    }

    [Fact]
    public async Task GlossaryMarks_SentAfterSave_FirstOccurrenceOnly()
    {
        var vault = await OpenAsync();
        var cut = await RenderAsync();
        var first = KeyOf("Achtsamkeit ist ein Wort");
        var second = KeyOf("Noch einmal Achtsamkeit");
        var entry = Appendix.Glossary(Book).Single().Key;

        // After loading: one mark, at the first occurrence only.
        cut.WaitForAssertion(() => Assert.NotEmpty(Editor.AppendixLabels));
        Assert.Equal([new GlossaryMark(first, 0, 11, "Achtsamkeit — Bewusstes Wahrnehmen.", entry)], Editor.AppendixLabels[^1].Marks);
        Assert.Equal(new RefLabel("AB", "Anna Berg — Achtsamkeit, Stille", false), Editor.AppendixLabels[^1].People[Guid.Parse(AnnaId)]);
        Assert.Equal(new RefLabel("–", "Anna Berg: Achtsam leben. 2020", false), Editor.AppendixLabels[^1].Sources[Guid.Parse(SourceId)]);

        // Typing the term away moves the mark to the next occurrence once the text is saved.
        var doc = JsonNode.Parse(Editor.Json)!;
        var block = doc["content"]!.AsArray().Single(n => (string?)n!["attrs"]?["key"] == first.ToString("D"))!;
        block["content"]![0]!["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Ein Wort" });
        await cut.InvokeAsync(() => Editor.Callbacks!.OnDocumentChanged(doc.ToJsonString()));
        Time.Advance(TimeSpan.FromMilliseconds(1000));

        cut.WaitForAssertion(() => Assert.Contains("\t- Ein Wort\n", vault.Read(BookPath)));
        await cut.InvokeAsync(() => State.FlushEditor!());
        Assert.Single(Editor.Documents);
        Assert.Equal([new GlossaryMark(second, 12, 11, "Achtsamkeit — Bewusstes Wahrnehmen.", entry)], Editor.AppendixLabels[^1].Marks);

        // Nothing changed: nothing is pushed again.
        var pushes = Editor.AppendixLabels.Count;
        await cut.InvokeAsync(() => State.FlushEditor!());
        Assert.Equal(pushes, Editor.AppendixLabels.Count);
    }

    [Fact]
    public async Task BookWithoutAppendix_PushesNoAppendixLabels()
    {
        await OpenAsync(Head + Text);
        var cut = await RenderAsync();
        await cut.InvokeAsync(() => State.FlushEditor!());

        Assert.Empty(Editor.AppendixLabels);
        Assert.DoesNotContain("appendix", Editor.Calls);
    }

    [Fact]
    public async Task PersonClick_OpensPeopleDialogAtEntry()
    {
        await OpenAsync(Head + $"- # Eins\n\t- Laut (({AnnaId})) und {{{{quelle (({SourceId}))}}}}\n" + AppendixText);
        var cut = await RenderAsync();
        var requests = new List<(AppendixKind Kind, Guid? Entry)>();
        State.AppendixDialogRequested += (kind, entry) => requests.Add((kind, entry));

        await cut.InvokeAsync(() => Editor.Callbacks!.OnPersonClicked(Guid.Parse(AnnaId)));
        await cut.InvokeAsync(() => Editor.Callbacks!.OnSourceClicked(Guid.Parse(SourceId)));
        await cut.InvokeAsync(() => Editor.Callbacks!.OnGlossaryClicked(Appendix.Glossary(Book).Single().Key));
        // A citation without a source goes nowhere.
        await cut.InvokeAsync(() => Editor.Callbacks!.OnSourceClicked(Guid.NewGuid()));

        Assert.Equal(
            [
                (AppendixKind.People, (Guid?)Appendix.People(Book).Single().Key),
                (AppendixKind.Sources, Appendix.Sources(Book).Single().Key),
                (AppendixKind.Glossary, Appendix.Glossary(Book).Single().Key),
            ],
            requests);
    }

    [Fact]
    public async Task EditLocator_DoubleClick_ChangesTheMacro()
    {
        const string Cited = "Achtsamkeit{{quelle ((" + SourceId + ")) S. 12}} ist ein Wort";
        var text = Head + Text.Replace("Achtsamkeit ist ein Wort", Cited) + AppendixText;
        var vault = await OpenAsync(text);
        var cut = await RenderAsync();
        await MoveToAsync(cut, Cited, 3);
        var key = KeyOf(Cited);

        // The editor reports the double click with the citation's offset in the shown text.
        await cut.InvokeAsync(() => Editor.Callbacks!.OnCitationEdit(Guid.Parse(SourceId), key, 11));
        cut.WaitForAssertion(() => Assert.Equal("S. 12", cut.Find(".ne-citation-edit .ne-citation-locator").GetAttribute("value")));

        // Escape closes it without a change.
        await cut.InvokeAsync(() => cut.Find(".ne-citation-locator").KeyDown("Escape"));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-citation-edit")));
        Assert.Equal(text, vault.Read(BookPath));

        await cut.InvokeAsync(() => Editor.Callbacks!.OnCitationEdit(Guid.Parse(SourceId), key, 11));
        await cut.InvokeAsync(() => cut.Find(".ne-citation-locator").Input("Min. 14:30"));
        await cut.InvokeAsync(() => cut.Find(".ne-citation-locator").KeyDown("Enter"));

        Assert.Equal(
            text.Replace(Cited + "\n", Cited.Replace("S. 12", "Min. 14:30") + $"\n\t  updated:: {Stamp}\n"),
            vault.Read(BookPath));
        Assert.Equal(L("UndoCitationLocator"), State.Session!.Undo.NextDescription);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-citation-edit")));

        // An empty locator leaves the citation without one.
        await cut.InvokeAsync(() => Editor.Callbacks!.OnCitationEdit(Guid.Parse(SourceId), key, 11));
        await cut.InvokeAsync(() => cut.Find(".ne-citation-locator").Input(" "));
        await cut.InvokeAsync(() => cut.Find(".ne-citation-save").Click());
        Assert.Contains("\t- Achtsamkeit{{quelle ((" + SourceId + "))}} ist ein Wort\n", vault.Read(BookPath));
    }

    [Fact]
    public async Task DocMapper_RoundTrip_PersonAndCitation()
    {
        const string Line = "Laut **((" + AnnaId + "))** steht es{{quelle ((" + SourceId + ")) S. 12_a*b}} und ((ffffffff-ffff-4fff-8fff-ffffffffffff)) {{quelle ((eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee))}}";
        await OpenAsync(Head + "- # Eins\n\t- " + Line + "\n" + AppendixText);
        var index = AppendixIndex.Of(Book);
        var people = AppendixLabels.People(Book);
        var sources = AppendixLabels.Sources(Book, index);
        var missing = AppendixLabels.MissingSource(Services.GetRequiredService<IStringLocalizer<Strings>>());
        var snapshot = BookSnapshot.Create(Book, State.Session!.Vault, Book.Root.Key, true);

        var json = EditorDocMapper.ToJson(
            snapshot, null, null, id => people.GetValueOrDefault(id), id => sources.TryGetValue(id, out var s) ? s : missing);

        var para = JsonNode.Parse(json)!["content"]!.AsArray().Single(n => (string?)n!["type"] == "textBlock")!["content"]![0]!;
        var inline = para["content"]!.AsArray();
        Assert.Equal(
            ["text", "personRef", "text", "sourceCite", "text", "blockRef", "text", "sourceCite"],
            inline.Select(n => (string?)n!["type"]));
        Assert.Equal("AB", (string?)inline[1]!["attrs"]!["label"]);
        Assert.Equal("bold", (string?)inline[1]!["marks"]![0]!["type"]);
        Assert.Equal(("[1, S. 12_a*b]", "S. 12_a*b"), ((string?)inline[3]!["attrs"]!["label"], (string?)inline[3]!["attrs"]!["locator"]));
        Assert.Equal((L("CiteLabelMissing"), true), ((string?)inline[7]!["attrs"]!["label"], (bool)inline[7]!["attrs"]!["missing"]!));
        // "Laut " (5) + person + " steht es" (9) + citation + " und " (5) + reference + " " + citation.
        Assert.Equal(24, ShownText(para).Length);

        // Unchanged, the paragraph keeps its Markdown.
        var back = EditorDocMapper.FromJson(json, snapshot.ScopeKey, true);
        Assert.Equal(Line, back.Nodes.OfType<SnapshotTextBlock>().Single().Text);

        // Changed, it is written anew: the person as a reference, the citation with its locator as it was (no escapes).
        inline[0]!["text"] = "Nun laut ";
        back = EditorDocMapper.FromJson(para.Root.ToJsonString(), snapshot.ScopeKey, true);
        Assert.Equal("Nun laut " + Line[5..], back.Nodes.OfType<SnapshotTextBlock>().Single().Text);
    }

    [Fact]
    public async Task GlossaryHit_InMultiLineAndEscapedBlock_LandsOnTheWordInTheEditor()
    {
        // The second line starts with an escaped heading marker; the first has emphasis, an escaped star and a person.
        const string Block = "\t- Erste *Zeile* \\*x ((" + AnnaId + "))\n\t  \\# Zeile mit der Achtsamkeit\n";
        await OpenAsync(Head + "- # Eins\n" + Block + AppendixText);
        var hit = Assert.Single(AppendixIndex.Of(Book).GlossaryHits);
        var snapshot = BookSnapshot.Create(Book, State.Session!.Vault, Book.Root.Key, true);
        var people = AppendixLabels.People(Book);

        var json = EditorDocMapper.ToJson(snapshot, null, null, id => people.GetValueOrDefault(id));

        var block = JsonNode.Parse(json)!["content"]!.AsArray().Single(n => (string?)n!["attrs"]?["key"] == hit.ParagraphKey.ToString("D"))!;
        var shown = ShownText(block["content"]![0]!);
        Assert.Equal("Erste Zeile *x ￼\n# Zeile mit der Achtsamkeit", shown);
        Assert.Equal("Achtsamkeit", shown.Substring(hit.Start, hit.Length));
    }
}
