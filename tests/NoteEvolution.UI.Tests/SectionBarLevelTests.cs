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
/// The level buttons „Text · H1 … H6“ in the bar of the active block (cross references spec 4): the current level is
/// pressed, a level the block cannot take is greyed out with the reason, a click converts it with one undo entry.
/// </summary>
public class SectionBarLevelTests : UiTestContext
{
    private const string BookPath = "pages/Buch - Alpha.md";

    private const string BookText =
        "title:: Alpha\ntype:: book\n\n" +
        "- # Eins\n" +
        "\t- Erster Text\n" +
        "\t\t- Ein Punkt\n" +
        "\t- Zweiter Text\n" +
        "\t- ## Eins-A\n" +
        "\t\t- Text A\n" +
        "\t\t- ### Eins-A-i\n" +
        "- # Zwei\n";

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

    private Task MoveToAsync(IRenderedComponent<EditorPane> cut, string content) =>
        cut.InvokeAsync(() => Editor.Callbacks!.OnCursorChanged(CursorOf(content)));

    /// <summary>The level button for <paramref name="level"/> (0 = Text).</summary>
    private static AngleSharp.Dom.IElement Button(IRenderedComponent<EditorPane> cut, int level) => cut.FindAll(".ne-sec-level")[level];

    /// <summary>The label of the pressed level button.</summary>
    private static string Pressed(IRenderedComponent<EditorPane> cut) =>
        cut.FindAll(".ne-sec-level").Single(button => button.GetAttribute("aria-pressed") == "true").TextContent.Trim();

    private Task ClickAsync(IRenderedComponent<EditorPane> cut, int level) => cut.InvokeAsync(() => Button(cut, level).Click());

    [Fact]
    public async Task Levels_CurrentPressed_BlockedGreyedWithReason()
    {
        var cut = await RenderAsync();

        // A text block shows „Text“; every heading level fits its H1 section.
        await MoveToAsync(cut, "Erster Text");
        cut.WaitForAssertion(() => Assert.Equal("Text", Pressed(cut)));
        Assert.Equal(["Text", "H1", "H2", "H3", "H4", "H5", "H6"], cut.FindAll(".ne-sec-level").Select(b => b.TextContent.Trim()));
        Assert.Equal(Text("SectionLevels"), cut.Find(".ne-sec-levels").GetAttribute("aria-label"));
        Assert.All(Enumerable.Range(1, 6), level => Assert.False(Button(cut, level).HasAttribute("disabled")));
        Assert.Equal(string.Format(Text("SectionLevelHeadingTitle"), 2), Button(cut, 2).GetAttribute("title"));
        Assert.Equal(Text("SectionLevelCurrent"), Button(cut, 0).GetAttribute("title"));

        // A sub-point is text too.
        await MoveToAsync(cut, "Ein Punkt");
        cut.WaitForAssertion(() => Assert.Equal("Text", Pressed(cut)));

        // In an H2 section an H1 would take over sections not its own: greyed out, the tooltip says why.
        await MoveToAsync(cut, "Text A");
        cut.WaitForAssertion(() => Assert.True(Button(cut, 1).HasAttribute("disabled")));
        Assert.Equal(Text("LevelBlockedAboveSection"), Button(cut, 1).GetAttribute("title"));
        Assert.False(Button(cut, 2).HasAttribute("disabled"));

        // A heading shows its level; above its parent's level it is placed out of that section, so only a level that
        // would put a sub-heading at H7 is greyed out.
        await MoveToAsync(cut, "## Eins-A");
        cut.WaitForAssertion(() => Assert.Equal("H2", Pressed(cut)));
        Assert.True(Button(cut, 6).HasAttribute("disabled"));
        Assert.Equal(Text("LevelBlockedBelowH6"), Button(cut, 6).GetAttribute("title"));
        Assert.All([0, 1, 3, 4, 5], level => Assert.False(Button(cut, level).HasAttribute("disabled")));
        Assert.Equal(string.Format(Text("SectionLevelHeadingTitle"), 1), Button(cut, 1).GetAttribute("title"));
        Assert.Equal(Text("SectionLevelTextTitle"), Button(cut, 0).GetAttribute("title"));
    }

