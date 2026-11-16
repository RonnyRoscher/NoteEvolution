using AngleSharp.Dom;
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
/// The marked range dragged by the grip of the section bar (<see cref="AppState.DraggedRange"/>) dropped on an outline
/// (Markierung verschieben, spec 5): a heading head as a section dragged in the outline (on, before, at the end), a text
/// block or detail head as text blocks dragged out of an editor (only on a heading or the prologue). Also the outline's
/// own section drag, which takes the section's read tail along (spec 3).
/// </summary>
public class OutlineRangeDropTests : UiTestContext
{
    private const string BookPath = "pages/Buch - Alpha.md";

    private const string Head = "title:: Alpha\ntype:: book\n\n";

    private const string Vorspann = "- Vorspann Text\n";

    private const string EinsHead = "- # Eins\n";

    private const string Erster = "\t- Erster Text\n";

    private const string ErsterDetails = "\t\t- Detail A\n\t\t\t- Tiefer A\n\t\t- Detail B\n";

    private const string Zweiter = "\t- Zweiter Text\n";

    private const string EinsA = "\t- ## Eins-A\n\t\t- Unter Text\n";

    private const string Zwei = "- # Zwei\n\t- Dritter Text\n";

    private const string BookText = Head + Vorspann + EinsHead + Erster + ErsterDetails + Zweiter + EinsA + Zwei;

    private TestVault _vault = TestVault.Create((BookPath, BookText));

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

    private static IElement Item(IRenderedComponent<OutlinePane> cut, string title) =>
        cut.FindAll(".ne-outline-item").First(li => li.QuerySelector(".ne-outline-row .ne-outline-title")?.TextContent == title);

    private static IElement Row(IRenderedComponent<OutlinePane> cut, string title) =>
        Item(cut, title).QuerySelector(".ne-outline-row")!;

    private static IElement Before(IRenderedComponent<OutlinePane> cut, string title) =>
        Item(cut, title).QuerySelector(".ne-drop-before")!;

    private async Task<IRenderedComponent<OutlinePane>> RenderAsync()
    {
        await OpenSessionAsync(_vault);
        return Render<OutlinePane>();
    }

    /// <summary>Drags <paramref name="head"/> by the grip, as the section bar records it.</summary>
    private async Task DragAsync(IRenderedComponent<OutlinePane> cut, string head)
    {
        var element = ElementOf(head);
        await cut.InvokeAsync(() => State.DraggedRange = element);
    }

    [Fact]
    public async Task Outline_DraggedHeadingRange_OnBeforeEnd()
    {
        var cut = await RenderAsync();

        // On a heading: its last sub-section. The hints are those of a dragged section.
        await DragAsync(cut, "## Eins-A");
        await Before(cut, "Zwei").DragEnterAsync(new());
        Assert.Contains("drop-here", Before(cut, "Zwei").ClassList);
        await Row(cut, "Zwei").DragEnterAsync(new());
        Assert.Contains("drop-onto", Row(cut, "Zwei").ClassList);
        await Row(cut, "Zwei").DropAsync(new());

        Assert.Equal(Head + Vorspann + EinsHead + Erster + ErsterDetails + Zweiter + Zwei + EinsA, FileText);
        Assert.Null(State.DraggedRange);
        Assert.Equal(Text("UndoMoveRange"), State.Session!.Undo.NextDescription);
        Assert.Empty(cut.FindAll(".ne-outline-error"));
        Assert.Empty(cut.FindAll(".drop-here, .drop-onto"));

        // Before a heading: in front of it, on its level.
        await DragAsync(cut, "## Eins-A");
        await Before(cut, "Zwei").DropAsync(new());

        Assert.Equal(Head + Vorspann + EinsHead + Erster + ErsterDetails + Zweiter + "- # Eins-A\n\t- Unter Text\n" + Zwei, FileText);

        // At the end: the last top-level section.
        await DragAsync(cut, "# Eins");
        await cut.Find(".ne-drop-end").DragEnterAsync(new());
        Assert.Contains("drop-here", cut.Find(".ne-drop-end").ClassList);
        await cut.Find(".ne-drop-end").DropAsync(new());

        Assert.Equal(Head + Vorspann + "- # Eins-A\n\t- Unter Text\n" + Zwei + EinsHead + Erster + ErsterDetails + Zweiter, FileText);
        Assert.Null(State.DraggedRange);
    }

