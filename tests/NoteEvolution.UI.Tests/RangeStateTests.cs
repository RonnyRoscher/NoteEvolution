using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NoteEvolution.AI.Tests;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Editor;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>
/// The range level (package B, spec 2): its resets, its bounds, the marking the editor gets, and what "Relevant" and
/// the sources of the section bar refer to.
/// </summary>
public class RangeStateTests : UiTestContext
{
    private const string BookPath = "pages/Buch - Alpha.md";

    private const string OtherBookPath = "pages/Buch - Beta.md";

    private const string IdA = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1";

    private const string IdB = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb1";

    private const string IdC = "cccccccc-cccc-4ccc-8ccc-ccccccccccc1";

    private const string IdD = "dddddddd-dddd-4ddd-8ddd-ddddddddddd1";

    private const string BookText =
        "title:: Alpha\ntype:: book\n\n" +
        "- Vorspann Text\n" +
        "- # Eins\n" +
        "\t- Erster Text\n" +
        $"\t  source:: (({IdA}))\n" +
        "\t\t- Ein Detail\n" +
        "\t- Zweiter Text\n" +
        $"\t  source:: (({IdB})), (({IdA}))\n" +
        "\t- ## Eins-A\n" +
        "\t\t- Unter Text\n" +
        $"\t\t  source:: (({IdC}))\n" +
        "- # Zwei\n" +
        "\t- Dritter Text\n" +
        $"\t  source:: (({IdD}))\n";

    private const string NotesText =
        $"- Notiz A\n  id:: {IdA}\n" +
        $"- Notiz B\n  id:: {IdB}\n" +
        $"- Notiz C\n  id:: {IdC}\n" +
        $"- Notiz D\n  id:: {IdD}\n";

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TestVault _vault = TestVault.Create(
        (BookPath, BookText),
        (OtherBookPath, "title:: Beta\ntype:: book\n\n- # Anderes\n\t- Beta Text\n"),
        ("pages/Notizen.md", NotesText));

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

    private string Text(string key, params object[] arguments) =>
        Services.GetRequiredService<IStringLocalizer<Strings>>()[key, arguments].Value;

    private Guid KeyOf(string content) => Book.Page.AllBlocks().Single(b => b.Content == content).Key;

    private CursorInfo CursorOf(string content, int offset = 0)
    {
        var element = BookElements.Find(Book, KeyOf(content))!;
        return new CursorInfo(element.Kind, element.Key, BookElements.TextBlockOf(Book, element)?.Key, offset);
    }

    private async Task OpenAsync()
    {
        await OpenSessionAsync(_vault);
        State.CurrentBook = State.Session!.Vault.FindBook("Buch - Alpha");
    }

