using System.Text;
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

/// <summary>
/// The status of a text block or detail changed in the section bar or with Ctrl+Shift+N, and „Bemerkung anfügen“
/// (status spec 2, „Ändern“, „Bemerkung anfügen“): each a command on the book file with one undo entry.
/// </summary>
public class StatusCommandUiTests : UiTestContext
{
    private const string BookPath = "pages/Buch - Alpha.md";

    private const string BookText =
        "title:: Alpha\ntype:: book\n\n" +
        "- # Eins\n" +
        "\t- Erster Text\n" +
        "\t  status:: relevant\n" +
        "\t\t- Ein Detail\n" +
        "\t\t- Notiz Detail #notiz\n" +
        "\t- Zweiter Text\n" +
        "- # Zwei\n" +
        "\t- Dritter Text\n";

    /// <summary>The book after a change below "Erster Text": the text block got <c>updated::</c> after its status line.</summary>
    private string Stamped => BookText.Replace("\t  status:: relevant\n", "\t  status:: relevant\n\t  updated:: " + Stamp + "\n");

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

    private Book Book => State.CurrentBook!;

    private string Text(string key) => Services.GetRequiredService<IStringLocalizer<Strings>>()[key].Value;

    private Guid KeyOf(string content) => Book.Page.AllBlocks().Single(b => b.Content == content).Key;

    private CursorInfo CursorOf(string content)
    {
        var element = BookElements.Find(Book, KeyOf(content))!;
        return new CursorInfo(element.Kind, element.Key, BookElements.TextBlockOf(Book, element)?.Key, 0);
    }