    [Fact]
    public async Task Outline_DraggedHeadingRange_IntoItsOwnSection_ShowsTheMessage()
    {
        var cut = await RenderAsync();

        await DragAsync(cut, "# Eins");
        await Row(cut, "Eins-A").DropAsync(new());

        Assert.Equal(BookText, FileText);
        Assert.Equal(Text("OutlineMoveInvalid"), cut.Find(".ne-outline-error").TextContent);
        Assert.Null(State.DraggedRange);
    }

    [Fact]
    public async Task Outline_DraggedTextBlockRange_OnAHeading_EndOfItsText()
    {
        var cut = await RenderAsync();

        // The hints are those of dragged text blocks: on a heading or the prologue, not before a heading.
        await DragAsync(cut, "Zweiter Text");
        await Before(cut, "Zwei").DragEnterAsync(new());
        Assert.DoesNotContain("drop-here", Before(cut, "Zwei").ClassList);
        await Row(cut, "Vorspann").DragEnterAsync(new());
        Assert.Contains("drop-onto", Row(cut, "Vorspann").ClassList);
        await Row(cut, "Zwei").DragEnterAsync(new());
        Assert.Contains("drop-onto", Row(cut, "Zwei").ClassList);
        await Row(cut, "Zwei").DropAsync(new());

        Assert.Equal(Head + Vorspann + EinsHead + Erster + ErsterDetails + EinsA + Zwei + Zweiter, FileText);
        Assert.Null(State.DraggedRange);
        Assert.Equal(Text("UndoMoveRange"), State.Session!.Undo.NextDescription);
    }

    [Fact]
    public async Task Outline_DraggedDetailRange_OnAHeading_BecomesItsLastTextBlock()
    {
        var cut = await RenderAsync();

        await DragAsync(cut, "Detail A");
        await Row(cut, "Zwei").DropAsync(new());

        var zwei = Book.Root.Children.Single(n => n.Title == "Zwei");
        var last = zwei.TextBlocks.Last();
        Assert.Equal("Detail A", last.Block.Content);
        Assert.Equal(["Tiefer A"], last.Paragraphs.Select(p => p.Block.Content));
        Assert.Equal(["Detail B"], Book.FindTextBlock(KeyOf("Erster Text"))!.Paragraphs.Select(p => p.Block.Content));
        Assert.Equal(Text("UndoMoveRange"), State.Session!.Undo.NextDescription);

        // The undo takes the move back with the new text block's dates; the id:: the change history gave the former text
        // block (its sub-points changed) comes back on the save after the undo, as in MoveRangeTests.
        Assert.True(State.Session!.TryUndo(out var error), error?.ToString());
        AssertSameBesidesNewIds(BookText, FileText);
        Assert.Equal(BookText.Length, WithoutNewIds(FileText, BookText).Length);
    }

    [Fact]
    public async Task Outline_DraggedHeadingRange_OnThePrologue_LastTopLevelSection()
    {
        var cut = await RenderAsync();

        // As a section dragged in the outline: the root's last sub-section (the prologue row shows no hint for it).
        await DragAsync(cut, "## Eins-A");
        await Row(cut, "Vorspann").DropAsync(new());

        Assert.Equal(Head + Vorspann + EinsHead + Erster + ErsterDetails + Zweiter + Zwei + "- # Eins-A\n\t- Unter Text\n", FileText);
        Assert.Null(State.DraggedRange);
        Assert.Empty(cut.FindAll(".ne-outline-error"));
    }

