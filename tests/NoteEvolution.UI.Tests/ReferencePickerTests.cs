using System.Text.RegularExpressions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Editor;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>„Verweis einfügen“ in the section bar (cross references spec 2.2): the search for the target and the command.</summary>
public partial class ReferencePickerTests : UiTestContext
{
    private const string BookPath = "pages/Buch - Alpha.md";

    private const string BookText =
        "title:: Alpha\ntype:: book\n\n" +
        "- # Eins\n" +
        "\t- Erster **fetter** Text\n" +
        "\t\t- Ein Detail\n" +
        "\t- ## Unter\n" +
        "\t\t- Zweiter Text\n" +
        "- # Zwei\n" +
        "\t- Dritter Text\n";

    private static readonly string[] AllCandidates =
        ["Eins", "Eins › Erster fetter Text", "Eins › Unter", "Unter › Zweiter Text", "Zwei", "Zwei › Dritter Text"];

    private readonly TestVault _vault = TestVault.Create((BookPath, BookText));

    protected override void Dispose(bool disposing)
    {
        // The session is closed first, then the vault folder is deleted.
        base.Dispose(disposing);
        if (disposing)
        {
            _vault.Dispose();
        }
    }

    [GeneratedRegex(@"id:: ([0-9a-f-]{36})")]
    private static partial Regex IdRegex();

    private Book Book => State.CurrentBook!;

    private string Text(string key) => Services.GetRequiredService<IStringLocalizer<Strings>>()[key].Value;

    private Guid KeyOf(string content) => Book.Page.AllBlocks().Single(b => b.Content == content).Key;

