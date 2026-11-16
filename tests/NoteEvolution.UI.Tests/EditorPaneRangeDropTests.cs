using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Editor;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>
/// The marked range dragged by the grip of the section bar (<see cref="AppState.DraggedRange"/>) dropped in an editor
/// (Markierung verschieben, spec 5): the editor's drop callback moves it onto the element under the pointer, in this
/// editor's view, which becomes the active one first.
/// </summary>
public class EditorPaneRangeDropTests : UiTestContext
{
    private const string BookPath = "pages/Buch - Alpha.md";

    private const string NotesPath = "pages/Ideen.md";

    private const string NotesText = "- Gedächtnis braucht Schlaf\n";

    private const string Head = "title:: Alpha\ntype:: book\n\n";

    private const string Vorspann = "- Vorspann Text\n";

    private const string EinsHead = "- # Eins\n";

    private const string Erster = "\t- Erster Text\n";

    private const string ErsterDetails = "\t\t- Detail A\n\t\t\t- Tiefer A\n\t\t- Detail B\n";

    private const string Zweiter = "\t- Zweiter Text\n";

    private const string EinsA = "\t- ## Eins-A\n\t\t- Unter Text\n";

    private const string Zwei = "- # Zwei\n\t- Dritter Text\n";

    private const string BookText = Head + Vorspann + EinsHead + Erster + ErsterDetails + Zweiter + EinsA + Zwei;

    private readonly TestVault _vault = TestVault.Create((BookPath, BookText), (NotesPath, NotesText));

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

    private string FileText => _vault.Read(BookPath);

    private string Text(string key) => Services.GetRequiredService<IStringLocalizer<Strings>>()[key].Value;

    private Guid KeyOf(string content) => Book.Page.AllBlocks().Single(b => b.Content == content).Key;

    private BookElement ElementOf(string content) => BookElements.Find(Book, KeyOf(content))!;

    private async Task<IRenderedComponent<EditorPane>> RenderAsync()
    {
        await OpenSessionAsync(_vault);
        var cut = Render<EditorPane>();
        cut.WaitForAssertion(() => Assert.NotEmpty(Editor.Documents));
        return cut;
    }

    /// <summary>
    /// Drags <paramref name="head"/> by the grip (a note key left behind by an earlier drag too) and drops it on
    /// <paramref name="element"/> (<c>null</c>: above the first element) as the editor's script reports it.
    /// </summary>
    private async Task DropAsync(IRenderedComponent<EditorPane> cut, string head, string? element)
    {
        var target = element is null ? (Guid?)null : KeyOf(element);
        State.DraggedNoteKey = State.Session!.Notes.All().Single().Key;
        State.DraggedRange = ElementOf(head);
        await cut.InvokeAsync(() => Editor.Callbacks!.OnRangeDropped(target));
    }

    [Fact]
    public async Task MoveRange_Heading_DroppedOnATextBlock_GoesAfterItsSection_NoNoteAdopted()
    {
        var cut = await RenderAsync();

        await DropAsync(cut, "## Eins-A", "Dritter Text");

        Assert.Equal(Head + Vorspann + EinsHead + Erster + ErsterDetails + Zweiter + Zwei + "- # Eins-A\n\t- Unter Text\n", FileText);
        Assert.Equal(NotesText, _vault.Read(NotesPath));
        Assert.Null(State.DraggedRange);
        Assert.Equal(Text("UndoMoveRange"), State.Session!.Undo.NextDescription);
        Assert.Empty(cut.FindAll(".ne-editor-error"));
        cut.WaitForAssertion(() => Assert.Equal(KeyOf("# Eins-A"), Editor.Reveals.LastOrDefault()));
    }

    [Fact]
    public async Task MoveRange_TextBlock_DroppedOnATextBlock_GoesAfterIt()
    {
        var cut = await RenderAsync();

        await DropAsync(cut, "Zweiter Text", "Dritter Text");

        Assert.Equal(Head + Vorspann + EinsHead + Erster + ErsterDetails + EinsA + Zwei + Zweiter, FileText);
        Assert.Equal(NotesText, _vault.Read(NotesPath));
    }

    [Fact]
    public async Task MoveRange_TextBlock_DroppedAboveTheFirstElement_FirstOfThePrologue()
    {
        var cut = await RenderAsync();

        await DropAsync(cut, "Dritter Text", null);

        Assert.Equal(Head + "- Dritter Text\n" + Vorspann + EinsHead + Erster + ErsterDetails + Zweiter + EinsA + "- # Zwei\n", FileText);
    }

    [Fact]
    public async Task MoveRange_Detail_DroppedOnATextBlock_BecomesItsFirstSubPoint()
    {
        var cut = await RenderAsync();

        await DropAsync(cut, "Detail B", "Dritter Text");

        // The change history may give the text blocks whose sub-points changed an id:: when they are saved.
        Assert.EndsWith("\t\t- Detail B\n", FileText);
        Assert.Contains("\t\t- Detail A\n\t\t\t- Tiefer A\n\t- Zweiter Text\n", FileText);
        Assert.Equal(["Detail B"], Book.FindTextBlock(KeyOf("Dritter Text"))!.Paragraphs.Select(p => p.Block.Content));
        Assert.Equal(NotesText, _vault.Read(NotesPath));
    }

    [Fact]
    public async Task MoveRange_Refused_ShowsTheMessage_ChangesNothing()
    {
        var cut = await RenderAsync();

        // Into its own section.
        await DropAsync(cut, "# Eins", "Erster Text");

        Assert.Equal(BookText, FileText);
        cut.WaitForAssertion(() => Assert.Equal(Text("OutlineMoveInvalid"), cut.Find(".ne-editor-error").TextContent));
        Assert.Null(State.DraggedRange);
    }

    [Fact]
    public async Task MoveRange_WithoutADraggedRange_DropMovesNothing()
    {
        var cut = await RenderAsync();

        await cut.InvokeAsync(() => Editor.Callbacks!.OnRangeDropped(KeyOf("Dritter Text")));

        Assert.Equal(BookText, FileText);
        Assert.False(State.Session!.Undo.CanUndo);
    }

    [Fact]
    public async Task MoveRange_DroppedInTheOtherHalfsEditor_MovesThere_MakesThatViewActive()
    {
        UseEditorPerPane();
        await OpenSessionAsync(_vault);
        State.SetSplit(true);
        var upper = Render<EditorPane>(p => p.Add(e => e.View, State.Views[0]));
        var lower = Render<EditorPane>(p => p.Add(e => e.View, State.Views[1]));
        upper.WaitForAssertion(() => Assert.NotEmpty(Editors[0].Documents));
        lower.WaitForAssertion(() => Assert.NotEmpty(Editors[1].Documents));
        Assert.Same(State.Views[0], State.ActiveView);

        // The range is marked and dragged in the upper half, and dropped in the lower half's editor.
        var head = ElementOf("Zweiter Text");
        State.Cursor = new CursorInfo(head.Kind, head.Key, head.Key, 0);
        State.DraggedRange = head;
        await lower.InvokeAsync(() => Editors[1].Callbacks!.OnRangeDropped(KeyOf("Dritter Text")));

        Assert.Equal(Head + Vorspann + EinsHead + Erster + ErsterDetails + EinsA + Zwei + Zweiter, FileText);
        Assert.Same(State.Views[1], State.ActiveView);
        lower.WaitForAssertion(() => Assert.Equal(KeyOf("Zweiter Text"), Editors[1].Reveals.LastOrDefault()));
        Assert.Null(State.DraggedRange);
    }
}