    [Fact]
    public async Task Outline_DropHintGoes_WhenTheDraggedRangeIsClearedElsewhere()
    {
        var cut = await RenderAsync();

        await DragAsync(cut, "## Eins-A");
        await Row(cut, "Zwei").DragEnterAsync(new());
        Assert.Contains("drop-onto", Row(cut, "Zwei").ClassList);

        // The range is dropped in an editor (or the drag given up): the editor clears it.
        await cut.InvokeAsync(() => State.DraggedRange = null);

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".drop-here, .drop-onto")));
    }

    [Fact]
    public async Task Outline_SectionDrag_ClearsAStaleDraggedRange_MovesTheSection()
    {
        var cut = await RenderAsync();

        // A grip drag whose end never reached Blazor left its range behind.
        await DragAsync(cut, "Detail A");

        Row(cut, "Zwei").DragStart();
        Assert.Null(State.DraggedRange);
        Row(cut, "Eins").Drop();

        Assert.Equal(Head + Vorspann + EinsHead + Erster + ErsterDetails + Zweiter + EinsA + "\t- ## Zwei\n\t\t- Dritter Text\n", FileText);
        Assert.Equal(Text("UndoOutlineMove"), State.Session!.Undo.NextDescription);
    }

    [Fact]
    public async Task Outline_DraggedTextBlockRange_BeforeAHeading_DoesNothing()
    {
        UseEditorPerPane();
        await OpenSessionAsync(_vault);
        State.SetSplit(true);
        var cut = Render<OutlinePane>(p => p.Add(o => o.View, State.Views[1]));
        Assert.Same(State.Views[0], State.ActiveView);

        await DragAsync(cut, "Zweiter Text");
        await Before(cut, "Zwei").DropAsync(new());
        await DragAsync(cut, "Detail A");
        await cut.Find(".ne-drop-end").DropAsync(new());

        Assert.Equal(BookText, FileText);
        Assert.False(State.Session!.Undo.CanUndo);
        Assert.Null(State.DraggedRange);
        Assert.Empty(cut.FindAll(".ne-outline-error"));
        Assert.Same(State.Views[0], State.ActiveView);
    }

    [Fact]
    public async Task Outline_DropFromTheOtherHalf()
    {
        UseEditorPerPane();
        await OpenSessionAsync(_vault);
        State.SetSplit(true);
        var upper = Render<EditorPane>(p => p.Add(e => e.View, State.Views[0]));
        var lower = Render<EditorPane>(p => p.Add(e => e.View, State.Views[1]));
        upper.WaitForAssertion(() => Assert.NotEmpty(Editors[0].Documents));
        lower.WaitForAssertion(() => Assert.NotEmpty(Editors[1].Documents));
        var outline2 = Render<OutlinePane>(p => p.Add(o => o.View, State.Views[1]));
        Assert.Same(State.Views[0], State.ActiveView);

        // The range is marked and dragged in the upper half, and dropped on the lower half's outline.
        var head = ElementOf("## Eins-A");
        State.Cursor = new CursorInfo(head.Kind, head.Key, head.Key, 0);
        await DragAsync(outline2, "## Eins-A");
        await Row(outline2, "Zwei").DropAsync(new());

        Assert.Equal(Head + Vorspann + EinsHead + Erster + ErsterDetails + Zweiter + Zwei + EinsA, FileText);
        Assert.Same(State.Views[1], State.ActiveView);
        lower.WaitForAssertion(() => Assert.Equal(KeyOf("## Eins-A"), Editors[1].Reveals.LastOrDefault()));
        Assert.Null(State.DraggedRange);
    }

    [Fact]
    public async Task Outline_SectionDrag_TakesTheReadTailAlong()
    {
        // "Eins" is followed on its own indentation by a text block and a deeper heading the book reads as its content.
        _vault.Dispose();
        _vault = TestVault.Create((BookPath, Head + "- # Eins\n- Tail Text\n- ## Tief\n\t- Tief Text\n- # Zwei\n"));
        var cut = await RenderAsync();

        Row(cut, "Eins").DragStart();
        Row(cut, "Zwei").Drop();

        Assert.Equal(Head + "- # Zwei\n\t- ## Eins\n\t\t- Tail Text\n\t\t- ### Tief\n\t\t\t- Tief Text\n", FileText);
        Assert.Empty(cut.FindAll(".ne-outline-error"));
    }
}
