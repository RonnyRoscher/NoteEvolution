using Bunit;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Editor;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>
/// The outline and the editor of each half of a split view work on their own <see cref="BookView"/>: a cursor or a
/// click there makes it the active view, and jumps land in the active half only.
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

    private static string SelectedRow(IRenderedComponent<OutlinePane> outline) =>
        outline.Find(".ne-outline-item .ne-outline-row.selected .ne-outline-title").TextContent;

    [Fact]
    public async Task CursorInPane2_ActivatesIt_BarOnlyThere()
    {
        var (upper, lower) = await RenderEditorsAsync();
        Assert.Single(upper.FindAll(".ne-section-bar"));
        Assert.Empty(lower.FindAll(".ne-section-bar"));
        Assert.Contains("active", upper.Find(".ne-editor-pane").ClassList);

        var cursor = CursorIn("Dritter Text");
        await lower.InvokeAsync(() => Editors[1].Callbacks!.OnCursorChanged(cursor));

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
        await lower.InvokeAsync(() => Editors[1].Callbacks!.OnCursorChanged(CursorIn("Erster Text")));
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
        await lower.InvokeAsync(() => Editors[1].Callbacks!.OnCursorChanged(CursorIn("Erster Text")));
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