    /// <summary>Opens the vault, renders the editor and lets it report a visible marking box.</summary>
    private async Task<IRenderedComponent<EditorPane>> RenderAsync()
    {
        await OpenSessionAsync(_vault);
        var cut = Render<EditorPane>();
        cut.WaitForAssertion(() => Assert.NotEmpty(Editor.Documents));
        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionBoxMoved(120.5, true));
        return cut;
    }

    /// <summary>Moves the cursor (as the editor reports it) to the element whose block has <paramref name="content"/>.</summary>
    private Task MoveToAsync(IRenderedComponent<EditorPane> cut, string content) =>
        cut.InvokeAsync(() => Editor.Callbacks!.OnCursorChanged(CursorOf(content)));

    /// <summary>The status the selection in the bar shows (the value of its selected option).</summary>
    private static string? Shown(IRenderedComponent<EditorPane> cut) =>
        cut.Find("select.ne-sec-status").QuerySelector("option[selected]")?.GetAttribute("value");

    /// <summary>Asserts the book file and, with <see cref="LineDiff"/>, that exactly the given lines differ from <paramref name="before"/>.</summary>
    private void AssertBook(string before, string expected, string[] removed, string[] added)
    {
        var after = _vault.Read(BookPath);
        Assert.Equal(expected, after);
        var (removedIndices, addedIndices) = LineDiff.Changed(Encoding.UTF8.GetBytes(before), Encoding.UTF8.GetBytes(after));
        Assert.Equal(removed, removedIndices.Select(i => before.Split('\n')[i]));
        Assert.Equal(added, addedIndices.Select(i => after.Split('\n')[i]));
    }

    [Fact]
    public async Task StatusSelect_ShowsCurrent_ChangesFile_Undo()
    {
        var cut = await RenderAsync();
        var session = State.Session!;

        // The selection shows the status of the element at the cursor: from status::, from #notiz, or a draft.
        await MoveToAsync(cut, "Erster Text");
        cut.WaitForAssertion(() => Assert.Equal("Relevant", Shown(cut)));
        Assert.Equal(
            new[] { "StatusDraft", "StatusRelevant", "StatusRemark", "StatusWording", "StatusDone" }.Select(Text),
            cut.FindAll("select.ne-sec-status option").Select(option => option.TextContent.Trim()));
        Assert.Equal(Text("SectionStatus"), cut.Find("select.ne-sec-status").GetAttribute("title"));
        await MoveToAsync(cut, "Notiz Detail #notiz");
        cut.WaitForAssertion(() => Assert.Equal("Remark", Shown(cut)));
        await MoveToAsync(cut, "Ein Detail");
        cut.WaitForAssertion(() => Assert.Equal("Draft", Shown(cut)));

        // Changing it writes only the status:: line; the cursor stays where it is.
        var documents = Editor.Documents.Count;
        await cut.InvokeAsync(() => cut.Find("select.ne-sec-status").Change("Wording"));

        var wording = Stamped.Replace("\t\t- Ein Detail\n", "\t\t- Ein Detail\n\t\t  status:: formulierung\n");
        AssertBook(BookText, wording, [], ["\t  updated:: " + Stamp, "\t\t  status:: formulierung"]);
        cut.WaitForAssertion(() => Assert.Equal("Wording", Shown(cut)));
        Assert.Equal(Text("UndoStatus"), session.Undo.NextDescription);
        Assert.Empty(Editor.Reveals);
        Assert.Equal(KeyOf("Ein Detail"), State.Cursor!.Key);
        Assert.True(Editor.Documents.Count > documents);
        Assert.Contains("\"status\":\"formulierung\"", Editor.Json);
        Assert.Empty(cut.FindAll(".ne-editor-error"));

        await MoveToAsync(cut, "Erster Text");
        await cut.InvokeAsync(() => cut.Find("select.ne-sec-status").Change("Done"));

        AssertBook(
            wording,
            wording.Replace("\t  status:: relevant\n", "\t  status:: fertig\n"),
            ["\t  status:: relevant"],
            ["\t  status:: fertig"]);
        cut.WaitForAssertion(() => Assert.Equal("Done", Shown(cut)));

        // Each change is one undo entry.
        Assert.True(session.TryUndo(out var error), error?.ToString());
        Assert.Equal(wording, _vault.Read(BookPath));
        Assert.True(session.TryUndo(out error), error?.ToString());
        Assert.Equal(BookText, _vault.Read(BookPath));
        Assert.False(session.Undo.CanUndo);
    }

    [Fact]
    public async Task SplitButton_OnTextBlockOnly_SplitsInTheEditor()
    {
        var cut = await RenderAsync();

        // On a heading there is nothing to split.
        await MoveToAsync(cut, "# Zwei");
        cut.WaitForAssertion(() => Assert.True(cut.Find(".ne-sec-split").HasAttribute("disabled")));
        Assert.Equal(Text("SectionSplit"), cut.Find(".ne-sec-split").TextContent.Trim());
        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionCommand(SectionCommand.Split));
        Assert.DoesNotContain("split", Editor.Calls);

        // On a text block the editor splits it at the cursor, as with Ctrl+Enter; the book changes only through its save.
        await MoveToAsync(cut, "Dritter Text");
        cut.WaitForAssertion(() => Assert.False(cut.Find(".ne-sec-split").HasAttribute("disabled")));
        cut.Find(".ne-sec-split").Click();

        cut.WaitForAssertion(() => Assert.Single(Editor.Calls, c => c == "split"));
        Assert.Equal(BookText, _vault.Read(BookPath));
    }

    [Fact]
    public async Task StatusSelect_DisabledOnHeadingAndConflict()
    {
        var cut = await RenderAsync();
        var session = State.Session!;

        // A heading has no status: the selection and „Bemerkung anfügen“ are greyed out, and nothing changes.
        await MoveToAsync(cut, "# Zwei");
        cut.WaitForAssertion(() => Assert.True(cut.Find("select.ne-sec-status").HasAttribute("disabled")));
        Assert.True(cut.Find(".ne-sec-remark").HasAttribute("disabled"));
        Assert.Equal(Text("SectionAddRemark"), cut.Find(".ne-sec-remark").TextContent.Trim());
        Assert.False(ManuscriptCommands.CanSetStatus(State));
        Assert.Null(ManuscriptCommands.StatusAt(State));
        Assert.Null(
            await cut.InvokeAsync(() => ManuscriptCommands.SetStatusAsync(State, BlockStatus.Remark, "Status ändern", NullLogger.Instance)));
        Assert.False(ManuscriptCommands.CanRun(State, SectionCommand.ToggleRemark));
        Assert.False(ManuscriptCommands.CanRun(State, SectionCommand.AddRemark));
        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionCommand(SectionCommand.ToggleRemark));
        Assert.Equal(BookText, _vault.Read(BookPath));
        Assert.False(session.Undo.CanUndo);

        await MoveToAsync(cut, "Dritter Text");
        cut.WaitForAssertion(() => Assert.False(cut.Find("select.ne-sec-status").HasAttribute("disabled")));
        Assert.False(cut.Find(".ne-sec-remark").HasAttribute("disabled"));

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

        cut.WaitForAssertion(() => Assert.True(cut.Find("select.ne-sec-status").HasAttribute("disabled")));
        Assert.True(cut.Find(".ne-sec-remark").HasAttribute("disabled"));
        Assert.False(ManuscriptCommands.CanSetStatus(State));
        Assert.False(ManuscriptCommands.CanRun(State, SectionCommand.ToggleRemark));

        // The local change of the conflict counts as the editor's unsaved text, which refuses first; without that check
        // the open conflict refuses the commands.
        State.HasUnsavedEditorText = null;
        Assert.Equal(
            new AdoptMessage("EditorConflict", true),
            await cut.InvokeAsync(() => ManuscriptCommands.SetStatusAsync(State, BlockStatus.Remark, "Status ändern", NullLogger.Instance)));
        Assert.Equal(
            new AdoptMessage("EditorConflict", true),
            await cut.InvokeAsync(() => ManuscriptCommands.RunAsync(State, SectionCommand.ToggleRemark, "Status ändern", NullLogger.Instance)));
        Assert.Equal(
            new AdoptMessage("EditorConflict", true),
            await cut.InvokeAsync(() => ManuscriptCommands.RunAsync(State, SectionCommand.AddRemark, "Bemerkung anfügen", NullLogger.Instance)));
        Assert.Equal(external, _vault.Read(BookPath));
        Assert.False(session.Undo.CanUndo);
    }

    [Fact]
    public async Task StatusSelect_DisabledOnReadOnlyBook()
    {
        // An unclosed code fence makes the book read-only.
        var path = Path.Combine(_vault.Root, BookPath);
        File.WriteAllText(path, BookText + "- ```\n  offen\n");
        var cut = await RenderAsync();
        Assert.True(Book.Page.IsReadOnly);

        await MoveToAsync(cut, "Dritter Text");

        cut.WaitForAssertion(() => Assert.True(cut.Find("select.ne-sec-status").HasAttribute("disabled")));
        Assert.True(cut.Find(".ne-sec-remark").HasAttribute("disabled"));
        Assert.Equal(
            new AdoptMessage("EditorReadOnly", true),
            await cut.InvokeAsync(() => ManuscriptCommands.SetStatusAsync(State, BlockStatus.Remark, "Status ändern", NullLogger.Instance)));
        Assert.Equal(BookText + "- ```\n  offen\n", _vault.Read(BookPath));
        Assert.False(State.Session!.Undo.CanUndo);
    }

    [Fact]
    public async Task ToggleRemark_ViaEditorCommand_DraftToRemarkAndBack()
    {
        var cut = await RenderAsync();
        var session = State.Session!;

        // Ctrl+Shift+N on a draft detail makes it a remark; the detail stays in the file, only the status:: line is new.
        await MoveToAsync(cut, "Ein Detail");
        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionCommand(SectionCommand.ToggleRemark));

        var remark = Stamped.Replace("\t\t- Ein Detail\n", "\t\t- Ein Detail\n\t\t  status:: bemerkung\n");
        AssertBook(BookText, remark, [], ["\t  updated:: " + Stamp, "\t\t  status:: bemerkung"]);
        Assert.Equal(Text("UndoStatus"), session.Undo.NextDescription);
        Assert.Empty(Editor.Reveals);
        Assert.Contains("\"status\":\"bemerkung\"", Editor.Json);
        cut.WaitForAssertion(() => Assert.Equal("Remark", Shown(cut)));
        Assert.Empty(cut.FindAll(".ne-editor-error"));

        // Again: back to a draft, the line goes; the detail is still there.
        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionCommand(SectionCommand.ToggleRemark));

        Assert.Equal(Stamped, _vault.Read(BookPath));
        Assert.Equal(KeyOf("Ein Detail"), State.Cursor!.Key);

        // Any other status becomes a remark.
        await MoveToAsync(cut, "Erster Text");
        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionCommand(SectionCommand.ToggleRemark));

        var relevantToRemark = Stamped.Replace("\t  status:: relevant\n", "\t  status:: bemerkung\n");
        AssertBook(Stamped, relevantToRemark, ["\t  status:: relevant"], ["\t  status:: bemerkung"]);

        // A remark by #notiz becomes a draft: the tag goes.
        await MoveToAsync(cut, "Notiz Detail #notiz");
        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionCommand(SectionCommand.ToggleRemark));

        AssertBook(
            relevantToRemark,
            relevantToRemark.Replace("\t\t- Notiz Detail #notiz\n", "\t\t- Notiz Detail\n"),
            ["\t\t- Notiz Detail #notiz"],
            ["\t\t- Notiz Detail"]);

        // Each toggle is one undo entry.
        Assert.True(session.TryUndo(out var error), error?.ToString());
        Assert.Equal(relevantToRemark, _vault.Read(BookPath));
        Assert.True(session.TryUndo(out error), error?.ToString());
        Assert.Equal(Stamped, _vault.Read(BookPath));
    }

    [Fact]
    public async Task AddRemark_Button_AppendsDetail_RevealsIt_Undo()
    {
        var cut = await RenderAsync();
        var session = State.Session!;

        // From a detail: the remark goes after the last detail of its text block.
        await MoveToAsync(cut, "Ein Detail");
        cut.WaitForAssertion(() => Assert.False(cut.Find(".ne-sec-remark").HasAttribute("disabled")));
        await cut.InvokeAsync(() => cut.Find(".ne-sec-remark").Click());

        AssertBook(
            BookText,
            Stamped.Replace("\t\t- Notiz Detail #notiz\n", "\t\t- Notiz Detail #notiz\n\t\t-\n\t\t  status:: bemerkung\n"),
            [],
            ["\t  updated:: " + Stamp, "\t\t-", "\t\t  status:: bemerkung"]);
        var added = Book.FindTextBlock(KeyOf("Erster Text"))!.Paragraphs[^1].Block.Key;
        Assert.Equal(BlockStatus.Remark, Book.FindTextBlock(KeyOf("Erster Text"))!.Paragraphs[^1].Status);
        cut.WaitForAssertion(() => Assert.Equal(added, Assert.Single(Editor.Reveals)));
        Assert.Equal((ElementKind.Detail, added), (State.Cursor!.Kind, State.Cursor.Key));
        Assert.Equal(Text("UndoAddRemark"), session.Undo.NextDescription);
        Assert.Empty(cut.FindAll(".ne-editor-error"));

        Assert.True(session.TryUndo(out var error), error?.ToString());

        Assert.Equal(BookText, _vault.Read(BookPath));
        Assert.False(session.Undo.CanUndo);
    }

    [Fact]
    public async Task Status_UnsavedEditorText_Refused()
    {
        var session = await OpenSessionAsync(_vault);
        State.Cursor = CursorOf("Ein Detail");
        State.FlushEditor = () => Task.CompletedTask;
        State.HasUnsavedEditorText = () => true;
        var unsaved = new AdoptMessage("SectionCommandUnsaved", true);

        Assert.Equal(unsaved, await ManuscriptCommands.SetStatusAsync(State, BlockStatus.Done, "Status ändern", NullLogger.Instance));
        Assert.Equal(unsaved, await ManuscriptCommands.RunAsync(State, SectionCommand.ToggleRemark, "Status ändern", NullLogger.Instance));
        Assert.Equal(unsaved, await ManuscriptCommands.RunAsync(State, SectionCommand.AddRemark, "Bemerkung anfügen", NullLogger.Instance));

        // A flush that throws is refused the same way.
        State.HasUnsavedEditorText = null;
        State.FlushEditor = () => throw new IOException("Kein Speichern.");
        Assert.Equal(unsaved, await ManuscriptCommands.SetStatusAsync(State, BlockStatus.Done, "Status ändern", NullLogger.Instance));

        Assert.Equal(BookText, _vault.Read(BookPath));
        Assert.Equal(BlockStatus.Draft, Book.FindTextBlock(KeyOf("Erster Text"))!.Paragraphs[0].Status);
        Assert.False(session.Undo.CanUndo);
    }
}
