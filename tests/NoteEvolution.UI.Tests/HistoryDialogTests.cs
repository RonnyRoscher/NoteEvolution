using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Components.Dialogs;
using NoteEvolution.UI.Editor;
using NoteEvolution.UI.History;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>The history window of a text block, the bar's „Historie“ and „Entfernen“ and the list of deleted blocks (block history spec 5 to 7).</summary>
public class HistoryDialogTests : UiTestContext
{
    private const string BookPath = "pages/Buch.md";
    private const string BookName = "Buch";
    private const string BlockId = "11111111-1111-4111-8111-111111111111";

    private const string BookText =
        "title:: Buch\ntype:: book\n\n" +
        "- # Eins\n" +
        "\t- Erster Text\n" +
        $"\t  id:: {BlockId}\n" +
        "\t\t- Punkt A\n" +
        "\t- Zweiter Text\n" +
        "- # Zwei\n" +
        "\t- Dritter Text\n" +
        "\t  id:: 22222222-2222-4222-8222-222222222222\n";

    private static readonly DateTimeOffset At = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    private readonly TestVault _vault = TestVault.Create((BookPath, BookText));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _vault.Dispose();
        }
    }

    private string Text(string key, params object[] arguments) =>
        Services.GetRequiredService<IStringLocalizer<Strings>>()[key, arguments].Value;

    private static BlockStand Stand(string text, params (string Text, int Depth)[] points) =>
        new(text, [.. points.Select(p => new StandParagraph(p.Text, p.Depth, BlockStatus.Draft))], BlockStatus.Draft, []);

    /// <summary>Stores two versions of "Erster Text": the older with a changed word, the newer with another sub-point.</summary>
    private static void AddVersions(VaultSession session)
    {
        var id = Guid.Parse(BlockId);
        session.History!.Add(id, BookName, At, HistoryReason.First, Stand("Erster alter Text", ("Punkt A", 1)));
        session.History.Add(id, BookName, At.AddMinutes(10), HistoryReason.Pause, Stand("Erster Text", ("Punkt A", 1), ("Punkt B", 1)));
    }

    private IRenderedComponent<HistoryDialog> RenderDialog() =>
        Render<HistoryDialog>(parameters => parameters.Add(p => p.BlockId, Guid.Parse(BlockId)));

    [Fact]
    public async Task Dialog_ListsVersions_NewestFirst_WithReason()
    {
        var session = await OpenSessionAsync(_vault);
        AddVersions(session);

        var cut = RenderDialog();

        Assert.Equal([Text("HistoryReasonPause"), Text("HistoryReasonFirst")], cut.FindAll(".ne-history-reason").Select(e => e.TextContent));
        Assert.Single(cut.FindAll(".ne-history-selected"));
        Assert.Contains(Text("HistoryReasonPause"), cut.Find(".ne-history-selected").TextContent);
    }

    [Fact]
    public async Task Dialog_SelectVersion_ShowsDiffMarks()
    {
        var session = await OpenSessionAsync(_vault);
        AddVersions(session);
        var cut = RenderDialog();

        cut.FindAll(".ne-history-version")[1].Click();

        // The older version against the block as it is now: its "alter" is gone, and Punkt B is not there yet.
        Assert.Equal("alter ", cut.Find(".ne-history-removed").TextContent);
        Assert.Empty(cut.FindAll(".ne-history-added"));
        Assert.Contains("Erster", cut.Find(".ne-history-compare").TextContent);
    }

    [Fact]
    public async Task Dialog_Restore_ReplacesText_Closes()
    {
        var session = await OpenSessionAsync(_vault);
        AddVersions(session);
        var closed = false;
        var cut = Render<HistoryDialog>(parameters => parameters
            .Add(p => p.BlockId, Guid.Parse(BlockId))
            .Add(p => p.OnClose, () => closed = true));
        cut.FindAll(".ne-history-version")[1].Click();

        cut.Find(".ne-history-restore").Click();

        cut.WaitForAssertion(() => Assert.True(closed));
        Assert.Equal("Erster alter Text", State.CurrentBook!.FindTextBlockById(Guid.Parse(BlockId))!.Text);
        Assert.Equal(Text("UndoRestore"), session.Undo.NextDescription);
    }

    [Fact]
    public async Task Dialog_ReadOnlyBook_RestoreDisabled_WithReason_ViewStillWorks()
    {
        // An unclosed code fence makes the book read-only.
        using var vault = TestVault.Create((BookPath, BookText + "- ```\n  offen\n"));
        var session = await OpenSessionAsync(vault);
        AddVersions(session);

        var cut = RenderDialog();
        cut.FindAll(".ne-history-version")[1].Click();

        Assert.True(cut.Find(".ne-history-restore").HasAttribute("disabled"));
        Assert.Equal(Text("EditorReadOnly"), cut.Find(".ne-history-restore").GetAttribute("title"));
        Assert.NotEmpty(cut.FindAll(".ne-history-removed"));
    }

    // The bar: rendered by the editor pane, which places it where the marking is.

    private async Task<IRenderedComponent<EditorPane>> RenderEditorAsync(VaultSession session)
    {
        var cut = Render<EditorPane>();
        cut.WaitForAssertion(() => Assert.NotEmpty(Editor.Documents));
        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionBoxMoved(120.5, true));
        return cut;
    }

    private Task MoveToAsync(IRenderedComponent<EditorPane> cut, string content)
    {
        var book = State.CurrentBook!;
        var key = book.Page.AllBlocks().Single(b => b.Content.StartsWith(content, StringComparison.Ordinal)).Key;
        var element = BookElements.Find(book, key)!;
        return cut.InvokeAsync(() => Editor.Callbacks!.OnCursorChanged(new CursorInfo(element.Kind, element.Key, BookElements.TextBlockOf(book, element)?.Key, 0)));
    }

    [Fact]
    public async Task Bar_HistoryButton_OnTextBlockAndDetail_OpensBlocksHistory()
    {
        var session = await OpenSessionAsync(_vault);
        AddVersions(session);
        var cut = await RenderEditorAsync(session);
        var requested = new List<Guid>();
        State.BlockHistoryRequested += requested.Add;

        await MoveToAsync(cut, "Erster Text");
        cut.WaitForAssertion(() => Assert.False(cut.Find(".ne-sec-history").HasAttribute("disabled")));
        Assert.Equal(Text("SectionHistory"), cut.Find(".ne-sec-history").TextContent.Trim());
        Assert.Equal(Text("SectionHistoryTitle"), cut.Find(".ne-sec-history").GetAttribute("title"));
        cut.Find(".ne-sec-history").Click();

        // At the detail the button opens the history of its text block.
        await MoveToAsync(cut, "Punkt A");
        cut.WaitForAssertion(() => Assert.False(cut.Find(".ne-sec-history").HasAttribute("disabled")));
        cut.Find(".ne-sec-history").Click();

        var textKey = State.CurrentBook!.FindTextBlockById(Guid.Parse(BlockId))!.Key;
        Assert.Equal([textKey, textKey], requested);
    }

    [Fact]
    public async Task Bar_HistoryButton_NotOnHeading()
    {
        var session = await OpenSessionAsync(_vault);
        AddVersions(session);
        var cut = await RenderEditorAsync(session);

        await MoveToAsync(cut, "# Eins");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".ne-sec-delete")));
        Assert.Empty(cut.FindAll(".ne-sec-history"));
    }

    [Fact]
    public async Task Bar_HistoryButton_NoVersions_DisabledWithReason()
    {
        var session = await OpenSessionAsync(_vault);
        var cut = await RenderEditorAsync(session);

        // A block with id:: but no versions, and one without id::.
        await MoveToAsync(cut, "Dritter Text");
        cut.WaitForAssertion(() => Assert.True(cut.Find(".ne-sec-history").HasAttribute("disabled")));
        Assert.Equal(Text("SectionHistoryNone"), cut.Find(".ne-sec-history").GetAttribute("title"));

        await MoveToAsync(cut, "Zweiter Text");
        cut.WaitForAssertion(() => Assert.True(cut.Find(".ne-sec-history").HasAttribute("disabled")));
        Assert.Equal(Text("SectionHistoryNone"), cut.Find(".ne-sec-history").GetAttribute("title"));
    }

    [Fact]
    public async Task Bar_HistoryButton_StoreClosed_BarRenders_ButtonDisabled()
    {
        var session = await OpenSessionAsync(_vault);
        AddVersions(session);
        var cut = await RenderEditorAsync(session);
        session.History!.Dispose();

        await MoveToAsync(cut, "Erster Text");

        cut.WaitForAssertion(() => Assert.True(cut.Find(".ne-sec-history").HasAttribute("disabled")));
        Assert.Equal(Text("SectionHistoryNone"), cut.Find(".ne-sec-history").GetAttribute("title"));
    }

    [Fact]
    public async Task Bar_Delete_LabelByElement()
    {
        var session = await OpenSessionAsync(_vault);
        var cut = await RenderEditorAsync(session);

        await MoveToAsync(cut, "Erster Text");
        cut.WaitForAssertion(() => Assert.Equal(Text("SectionRemove"), cut.Find(".ne-sec-delete").TextContent.Trim()));
        Assert.Equal(Text("SectionRemoveTitle"), cut.Find(".ne-sec-delete").GetAttribute("title"));

        await MoveToAsync(cut, "Punkt A");
        cut.WaitForAssertion(() => Assert.Equal(Text("SectionRemove"), cut.Find(".ne-sec-delete").TextContent.Trim()));
        Assert.Equal(Text("SectionRemoveTitle"), cut.Find(".ne-sec-delete").GetAttribute("title"));

        await MoveToAsync(cut, "# Eins");
        cut.WaitForAssertion(() => Assert.Equal(Text("SectionDelete"), cut.Find(".ne-sec-delete").TextContent.Trim()));
        Assert.Null(cut.Find(".ne-sec-delete").GetAttribute("title"));
    }

    [Fact]
    public async Task Deleted_ListedInMenu_Restore_LeavesList()
    {
        var session = await OpenSessionAsync(_vault);
        var block = State.CurrentBook!.FindTextBlockById(Guid.Parse(BlockId))!;
        session.Links.DeleteTextBlock(State.CurrentBook, block.Key);
        State.RefreshBook();

        // The menu entry is there and opens the list.
        var opened = false;
        var header = Render<HeaderBar>(parameters => parameters.Add(p => p.OnDeletedBlocks, () => opened = true));
        header.Find(".ne-assistants-toggle").Click();
        Assert.False(header.Find(".ne-assistant-deleted").HasAttribute("disabled"));
        Assert.Equal(Text("AssistantDeletedBlocks"), header.Find(".ne-assistant-deleted").TextContent);
        header.Find(".ne-assistant-deleted").Click();
        Assert.True(opened);

        var cut = Render<DeletedBlocksDialog>();
        var item = Assert.Single(cut.FindAll(".ne-deleted-item"));
        Assert.Contains("Erster Text", item.TextContent);
        Assert.Contains("Eins", cut.Find(".ne-deleted-section").TextContent);

        cut.Find(".ne-deleted-restore").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-deleted-item")));
        Assert.NotNull(State.CurrentBook!.FindTextBlockById(Guid.Parse(BlockId)));
        Assert.Equal(Text("DeletedBlocksEmpty"), cut.Find(".ne-deleted-empty").TextContent);
    }

    [Fact]
    public async Task Deleted_Empty_Hint()
    {
        await OpenSessionAsync(_vault);

        var cut = Render<DeletedBlocksDialog>();

        Assert.Equal(Text("DeletedBlocksEmpty"), cut.Find(".ne-deleted-empty").TextContent);
        Assert.Empty(cut.FindAll(".ne-deleted-item"));
    }

    [Fact]
    public async Task Deleted_ShowHistoryOfABlock()
    {
        var session = await OpenSessionAsync(_vault);
        AddVersions(session);
        var block = State.CurrentBook!.FindTextBlockById(Guid.Parse(BlockId))!;
        session.Links.DeleteTextBlock(State.CurrentBook, block.Key);
        State.RefreshBook();
        var cut = Render<DeletedBlocksDialog>();

        cut.Find(".ne-deleted-history").Click();

        // The versions of the deleted block, compared with its last stand; restoring is done from the list.
        var versions = cut.FindAll(".ne-history-version");
        Assert.Equal(session.History!.Versions(Guid.Parse(BlockId)).Count, versions.Count);
        Assert.True(versions.Count >= 2);
        Assert.Empty(cut.FindAll(".ne-history-restore"));
    }
}
