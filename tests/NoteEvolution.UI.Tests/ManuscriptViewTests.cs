using System.Text.Json.Nodes;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NoteEvolution.Core.Books;
using NoteEvolution.Pdf;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Components.Dialogs;
using NoteEvolution.UI.Editor;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;
using static NoteEvolution.UI.Tests.NotesTestData;

namespace NoteEvolution.UI.Tests;

/// <summary>The manuscript as the only view: the whole book in the editor, the cursor element drives the current section.</summary>
public class ManuscriptViewTests : UiTestContext
{
    private const string BookPath = "pages/Buch - Alpha.md";

    private const string GoneNote = "44444444-4444-4444-8444-444444444444";

    private const string DritterId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb1";

    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(1500);

    private readonly List<TestVault> _vaults = [];

    public ManuscriptViewTests()
    {
        Services.AddSingleton<IPdfExporter>(new NoPdfExporter());
    }

    protected override void Dispose(bool disposing)
    {
        // The session (and its vector cache in the vault) is closed first, then the vault folders are deleted.
        base.Dispose(disposing);
        if (disposing)
        {
            _vaults.ForEach(v => v.Dispose());
        }
    }

    /// <summary>A prologue, two top-level sections (one with a sub-section) and a text block with a detail.</summary>
    private TestVault Alpha()
    {
        var vault = TestVault.Create(
            (BookPath,
                "title:: Alpha\ntype:: book\n\n" +
                "- Vorspann Text\n" +
                "- # Eins\n" +
                "\t- Erster Text\n" +
                "\t\t- Ein Detail\n" +
                "\t- Zweiter Text\n" +
                "\t- ## Eins-A\n" +
                "\t\t- Unter Text\n" +
                "- # Zwei\n" +
                "\t- Dritter Text\n" +
                $"\t  id:: {DritterId}\n" +
                $"\t  source:: (({GoneNote}))\n"));
        _vaults.Add(vault);
        return vault;
    }

    private Book Book => State.CurrentBook!;

    private OutlineNode Node(string title) => Find(Book.Root, title)!;

    private static OutlineNode? Find(OutlineNode node, string title) =>
        node.Title == title && node.Block is not null ? node : node.Children.Select(c => Find(c, title)).FirstOrDefault(n => n is not null);

    private TextBlock Block(string text) => TextBlocks(Book.Root).First(t => t.Text == text);

    private static IEnumerable<TextBlock> TextBlocks(OutlineNode node) => node.TextBlocks.Concat(node.Children.SelectMany(TextBlocks));

    private string Text(string key, params object[] args) =>
        Services.GetRequiredService<IStringLocalizer<Strings>>()[key, args].Value;

    private async Task<IRenderedComponent<EditorPane>> RenderEditorAsync(TestVault tv)
    {
        await OpenSessionAsync(tv);
        var cut = Render<EditorPane>();
        cut.WaitForAssertion(() => Assert.NotEmpty(Editor.Documents));
        return cut;
    }

    private Task CursorAsync(IRenderedComponent<EditorPane> cut, CursorInfo? cursor) =>
        cut.InvokeAsync(() => Editor.Callbacks!.OnCursorChanged(cursor));

    private static List<string?> Types(string json) =>
        [.. JsonNode.Parse(json)!["content"]!.AsArray().Select(n => (string?)n!["type"])];

    [Fact]
    public void Header_HasNoViewToggle()
    {
        var cut = Render<HeaderBar>();

        Assert.Empty(cut.FindAll(".ne-mode, .ne-mode-section, .ne-mode-manuscript"));
    }

    [Fact]
    public async Task Editor_LoadsWholeBook_WithAllHeadings()
    {
        var tv = Alpha();
        await OpenSessionAsync(tv);
        State.CurrentSectionKey = Node("Zwei").Key;

        var cut = Render<EditorPane>();

        cut.WaitForAssertion(() => Assert.NotEmpty(Editor.Documents));
        Assert.Equal(
            ["textBlock", "heading", "textBlock", "textBlock", "heading", "textBlock", "heading", "textBlock"],
            Types(Editor.Json));
        Assert.Single(cut.FindAll(".ne-editor-chips input"));

        // Another section changes nothing in the document.
        State.CurrentSectionKey = Node("Eins").Key;
        await cut.InvokeAsync(State.Notify);
        await cut.InvokeAsync(() => State.FlushEditor!());
        Assert.Single(Editor.Documents);
    }