    /// <summary>Opens the vault (the book Alpha) and renders the editor.</summary>
    private async Task<IRenderedComponent<EditorPane>> RenderAsync()
    {
        await OpenAsync();
        var cut = Render<EditorPane>();
        cut.WaitForAssertion(() => Assert.NotEmpty(Editor.Documents));
        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionBoxMoved(100, true));
        return cut;
    }

    /// <summary>Moves the cursor (as the editor reports it) to the element whose block has <paramref name="content"/>.</summary>
    private Task MoveToAsync(IRenderedComponent<EditorPane> cut, string content, int offset = 0) =>
        cut.InvokeAsync(() => Editor.Callbacks!.OnCursorChanged(CursorOf(content, offset)));

    /// <summary>Runs a range shortcut as the editor sends it.</summary>
    private Task CommandAsync(IRenderedComponent<EditorPane> cut, SectionCommand command) =>
        cut.InvokeAsync(() => Editor.Callbacks!.OnSectionCommand(command));

    private MarkedNode Node(string content, bool ownTextOnly = false) => new(KeyOf(content), ownTextOnly);

    private static async Task Eventually(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "The condition was not met in time.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Range_ResetsOnElementChange_NotOnOffsetChange()
    {
        var cut = await RenderAsync();
        await MoveToAsync(cut, "Erster Text");

        await CommandAsync(cut, SectionCommand.RangeUp);
        Assert.Equal(1, State.RangeLevel);
        Assert.Equal(new BookElement(ElementKind.Heading, KeyOf("# Eins")), State.CurrentRange!.Head);

        // Typing in the same element moves only the offset.
        await MoveToAsync(cut, "Erster Text", 5);
        Assert.Equal(1, State.RangeLevel);

        // A detail of the same text block is another element.
        await MoveToAsync(cut, "Ein Detail");
        Assert.Equal(0, State.RangeLevel);

        await CommandAsync(cut, SectionCommand.RangeDown);
        Assert.Equal(-1, State.RangeLevel);
        await MoveToAsync(cut, "Zweiter Text");
        Assert.Equal(0, State.RangeLevel);
    }

    [Fact]
    public async Task Range_ResetsOnBookSwitch_And_Reveal()
    {
        await OpenAsync();
        State.Cursor = CursorOf("Erster Text");
        State.ChangeRange(1);
        Assert.Equal(1, State.RangeLevel);

        // The same book taken from the vault again keeps the level.
        State.RefreshBook();
        Assert.Equal(1, State.RangeLevel);

        State.RevealElement(KeyOf("Erster Text"));
        Assert.Equal(0, State.RangeLevel);

        State.ChangeRange(1);
        Assert.Equal(1, State.RangeLevel);
        State.CurrentBook = State.Session!.Vault.FindBook("Buch - Beta");
        Assert.Equal(0, State.RangeLevel);

        State.CurrentBook = State.Session.Vault.FindBook("Buch - Alpha");
        State.Cursor = CursorOf("Erster Text");
        State.ChangeRange(1);
        Assert.Equal(1, State.RangeLevel);
        State.Session = null;
        Assert.Equal(0, State.RangeLevel);
    }

    [Fact]
    public async Task RangeUp_PushesMarkedNodesOfParent()
    {
        var cut = await RenderAsync();
        Assert.Empty(Editor.Marked);

        await MoveToAsync(cut, "Erster Text");
        cut.WaitForAssertion(() => Assert.Equal([Node("Erster Text")], Assert.Single(Editor.Marked)));

        // A change of the offset alone pushes nothing.
        await MoveToAsync(cut, "Erster Text", 3);

        await CommandAsync(cut, SectionCommand.RangeUp);

        cut.WaitForAssertion(() => Assert.Equal(2, Editor.Marked.Count));
        Assert.Equal(
            [Node("# Eins"), Node("Erster Text"), Node("Zweiter Text"), Node("## Eins-A"), Node("Unter Text")],
            Editor.Marked[^1]);
        // The range is no change of the document: nothing is loaded again.
        Assert.Single(Editor.Documents);

        await CommandAsync(cut, SectionCommand.RangeDown);
        await CommandAsync(cut, SectionCommand.RangeDown);

        cut.WaitForAssertion(() => Assert.Equal([Node("Erster Text", true)], Editor.Marked[^1]));
        Assert.Equal(4, Editor.Marked.Count);

        // Without a cursor nothing is marked.
        await cut.InvokeAsync(() => Editor.Callbacks!.OnCursorChanged(null));
        cut.WaitForAssertion(() => Assert.Empty(Editor.Marked[^1]));
        Assert.Single(Editor.Documents);
    }

    [Fact]
    public async Task RangeBounds_UpDisabledAtBook_DownDisabledAtMinus1()
    {
        await OpenAsync();
        Assert.Null(State.CurrentRange);
        Assert.False(State.CanRangeUp);
        Assert.False(State.CanRangeDown);
        State.ChangeRange(1);
        Assert.Equal(0, State.RangeLevel);

        // "Unter Text" lies in "## Eins-A" in "# Eins" in the book.
        State.Cursor = CursorOf("Unter Text");
        Assert.True(State.CanRangeUp);
        Assert.True(State.CanRangeDown);

        State.ChangeRange(2);
        Assert.Equal(2, State.RangeLevel);
        Assert.True(State.CanRangeUp);
        State.ChangeRange(1);
        Assert.Equal(3, State.RangeLevel);
        Assert.Null(State.CurrentRange!.Head);
        Assert.False(State.CanRangeUp);
        State.ChangeRange(1);
        Assert.Equal(3, State.RangeLevel);

        State.ChangeRange(-3);
        Assert.Equal(0, State.RangeLevel);
        Assert.True(State.CanRangeDown);
        State.ChangeRange(-1);
        Assert.Equal(-1, State.RangeLevel);
        Assert.False(State.CanRangeDown);
        Assert.True(State.CanRangeUp);
        State.ChangeRange(-1);
        Assert.Equal(-1, State.RangeLevel);
    }

    [Fact]
    public async Task Relevant_UsesRangeTopic_PerLevel()
    {
        var embedder = new FakeEmbedder();
        await OpenSessionAsync(_vault, UseAi(installed: true, () => embedder));
        await Eventually(() => State.Session!.AiStatus.State == AiState.Ready);
        State.CurrentBook = State.Session!.Vault.FindBook("Buch - Alpha");

        // Heading at −1: its own text blocks, not those of its sub-sections.
        State.Cursor = CursorOf("# Eins");
        State.CurrentSectionKey = KeyOf("# Eins");
        State.ChangeRange(-1);
        var cut = Render<NotesPane>();
        await Eventually(() => embedder.Texts.Contains("query: Zweiter Text"));
        Assert.DoesNotContain("query: Unter Text", embedder.Texts);

        // Heading at 0: all text blocks of the range, sub-sections included.
        await Step(cut, () => State.ChangeRange(1));
        await Eventually(() => embedder.Texts.Contains("query: Unter Text"));

        // Text block at 1: the text blocks of its heading's section (not the cursor's neighbourhood).
        await Step(cut, () =>
        {
            State.Cursor = CursorOf("Dritter Text");
            State.CurrentSectionKey = KeyOf("# Zwei");
            State.ChangeRange(1);
        });
        await Eventually(() => embedder.Texts.Contains("query: Dritter Text"));

        // Text block at −1: only its own text, without its detail.
        await Step(cut, () =>
        {
            State.Cursor = CursorOf("Erster Text");
            State.CurrentSectionKey = KeyOf("# Eins");
            State.ChangeRange(-1);
        });
        await Eventually(() => embedder.Texts.Contains("query: Erster Text"));

        // Detail at −1: only the detail's text. The cursor move is shown (level 0) before the range is narrowed.
        await Step(cut, () =>
        {
            State.Cursor = CursorOf("Ein Detail");
            State.Notify();
        });
        await Step(cut, () => State.ChangeRange(-1));
        await Eventually(() => embedder.Texts.Contains("query: Ein Detail"));
    }

    /// <summary>Changes the state, waits until the notes pane has rendered because of it and lets the debounce pass.</summary>
    private async Task Step(IRenderedComponent<NotesPane> cut, Action change)
    {
        var rendered = cut.RenderCount;
        await cut.InvokeAsync(change);
        cut.WaitForState(() => cut.RenderCount > rendered);
        Time.Advance(TimeSpan.FromMilliseconds(1500));
    }

    [Fact]
    public async Task Sources_FollowRange()
    {
        var cut = await RenderAsync();
        string Sources() => cut.Find(".ne-sec-sources").TextContent.Trim();

        await MoveToAsync(cut, "Erster Text");
        cut.WaitForAssertion(() => Assert.Equal(Text("SectionSourcesOne"), Sources()));

        await CommandAsync(cut, SectionCommand.RangeUp);
        cut.WaitForAssertion(() => Assert.Equal(Text("SectionSources", 3), Sources()));

        await CommandAsync(cut, SectionCommand.RangeUp);
        cut.WaitForAssertion(() => Assert.Equal(Text("SectionSources", 4), Sources()));

        // A heading at −1: only its own text blocks.
        await MoveToAsync(cut, "# Eins");
        cut.WaitForAssertion(() => Assert.Equal(Text("SectionSources", 3), Sources()));
        await CommandAsync(cut, SectionCommand.RangeDown);
        cut.WaitForAssertion(() => Assert.Equal(Text("SectionSources", 2), Sources()));

        // A detail at −1: its text block's sources.
        await MoveToAsync(cut, "Ein Detail");
        await CommandAsync(cut, SectionCommand.RangeDown);
        Assert.Equal(-1, State.RangeLevel);
        cut.WaitForAssertion(() => Assert.Equal(Text("SectionSourcesOne"), Sources()));
    }

    [Fact]
    public async Task IrregularSection_CommandsNoLongerLocked()
    {
        // "## B2" is not deeper than "## B": the marking follows the tree, so the commands act on what is marked.
        const string text = "title:: Alpha\ntype:: book\n\n- # Eins\n\t- ## A\n\t- ## B\n\t\t- ## B2\n\t\t- Text B\n";
        using var tv = TestVault.Create((BookPath, text));
        var session = await OpenSessionAsync(tv);
        Assert.NotEmpty(Book.Warnings);
        State.Cursor = CursorOf("## B");

        foreach (var command in new[] { SectionCommand.Delete, SectionCommand.Indent, SectionCommand.Outdent, SectionCommand.RemoveHeading })
        {
            Assert.True(ManuscriptCommands.CanRun(State, command), $"{command}");
        }

        Assert.Null(await ManuscriptCommands.RunAsync(State, SectionCommand.Outdent, "Gliederung ändern", NullLogger.Instance));
        Assert.NotEqual(text, tv.Read(BookPath));
        Assert.True(session.Undo.CanUndo);
    }
}