    /// <summary>Opens the vault, renders the editor and lets it report a visible marking box.</summary>
    private async Task<IRenderedComponent<EditorPane>> RenderAsync()
    {
        await OpenSessionAsync(_vault);
        var cut = Render<EditorPane>();
        cut.WaitForAssertion(() => Assert.NotEmpty(Editor.Documents));
        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionBoxMoved(120.5, true));
        return cut;
    }

    /// <summary>Moves the cursor (as the editor reports it) into the element whose block has <paramref name="content"/>.</summary>
    private Task MoveToAsync(IRenderedComponent<EditorPane> cut, string content, int offset = 0)
    {
        var element = BookElements.Find(Book, KeyOf(content))!;
        var cursor = new CursorInfo(element.Kind, element.Key, BookElements.TextBlockOf(Book, element)?.Key, offset);
        return cut.InvokeAsync(() => Editor.Callbacks!.OnCursorChanged(cursor));
    }

    private static IEnumerable<string> Options(IRenderedComponent<EditorPane> cut) =>
        cut.FindAll(".ne-ref-option").Select(option => option.TextContent.Trim());

    private static string? Selected(IRenderedComponent<EditorPane> cut) =>
        cut.FindAll(".ne-ref-option.ne-ref-selected").SingleOrDefault()?.TextContent.Trim();

    private static Task OpenAsync(IRenderedComponent<EditorPane> cut) => cut.InvokeAsync(() => cut.Find(".ne-sec-ref").Click());

    private static Task KeyAsync(IRenderedComponent<EditorPane> cut, string key) =>
        cut.InvokeAsync(() => cut.Find(".ne-ref-query").KeyDown(key));

    [Fact]
    public async Task Button_EnabledOnlyForTextBlocksAndDetails()
    {
        var cut = await RenderAsync();

        await MoveToAsync(cut, "# Eins");
        cut.WaitForAssertion(() => Assert.True(cut.Find(".ne-sec-ref").HasAttribute("disabled")));
        Assert.False(ManuscriptCommands.CanInsertReference(State));
        Assert.Equal(Text("SectionInsertRef"), cut.Find(".ne-sec-ref").TextContent.Trim());

        await MoveToAsync(cut, "Erster **fetter** Text");
        cut.WaitForAssertion(() => Assert.False(cut.Find(".ne-sec-ref").HasAttribute("disabled")));
        Assert.True(ManuscriptCommands.CanInsertReference(State));

        await MoveToAsync(cut, "Ein Detail");
        cut.WaitForAssertion(() => Assert.False(cut.Find(".ne-sec-ref").HasAttribute("disabled")));

        // An open search closes when the cursor moves to a heading.
        await OpenAsync(cut);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".ne-ref-picker")));
        await MoveToAsync(cut, "## Unter");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-ref-picker")));

        // Back in a text block it stays closed (it would take the focus from the editor).
        await MoveToAsync(cut, "Dritter Text");
        cut.WaitForAssertion(() => Assert.False(cut.Find(".ne-sec-ref").HasAttribute("disabled")));
        Assert.Empty(cut.FindAll(".ne-ref-picker"));
        Assert.Equal("false", cut.Find(".ne-sec-ref").GetAttribute("aria-expanded"));
    }

    [Fact]
    public void Picker_ReferenceInATextBlock_ShowsAsArrow()
    {
        var text = BookText.Replace("\t- Dritter Text\n", "\t- Dritter ((aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa)) Text\n");
        var book = Book.Load(LogseqParser.Parse("Buch - Alpha.md", System.Text.Encoding.UTF8.GetBytes(text)));

        var cut = Render<ReferencePicker>(p => p.Add(x => x.Book, book));

        Assert.Contains("Zwei › Dritter → Text", cut.FindAll(".ne-ref-option").Select(o => o.TextContent.Trim()));
    }

    [Fact]
    public void Picker_ShowsAtMost200Matches_FilterFirst()
    {
        var text = "title:: Alpha\ntype:: book\n\n- # Eins\n" + string.Concat(Enumerable.Range(1, 250).Select(i => $"\t- Text {i}\n"));
        var book = Book.Load(LogseqParser.Parse("Buch - Alpha.md", System.Text.Encoding.UTF8.GetBytes(text)));

        var cut = Render<ReferencePicker>(p => p.Add(x => x.Book, book));

        // 251 candidates: the first 200 are shown, the rest is counted; typing narrows it.
        Assert.Equal(200, cut.FindAll(".ne-ref-option").Count);
        Assert.Equal(string.Format(Text("RefPickerMore"), 51), cut.Find(".ne-ref-more").TextContent.Trim());
        cut.Find(".ne-ref-query").Input("Text 24");
        Assert.Equal(
            ["Eins › Text 24", "Eins › Text 240", "Eins › Text 241", "Eins › Text 242", "Eins › Text 243", "Eins › Text 244",
             "Eins › Text 245", "Eins › Text 246", "Eins › Text 247", "Eins › Text 248", "Eins › Text 249"],
            cut.FindAll(".ne-ref-option").Select(o => o.TextContent.Trim()));
        Assert.Empty(cut.FindAll(".ne-ref-more"));

        // The arrow keys stop at the last option shown.
        cut.Find(".ne-ref-query").Input("");
        for (var i = 0; i < 210; i++)
        {
            cut.Find(".ne-ref-query").KeyDown("ArrowDown");
        }

        Assert.Equal("Eins › Text 199", cut.Find(".ne-ref-option.ne-ref-selected").TextContent.Trim());
    }

    [Fact]
    public async Task Picker_ListsHeadingsAndTextBlocks_FiltersIgnoringCase()
    {
        var cut = await RenderAsync();

        // From a detail every heading and text block is offered, in book order.
        await MoveToAsync(cut, "Ein Detail");
        await OpenAsync(cut);
        cut.WaitForAssertion(() => Assert.Equal(AllCandidates, Options(cut)));
        Assert.Equal(["ne-ref-heading", "ne-ref-text", "ne-ref-heading", "ne-ref-text", "ne-ref-heading", "ne-ref-text"],
            cut.FindAll(".ne-ref-option").Select(option => option.ClassList.Contains("ne-ref-heading") ? "ne-ref-heading" : "ne-ref-text"));
        Assert.Equal("Eins", Selected(cut));

        await cut.InvokeAsync(() => cut.Find(".ne-ref-query").Input("UNTER"));
        cut.WaitForAssertion(() => Assert.Equal(["Eins › Unter", "Unter › Zweiter Text"], Options(cut)));

        await cut.InvokeAsync(() => cut.Find(".ne-ref-query").Input("fetter"));
        cut.WaitForAssertion(() => Assert.Equal(["Eins › Erster fetter Text"], Options(cut)));

        await cut.InvokeAsync(() => cut.Find(".ne-ref-query").Input("nirgends"));
        cut.WaitForAssertion(() => Assert.Empty(Options(cut)));
        Assert.Equal(Text("RefPickerNone"), cut.Find(".ne-ref-none").TextContent.Trim());

        // From a text block it is not offered as its own target.
        await KeyAsync(cut, "Escape");
        await MoveToAsync(cut, "Erster **fetter** Text");
        await OpenAsync(cut);
        cut.WaitForAssertion(() => Assert.Equal(AllCandidates.Where(c => c != "Eins › Erster fetter Text"), Options(cut)));
    }

    [Fact]
    public async Task ArrowsAndEnter_InsertAtCursorInsideBold_OneUndoStep()
    {
        var cut = await RenderAsync();
        var session = State.Session!;

        // The editor shows "Erster fetter Text"; offset 9 is after "Erster fe".
        await MoveToAsync(cut, "Erster **fetter** Text", 9);
        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionCommand(SectionCommand.RangeUp));
        var level = State.RangeLevel;
        Assert.NotEqual(0, level);
        await OpenAsync(cut);
        await cut.InvokeAsync(() => cut.Find(".ne-ref-query").Input("zwei"));
        cut.WaitForAssertion(() => Assert.Equal(["Unter › Zweiter Text", "Zwei", "Zwei › Dritter Text"], Options(cut)));
        await KeyAsync(cut, "ArrowDown");
        await KeyAsync(cut, "ArrowDown");
        await KeyAsync(cut, "ArrowDown");
        cut.WaitForAssertion(() => Assert.Equal("Zwei › Dritter Text", Selected(cut)));
        await KeyAsync(cut, "ArrowUp");
        cut.WaitForAssertion(() => Assert.Equal("Zwei", Selected(cut)));

        // Each arrow key keeps the selected option in view; at the end of the list it stays on the last one.
        Assert.Equal(
            [1, 2, 2, 1],
            JSInterop.Invocations["neLayout.scrollOptionIntoView"].Select(call => (int)call.Arguments[1]!));
        await KeyAsync(cut, "Enter");

        // The heading gets an id:: (no date), the reference goes in inside the bold text, its text block is stamped.
        var after = _vault.Read(BookPath);
        var id = IdRegex().Match(after).Groups[1].Value;
        Assert.Equal(
            BookText
                .Replace("\t- Erster **fetter** Text\n", $"\t- Erster **fe(({id}))tter** Text\n\t  updated:: {Stamp}\n")
                .Replace("- # Zwei\n", $"- # Zwei\n  id:: {id}\n"),
            after);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-ref-picker")));
        Assert.Equal(Text("UndoReference"), session.Undo.NextDescription);
        // The cursor goes right after the reference (9 + the one position the editor gives it); the range keeps its level.
        var element = KeyOf($"Erster **fe(({id}))tter** Text");
        cut.WaitForAssertion(() => Assert.Equal([element], Editor.Reveals));
        Assert.Equal([10], Editor.RevealOffsets);
        Assert.Equal((element, 10), (State.Cursor!.Key, State.Cursor.Offset));
        Assert.Equal(level, State.RangeLevel);
        Assert.Empty(cut.FindAll(".ne-editor-error"));

        // One undo takes back the reference and the id.
        Assert.True(session.TryUndo(out var error), error?.ToString());
        Assert.Equal(BookText, _vault.Read(BookPath));
        Assert.False(session.Undo.CanUndo);
    }

    [Fact]
    public async Task Escape_Cancels_ClickInserts()
    {
        var cut = await RenderAsync();

        await MoveToAsync(cut, "Ein Detail", 3);
        await OpenAsync(cut);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".ne-ref-picker")));
        await KeyAsync(cut, "Escape");

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-ref-picker")));
        Assert.Equal(BookText, _vault.Read(BookPath));
        Assert.False(State.Session!.Undo.CanUndo);

        // A click on an option inserts too; a detail's reference stamps its text block.
        await OpenAsync(cut);
        await cut.InvokeAsync(() => cut.FindAll(".ne-ref-option").Single(o => o.TextContent.Trim() == "Zwei › Dritter Text").Click());

        var after = _vault.Read(BookPath);
        var id = IdRegex().Match(after).Groups[1].Value;
        Assert.Equal(
            BookText
                .Replace("\t- Erster **fetter** Text\n", $"\t- Erster **fetter** Text\n\t  updated:: {Stamp}\n")
                .Replace("\t\t- Ein Detail\n", $"\t\t- Ein(({id})) Detail\n")
                .Replace("\t- Dritter Text\n", $"\t- Dritter Text\n\t  id:: {id}\n"),
            after);
    }

    [Fact]
    public async Task ReadOnlyBook_Refused()
    {
        // An unclosed code fence makes the book read-only.
        var path = Path.Combine(_vault.Root, BookPath);
        File.WriteAllText(path, BookText + "- ```\n  offen\n");
        var cut = await RenderAsync();
        Assert.True(Book.Page.IsReadOnly);

        await MoveToAsync(cut, "Dritter Text");

        cut.WaitForAssertion(() => Assert.True(cut.Find(".ne-sec-ref").HasAttribute("disabled")));
        Assert.False(ManuscriptCommands.CanInsertReference(State));
        Assert.Equal(
            new AdoptMessage("EditorReadOnly", true),
            await cut.InvokeAsync(() => ManuscriptCommands.InsertReferenceAsync(State, KeyOf("# Eins"), "Verweis einfügen", NullLogger.Instance)));
        Assert.Equal(BookText + "- ```\n  offen\n", _vault.Read(BookPath));
        Assert.False(State.Session!.Undo.CanUndo);
    }

    [Fact]
    public async Task OpenConflict_Refused()
    {
        var cut = await RenderAsync();
        var session = State.Session!;
        await MoveToAsync(cut, "Dritter Text");
        var target = KeyOf("# Eins");

        // A local change of the heading collides with an external change of the same line (R26).
        var path = Path.Combine(_vault.Root, BookPath);
        var external = BookText.Replace("- # Zwei\n", "- # Zwei extern\n");
        await cut.InvokeAsync(() =>
        {
            OutlineEditor.Rename(Book, KeyOf("# Zwei"), "Zwei lokal");
            File.WriteAllText(path, external);
            session.HandleExternalChange(path);
        });
        Assert.True(session.HasOpenConflict(path));

        cut.WaitForAssertion(() => Assert.True(cut.Find(".ne-sec-ref").HasAttribute("disabled")));
        Assert.False(ManuscriptCommands.CanInsertReference(State));

        // The local change of the conflict counts as the editor's unsaved text, which refuses first; without that check
        // the open conflict refuses the command.
        State.HasUnsavedEditorText = null;
        Assert.Equal(
            new AdoptMessage("EditorConflict", true),
            await cut.InvokeAsync(() => ManuscriptCommands.InsertReferenceAsync(State, target, "Verweis einfügen", NullLogger.Instance)));
        Assert.Equal(external, _vault.Read(BookPath));
        Assert.False(session.Undo.CanUndo);
    }
}