    [Fact]
    public async Task CursorInTextBlock_SetsSectionAndTextBlock()
    {
        var tv = Alpha();
        var cut = await RenderEditorAsync(tv);
        var changed = 0;
        State.Changed += () => changed++;
        var under = Block("Unter Text");

        await CursorAsync(cut, new CursorInfo(ElementKind.TextBlock, under.Key, under.Key, 3));

        Assert.Equal(new CursorInfo(ElementKind.TextBlock, under.Key, under.Key, 3), State.Cursor);
        Assert.Equal(new BookElement(ElementKind.TextBlock, under.Key), State.CurrentElement);
        Assert.Equal(Node("Eins-A").Key, State.CurrentSectionKey);
        Assert.Equal(under.Key, State.CursorTextBlockKey);
        Assert.Equal(1, changed);

        // Only the offset moved: the state follows without a notification (no save, no reload per keystroke).
        await CursorAsync(cut, new CursorInfo(ElementKind.TextBlock, under.Key, under.Key, 4));
        Assert.Equal(4, State.Cursor!.Offset);
        Assert.Equal(1, changed);

        // A detail: its text block and that block's section.
        var first = Block("Erster Text");
        var detail = first.Paragraphs.Single().Block.Key;
        await CursorAsync(cut, new CursorInfo(ElementKind.Detail, detail, first.Key, 0));
        Assert.Equal(new BookElement(ElementKind.Detail, detail), State.CurrentElement);
        Assert.Equal(Node("Eins").Key, State.CurrentSectionKey);
        Assert.Equal(first.Key, State.CursorTextBlockKey);
        Assert.Equal(2, changed);

        // The prologue is the book root; no cursor keeps the last section.
        var prologue = Block("Vorspann Text");
        await CursorAsync(cut, new CursorInfo(ElementKind.TextBlock, prologue.Key, prologue.Key, 0));
        Assert.Equal(Guid.Empty, State.CurrentSectionKey);
        await CursorAsync(cut, new CursorInfo(ElementKind.TextBlock, under.Key, under.Key, 0));
        await CursorAsync(cut, null);
        Assert.Null(State.CurrentElement);
        Assert.Equal(Node("Eins-A").Key, State.CurrentSectionKey);

        // The cursor never reloads the editor, and typed text still waits for its debounce.
        var before = tv.Read(BookPath);
        await cut.InvokeAsync(() => Editor.Callbacks!.OnDocumentChanged(Editor.Json.Replace("Zweiter Text", "Zweiter Text, neu")));
        await CursorAsync(cut, new CursorInfo(ElementKind.Heading, Node("Zwei").Key, null, 0));
        Assert.Equal(Node("Zwei").Key, State.CurrentSectionKey);
        Assert.Equal(before, tv.Read(BookPath));
        Time.Advance(TimeSpan.FromMilliseconds(1000));
        cut.WaitForAssertion(() => Assert.Contains("\t- Zweiter Text, neu\n", tv.Read(BookPath)));
        await cut.InvokeAsync(() => State.FlushEditor!());
        Assert.Single(Editor.Documents);
    }

    [Fact]
    public async Task CursorOnHeading_TopicIsSection_NoCursorTextBlock()
    {
        var tv = Alpha();
        var cut = await RenderEditorAsync(tv);
        var zwei = Node("Zwei");

        await CursorAsync(cut, new CursorInfo(ElementKind.Heading, zwei.Key, null, 0));

        Assert.Equal(new BookElement(ElementKind.Heading, zwei.Key), State.CurrentElement);
        Assert.Equal(zwei.Key, State.CurrentSectionKey);
        Assert.Null(State.CursorTextBlockKey);
    }

