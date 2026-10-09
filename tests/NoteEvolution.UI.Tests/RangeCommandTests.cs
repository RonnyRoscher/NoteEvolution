using System.Text;
using System.Text.Json.Nodes;
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
/// The range buttons of the section bar and the commands on the range (package B, spec 2, 4, 5): merging the text
/// blocks per heading and putting the range under a new heading, each with one undo entry.
/// </summary>
public class RangeCommandTests : UiTestContext
{
    private const string BookPath = "pages/Buch - Alpha.md";

    private const string NotesPath = "pages/Notizen.md";

    private const string ErsterId = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee1";

    private const string ZweiterId = "ffffffff-ffff-4fff-8fff-fffffffffff1";

    private const string NoteA = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1";

    private const string NoteB = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb1";

    private const string BookText =
        "title:: Alpha\ntype:: book\n\n" +
        "- Vorspann Text\n" +
        "- # Eins\n" +
        "\t- Erster Text\n" +
        $"\t  id:: {ErsterId}\n" +
        $"\t  source:: (({NoteA}))\n" +
        "\t\t- Ein Detail\n" +
        "\t- Zweiter Text\n" +
        $"\t  id:: {ZweiterId}\n" +
        $"\t  source:: (({NoteB}))\n" +
        "\t- ## Eins-A\n" +
        "\t\t- Unter Text\n" +
        "\t\t- Unter Zwei\n" +
        "- # Zwei\n" +
        "\t- Dritter Text\n";

    private const string NotesText =
        "- Notiz A\n" +
        $"  id:: {NoteA}\n" +
        $"  used-in:: [[Buch - Alpha]] (({ErsterId}))\n" +
        "- Notiz B\n" +
        $"  id:: {NoteB}\n" +
        $"  used-in:: [[Buch - Alpha]] (({ZweiterId}))\n";

    private readonly TestVault _vault = TestVault.Create((BookPath, BookText), (NotesPath, NotesText));

    /// <summary>Vaults a test opens instead of <see cref="_vault"/>.</summary>
    private readonly List<TestVault> _otherVaults = [];

    protected override void Dispose(bool disposing)
    {
        // The session is closed first, then the vault folders are deleted.
        base.Dispose(disposing);
        if (disposing)
        {
            _vault.Dispose();
            _otherVaults.ForEach(v => v.Dispose());
        }
    }

    /// <summary>Opens a vault with only the book <paramref name="bookText"/> (at <see cref="BookPath"/>) instead of <see cref="_vault"/>.</summary>
    private async Task<TestVault> OpenOtherAsync(string bookText)
    {
        var vault = TestVault.Create((BookPath, bookText));
        _otherVaults.Add(vault);
        await OpenSessionAsync(vault);
        return vault;
    }

    private Book Book => State.CurrentBook!;

