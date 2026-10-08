using System.Text;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Model;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Editor;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>The structure commands of the manuscript (spec 3) run on the book file, with one undo entry each.</summary>
public class ManuscriptCommandTests : UiTestContext
{
    private const string BookPath = "pages/Buch - Alpha.md";

    private const string NotePath = "pages/Notiz.md";

    private const string NoteId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1";

    private const string DritterId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb1";

    private const string Undo = "Gliederung ändern";

    private const string BookText =
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
        $"\t  source:: (({NoteId}))\n";

    private const string NoteText =
        "- Eine Notiz\n" +
        $"  id:: {NoteId}\n" +
        $"  used-in:: [[Buch - Alpha]] (({DritterId}))\n";

    private readonly TestVault _vault = TestVault.Create((BookPath, BookText), (NotePath, NoteText));

    protected override void Dispose(bool disposing)
    {
        // The session is closed first, then the vault folder is deleted.
        base.Dispose(disposing);
        if (disposing)
        {
            _vault.Dispose();
        }
    }

    private string FullPath => Path.Combine(_vault.Root, BookPath);

    private Book Book => State.CurrentBook!;

    private string Text(string key) => Services.GetRequiredService<IStringLocalizer<Strings>>()[key].Value;

    private Task<AdoptMessage?> RunAsync(SectionCommand command) =>
        ManuscriptCommands.RunAsync(State, command, Undo, NullLogger.Instance);

    private Guid KeyOf(string content) => Book.Page.AllBlocks().Single(b => b.Content == content).Key;

    /// <summary>Puts the cursor (as the editor reports it) at the start of the element whose block has <paramref name="content"/>.</summary>
    private void CursorAt(string content) => State.Cursor = CursorOf(content);

    private CursorInfo CursorOf(string content)
    {
        var element = BookElements.Find(Book, KeyOf(content))!;
        return new CursorInfo(element.Kind, element.Key, BookElements.TextBlockOf(Book, element)?.Key, 0);
    }

    /// <summary>Asserts the book file and, with <see cref="LineDiff"/>, that exactly the given lines differ from <see cref="BookText"/>.</summary>
    private void AssertBook(string expected, string[] removed, string[] added)
    {
        var after = _vault.Read(BookPath);
        Assert.Equal(expected, after);
        var (removedIndices, addedIndices) = LineDiff.Changed(Encoding.UTF8.GetBytes(BookText), Encoding.UTF8.GetBytes(after));
        Assert.Equal(removed, removedIndices.Select(i => BookText.Split('\n')[i]));
        Assert.Equal(added, addedIndices.Select(i => after.Split('\n')[i]));
    }

    /// <summary>The reveal target is the cursor element and its section the current section.</summary>
    private void AssertRevealed(Guid key, ElementKind kind)
    {
        Assert.Equal(key, State.PendingReveal);
        Assert.Equal((kind, key), (State.Cursor!.Kind, State.Cursor.Key));
        Assert.Equal(BookElements.SectionOf(Book, BookElements.Find(Book, key)!), State.CurrentSectionKey);
    }

    [Fact]
    public async Task InsertAfter_Heading_SavesAndRevealsNewHeading()
    {
        var session = await OpenSessionAsync(_vault);
        CursorAt("## Eins-A");

        var message = await RunAsync(SectionCommand.InsertAfter);

        Assert.Null(message);
        AssertBook(BookText.Replace("\t\t- Unter Text\n", "\t\t- Unter Text\n\t- ## \n"), [], ["\t- ## "]);
        var added = Book.Page.AllBlocks().Single(b => b.Content == "## ").Key;
        Assert.Equal(2, Book.FindNode(added)!.Level);
        AssertRevealed(added, ElementKind.Heading);
        Assert.Equal(Undo, session.Undo.NextDescription);
    }

    [Fact]
    public async Task InsertChild_TextBlock_RevealsNewDetail()
    {
        await OpenSessionAsync(_vault);
        CursorAt("Zweiter Text");

        var message = await RunAsync(SectionCommand.InsertChild);

        Assert.Null(message);
        AssertBook(BookText.Replace("\t- Zweiter Text\n", "\t- Zweiter Text\n\t\t-\n"), [], ["\t\t-"]);
        var added = KeyOf("");
        AssertRevealed(added, ElementKind.Detail);
        Assert.Equal(KeyOf("Zweiter Text"), State.Cursor!.TextBlockKey);
    }