    [Fact]
    public async Task Cursor_UnknownDetailKey_FallsBackToTextBlock()
    {
        var tv = Alpha();
        var cut = await RenderEditorAsync(tv);
        var first = Block("Erster Text");

        // A paragraph the book stored under another key: its text block.
        await CursorAsync(cut, new CursorInfo(ElementKind.Detail, Guid.NewGuid(), first.Key, 5));

        Assert.Equal(new BookElement(ElementKind.TextBlock, first.Key), State.CurrentElement);
        Assert.Equal(Node("Eins").Key, State.CurrentSectionKey);

        // A text block that is unknown as well: the nearest heading (the section the cursor was in).
        var under = Block("Unter Text");
        await CursorAsync(cut, new CursorInfo(ElementKind.TextBlock, under.Key, under.Key, 0));
        await CursorAsync(cut, new CursorInfo(ElementKind.Detail, Guid.NewGuid(), Guid.NewGuid(), 0));

        Assert.Equal(new BookElement(ElementKind.Heading, Node("Eins-A").Key), State.CurrentElement);
        Assert.Equal(Node("Eins-A").Key, State.CurrentSectionKey);

        // In the prologue there is no heading before it: the start of the book.
        var prologue = Block("Vorspann Text");
        await CursorAsync(cut, new CursorInfo(ElementKind.TextBlock, prologue.Key, prologue.Key, 0));
        await CursorAsync(cut, new CursorInfo(ElementKind.TextBlock, Guid.NewGuid(), null, 0));

        Assert.Null(State.CurrentElement);
        Assert.Equal(Guid.Empty, State.CurrentSectionKey);
    }

    [Fact]
    public async Task OutlineClick_RevealsHeading_AndHighlightsNode()
    {
        var tv = Alpha();
        var editor = await RenderEditorAsync(tv);
        var outline = Render<OutlinePane>();
        var zwei = Node("Zwei");

        Row(outline, "Zwei").Click();

        editor.WaitForAssertion(() => Assert.Equal([zwei.Key], Editor.Reveals));
        Assert.Equal(zwei.Key, State.CurrentSectionKey);
        Assert.Equal(new BookElement(ElementKind.Heading, zwei.Key), State.CurrentElement);
        Assert.Contains("selected", Row(outline, "Zwei").ClassList);
        Assert.Single(Editor.Documents);

        // A second click reveals it again (the user may have scrolled away).
        Row(outline, "Zwei").Click();
        editor.WaitForAssertion(() => Assert.Equal([zwei.Key, zwei.Key], Editor.Reveals));

        // The cursor in a text block highlights the heading it belongs to.
        var under = Block("Unter Text");
        await CursorAsync(editor, new CursorInfo(ElementKind.TextBlock, under.Key, under.Key, 0));
        outline.WaitForAssertion(() => Assert.Contains("selected", Row(outline, "Eins-A").ClassList));
        Assert.DoesNotContain("selected", Row(outline, "Zwei").ClassList);
    }

    [Fact]
    public async Task NoteCardUsedJump_RevealsBlock()
    {
        using var tv = Create();
        var session = await OpenSessionAsync(tv);
        State.CurrentBook = session.Vault.FindBook("Buch - Alpha");
        State.CurrentSectionKey = Guid.Empty;
        var editor = Render<EditorPane>();
        editor.WaitForAssertion(() => Assert.Single(Editor.Documents));
        var card = Render<NoteCard>(p => p.Add(c => c.Note, Note(session, "Gedächtnis wird")));

        card.Find(".ne-note-used").Click();

        var beta = session.Vault.FindBook(BetaBook)!;
        var target = TextBlocks(beta.Root).Single(t => t.Text == "Beta Text");
        // The other book is loaded first, then the block is revealed in it.
        editor.WaitForAssertion(() => Assert.Equal(["doc", "doc", $"reveal:{target.Key}"], Editor.Calls));
        Assert.Equal(BetaBook, State.CurrentBook!.LinkName);
        Assert.Contains("Beta Text", Editor.Json);
        Assert.Equal(target.Section.Key, State.CurrentSectionKey);
        Assert.Equal(target.Key, State.CursorTextBlockKey);
    }

