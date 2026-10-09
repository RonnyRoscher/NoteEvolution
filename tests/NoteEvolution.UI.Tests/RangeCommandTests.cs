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
            (Text("RangeHeading", "Eins"), 2),
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
    public async Task RangeShortcut_AltArrow_ChangesLevel()
    {
        var cut = await RenderAsync();
        await MoveToAsync(cut, "Erster Text");
        cut.WaitForAssertion(() => Assert.Equal(Text("RangeTextBlock"), Label(cut)));

        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionCommand(SectionCommand.RangeUp));

        cut.WaitForAssertion(() => Assert.Equal(Text("RangeHeading", "Eins"), Label(cut)));
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
        cut.WaitForAssertion(() => Assert.Equal(Text("RangeHeading", "Eins"), Label(cut)));
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
        cut.WaitForAssertion(() => Assert.Equal(Text("RangeHeading", "Zwei"), Label(cut)));
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
        cut.WaitForAssertion(() => Assert.Equal(Text("RangeHeading", "Eins") + Text("RangeOwnOnly"), Label(cut)));
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
        cut.WaitForAssertion(() => Assert.Equal(Text("RangeHeading", Text("OutlineUntitled")), Label(cut)));
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
        cut.WaitForAssertion(() => Assert.Equal(Text("RangeHeading", "Eins"), Label(cut)));
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
}