    [Fact]
    public async Task Indent_Outdent_Heading()
    {
        await OpenSessionAsync(_vault);
        var zwei = KeyOf("# Zwei");
        CursorAt("# Zwei");

        Assert.Null(await RunAsync(SectionCommand.Indent));

        var indented = BookText.Replace(
            "- # Zwei\n\t- Dritter Text\n" + $"\t  id:: {DritterId}\n" + $"\t  source:: (({NoteId}))\n",
            "\t- ## Zwei\n\t\t- Dritter Text\n" + $"\t\t  id:: {DritterId}\n" + $"\t\t  source:: (({NoteId}))\n");
        Assert.Equal(indented, _vault.Read(BookPath));
        Assert.Equal(KeyOf("# Eins"), Book.FindNode(zwei)!.Parent!.Key);
        AssertRevealed(zwei, ElementKind.Heading);

        // The cursor stays on the moved heading: outdenting it brings the file back.
        Assert.Null(await RunAsync(SectionCommand.Outdent));

        Assert.Equal(BookText, _vault.Read(BookPath));
        Assert.Equal(Book.Root.Key, Book.FindNode(zwei)!.Parent!.Key);
        AssertRevealed(zwei, ElementKind.Heading);
    }

    [Fact]
    public async Task RemoveHeading_RevealsFirstMovedItem()
    {
        await OpenSessionAsync(_vault);
        CursorAt("## Eins-A");

        Assert.Null(await RunAsync(SectionCommand.RemoveHeading));

        AssertBook(
            BookText.Replace("\t- ## Eins-A\n\t\t- Unter Text\n", "\t- Unter Text\n"),
            ["\t- ## Eins-A", "\t\t- Unter Text"],
            ["\t- Unter Text"]);
        AssertRevealed(KeyOf("Unter Text"), ElementKind.TextBlock);
        Assert.Equal(KeyOf("# Eins"), State.CurrentSectionKey);
    }

    [Fact]
    public async Task Delete_Detail_Heading_TextBlock()
    {
        var session = await OpenSessionAsync(_vault);

        CursorAt("Ein Detail");
        Assert.Null(await RunAsync(SectionCommand.Delete));
        AssertBook(BookText.Replace("\t\t- Ein Detail\n", ""), ["\t\t- Ein Detail"], []);
        Assert.Equal(Undo, session.Undo.NextDescription);

        CursorAt("Zweiter Text");
        Assert.Null(await RunAsync(SectionCommand.Delete));
        Assert.Equal(BookText.Replace("\t\t- Ein Detail\n", "").Replace("\t- Zweiter Text\n", ""), _vault.Read(BookPath));
        Assert.Equal("Löschen", session.Undo.NextDescription);

        // The heading goes with its whole section, and the usages of its text blocks leave the notes.
        CursorAt("# Zwei");
        Assert.Null(await RunAsync(SectionCommand.Delete));
        Assert.Equal(
            "title:: Alpha\ntype:: book\n\n- Vorspann Text\n- # Eins\n\t- Erster Text\n\t- ## Eins-A\n\t\t- Unter Text\n",
            _vault.Read(BookPath));
        Assert.DoesNotContain(DritterId, _vault.Read(NotePath));
        Assert.Equal("Löschen", session.Undo.NextDescription);
        Assert.DoesNotContain(Book.Page.AllBlocks(), b => b.Content == "# Zwei");
    }

    [Fact]
    public async Task Command_UnsavedEditorText_Refused_NothingChanged()
    {
        var session = await OpenSessionAsync(_vault);
        CursorAt("# Eins");
        State.FlushEditor = () => Task.CompletedTask;
        State.HasUnsavedEditorText = () => true;

        var message = await RunAsync(SectionCommand.InsertAfter);

        Assert.Equal(new AdoptMessage("SectionCommandUnsaved", true), message);
        Assert.Equal(BookText, _vault.Read(BookPath));
        Assert.DoesNotContain(Book.Page.AllBlocks(), b => b.Content == "# ");
        Assert.False(session.Undo.CanUndo);

        // A flush that throws is refused the same way.
        State.HasUnsavedEditorText = null;
        State.FlushEditor = () => throw new IOException("Kein Speichern.");

        Assert.Equal(new AdoptMessage("SectionCommandUnsaved", true), await RunAsync(SectionCommand.InsertAfter));
        Assert.Equal(BookText, _vault.Read(BookPath));
        Assert.False(session.Undo.CanUndo);
    }

