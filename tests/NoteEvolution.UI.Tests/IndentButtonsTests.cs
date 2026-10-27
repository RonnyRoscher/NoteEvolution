using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Editor;
using NoteEvolution.UI.Resources;

namespace NoteEvolution.UI.Tests;

/// <summary>
/// „⇤ Ausrücken“ and „Einrücken ⇥“ in the bar of the active block, Tab / Shift+Tab where they leave the editor, and the
/// current level button placing a misplaced heading (Einordnen spec 2, 4): each change is one undo entry.
/// </summary>
public class IndentButtonsTests : UiTestContext
{
    private const string BookPath = "pages/Buch - Alpha.md";

    private const string BookText =
        "title:: Alpha\ntype:: book\n\n" +
        "- # Eins\n" +
        "\t- Erster Text\n" +
        "\t\t- Punkt A\n" +
        "\t\t- Punkt B\n" +
        "\t\t\t- Punkt B1\n" +
        "\t- Zweiter Text\n" +
        "\t- ## Eins-A\n" +
        "\t\t- Text A\n" +
        "- # Zwei\n" +
        "\t- ## Zwei-A\n" +
        "- # Drei\n" +
        "\t- ## D2\n" +
        "\t\t- ### D3\n" +
        "\t\t\t- #### D4\n" +
        "\t\t\t\t- ##### D5\n" +
        "\t\t\t\t\t- ###### D6\n";

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

    private string FilePath => Path.Combine(_vault.Root, BookPath);

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

    private static Task ClickAsync(IRenderedComponent<EditorPane> cut, string selector) => cut.InvokeAsync(() => cut.Find(selector).Click());

    /// <summary>Undoes the last change and checks the file is as it was.</summary>
    private void UndoAndCheck(byte[] before)
    {
        Assert.True(State.Session!.TryUndo(out var error), error?.ToString());
        Assert.Equal(before, File.ReadAllBytes(FilePath));
    }

    /// <summary>The text after the text block "Zweiter Text" became the last sub-point of "Erster Text".</summary>
    private string TextBlockIndented =>
        BookText.Replace(
            "\t- Erster Text\n\t\t- Punkt A\n\t\t- Punkt B\n\t\t\t- Punkt B1\n\t- Zweiter Text\n",
            $"\t- Erster Text\n\t  updated:: {Stamp}\n\t\t- Punkt A\n\t\t- Punkt B\n\t\t\t- Punkt B1\n\t\t- Zweiter Text\n");

    /// <summary>The text after "Punkt A" became a text block of its own, taking its later siblings along.</summary>
    private string DetailOutdented =>
        BookText.Replace(
            "\t- Erster Text\n\t\t- Punkt A\n\t\t- Punkt B\n\t\t\t- Punkt B1\n",
            $"\t- Erster Text\n\t  updated:: {Stamp}\n\t- Punkt A\n\t  created:: {Stamp}\n\t  updated:: {Stamp}\n\t\t- Punkt B\n\t\t\t- Punkt B1\n");

    [Fact]
    public async Task Buttons_HeadingTextDetail_CallTheRightCommand_FileChecked()
    {
        var cut = await RenderAsync();
        var before = File.ReadAllBytes(FilePath);

        // A text block goes into the text block before it, with its own details; the cursor stays in it.
        await MoveToAsync(cut, "Zweiter Text");
        await ClickAsync(cut, ".ne-sec-indent-any");
        Assert.Equal(TextBlockIndented, _vault.Read(BookPath));
        Assert.Equal(Text("UndoStructure"), State.Session!.Undo.NextDescription);
        Assert.Equal(ElementKind.Detail, State.Cursor!.Kind);
        Assert.Equal(KeyOf("Zweiter Text"), State.Cursor.Key);
        Assert.Empty(cut.FindAll(".ne-editor-error"));
        UndoAndCheck(before);

        // A detail of level 1 becomes a text block of its own after its text block.
        await MoveToAsync(cut, "Punkt A");
        await ClickAsync(cut, ".ne-sec-outdent-any");
        Assert.Equal(DetailOutdented, _vault.Read(BookPath));
        Assert.Equal(ElementKind.TextBlock, State.Cursor!.Kind);
        UndoAndCheck(before);

        // A heading one level higher takes along what followed it in its section (here nothing): it goes after "# Eins".
        await MoveToAsync(cut, "## Eins-A");
        await ClickAsync(cut, ".ne-sec-outdent-any");
        Assert.Equal(
            BookText.Replace("\t- ## Eins-A\n\t\t- Text A\n", "").Replace("- # Zwei\n", "- # Eins-A\n\t- Text A\n- # Zwei\n"),
            _vault.Read(BookPath));
        Assert.Equal(KeyOf("# Eins-A"), State.Cursor!.Key);
        UndoAndCheck(before);

        // A heading one level deeper goes under the previous heading of its level, with its sub-headings.
        await MoveToAsync(cut, "# Zwei");
        await ClickAsync(cut, ".ne-sec-indent-any");
        Assert.Equal(BookText.Replace("- # Zwei\n\t- ## Zwei-A\n", "\t- ## Zwei\n\t\t- ### Zwei-A\n"), _vault.Read(BookPath));
        UndoAndCheck(before);

        // None of these went to the editor.
        Assert.Empty(Editor.DepthShifts);
    }