    private string Text(string key, params object[] arguments) =>
        Services.GetRequiredService<IStringLocalizer<Strings>>()[key, arguments].Value;

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
        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionBoxMoved(80, true));
        return cut;
    }

    /// <summary>Moves the cursor (as the editor reports it) to the element whose block has <paramref name="content"/>.</summary>
    private Task MoveToAsync(IRenderedComponent<EditorPane> cut, string content) =>
        cut.InvokeAsync(() => Editor.Callbacks!.OnCursorChanged(CursorOf(content)));

    private static Task ClickAsync(IRenderedComponent<EditorPane> cut, string selector) =>
        cut.InvokeAsync(() => cut.Find(selector).Click());

    private static string Label(IRenderedComponent<EditorPane> cut) => cut.Find(".ne-range-label").TextContent;

    private static bool Disabled(IRenderedComponent<EditorPane> cut, string selector) => cut.Find(selector).HasAttribute("disabled");

    /// <summary>Asserts, with <see cref="LineDiff"/>, that exactly the given lines differ between <paramref name="before"/> and <paramref name="after"/>.</summary>
    private static void AssertChangedLines(string before, string after, string[] removed, string[] added)
    {
        var (removedIndices, addedIndices) = LineDiff.Changed(Encoding.UTF8.GetBytes(before), Encoding.UTF8.GetBytes(after));
        Assert.Equal(removed, removedIndices.Select(i => before.Split('\n')[i]));
        Assert.Equal(added, addedIndices.Select(i => after.Split('\n')[i]));
    }

    /// <summary>The book after merging the range of "# Eins": its own blocks into one, and those of "## Eins-A" into one.</summary>
    private const string MergedBook =
        "title:: Alpha\ntype:: book\n\n" +
        "- Vorspann Text\n" +
        "- # Eins\n" +
        "\t- Erster Text\n" +
        $"\t  id:: {ErsterId}\n" +
        $"\t  source:: (({NoteA})), (({NoteB}))\n" +
        "\n" +
        "\t  Zweiter Text\n" +
        "\t\t- Ein Detail\n" +
        "\t- ## Eins-A\n" +
        "\t\t- Unter Text\n" +
        "\n" +
        "\t\t  Unter Zwei\n" +
        "- # Zwei\n" +
        "\t- Dritter Text\n";

    private const string MergedNotes =
        "- Notiz A\n" +
        $"  id:: {NoteA}\n" +
        $"  used-in:: [[Buch - Alpha]] (({ErsterId}))\n" +
        "- Notiz B\n" +
        $"  id:: {NoteB}\n" +
        $"  used-in:: [[Buch - Alpha]] (({ErsterId}))\n";

    /// <summary>The book after putting the range of "Erster Text" under a new heading: it and "Zweiter Text" go below it.</summary>
    private const string WrappedBook =
        "title:: Alpha\ntype:: book\n\n" +
        "- Vorspann Text\n" +
        "- # Eins\n" +
        "\t- ## \n" +
        "\t\t- Erster Text\n" +
        $"\t\t  id:: {ErsterId}\n" +
        $"\t\t  source:: (({NoteA}))\n" +
        "\t\t\t- Ein Detail\n" +
        "\t\t- Zweiter Text\n" +
        $"\t\t  id:: {ZweiterId}\n" +
        $"\t\t  source:: (({NoteB}))\n" +
        "\t- ## Eins-A\n" +
        "\t\t- Unter Text\n" +
        "\t\t- Unter Zwei\n" +
        "- # Zwei\n" +
        "\t- Dritter Text\n";

    [Fact]
    public async Task RangeButtons_ChangeLevel_LabelFollows()
    {
        var cut = await RenderAsync();
        await MoveToAsync(cut, "Ein Detail");
        cut.WaitForAssertion(() => Assert.Equal(Text("RangeDetail"), Label(cut)));
        Assert.Equal(Text("RangeUp"), cut.Find(".ne-range-up").TextContent.Trim());
        Assert.Equal(Text("RangeUpTitle"), cut.Find(".ne-range-up").GetAttribute("title"));
        Assert.Equal(Text("RangeDown"), cut.Find(".ne-range-down").TextContent.Trim());
        Assert.Equal(Text("RangeDownTitle"), cut.Find(".ne-range-down").GetAttribute("title"));
        Assert.False(Disabled(cut, ".ne-range-down"));

        await ClickAsync(cut, ".ne-range-down");
        cut.WaitForAssertion(() => Assert.Equal(Text("RangeDetail") + Text("RangeOwnOnly"), Label(cut)));
        Assert.Equal(-1, State.RangeLevel);
        Assert.True(Disabled(cut, ".ne-range-down"));

        (string Label, int Level)[] up =
        [
            (Text("RangeDetail"), 0),
            (Text("RangeTextBlock"), 1),
            (Text("RangeHeading", "# Eins"), 2),
            (Text("RangeBook"), 3),
        ];
        foreach (var (label, level) in up)
        {
            Assert.False(Disabled(cut, ".ne-range-up"));
            await ClickAsync(cut, ".ne-range-up");
            cut.WaitForAssertion(() => Assert.Equal(label, Label(cut)));
            Assert.Equal(level, State.RangeLevel);
        }

        Assert.True(Disabled(cut, ".ne-range-up"));
        Assert.False(Disabled(cut, ".ne-range-down"));

        // The editor marks the range the label names: the whole book.
        Assert.Equal(State.CurrentRange!.Nodes, Editor.Marked[^1]);

        // The range buttons change no file and push no undo entry.
        Assert.Equal(BookText, _vault.Read(BookPath));
        Assert.False(State.Session!.Undo.CanUndo);
    }

    [Fact]
    public async Task RangeLabel_OnlyHeadTruncated_SuffixApart_TitleIsFullLabel()
    {
        var cut = await RenderAsync();
        await MoveToAsync(cut, "## Eins-A");
        cut.WaitForAssertion(() => Assert.Equal(Text("RangeHeading", "## Eins-A"), Label(cut)));

        // Level 0: only the head, which is the part that may be cut off; the hover shows the full label.
        var label = cut.Find(".ne-range-label");
        Assert.Equal(Text("RangeHeading", "## Eins-A"), cut.Find(".ne-range-label > .ne-range-head").TextContent);
        Assert.Empty(cut.FindAll(".ne-range-own"));
        Assert.Equal(Label(cut), label.GetAttribute("title"));

        // Level −1: the suffix stands beside the head, never inside the part that is cut off.
        await ClickAsync(cut, ".ne-range-down");
        cut.WaitForAssertion(() => Assert.Equal(Text("RangeHeading", "## Eins-A") + Text("RangeOwnOnly"), Label(cut)));
        Assert.Equal(Text("RangeHeading", "## Eins-A"), cut.Find(".ne-range-label > .ne-range-head").TextContent);
        Assert.Equal(Text("RangeOwnOnly"), cut.Find(".ne-range-label > .ne-range-own").TextContent);
        Assert.Empty(cut.FindAll(".ne-range-head .ne-range-own"));
        Assert.Equal(Text("RangeHeading", "## Eins-A") + Text("RangeOwnOnly"), cut.Find(".ne-range-label").GetAttribute("title"));
    }

    [Fact]
    public async Task RangeShortcut_AltArrow_ChangesLevel()
    {
        var cut = await RenderAsync();
        await MoveToAsync(cut, "Erster Text");
        cut.WaitForAssertion(() => Assert.Equal(Text("RangeTextBlock"), Label(cut)));

        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionCommand(SectionCommand.RangeUp));

        cut.WaitForAssertion(() => Assert.Equal(Text("RangeHeading", "# Eins"), Label(cut)));
        Assert.Equal(1, State.RangeLevel);

        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionCommand(SectionCommand.RangeDown));
        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionCommand(SectionCommand.RangeDown));

        cut.WaitForAssertion(() => Assert.Equal(Text("RangeTextBlock") + Text("RangeOwnOnly"), Label(cut)));
        Assert.Equal(-1, State.RangeLevel);
    }

    [Fact]
    public async Task Merge_FromHeadingRange_MergesOwnBlocks_RevealsFirst()
    {
        var cut = await RenderAsync();
        var session = State.Session!;
        var erster = KeyOf("Erster Text");
        await MoveToAsync(cut, "Erster Text");
        await ClickAsync(cut, ".ne-range-up");
        cut.WaitForAssertion(() => Assert.Equal(Text("RangeHeading", "# Eins"), Label(cut)));
        Assert.Equal(Text("SectionMerge"), cut.Find(".ne-sec-merge").TextContent.Trim());
        Assert.False(Disabled(cut, ".ne-sec-merge"));

        await ClickAsync(cut, ".ne-sec-merge");

        Assert.Equal(MergedBook, _vault.Read(BookPath));
        AssertChangedLines(
            BookText, MergedBook,
            removed: [$"\t  source:: (({NoteA}))", "\t- Zweiter Text", $"\t  id:: {ZweiterId}", $"\t  source:: (({NoteB}))", "\t\t- Unter Zwei"],
            added: [$"\t  source:: (({NoteA})), (({NoteB}))", "", "\t  Zweiter Text", "", "\t\t  Unter Zwei"]);
        Assert.Equal(MergedNotes, _vault.Read(NotesPath));
        AssertChangedLines(NotesText, MergedNotes, [$"  used-in:: [[Buch - Alpha]] (({ZweiterId}))"], [$"  used-in:: [[Buch - Alpha]] (({ErsterId}))"]);

        // The cursor goes to the start of the first remaining block, the range back to level 0.
        cut.WaitForAssertion(() => Assert.Equal(erster, Assert.Single(Editor.Reveals)));
        Assert.Equal(erster, State.Cursor!.Key);
        Assert.Equal(0, State.RangeLevel);
        cut.WaitForAssertion(() => Assert.Equal(Text("RangeTextBlock"), Label(cut)));
        Assert.Equal("Zusammenfügen", session.Undo.NextDescription);
        Assert.Empty(cut.FindAll(".ne-editor-error"));
    }

    [Fact]
    public async Task Merge_Disabled_WhenNoGroup()
    {
        var cut = await RenderAsync();
        var session = State.Session!;

        // A text block alone, and a heading with a single own text block, have nothing to merge.
        await MoveToAsync(cut, "Erster Text");
        cut.WaitForAssertion(() => Assert.Equal(Text("RangeTextBlock"), Label(cut)));
        Assert.True(Disabled(cut, ".ne-sec-merge"));
        await MoveToAsync(cut, "# Zwei");
        cut.WaitForAssertion(() => Assert.Equal(Text("RangeHeading", "# Zwei"), Label(cut)));
        Assert.True(Disabled(cut, ".ne-sec-merge"));
        Assert.False(ManuscriptCommands.CanRun(State, SectionCommand.Merge));

        // The command itself does nothing either.
        Assert.Null(await cut.InvokeAsync(() => ManuscriptCommands.RunAsync(State, SectionCommand.Merge, "x", NullLogger.Instance)));
        Assert.Equal(BookText, _vault.Read(BookPath));
        Assert.Equal(NotesText, _vault.Read(NotesPath));
        Assert.False(session.Undo.CanUndo);

        // A heading's own text blocks (level −1) can be merged.
        await MoveToAsync(cut, "# Eins");
        await ClickAsync(cut, ".ne-range-down");
        cut.WaitForAssertion(() => Assert.Equal(Text("RangeHeading", "# Eins") + Text("RangeOwnOnly"), Label(cut)));
        Assert.False(Disabled(cut, ".ne-sec-merge"));
    }

    [Fact]
    public async Task Merge_NoteConflictOpen_Refused()
    {
        var cut = await RenderAsync();
        var session = State.Session!;

        // A local change of the notes page collides with an external change of the same line.
        var notesPath = Path.Combine(_vault.Root, NotesPath);
        var external = NotesText.Replace("- Notiz B\n", "- Notiz B extern\n");
        await cut.InvokeAsync(() =>
        {
            session.Vault.FindPageByPath(notesPath)!.AllBlocks().Single(b => b.Content == "Notiz B").SetContent("Notiz B lokal");
            File.WriteAllText(notesPath, external);
            session.HandleExternalChange(notesPath);
        });
        Assert.True(session.HasOpenConflict(notesPath));

        await MoveToAsync(cut, "# Eins");
        cut.WaitForAssertion(() => Assert.False(Disabled(cut, ".ne-sec-merge")));

        await ClickAsync(cut, ".ne-sec-merge");

        cut.WaitForAssertion(() => Assert.Equal(Text("EditorConflict"), cut.Find(".ne-editor-error").TextContent));
        Assert.Equal(BookText, _vault.Read(BookPath));
        Assert.Equal(external, _vault.Read(NotesPath));
        Assert.False(session.Undo.CanUndo);
    }

    [Fact]
    public async Task Wrap_FromTextBlockRange_RevealsNewHeading()
    {
        var cut = await RenderAsync();
        var session = State.Session!;
        await MoveToAsync(cut, "Erster Text");
        cut.WaitForAssertion(() => Assert.Equal(Text("RangeTextBlock"), Label(cut)));
        Assert.Equal(Text("SectionWrap"), cut.Find(".ne-sec-wrap").TextContent.Trim());
        Assert.False(Disabled(cut, ".ne-sec-wrap"));

        await ClickAsync(cut, ".ne-sec-wrap");

        Assert.Equal(WrappedBook, _vault.Read(BookPath));
        Assert.Equal(NotesText, _vault.Read(NotesPath));
        var heading = KeyOf("## ");
        cut.WaitForAssertion(() => Assert.Equal(heading, Assert.Single(Editor.Reveals)));
        Assert.Equal((ElementKind.Heading, heading), (State.Cursor!.Kind, State.Cursor.Key));
        Assert.Equal(heading, State.CurrentSectionKey);
        cut.WaitForAssertion(() => Assert.Equal(Text("RangeHeading", "## " + Text("OutlineUntitled")), Label(cut)));
        Assert.Equal(Text("UndoWrap"), session.Undo.NextDescription);
        Assert.Empty(cut.FindAll(".ne-editor-error"));
    }

    [Fact]
    public async Task Wrap_Disabled_ForDetailAndBook()
    {
        var cut = await RenderAsync();
        var session = State.Session!;
        await MoveToAsync(cut, "Ein Detail");
        cut.WaitForAssertion(() => Assert.Equal(Text("RangeDetail"), Label(cut)));
        Assert.True(Disabled(cut, ".ne-sec-wrap"));

        // The range of the detail's text block and of its heading can be wrapped, the whole book cannot.
        await ClickAsync(cut, ".ne-range-up");
        cut.WaitForAssertion(() => Assert.Equal(Text("RangeTextBlock"), Label(cut)));
        Assert.False(Disabled(cut, ".ne-sec-wrap"));
        await ClickAsync(cut, ".ne-range-up");
        cut.WaitForAssertion(() => Assert.Equal(Text("RangeHeading", "# Eins"), Label(cut)));
        Assert.False(Disabled(cut, ".ne-sec-wrap"));
        await ClickAsync(cut, ".ne-range-up");
        cut.WaitForAssertion(() => Assert.Equal(Text("RangeBook"), Label(cut)));
        Assert.True(Disabled(cut, ".ne-sec-wrap"));
        Assert.False(ManuscriptCommands.CanRun(State, SectionCommand.Wrap));

        Assert.Null(await cut.InvokeAsync(() => ManuscriptCommands.RunAsync(State, SectionCommand.Wrap, "x", NullLogger.Instance)));
        Assert.Equal(BookText, _vault.Read(BookPath));
        Assert.False(session.Undo.CanUndo);
    }

    [Fact]
    public async Task Wrap_Undo_ViaHeader_RestoresStructure()
    {
        var cut = await RenderAsync();
        var header = Render<HeaderBar>();
        var session = State.Session!;
        await MoveToAsync(cut, "Erster Text");
        await ClickAsync(cut, ".ne-sec-wrap");
        Assert.Equal(WrappedBook, _vault.Read(BookPath));

        // A title typed into the new heading does not block the undo; it goes with the heading.
        await cut.InvokeAsync(() =>
        {
            OutlineEditor.Rename(Book, KeyOf("## "), "Neu");
            Assert.True(session.TrySave(Book.Page, out _));
        });
        Assert.Equal(WrappedBook.Replace("\t- ## \n", "\t- ## Neu\n"), _vault.Read(BookPath));
        header.WaitForAssertion(() => Assert.Equal(Text("UndoWrap"), header.Find(".ne-undo").GetAttribute("title")));

        await header.InvokeAsync(() => header.Find(".ne-undo").Click());

        Assert.Equal(BookText, _vault.Read(BookPath));
        header.WaitForAssertion(() => Assert.Empty(header.FindAll(".ne-undo-error")));
        Assert.False(session.Undo.CanUndo);
    }

    [Fact]
    public async Task Merge_Undo_ViaHeader()
    {
        var cut = await RenderAsync();
        var header = Render<HeaderBar>();
        var session = State.Session!;
        await MoveToAsync(cut, "# Eins");
        await ClickAsync(cut, ".ne-sec-merge");
        Assert.Equal(MergedBook, _vault.Read(BookPath));
        header.WaitForAssertion(() => Assert.Equal("Zusammenfügen", header.Find(".ne-undo").GetAttribute("title")));

        await header.InvokeAsync(() => header.Find(".ne-undo").Click());

        Assert.Equal(BookText, _vault.Read(BookPath));
        Assert.Equal(NotesText, _vault.Read(NotesPath));
        header.WaitForAssertion(() => Assert.Empty(header.FindAll(".ne-undo-error")));
        Assert.False(session.Undo.CanUndo);
    }

    [Fact]
    public async Task DeleteIndentOutdent_OwnOnlyRange_RefusedWhenTheElementHasSubElements()
    {
        const string book =
            "title:: Alpha\ntype:: book\n\n" +
            "- # Eins\n" +
            "\t- ## Eins-0\n" +
            "\t- ## Eins-A\n" +
            "\t\t- Text\n" +
            "\t\t\t- Detail\n" +
            "\t\t\t\t- Tiefer\n" +
            "\t\t- Ohne\n" +
            "\t\t- ### Tief\n";
        var vault = await OpenOtherAsync(book);
        var session = State.Session!;

        void At(string content, int level)
        {
            var element = BookElements.Find(Book, KeyOf(content))!;
            State.Cursor = new CursorInfo(element.Kind, element.Key, BookElements.TextBlockOf(Book, element)?.Key, 0);
            State.RangeLevel = level;
        }

        bool CanRun(SectionCommand command) => ManuscriptCommands.CanRun(State, command);

        // Level 0 marks the element with everything below it, which is what the commands act on.
        At("## Eins-A", 0);
        Assert.Equal([true, true, true], new[] { CanRun(SectionCommand.Delete), CanRun(SectionCommand.Indent), CanRun(SectionCommand.Outdent) });
        At("Text", 0);
        Assert.True(CanRun(SectionCommand.Delete));
        At("Detail", 0);
        Assert.True(CanRun(SectionCommand.Delete));

        // Level −1 marks less than a heading with sub-headings, a text block with details or a detail with deeper ones.
        At("## Eins-A", -1);
        Assert.Equal([false, false, false], new[] { CanRun(SectionCommand.Delete), CanRun(SectionCommand.Indent), CanRun(SectionCommand.Outdent) });
        Assert.Null(await Run(SectionCommand.Delete));
        Assert.Null(await Run(SectionCommand.Outdent));
        At("Text", -1);
        Assert.False(CanRun(SectionCommand.Delete));
        Assert.Null(await Run(SectionCommand.Delete));
        At("Detail", -1);
        Assert.False(CanRun(SectionCommand.Delete));
        Assert.Null(await Run(SectionCommand.Delete));
        Assert.Equal(book, vault.Read(BookPath));
        Assert.False(session.Undo.CanUndo);

        // Without sub-elements level −1 marks all the commands act on.
        At("### Tief", -1);
        Assert.True(CanRun(SectionCommand.Delete));
        Assert.True(CanRun(SectionCommand.Outdent));
        At("Ohne", -1);
        Assert.True(CanRun(SectionCommand.Delete));
        At("Tiefer", -1);
        Assert.True(CanRun(SectionCommand.Delete));
        At("## Eins-0", -1);
        Assert.True(CanRun(SectionCommand.Delete));
        Assert.True(CanRun(SectionCommand.Outdent));

        Task<AdoptMessage?> Run(SectionCommand command) =>
            ManuscriptCommands.RunAsync(State, command, "x", NullLogger.Instance);
    }

    [Fact]
    public async Task Merge_ConflictingProperties_RefusedWithMessage()
    {
        const string book =
            "title:: Alpha\ntype:: book\n\n" +
            "- # Eins\n" +
            "\t- Erster Text\n" +
            "\t  status:: offen\n" +
            "\t- Zweiter Text\n" +
            "\t  status:: fertig\n";
        var vault = await OpenOtherAsync(book);
        State.Cursor = CursorOf("# Eins");
        Assert.True(ManuscriptCommands.CanRun(State, SectionCommand.Merge));

        var message = await ManuscriptCommands.RunAsync(State, SectionCommand.Merge, "x", NullLogger.Instance);

        Assert.Equal(new AdoptMessage("SectionMergeRefused", true), message);
        Assert.Equal(
            "Die Blöcke lassen sich nicht zusammenfügen: unterschiedliche Eigenschaften oder ein Verweis an anderer Stelle.",
            Text("SectionMergeRefused"));
        Assert.Equal(book, vault.Read(BookPath));
        Assert.False(State.Session!.Undo.CanUndo);
    }

    [Theory]
    [InlineData(SectionCommand.Merge)]
    [InlineData(SectionCommand.Wrap)]
    public async Task MergeWrap_UnsavedEditorText_Refused(SectionCommand command)
    {
        var cut = await RenderAsync();
        var session = State.Session!;
        await MoveToAsync(cut, "# Eins");
        cut.WaitForAssertion(() => Assert.True(ManuscriptCommands.CanRun(State, command)));
        State.HasUnsavedEditorText = () => true;

        var message = await cut.InvokeAsync(() => ManuscriptCommands.RunAsync(State, command, "x", NullLogger.Instance));

        Assert.Equal(new AdoptMessage("SectionCommandUnsaved", true), message);
        Assert.Equal(BookText, _vault.Read(BookPath));
        Assert.Equal(NotesText, _vault.Read(NotesPath));
        Assert.False(session.Undo.CanUndo);
    }

    [Theory]
    [InlineData(SectionCommand.Merge, 2)]
    [InlineData(SectionCommand.Wrap, 1)]
    public async Task MergeWrap_RangeChangedBySave_Refused(SectionCommand command, int level)
    {
        var cut = await RenderAsync();
        var session = State.Session!;
        var erster = KeyOf("Erster Text");

        // The editor has a new detail in "Erster Text" that the book only knows after the save.
        var detail = Guid.NewGuid();
        var doc = JsonNode.Parse(Editor.Json)!;
        var textBlock = doc["content"]!.AsArray().Single(n => (string?)n!["attrs"]?["key"] == erster.ToString("D"))!;
        textBlock["content"]!.AsArray().Add(new JsonObject
        {
            ["type"] = "para",
            ["attrs"] = new JsonObject { ["key"] = detail.ToString("D"), ["depth"] = 1, ["isNote"] = false },
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Neues Detail" }),
        });
        await cut.InvokeAsync(() => Editor.Callbacks!.OnDocumentChanged(doc.ToJsonString()));
        await cut.InvokeAsync(() => Editor.Callbacks!.OnCursorChanged(new CursorInfo(ElementKind.Detail, detail, erster, 0)));
        Assert.Equal(new BookElement(ElementKind.TextBlock, erster), State.CurrentElement);
        for (var i = 0; i < level; i++)
        {
            await ClickAsync(cut, ".ne-range-up");
        }

        // Before the save the range is that of "# Eins" (Wrap) or of the whole book (Merge).
        cut.WaitForAssertion(() => Assert.Equal(level == 1 ? Text("RangeHeading", "# Eins") : Text("RangeBook"), Label(cut)));
        Assert.True(ManuscriptCommands.CanRun(State, command));

        var message = await cut.InvokeAsync(() => ManuscriptCommands.RunAsync(State, command, "x", NullLogger.Instance));

        // The save makes the detail the cursor element, so the same level is another range: nothing is merged or wrapped.
        Assert.Null(message);
        Assert.Equal(BookText.Replace("\t\t- Ein Detail\n", "\t\t- Ein Detail\n\t\t- Neues Detail\n"), _vault.Read(BookPath));
        Assert.Equal(NotesText, _vault.Read(NotesPath));
        Assert.NotEqual("Zusammenfügen", session.Undo.NextDescription);
        Assert.NotEqual(Text("UndoWrap"), session.Undo.NextDescription);
    }
}
