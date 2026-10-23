using System.Text.Json.Nodes;
using Bunit;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Editor;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>
/// The outline and the editor of each half of a split view work on their own <see cref="BookView"/>: the focus in an
/// editor or a click in an outline makes it the active view (a cursor report alone does not), and jumps land in the
/// active half only.
/// </summary>
public class BookViewPaneTests : UiTestContext
{
    private readonly TestVault _vault = TestVault.Create(
        ("pages/Buch - Alpha.md",
            "title:: Alpha\ntype:: book\n\n" +
            "- # Eins\n" +
            "\t- Erster Text\n" +
            "- # Zwei\n" +
            "\t- Dritter Text\n" +
            "\t  updated:: 2026-10-05T10:00\n"));

    private Book Book => State.CurrentBook!;

    private BookView Upper => State.Views[0];

    private BookView Lower => State.Views[1];

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _vault.Dispose();
        }
    }

    private OutlineNode Section(string title) => Book.Root.Children.Single(n => n.Title == title);

    private Guid KeyOf(string content) => Book.Page.AllBlocks().Single(b => b.Content == content).Key;

    private CursorInfo CursorIn(string content) => new(ElementKind.TextBlock, KeyOf(content), KeyOf(content), 0);

    /// <summary>
    /// Opens the vault at the section "Eins", splits the view (both halves start there) and renders the editors of both
    /// halves, each with its own fake editor (<c>Editors[0]</c> upper, <c>Editors[1]</c> lower), once both show the book.
    /// </summary>
    private async Task<(IRenderedComponent<EditorPane> Upper, IRenderedComponent<EditorPane> Lower)> RenderEditorsAsync()
    {
        UseEditorPerPane();
        await OpenSessionAsync(_vault);
        State.CurrentSectionKey = Section("Eins").Key;
        State.SetSplit(true);
        var upper = Render<EditorPane>(p => p.Add(e => e.View, Upper));
        var lower = Render<EditorPane>(p => p.Add(e => e.View, Lower));
        Assert.Equal(2, Editors.Count);
        upper.WaitForAssertion(() => Assert.NotEmpty(Editors[0].Documents));
        lower.WaitForAssertion(() => Assert.NotEmpty(Editors[1].Documents));
        return (upper, lower);
    }

    /// <summary>A click into an editor as the script reports it: the focus, then the cursor.</summary>
    private static async Task ClickIntoAsync(IRenderedComponent<EditorPane> pane, FakeEditorInterop editor, CursorInfo cursor)
    {
        await pane.InvokeAsync(() => editor.Callbacks!.OnEditorFocused());
        await pane.InvokeAsync(() => editor.Callbacks!.OnCursorChanged(cursor));
    }

    /// <summary>The document <paramref name="editor"/> shows last, with the own text of its first text block replaced by <paramref name="text"/>.</summary>
    private static string WithFirstText(FakeEditorInterop editor, string text)
    {
        var doc = JsonNode.Parse(editor.Json)!;
        var block = doc["content"]!.AsArray().First(n => (string?)n!["type"] == "textBlock")!;
        block["content"]![0]!["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
        return doc.ToJsonString();
    }

    private string BookFile => File.ReadAllText(Path.Combine(_vault.Root, "pages", "Buch - Alpha.md"));

    private static string SelectedRow(IRenderedComponent<OutlinePane> outline) =>
        outline.Find(".ne-outline-item .ne-outline-row.selected .ne-outline-title").TextContent;

    [Fact]
    public async Task ClickInPane2_ActivatesIt_BarOnlyThere()
    {
        var (upper, lower) = await RenderEditorsAsync();
        Assert.Single(upper.FindAll(".ne-section-bar"));
        Assert.Empty(lower.FindAll(".ne-section-bar"));
        Assert.Contains("active", upper.Find(".ne-editor-pane").ClassList);

        var cursor = CursorIn("Dritter Text");
        await ClickIntoAsync(lower, Editors[1], cursor);

        Assert.Same(Lower, State.ActiveView);
        Assert.Equal(cursor, Lower.Cursor);
        Assert.Equal(Section("Zwei").Key, Lower.CurrentSectionKey);
        Assert.Null(Upper.Cursor);
        Assert.Equal(Section("Eins").Key, Upper.CurrentSectionKey);
        lower.WaitForAssertion(() => Assert.Single(lower.FindAll(".ne-section-bar")));
        Assert.Empty(upper.FindAll(".ne-section-bar"));
        Assert.Contains("active", lower.Find(".ne-editor-pane").ClassList);
        Assert.DoesNotContain("active", upper.Find(".ne-editor-pane").ClassList);

        // Each editor marks its own view's range: the lower one the text block, the upper one nothing.
        lower.WaitForAssertion(() => Assert.Contains(Editors[1].Marked[^1], n => n.Key == cursor.Key));
        Assert.DoesNotContain(Editors[0].Marked, nodes => nodes.Count > 0);
    }

    [Fact]
    public async Task Reveal_FromRecentChanges_LandsInTheActivePaneOnly()
    {
        var (upper, lower) = await RenderEditorsAsync();
        await ClickIntoAsync(lower, Editors[1], CursorIn("Erster Text"));
        Assert.Same(Lower, State.ActiveView);
        var upperReveals = Editors[0].Reveals.Count;
        var list = Render<RecentChangesList>();

        await list.Find(".ne-recent-item").ClickAsync(new());

        lower.WaitForAssertion(() => Assert.Equal(KeyOf("Dritter Text"), Editors[1].Reveals.LastOrDefault()));
        Assert.Equal(upperReveals, Editors[0].Reveals.Count);
        Assert.Same(Lower, State.ActiveView);
        Assert.Equal(Section("Zwei").Key, Lower.CurrentSectionKey);
        Assert.Equal(Section("Eins").Key, Upper.CurrentSectionKey);
        Assert.Null(Upper.PendingReveal);
    }

    [Fact]
    public async Task RecentChangesInOutline1_ActivatesView1_RevealsThereOnly()
    {
        var (upper, lower) = await RenderEditorsAsync();
        await ClickIntoAsync(lower, Editors[1], CursorIn("Erster Text"));
        var lowerReveals = Editors[1].Reveals.Count;
        var outline = Render<OutlinePane>(p => p.Add(o => o.View, Upper));

        await outline.Find(".ne-recent-item").ClickAsync(new());

        Assert.Same(Upper, State.ActiveView);
        upper.WaitForAssertion(() => Assert.Equal(KeyOf("Dritter Text"), Editors[0].Reveals.LastOrDefault()));
        Assert.Equal(lowerReveals, Editors[1].Reveals.Count);
        Assert.Equal(KeyOf("Erster Text"), Lower.Cursor?.Key);
    }

    [Fact]
    public async Task Outline2_Click_ActivatesView2_AndMarksItsRow()
    {
        var (upper, lower) = await RenderEditorsAsync();
        var outline1 = Render<OutlinePane>(p => p.Add(o => o.View, Upper));
        var outline2 = Render<OutlinePane>(p => p.Add(o => o.View, Lower));
        Assert.Equal("Eins", SelectedRow(outline1));
        Assert.Equal("Eins", SelectedRow(outline2));
        var upperReveals = Editors[0].Reveals.Count;

        var row = outline2.FindAll(".ne-outline-item .ne-outline-row").Single(r => r.TextContent.Contains("Zwei"));
        await row.ClickAsync(new());

        Assert.Same(Lower, State.ActiveView);
        Assert.Equal(Section("Zwei").Key, Lower.CurrentSectionKey);
        outline2.WaitForAssertion(() => Assert.Equal("Zwei", SelectedRow(outline2)));
        outline1.WaitForAssertion(() => Assert.Equal("Eins", SelectedRow(outline1)));
        lower.WaitForAssertion(() => Assert.Equal(Section("Zwei").Key, Editors[1].Reveals.LastOrDefault()));
        Assert.Equal(upperReveals, Editors[0].Reveals.Count);
    }

    [Fact]
    public async Task CursorReportInInactivePane_AfterTheOtherHalfSaved_DoesNotActivateIt()
    {
        var (upper, lower) = await RenderEditorsAsync();
        Editors[1].CursorPlaced = true;
        var kept = CursorIn("Dritter Text") with { Offset = 3 };
        Editors[1].CursorAfterLoad = () => kept;
        await lower.InvokeAsync(() => Editors[1].Callbacks!.OnCursorChanged(CursorIn("Dritter Text")));
        Assert.Same(Upper, State.ActiveView);
        Assert.Equal(Section("Zwei").Key, Lower.CurrentSectionKey);
        var lowerDocuments = Editors[1].Documents.Count;

        // Typing in the upper half; its autosave makes the lower editor reload, which reports its kept cursor.
        await upper.InvokeAsync(() => Editors[0].Callbacks!.OnDocumentChanged(WithFirstText(Editors[0], "Oben getippt")));
        AdvanceUntil(lower, () => Assert.True(Editors[1].Documents.Count > lowerDocuments), TimeSpan.FromMilliseconds(500));
        Assert.Contains("Oben getippt", BookFile);
        Assert.True(Editors[1].KeepCursor[^1]);

        Assert.Same(Upper, State.ActiveView);
        Assert.Equal(kept, Lower.Cursor);
        Assert.Single(upper.FindAll(".ne-section-bar"));
        Assert.Empty(lower.FindAll(".ne-section-bar"));
    }

    [Fact]
    public async Task FocusInPane2_SavesPane1First_ActivatesPane2_BarMoves()
    {
        var (upper, lower) = await RenderEditorsAsync();
        await upper.InvokeAsync(() => Editors[0].Callbacks!.OnDocumentChanged(WithFirstText(Editors[0], "Noch nicht gespeichert")));
        Assert.DoesNotContain("Noch nicht gespeichert", BookFile);

        await lower.InvokeAsync(() => Editors[1].Callbacks!.OnEditorFocused());

        Assert.Contains("Noch nicht gespeichert", BookFile);
        Assert.Same(Lower, State.ActiveView);
        lower.WaitForAssertion(() => Assert.Single(lower.FindAll(".ne-section-bar")));
        upper.WaitForAssertion(() => Assert.Empty(upper.FindAll(".ne-section-bar")));
        Assert.Contains("active", lower.Find(".ne-editor-pane").ClassList);
    }

    [Fact]
    public async Task Dispose_UnregistersFromItsOwnViewOnly()
    {
        var (upper, lower) = await RenderEditorsAsync();
        Assert.NotNull(Upper.FlushEditor);
        Assert.NotNull(Lower.FlushEditor);

        await lower.Instance.DisposeAsync();

        Assert.Null(Lower.FlushEditor);
        Assert.Null(Lower.WarnUnsavedText);
        Assert.Null(Lower.HasUnsavedEditorText);
        Assert.NotNull(Upper.FlushEditor);
        Assert.NotNull(Upper.WarnUnsavedText);
        Assert.NotNull(Upper.HasUnsavedEditorText);
    }
}