    [Fact]
    public async Task LinkCheckJump_RevealsBlock()
    {
        var tv = Alpha();
        var editor = await RenderEditorAsync(tv);
        var closed = 0;
        var dialog = Render<LinkCheckDialog>(p => p.Add(c => c.OnClose, EventCallback.Factory.Create(this, () => closed++)));

        dialog.Find(".ne-linkcheck-goto").Click();

        var target = Block("Dritter Text");
        editor.WaitForAssertion(() => Assert.Equal([target.Key], Editor.Reveals));
        Assert.Equal(Node("Zwei").Key, State.CurrentSectionKey);
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task RelevantTab_UsesCursorTopic_And_HeadingTopic()
    {
        var notes = string.Concat(
            Enumerable.Range(1, 10).Select(i => $"- Schlaf Träume Nummer {i}\n")
            .Concat(Enumerable.Range(1, 10).Select(i => $"- Musik Gitarre Klavier Nummer {i}\n"))
            .Concat(Enumerable.Range(1, 10).Select(i => $"- Suppe Gemüse kochen Nummer {i}\n")));
        var tv = TestVault.Create(
            (BookPath,
                "title:: Alpha\ntype:: book\n\n" +
                "- # Schlafen\n\t- Schlaf und Träume im Alltag\n" +
                "- # Musizieren\n\t- Musik Gitarre Klavier spielen\n" +
                "- # Kochen\n\t- Suppe kochen mit Gemüse\n"),
            ("pages/Ideen.md", notes));
        _vaults.Add(tv);
        await OpenWithAiAsync(tv);
        State.CurrentSectionKey = Node("Schlafen").Key;
        var editor = Render<EditorPane>();
        editor.WaitForAssertion(() => Assert.NotEmpty(Editor.Documents));
        var pane = Render<NotesPane>();
        pane.WaitForAssertion(() => Assert.StartsWith("Schlaf", First(pane)));

        // The cursor in a text block: that block and its neighbours.
        var music = Block("Musik Gitarre Klavier spielen");
        await CursorAsync(editor, new CursorInfo(ElementKind.TextBlock, music.Key, music.Key, 0));
        pane.WaitForState(() =>
        {
            Time.Advance(Debounce);
            return First(pane).StartsWith("Musik", StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(10));

        // The cursor on a heading: that heading's section.
        await CursorAsync(editor, new CursorInfo(ElementKind.Heading, Node("Kochen").Key, null, 0));
        pane.WaitForState(() =>
        {
            Time.Advance(Debounce);
            return First(pane).StartsWith("Suppe", StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task PdfCurrentSection_IsSectionOfCursor()
    {
        var tv = Alpha();
        var editor = await RenderEditorAsync(tv);
        var first = Block("Erster Text");
        await CursorAsync(editor, new CursorInfo(ElementKind.Detail, first.Paragraphs.Single().Block.Key, first.Key, 0));

        var dialog = Render<PdfExportDialog>();

        var section = dialog.Find("input[value='section']");
        Assert.False(section.HasAttribute("disabled"));
        Assert.True(section.HasAttribute("checked"));
        Assert.Contains(Text("PdfScopeSection", "Eins"), dialog.Find(".ne-pdf-scope").TextContent);

        // In the prologue the whole book is the current section.
        var prologue = Block("Vorspann Text");
        await CursorAsync(editor, new CursorInfo(ElementKind.TextBlock, prologue.Key, prologue.Key, 0));
        var again = Render<PdfExportDialog>();
        Assert.True(again.Find("input[value='section']").HasAttribute("disabled"));
        Assert.True(again.Find("input[value='book']").HasAttribute("checked"));
    }

    [Fact]
    public async Task EmptyHeading_OutlineShowsPlaceholder()
    {
        var tv = TestVault.Create((BookPath, "title:: Alpha\ntype:: book\n\n- # \n\t- Text\n- # Zwei\n"));
        _vaults.Add(tv);
        await OpenSessionAsync(tv);

        var cut = Render<OutlinePane>();

        Assert.Equal([Text("OutlineUntitled"), "Zwei"], cut.FindAll(".ne-outline-title").Select(e => e.TextContent));
    }

    private static AngleSharp.Dom.IElement Row(IRenderedComponent<OutlinePane> cut, string title) =>
        cut.FindAll(".ne-outline-row").First(row => row.QuerySelector(".ne-outline-title")?.TextContent == title);

    private static string First(IRenderedComponent<NotesPane> cut) =>
        cut.FindAll(".ne-relevant-list .ne-note-card > .ne-note-body > .ne-note-text").FirstOrDefault()?.TextContent.Trim() ?? "";

    /// <summary>The PDF dialog only needs an exporter to render; these tests never export.</summary>
    private sealed class NoPdfExporter : IPdfExporter
    {
        public PdfExportReport Export(Book book, Guid? scopeKey, PdfOptions options, Stream output) =>
            throw new NotSupportedException();
    }
}