    [Fact]
    public async Task TextBlockToHeading_ConvertsAndStaysActive_UndoRestoresBytes()
    {
        var cut = await RenderAsync();
        var session = State.Session!;
        var before = File.ReadAllBytes(Path.Combine(_vault.Root, BookPath));

        await MoveToAsync(cut, "Erster Text");
        await ClickAsync(cut, 2);

        // The sub-point becomes a text block with both dates, the following text block goes under the new heading.
        Assert.Equal(
            "title:: Alpha\ntype:: book\n\n" +
            "- # Eins\n" +
            "\t- ## Erster Text\n" +
            "\t\t- Ein Punkt\n" + $"\t\t  created:: {Stamp}\n\t\t  updated:: {Stamp}\n" +
            "\t\t- Zweiter Text\n" +
            "\t- ## Eins-A\n\t\t- Text A\n\t\t- ### Eins-A-i\n" +
            "- # Zwei\n",
            _vault.Read(BookPath));
        Assert.Equal(Text("UndoLevel"), session.Undo.NextDescription);

        // The converted element stays the active one.
        var heading = KeyOf("## Erster Text");
        Assert.Equal(heading, Editor.Reveals[^1]);
        Assert.Equal(heading, State.Cursor!.Key);
        cut.WaitForAssertion(() => Assert.Equal("H2", Pressed(cut)));
        Assert.Empty(cut.FindAll(".ne-editor-error"));

        Assert.True(session.TryUndo(out var error), error?.ToString());
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_vault.Root, BookPath)));
    }

    [Fact]
    public async Task SubPointToHeading_AfterItsTextBlock_UndoRestoresBytes()
    {
        var cut = await RenderAsync();
        var session = State.Session!;
        var before = File.ReadAllBytes(Path.Combine(_vault.Root, BookPath));

        await MoveToAsync(cut, "Ein Punkt");
        await ClickAsync(cut, 2);

        // The heading follows its text block, which is changed; the following text block goes under the heading.
        Assert.Equal(
            BookText.Replace(
                "\t- Erster Text\n\t\t- Ein Punkt\n\t- Zweiter Text\n",
                $"\t- Erster Text\n\t  updated:: {Stamp}\n\t- ## Ein Punkt\n\t\t- Zweiter Text\n"),
            _vault.Read(BookPath));
        Assert.Equal(KeyOf("## Ein Punkt"), State.Cursor!.Key);
        cut.WaitForAssertion(() => Assert.Equal("H2", Pressed(cut)));

        Assert.True(session.TryUndo(out var error), error?.ToString());
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_vault.Root, BookPath)));
    }

    [Fact]
    public async Task OneLineBlockWithId_EmptyTextBlockCarriesIt_UndoRestoresBytes()
    {
        const string id = "11111111-1111-1111-1111-111111111111";
        const string text = "title:: Alpha\ntype:: book\n\n- # Eins\n\t- Kapitel\n\t  id:: " + id + "\n";
        var path = Path.Combine(_vault.Root, BookPath);
        File.WriteAllText(path, text);
        var cut = await RenderAsync();
        var session = State.Session!;
        var before = File.ReadAllBytes(path);

        await MoveToAsync(cut, "Kapitel");
        await ClickAsync(cut, 2);

        // Cross references to the block keep a target: an empty text block under the heading takes its id::.
        Assert.Equal(
            "title:: Alpha\ntype:: book\n\n- # Eins\n\t- ## Kapitel\n\t\t-\n\t\t  id:: " + id + "\n" +
            $"\t\t  created:: {Stamp}\n\t\t  updated:: {Stamp}\n",
            _vault.Read(BookPath));
        Assert.Equal(KeyOf("## Kapitel"), Book.FindTextBlockById(Guid.Parse(id))!.Section.Key);

        Assert.True(session.TryUndo(out var error), error?.ToString());
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task HeadingLevel_ShiftsSubHeadings_UndoRestoresBytes()
    {
        var cut = await RenderAsync();
        var session = State.Session!;
        var before = File.ReadAllBytes(Path.Combine(_vault.Root, BookPath));

        await MoveToAsync(cut, "## Eins-A");
        await ClickAsync(cut, 4);

        Assert.Equal(
            BookText.Replace("\t- ## Eins-A\n", "\t- #### Eins-A\n").Replace("\t\t- ### Eins-A-i\n", "\t\t- ##### Eins-A-i\n"),
            _vault.Read(BookPath));
        Assert.Equal(KeyOf("#### Eins-A"), State.Cursor!.Key);
        cut.WaitForAssertion(() => Assert.Equal("H4", Pressed(cut)));

        Assert.True(session.TryUndo(out var error), error?.ToString());
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_vault.Root, BookPath)));
    }

    [Fact]
    public async Task HeadingToText_RemovesHeading_UndoRestoresBytes()
    {
        var cut = await RenderAsync();
        var session = State.Session!;
        var before = File.ReadAllBytes(Path.Combine(_vault.Root, BookPath));

        await MoveToAsync(cut, "## Eins-A");
        await ClickAsync(cut, 0);

        // As „Überschrift entfernen“: the title is a text block, the content moves up into "# Eins".
        Assert.Equal(
            BookText.Replace(
                "\t- ## Eins-A\n\t\t- Text A\n\t\t- ### Eins-A-i\n",
                $"\t- Eins-A\n\t  created:: {Stamp}\n\t  updated:: {Stamp}\n\t- Text A\n\t- ## Eins-A-i\n"),
            _vault.Read(BookPath));
        Assert.Equal(KeyOf("Eins-A"), State.Cursor!.Key);
        cut.WaitForAssertion(() => Assert.Equal("Text", Pressed(cut)));
        Assert.Equal(Text("UndoLevel"), session.Undo.NextDescription);

        Assert.True(session.TryUndo(out var error), error?.ToString());
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_vault.Root, BookPath)));
    }

    [Fact]
    public async Task Levels_DisabledOnReadOnlyBook()
    {
        // An unclosed code fence makes the book read-only.
        var path = Path.Combine(_vault.Root, BookPath);
        File.WriteAllText(path, BookText + "- ```\n  offen\n");
        var cut = await RenderAsync();
        Assert.True(Book.Page.IsReadOnly);

        await MoveToAsync(cut, "Erster Text");

        cut.WaitForAssertion(() => Assert.All(cut.FindAll(".ne-sec-level"), button => Assert.True(button.HasAttribute("disabled"))));
        Assert.False(ManuscriptCommands.CanSetLevel(State, 2));
        Assert.Equal(BookText + "- ```\n  offen\n", _vault.Read(BookPath));
    }
}