    [Fact]
    public async Task Command_OpenConflict_Refused()
    {
        var session = await OpenSessionAsync(_vault);
        CursorAt("# Eins");
        Assert.True(ManuscriptCommands.CanRun(State, SectionCommand.InsertAfter));

        // A local change of the heading collides with an external change of the same line.
        OutlineEditor.Rename(Book, KeyOf("# Eins"), "Eins lokal");
        var external = BookText.Replace("- # Eins\n", "- # Eins extern\n");
        File.WriteAllText(FullPath, external);
        session.HandleExternalChange(FullPath);
        Assert.True(session.HasOpenConflict(FullPath));

        Assert.False(ManuscriptCommands.CanRun(State, SectionCommand.InsertAfter));
        var message = await RunAsync(SectionCommand.InsertAfter);

        Assert.Equal(new AdoptMessage("EditorConflict", true), message);
        Assert.Equal(external, _vault.Read(BookPath));
        Assert.False(session.Undo.CanUndo);
    }

    [Fact]
    public async Task Command_SaveFails_FileUnchanged_PageFromDisk()
    {
        var session = await OpenSessionAsync(_vault);
        CursorAt("# Eins");

        // A read-only file cannot be replaced: the write fails after its retries.
        File.SetAttributes(FullPath, FileAttributes.ReadOnly);
        AdoptMessage? message;
        try
        {
            message = await RunAsync(SectionCommand.InsertAfter);
        }
        finally
        {
            File.SetAttributes(FullPath, FileAttributes.Normal);
        }

        Assert.Equal(new AdoptMessage("SectionCommandFailed", true), message);
        Assert.Equal(BookText, _vault.Read(BookPath));
        Assert.DoesNotContain(session.Vault.FindBook("Buch - Alpha")!.Page.AllBlocks(), b => b.Content == "# ");
        Assert.False(session.Undo.CanUndo);
    }

    [Fact]
    public async Task Command_NotPossible_NothingChanged()
    {
        var session = await OpenSessionAsync(_vault);

        // No previous heading of the same level, a level-1 heading, a text block: nothing to indent or outdent.
        CursorAt("# Eins");
        Assert.False(ManuscriptCommands.CanRun(State, SectionCommand.Indent));
        Assert.False(ManuscriptCommands.CanRun(State, SectionCommand.Outdent));
        Assert.Null(await RunAsync(SectionCommand.Indent));
        Assert.Null(await RunAsync(SectionCommand.Outdent));
        CursorAt("Erster Text");
        Assert.False(ManuscriptCommands.CanRun(State, SectionCommand.RemoveHeading));
        Assert.Null(await RunAsync(SectionCommand.Indent));
        Assert.Null(await RunAsync(SectionCommand.RemoveHeading));
        State.Cursor = null;
        Assert.False(ManuscriptCommands.CanRun(State, SectionCommand.InsertAfter));
        Assert.Null(await RunAsync(SectionCommand.InsertAfter));

        Assert.Equal(BookText, _vault.Read(BookPath));
        Assert.False(session.Undo.CanUndo);
    }

    [Fact]
    public async Task Undo_RestoresFile_ForEachStructureCommand()
    {
        var session = await OpenSessionAsync(_vault);
        (string Cursor, SectionCommand Command)[] cases =
        [
            ("# Eins", SectionCommand.InsertAfter),
            ("Erster Text", SectionCommand.InsertChild),
            ("Ein Detail", SectionCommand.InsertAfter),
            ("# Zwei", SectionCommand.Indent),
            ("## Eins-A", SectionCommand.Outdent),
            ("## Eins-A", SectionCommand.RemoveHeading),
            ("Ein Detail", SectionCommand.Delete),
        ];

        foreach (var (cursor, command) in cases)
        {
            CursorAt(cursor);
            Assert.Null(await RunAsync(command));
            Assert.NotEqual(BookText, _vault.Read(BookPath));
            Assert.Equal(Undo, session.Undo.NextDescription);

            Assert.True(session.TryUndo(out var error), $"{command} at {cursor}: {error}");

            Assert.Equal(BookText, _vault.Read(BookPath));
            Assert.False(session.Undo.CanUndo);
            State.RefreshBook();
        }
    }