    [Fact]
    public async Task DeeperDetail_UsesEditorShiftDepth()
    {
        var cut = await RenderAsync();
        var before = File.ReadAllBytes(FilePath);
        var undo = State.Session!.Undo.NextDescription;

        // A detail with one before it goes deeper in the editor, which saves it like typed text.
        await MoveToAsync(cut, "Punkt B");
        await ClickAsync(cut, ".ne-sec-indent-any");
        Assert.Equal([1], Editor.DepthShifts);

        // A detail of level 2 goes one step higher in the editor.
        await MoveToAsync(cut, "Punkt B1");
        await ClickAsync(cut, ".ne-sec-outdent-any");
        Assert.Equal([1, -1], Editor.DepthShifts);

        // The file and the undo stack are the editor's business here.
        Assert.Equal(before, File.ReadAllBytes(FilePath));
        Assert.Equal(undo, State.Session.Undo.NextDescription);
    }

    [Fact]
    public async Task Buttons_Blocked_TooltipNamesReason()
    {
        var cut = await RenderAsync();
        (string Cursor, string? Outdent, string? Indent)[] cases =
        [
            // A text block as the first element of its section, and one cannot go out of its section.
            ("Erster Text", "OutdentBlockedTextBlock", "IndentBlockedNoPreviousText"),
            ("Text A", "OutdentBlockedTextBlock", "IndentBlockedNoPreviousText"),
            ("Zweiter Text", "OutdentBlockedTextBlock", null),

            // A detail without one before it on its level.
            ("Punkt A", null, "IndentBlockedNoPreviousDetail"),
            ("Punkt B1", null, "IndentBlockedNoPreviousDetail"),
            ("Punkt B", null, null),

            // Headings: no previous heading of the level, H1 at the top, an H6 sub-heading at the bottom of the subtree.
            ("# Eins", "OutdentBlockedH1", "IndentBlockedNoPreviousHeading"),
            ("# Zwei", "OutdentBlockedH1", null),
            ("# Drei", "OutdentBlockedH1", "LevelBlockedBelowH6"),
            // The H6 itself and the H1 itself get their own reasons; the sub-heading keys stay for the other cases.
            ("###### D6", null, "IndentBlockedH6"),
            ("## Eins-A", null, "IndentBlockedNoPreviousHeading"),
        ];

        foreach (var (cursor, outdent, indent) in cases)
        {
            await MoveToAsync(cut, cursor);
            cut.WaitForAssertion(() =>
            {
                var outButton = cut.Find(".ne-sec-outdent-any");
                var inButton = cut.Find(".ne-sec-indent-any");
                Assert.True(outdent is not null == outButton.HasAttribute("disabled"), cursor);
                Assert.Equal(Text(outdent ?? "SectionOutdentTitle"), outButton.GetAttribute("title"));
                Assert.True(indent is not null == inButton.HasAttribute("disabled"), cursor);
                Assert.Equal(Text(indent ?? "SectionIndentTitle"), inButton.GetAttribute("title"));
            });
            Assert.Equal(Text("SectionOutdent"), cut.Find(".ne-sec-outdent-any").TextContent.Trim());
            Assert.Equal(Text("SectionIndent"), cut.Find(".ne-sec-indent-any").TextContent.Trim());
        }

        // A blocked command does nothing when it comes anyway (Tab in the first text block).
        var before = File.ReadAllBytes(FilePath);
        await MoveToAsync(cut, "Erster Text");
        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionCommand(SectionCommand.IndentText));
        Assert.Equal(before, File.ReadAllBytes(FilePath));
        Assert.Empty(Editor.DepthShifts);
    }

    [Fact]
    public async Task Buttons_DisabledOnReadOnlyBook()
    {
        // An unclosed code fence makes the book read-only.
        File.WriteAllText(FilePath, BookText + "- ```\n  offen\n");
        var cut = await RenderAsync();
        Assert.True(Book.Page.IsReadOnly);

        await MoveToAsync(cut, "Zweiter Text");

        cut.WaitForAssertion(() => Assert.True(cut.Find(".ne-sec-indent-any").HasAttribute("disabled")));
        Assert.True(cut.Find(".ne-sec-outdent-any").HasAttribute("disabled"));

        // Shift+Tab on a detail of level 1 is refused with the read-only message, the file stays.
        await MoveToAsync(cut, "Punkt A");
        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionCommand(SectionCommand.OutdentDetail));
        Assert.Equal(BookText + "- ```\n  offen\n", _vault.Read(BookPath));
        Assert.Empty(Editor.DepthShifts);
    }

    [Fact]
    public async Task Tab_FirstParagraph_IndentsTextBlock()
    {
        var cut = await RenderAsync();
        var before = File.ReadAllBytes(FilePath);
        await MoveToAsync(cut, "Zweiter Text");

        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionCommand(SectionCommand.IndentText));

        Assert.Equal(TextBlockIndented, _vault.Read(BookPath));
        Assert.Equal(KeyOf("Zweiter Text"), Editor.Reveals[^1]);
        Assert.Equal(Text("UndoStructure"), State.Session!.Undo.NextDescription);
        UndoAndCheck(before);
    }

    [Fact]
    public async Task ShiftTab_DetailLevel1_OutdentsIntoOwnTextBlock()
    {
        var cut = await RenderAsync();
        var before = File.ReadAllBytes(FilePath);
        await MoveToAsync(cut, "Punkt A");

        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionCommand(SectionCommand.OutdentDetail));

        Assert.Equal(DetailOutdented, _vault.Read(BookPath));
        Assert.Equal(KeyOf("Punkt A"), Editor.Reveals[^1]);
        Assert.Equal(KeyOf("Punkt A"), Book.FindTextBlock(KeyOf("Punkt A"))!.Key);
        Assert.Empty(Editor.DepthShifts);
        UndoAndCheck(before);
    }

    [Fact]
    public async Task CurrentLevelButton_OnMisplacedHeading_Places()
    {
        // "# Zwei" stands under "# Eins" in the file: the book warns about it.
        const string misplaced =
            "title:: Alpha\ntype:: book\n\n" +
            "- # Eins\n" +
            "\t- Text\n" +
            "\t- # Zwei\n" +
            "\t\t- Text Zwei\n";
        File.WriteAllText(FilePath, misplaced);
        var cut = await RenderAsync();
        var before = File.ReadAllBytes(FilePath);
        Assert.NotEmpty(Book.Warnings);

        await MoveToAsync(cut, "# Zwei");
        var current = cut.WaitForElement(".ne-sec-level[aria-pressed=\"true\"]");
        Assert.Equal("H1", current.TextContent.Trim());
        Assert.False(current.HasAttribute("disabled"));
        Assert.Equal(Text("SectionLevelPlace"), current.GetAttribute("title"));
        Assert.Contains("ne-sec-level-place", current.ClassList);

        await cut.InvokeAsync(() => cut.Find(".ne-sec-level[aria-pressed=\"true\"]").Click());

        Assert.Equal(
            "title:: Alpha\ntype:: book\n\n- # Eins\n\t- Text\n- # Zwei\n\t- Text Zwei\n",
            _vault.Read(BookPath));
        Assert.Empty(Book.Warnings);
        Assert.Equal(Text("UndoLevel"), State.Session!.Undo.NextDescription);
        Assert.Equal(KeyOf("# Zwei"), State.Cursor!.Key);

        // Placed, the current level is just the current level again: a click does nothing.
        cut.WaitForAssertion(() => Assert.Equal(
            Text("SectionLevelCurrent"), cut.Find(".ne-sec-level[aria-pressed=\"true\"]").GetAttribute("title")));
        Assert.DoesNotContain("ne-sec-level-place", cut.Find(".ne-sec-level[aria-pressed=\"true\"]").ClassList);
        var placed = File.ReadAllBytes(FilePath);
        await cut.InvokeAsync(() => cut.Find(".ne-sec-level[aria-pressed=\"true\"]").Click());
        Assert.Equal(placed, File.ReadAllBytes(FilePath));
        Assert.Equal(Text("UndoLevel"), State.Session.Undo.NextDescription);

        UndoAndCheck(before);
    }

    // Final review, finding 6. A sub-point typed but not yet saved (the debounce has not run): the command first flushes
    // the editor, reads the cursor's block from the book as saved, then acts. The cursor keeps the block's key through the
    // save, so the command acts on the element the user is in, with the typed text in the file.

    [Fact]
    public async Task ShiftTab_DetailWithPendingText_FlushesThenOutdentsThatDetail()
    {
        var cut = await RenderAsync();
        await MoveToAsync(cut, "Punkt A");
        await cut.InvokeAsync(() => Editor.Callbacks!.OnDocumentChanged(Editor.Json.Replace("Punkt A", "Punkt A, neu")));
        Assert.Equal(BookText, _vault.Read(BookPath));

        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionCommand(SectionCommand.OutdentDetail));

        // The typed text is saved and the detail it was typed in became a text block of its own, taking its later siblings.
        Assert.Equal(DetailOutdented.Replace("Punkt A", "Punkt A, neu"), _vault.Read(BookPath));
        Assert.Empty(Editor.DepthShifts);
        Assert.Empty(cut.FindAll(".ne-editor-error"));
    }

    [Fact]
    public async Task Tab_DetailWithPendingText_FlushesThenShiftsThatDetailInTheEditor()
    {
        var cut = await RenderAsync();
        await MoveToAsync(cut, "Punkt B");
        await cut.InvokeAsync(() => Editor.Callbacks!.OnDocumentChanged(System.Text.RegularExpressions.Regex.Replace(Editor.Json, "Punkt B(?!1)", "Punkt B, neu")));

        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionCommand(SectionCommand.IndentText));

        // Deeper is the editor's business (it saves the result like typed text); the pending text was saved before.
        // The save stamps the edited block (updated::), so the text is looked up, not compared as a whole.
        Assert.Contains("\t\t- Punkt B, neu\n", _vault.Read(BookPath));
        Assert.Equal([1], Editor.DepthShifts);
    }

    [Fact]
    public async Task Tab_TextBlockWithPendingText_FlushesThenIndentsThatTextBlock()
    {
        var cut = await RenderAsync();
        await MoveToAsync(cut, "Zweiter Text");
        await cut.InvokeAsync(() => Editor.Callbacks!.OnDocumentChanged(Editor.Json.Replace("Zweiter Text", "Zweiter Text, neu")));

        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionCommand(SectionCommand.IndentText));

        var file = _vault.Read(BookPath);
        Assert.Contains("\t\t- Zweiter Text, neu\n", file);
        Assert.DoesNotContain("\n\t- Zweiter Text", file);
        Assert.Empty(Editor.DepthShifts);
    }

    [Fact]
    public async Task TabAndShiftTab_CursorOnBlockTheBookDoesNotKnow_HarmlessNoOp()
    {
        // A sub-point that is typed and whose cursor was reported, but which the saved book does not hold (the save did not
        // bring it in): both commands change nothing and do not move the editor either.
        var cut = await RenderAsync();
        var before = File.ReadAllBytes(FilePath);
        await cut.InvokeAsync(() => Editor.Callbacks!.OnCursorChanged(new CursorInfo(ElementKind.Detail, Guid.NewGuid(), KeyOf("Erster Text"), 0)));

        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionCommand(SectionCommand.IndentText));
        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionCommand(SectionCommand.OutdentDetail));

        Assert.Equal(before, File.ReadAllBytes(FilePath));
        Assert.Empty(Editor.DepthShifts);
        Assert.Null(State.Session!.Undo.NextDescription);
        Assert.Empty(cut.FindAll(".ne-editor-error"));
    }
}