    [Fact]
    public async Task Undo_AfterTypingIntoNewSection_Refused()
    {
        var session = await OpenSessionAsync(_vault);
        CursorAt("# Eins");
        Assert.Null(await RunAsync(SectionCommand.InsertAfter));
        var added = State.Cursor!.Key;

        // A title is typed into the new heading and saved.
        var book = session.Vault.FindBook("Buch - Alpha")!;
        OutlineEditor.Rename(book, added, "Neu");
        session.Writer.Save(book.Page);
        var typed = _vault.Read(BookPath);
        Assert.Contains("- # Neu\n", typed);

        Assert.False(session.TryUndo(out _));

        Assert.Equal(typed, _vault.Read(BookPath));
    }

    [Fact]
    public async Task Shortcut_RaisesCommand_ViaFakeEditor()
    {
        var session = await OpenSessionAsync(_vault);
        var cut = Render<EditorPane>();
        cut.WaitForAssertion(() => Assert.NotEmpty(Editor.Documents));
        var documents = Editor.Documents.Count;
        await cut.InvokeAsync(() => Editor.Callbacks!.OnCursorChanged(CursorOf("# Eins")));

        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionCommand(SectionCommand.InsertAfter));

        Assert.Equal(BookText.Replace("- # Zwei\n", "- # \n- # Zwei\n"), _vault.Read(BookPath));
        var added = Book.Page.AllBlocks().Single(b => b.Content == "# ").Key;
        cut.WaitForAssertion(() => Assert.Equal(added, Assert.Single(Editor.Reveals)));
        Assert.True(Editor.Documents.Count > documents);
        Assert.Equal(Text("UndoStructure"), session.Undo.NextDescription);
        Assert.Empty(cut.FindAll(".ne-editor-error"));
    }

    [Fact]
    public async Task Shortcut_Refused_ShowsMessageInEditor()
    {
        File.WriteAllText(FullPath, BookText + "- ```\n  offen\n");
        await OpenSessionAsync(_vault);
        Assert.True(Book.Page.IsReadOnly);
        var cut = Render<EditorPane>();
        cut.WaitForAssertion(() => Assert.NotEmpty(Editor.Documents));
        await cut.InvokeAsync(() => Editor.Callbacks!.OnCursorChanged(CursorOf("# Eins")));

        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionCommand(SectionCommand.InsertAfter));

        cut.WaitForAssertion(() => Assert.Equal(Text("EditorReadOnly"), cut.Find(".ne-editor-error").TextContent));
    }

    [Fact]
    public async Task BookChange_UnmovedCursor_CurrentSectionFollows()
    {
        var session = await OpenSessionAsync(_vault);
        var cut = Render<EditorPane>();
        cut.WaitForAssertion(() => Assert.NotEmpty(Editor.Documents));
        var einsA = KeyOf("## Eins-A");
        await cut.InvokeAsync(() => Editor.Callbacks!.OnCursorChanged(CursorOf("## Eins-A")));
        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionCommand(SectionCommand.RemoveHeading));
        Assert.Equal(KeyOf("# Eins"), State.CurrentSectionKey);
        var cursor = State.Cursor;

        // Undo puts the heading back around the text block under the unmoved cursor: its section is current again.
        await cut.InvokeAsync(() =>
        {
            Assert.True(session.TryUndo(out _));
            State.RefreshBook();
            State.Notify();
        });

        Assert.Equal(BookText, _vault.Read(BookPath));
        cut.WaitForAssertion(() => Assert.Equal(einsA, State.CurrentSectionKey));
        Assert.Equal(cursor, State.Cursor);
    }
}
