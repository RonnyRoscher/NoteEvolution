using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Editor;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>
/// Moving the head of the marked range by its grip (Markierung verschieben, spec 5–7): a section, a text block or a
/// detail dropped in an editor or on an outline (<see cref="ManuscriptCommands.MoveRangeAsync"/>), and the shared drag
/// state (<see cref="AppState.DraggedRange"/>, <see cref="AppState.MovableRangeHead"/>).
/// </summary>
public class MoveRangeTests : UiTestContext
{
    private const string BookPath = "pages/Buch - Alpha.md";

    private const string Head = "title:: Alpha\ntype:: book\n\n";

    private const string Vorspann = "- Vorspann Text\n";

    private const string EinsHead = "- # Eins\n";

    private const string Erster = "\t- Erster Text\n";

    private const string ErsterDetails = "\t\t- Detail A\n\t\t\t- Tiefer A\n\t\t- Detail B\n";

    private const string Zweiter = "\t- Zweiter Text\n";

    private const string EinsA = "\t- ## Eins-A\n\t\t- Unter Text\n";

    private const string Zwei = "- # Zwei\n\t- Dritter Text\n";

    private const string BookText = Head + Vorspann + EinsHead + Erster + ErsterDetails + Zweiter + EinsA + Zwei;

    private const string Undo = "Bereich verschieben";

    private readonly List<TestVault> _vaults = [];

    protected override void Dispose(bool disposing)
    {
        // The session is closed first, then the vault folders are deleted.
        base.Dispose(disposing);
        if (disposing)
        {
            _vaults.ForEach(v => v.Dispose());
        }
    }

    private async Task<TestVault> OpenAsync(string bookText = BookText)
    {
        var vault = TestVault.Create((BookPath, bookText));
        _vaults.Add(vault);

        // A test may open several books in turn: the previous session lets go of its vault first.
        State.Session?.Dispose();
        await OpenSessionAsync(vault);
        return vault;
    }

    private Book Book => State.CurrentBook!;

    private string Text(string key) => Services.GetRequiredService<IStringLocalizer<Strings>>()[key].Value;

    private Guid KeyOf(string content) => Book.Page.AllBlocks().Single(b => b.Content == content).Key;

    private BookElement ElementOf(string content) => BookElements.Find(Book, KeyOf(content))!;

    private void PutCursorAt(string content, int rangeLevel = 0)
    {
        var element = ElementOf(content);
        State.Cursor = new CursorInfo(element.Kind, element.Key, BookElements.TextBlockOf(Book, element)?.Key, 0);
        State.RangeLevel = rangeLevel;
    }

    private Task<AdoptMessage?> MoveAsync(string head, RangeDrop drop) =>
        ManuscriptCommands.MoveRangeAsync(State, ElementOf(head), drop, Undo, NullLogger.Instance);

    private RangeDrop InEditor(string? element) => new RangeDrop.InEditor(element is null ? null : KeyOf(element));

    private RangeDrop OnSection(string heading) => new RangeDrop.OnSection(KeyOf(heading));

    private RangeDrop BeforeSection(string heading) => new RangeDrop.BeforeSection(KeyOf(heading));

    /// <summary>Moves <paramref name="head"/> to <paramref name="drop"/> and expects the file <paramref name="expected"/> with one undo step.</summary>
    private async Task AssertMovedAsync(string head, Func<RangeDrop> drop, string expected)
    {
        var vault = await OpenAsync();

        Assert.Null(await MoveAsync(head, drop()));

        // The change history gives a text block whose sub-points changed its id when it is saved.
        AssertSameBesidesNewIds(expected, vault.Read(BookPath));
        Assert.Equal(Undo, State.Session!.Undo.NextDescription);
    }

    /// <summary>Moves <paramref name="head"/> to <paramref name="drop"/> and expects nothing to change, without a message.</summary>
    private async Task AssertNothingAsync(string head, Func<RangeDrop> drop)
    {
        var vault = await OpenAsync();

        Assert.Null(await MoveAsync(head, drop()));

        Assert.Equal(BookText, vault.Read(BookPath));
        Assert.False(State.Session!.Undo.CanUndo);
    }

    // --- A section (spec 5.1) ---

    [Fact]
    public Task MoveRange_Heading_InEditorOnATextBlock_AfterItsSection_SameLevel() =>
        AssertMovedAsync("## Eins-A", () => InEditor("Dritter Text"),
            Head + Vorspann + EinsHead + Erster + ErsterDetails + Zweiter + Zwei + "- # Eins-A\n\t- Unter Text\n");

    [Fact]
    public Task MoveRange_Heading_InEditorOnAHeading_AfterThatSection() =>
        AssertMovedAsync("## Eins-A", () => InEditor("# Zwei"),
            Head + Vorspann + EinsHead + Erster + ErsterDetails + Zweiter + Zwei + "- # Eins-A\n\t- Unter Text\n");

    [Fact]
    public Task MoveRange_Heading_InEditorOnADetail_AfterTheDetailsSection() =>
        AssertMovedAsync("## Eins-A", () => InEditor("Detail A"),
            Head + Vorspann + EinsHead + Erster + ErsterDetails + Zweiter + "- # Eins-A\n\t- Unter Text\n" + Zwei);

    [Fact]
    public Task MoveRange_Heading_InEditorAboveTheFirstElement_FirstTopLevelSection() =>
        AssertMovedAsync("## Eins-A", () => InEditor(null),
            Head + Vorspann + "- # Eins-A\n\t- Unter Text\n" + EinsHead + Erster + ErsterDetails + Zweiter + Zwei);

    [Fact]
    public Task MoveRange_Heading_InEditorInThePrologue_FirstTopLevelSection() =>
        AssertMovedAsync("## Eins-A", () => InEditor("Vorspann Text"),
            Head + Vorspann + "- # Eins-A\n\t- Unter Text\n" + EinsHead + Erster + ErsterDetails + Zweiter + Zwei);

    [Fact]
    public Task MoveRange_Heading_OnSection_LastSubSection() =>
        AssertMovedAsync("# Zwei", () => OnSection("# Eins"),
            Head + Vorspann + EinsHead + Erster + ErsterDetails + Zweiter + EinsA + "\t- ## Zwei\n\t\t- Dritter Text\n");

    [Fact]
    public Task MoveRange_Heading_BeforeSection_SiblingBeforeIt() =>
        AssertMovedAsync("# Zwei", () => BeforeSection("## Eins-A"),
            Head + Vorspann + EinsHead + Erster + ErsterDetails + Zweiter + "\t- ## Zwei\n\t\t- Dritter Text\n" + EinsA);

    [Fact]
    public Task MoveRange_Heading_AtEnd_LastTopLevelSection() =>
        AssertMovedAsync("## Eins-A", () => new RangeDrop.AtEnd(),
            Head + Vorspann + EinsHead + Erster + ErsterDetails + Zweiter + Zwei + "- # Eins-A\n\t- Unter Text\n");

    [Fact]
    public Task MoveRange_Heading_BeforeItself_Nothing() =>
        AssertNothingAsync("## Eins-A", () => BeforeSection("## Eins-A"));

    // --- A text block (spec 5.2) ---

    [Fact]
    public Task MoveRange_TextBlock_InEditorOnAHeading_FirstTextBlockOfItsSection() =>
        AssertMovedAsync("Zweiter Text", () => InEditor("# Zwei"),
            Head + Vorspann + EinsHead + Erster + ErsterDetails + EinsA + "- # Zwei\n" + Zweiter + "\t- Dritter Text\n");

    [Fact]
    public Task MoveRange_TextBlock_InEditorOnATextBlock_AfterIt() =>
        AssertMovedAsync("Zweiter Text", () => InEditor("Dritter Text"),
            Head + Vorspann + EinsHead + Erster + ErsterDetails + EinsA + Zwei + Zweiter);

    [Fact]
    public Task MoveRange_TextBlock_InEditorOnADetail_AfterItsWholeTextBlock() =>
        AssertMovedAsync("Unter Text", () => InEditor("Detail A"),
            Head + Vorspann + EinsHead + Erster + ErsterDetails + "\t- Unter Text\n" + Zweiter + "\t- ## Eins-A\n" + Zwei);

    [Fact]
    public Task MoveRange_TextBlock_InEditorAboveTheFirstElement_FirstTextBlockOfThePrologue() =>
        AssertMovedAsync("Dritter Text", () => InEditor(null),
            Head + "- Dritter Text\n" + Vorspann + EinsHead + Erster + ErsterDetails + Zweiter + EinsA + "- # Zwei\n");

    [Fact]
    public Task MoveRange_TextBlock_OnSection_EndOfItsText() =>
        AssertMovedAsync("Dritter Text", () => OnSection("# Eins"),
            Head + Vorspann + EinsHead + Erster + ErsterDetails + Zweiter + "\t- Dritter Text\n" + EinsA + "- # Zwei\n");

    [Fact]
    public Task MoveRange_TextBlock_BeforeSection_Nothing() =>
        AssertNothingAsync("Zweiter Text", () => BeforeSection("# Zwei"));

    [Fact]
    public Task MoveRange_TextBlock_AtEnd_Nothing() =>
        AssertNothingAsync("Zweiter Text", () => new RangeDrop.AtEnd());

    // --- A detail (spec 5.3) ---

    private string DetailABecameTextBlock =>
        "\t- Detail A\n" + $"\t  created:: {Stamp}\n" + $"\t  updated:: {Stamp}\n" + "\t\t- Tiefer A\n";

    private string ErsterUpdated => Erster + $"\t  updated:: {Stamp}\n";

    [Fact]
    public Task MoveRange_Detail_InEditorOnAHeading_FirstTextBlockOfItsSection() =>
        AssertMovedAsync("Detail A", () => InEditor("# Zwei"),
            Head + Vorspann + EinsHead + ErsterUpdated + "\t\t- Detail B\n" + Zweiter + EinsA
            + "- # Zwei\n" + DetailABecameTextBlock + "\t- Dritter Text\n");

    [Fact]
    public Task MoveRange_Detail_InEditorOnATextBlock_ItsFirstSubPoint() =>
        AssertMovedAsync("Detail B", () => InEditor("Dritter Text"),
            Head + Vorspann + EinsHead + Erster + "\t\t- Detail A\n\t\t\t- Tiefer A\n" + Zweiter + EinsA + Zwei + "\t\t- Detail B\n");

    [Fact]
    public Task MoveRange_Detail_InEditorOnADetail_AfterIt_SameDepth() =>
        AssertMovedAsync("Tiefer A", () => InEditor("Detail B"),
            Head + Vorspann + EinsHead + Erster + "\t\t- Detail A\n\t\t- Detail B\n\t\t- Tiefer A\n" + Zweiter + EinsA + Zwei);

    [Fact]
    public Task MoveRange_Detail_InEditorAboveTheFirstElement_FirstTextBlockOfThePrologue() =>
        AssertMovedAsync("Detail B", () => InEditor(null),
            Head + "- Detail B\n" + $"  created:: {Stamp}\n" + $"  updated:: {Stamp}\n" + Vorspann + EinsHead + ErsterUpdated
            + "\t\t- Detail A\n\t\t\t- Tiefer A\n" + Zweiter + EinsA + Zwei);

    [Fact]
    public Task MoveRange_Detail_OnSection_LastTextBlockOfIt() =>
        AssertMovedAsync("Detail B", () => OnSection("# Zwei"),
            Head + Vorspann + EinsHead + ErsterUpdated + "\t\t- Detail A\n\t\t\t- Tiefer A\n" + Zweiter + EinsA + Zwei
            + "\t- Detail B\n" + $"\t  created:: {Stamp}\n" + $"\t  updated:: {Stamp}\n");

    [Fact]
    public Task MoveRange_Detail_BeforeSection_Nothing() =>
        AssertNothingAsync("Detail A", () => BeforeSection("# Zwei"));

    [Fact]
    public Task MoveRange_Detail_AtEnd_Nothing() =>
        AssertNothingAsync("Detail A", () => new RangeDrop.AtEnd());

    // --- Undo, reveal, refusals (spec 6, 7) ---

    [Fact]
    public async Task MoveRange_Undo_RestoresTheFileByteForByte()
    {
        // A section with a tail: the level-2 heading after "# Zwei" at the top level is read as part of it.
        const string tailBook =
            Head + "- # Eins\n\t- Text eins\n- # Zwei\n  collapsed:: true\n- ## Zwei-Tail\n\t- Tail text\n- Lose Text\n- # Drei\n\t- Text drei\n";
        var vault = await OpenAsync(tailBook);
        Assert.Null(await MoveAsync("# Zwei", OnSection("# Drei")));
        Assert.NotEqual(tailBook, vault.Read(BookPath));
        Assert.Contains("\t- ## Zwei\n", vault.Read(BookPath));
        Assert.Equal(Undo, State.Session!.Undo.NextDescription);

        Assert.True(State.Session!.TryUndo(out var error), error?.ToString());

        Assert.Equal(tailBook, vault.Read(BookPath));
        Assert.Equal(Undo, Text("UndoMoveRange"));

        // A detail made a text block: its dates and the former text block's updated:: go again. The change history gave
        // the former text block an id:: when the move was saved (its sub-points changed); the save after the undo gives
        // it back on purpose (BlockHistoryTracker: a block that lost its id by an undo), as after "Ausrücken".
        var undone = await UndoneAsync("Detail A", () => InEditor("# Zwei"));
        AssertSameBesidesNewIds(BookText, undone);
        Assert.Equal(BookText.Length, WithoutNewIds(undone, BookText).Length);
        Assert.DoesNotContain("created::", undone);
        Assert.DoesNotContain("updated::", undone);

        // A text block: byte for byte.
        Assert.Equal(BookText, await UndoneAsync("Zweiter Text", () => InEditor("Dritter Text")));

        async Task<string> UndoneAsync(string head, Func<RangeDrop> drop)
        {
            var book = await OpenAsync();
            Assert.Null(await MoveAsync(head, drop()));
            Assert.NotEqual(BookText, book.Read(BookPath));

            Assert.True(State.Session!.TryUndo(out var undoError), undoError?.ToString());

            return book.Read(BookPath);
        }
    }

    [Fact]
    public async Task MoveRange_RevealsAndMarksTheMovedHead()
    {
        await OpenAsync();
        State.SetSplit(true);

        // The other half shows "# Zwei"; the drop is in the lower half, which the caller made active.
        State.Views[0].Cursor = new CursorInfo(ElementKind.Heading, KeyOf("# Zwei"), null, 0);
        await State.ActivateAsync(State.Views[1]);
        PutCursorAt("Tiefer A", rangeLevel: 1);
        var detail = KeyOf("Detail A");
        Assert.Equal(new BookElement(ElementKind.Detail, detail), State.CurrentRange!.Head);

        Assert.Null(await MoveAsync("Detail A", InEditor("# Zwei")));

        Assert.Equal(new BookElement(ElementKind.TextBlock, detail), State.CurrentElement);
        Assert.Equal(0, State.RangeLevel);
        Assert.Equal(detail, State.PendingReveal);
        Assert.Equal(KeyOf("# Zwei"), State.CurrentSectionKey);
        Assert.Equal(KeyOf("# Zwei"), State.Views[0].Cursor!.Key);
    }

    [Fact]
    public async Task MoveRange_Level6_ShowsTheMessage_FileUnchanged()
    {
        const string deepBook =
            Head + "- # A\n\t- ## B\n\t\t- ### C\n\t\t\t- #### D\n\t\t\t\t- ##### E\n- # X\n\t- ## Y\n\t\t- Text y\n";
        var vault = await OpenAsync(deepBook);

        var message = await MoveAsync("# X", OnSection("##### E"));

        Assert.Equal(new AdoptMessage("MoveRangeTooDeep", true), message);
        Assert.Equal("Der Abschnitt würde tiefer als Ebene 6.", Text("MoveRangeTooDeep"));
        Assert.Equal(deepBook, vault.Read(BookPath));
        Assert.False(State.Session!.Undo.CanUndo);

        // Dropped in the editor on E's text: the same level as E, which fits.
        Assert.Null(await MoveAsync("## Y", InEditor("##### E")));
        Assert.Contains("\t\t\t\t- ##### Y\n", vault.Read(BookPath));
    }

    [Fact]
    public async Task MoveRange_IntoItsOwnSection_Refused()
    {
        var vault = await OpenAsync();
        var invalid = new AdoptMessage("OutlineMoveInvalid", true);

        Assert.Equal(invalid, await MoveAsync("# Eins", OnSection("## Eins-A")));
        Assert.Equal(invalid, await MoveAsync("# Eins", OnSection("# Eins")));
        Assert.Equal(invalid, await MoveAsync("# Eins", InEditor("Unter Text")));
        Assert.Equal(invalid, await MoveAsync("# Eins", InEditor("# Eins")));
        Assert.Equal(invalid, await MoveAsync("# Eins", BeforeSection("## Eins-A")));

        // A detail onto one of its own deeper details.
        Assert.Equal(invalid, await MoveAsync("Detail A", InEditor("Tiefer A")));

        Assert.Equal(BookText, vault.Read(BookPath));
        Assert.False(State.Session!.Undo.CanUndo);
    }

    [Fact]
    public async Task MoveRange_OpenConflict_Refused()
    {
        var vault = await OpenAsync();
        var session = State.Session!;
        var path = Path.Combine(vault.Root, BookPath);

        // A local change of the book collides with an external change of the same line.
        var external = BookText.Replace("\t- Dritter Text\n", "\t- Dritter Text extern\n");
        session.Vault.FindPageByPath(path)!.AllBlocks().Single(b => b.Content == "Dritter Text").SetContent("Dritter Text lokal");
        File.WriteAllText(path, external);
        session.HandleExternalChange(path);
        Assert.True(session.HasOpenConflict(path));

        var message = await ManuscriptCommands.MoveRangeAsync(
            State, new BookElement(ElementKind.TextBlock, KeyOf("Zweiter Text")), new RangeDrop.InEditor(null), Undo, NullLogger.Instance);

        Assert.Equal(new AdoptMessage("EditorConflict", true), message);
        Assert.Equal(external, vault.Read(BookPath));
        Assert.False(session.Undo.CanUndo);
    }

    [Fact]
    public async Task MoveRange_UnsavedEditorTextIsSavedFirst()
    {
        var vault = await OpenAsync();
        var session = State.Session!;
        var log = new List<string>();
        State.FlushEditor = () =>
        {
            log.Add("flush");
            var book = session.Vault.FindBook(Book.LinkName)!;
            book.Page.AllBlocks().Single(b => b.Content == "Dritter Text").SetContent("Dritter Text neu");
            Assert.True(session.TrySave(book.Page, out _));
            return Task.CompletedTask;
        };

        Assert.Null(await MoveAsync("Zweiter Text", InEditor("Dritter Text")));

        Assert.Equal(["flush"], log);
        var moved = Head + Vorspann + EinsHead + Erster + ErsterDetails + EinsA + "- # Zwei\n\t- Dritter Text neu\n" + Zweiter;
        AssertSameBesidesNewIds(moved, vault.Read(BookPath));

        // Undo takes the move back, not the saved text.
        Assert.True(session.TryUndo(out var error), error?.ToString());
        AssertSameBesidesNewIds(BookText.Replace("\t- Dritter Text\n", "\t- Dritter Text neu\n"), vault.Read(BookPath));

        // Text the editor could not save refuses the move.
        State.FlushEditor = null;
        State.HasUnsavedEditorText = () => true;
        var before = vault.Read(BookPath);
        Assert.Equal(new AdoptMessage("SectionCommandUnsaved", true), await MoveAsync("Zweiter Text", InEditor(null)));
        Assert.Equal(before, vault.Read(BookPath));
    }

    [Fact]
    public async Task MovableRangeHead_NullInTheListedCases()
    {
        // Without a book.
        Assert.Null(State.MovableRangeHead);

        var vault = await OpenAsync(
            BookText + "- # Glossar\n  anhang:: glossar\n\t- Begriff\n");

        // A head of every kind can be moved.
        PutCursorAt("Tiefer A", rangeLevel: 1);
        Assert.Equal(ElementOf("Detail A"), State.MovableRangeHead);
        PutCursorAt("Detail A", rangeLevel: 1);
        Assert.Equal(ElementOf("Erster Text"), State.MovableRangeHead);
        PutCursorAt("Erster Text", rangeLevel: 1);
        Assert.Equal(ElementOf("# Eins"), State.MovableRangeHead);

        // Also at level −1 the head (with everything below it) is what moves.
        PutCursorAt("# Eins", rangeLevel: -1);
        Assert.Equal(ElementOf("# Eins"), State.MovableRangeHead);

        // Without a head: the whole book.
        PutCursorAt("Erster Text", rangeLevel: 2);
        Assert.Null(State.CurrentRange!.Head);
        Assert.Null(State.MovableRangeHead);

        // The prologue's pseudo section: the prologue's text block one level up is the whole book, and the prologue
        // itself is no element.
        PutCursorAt("Vorspann Text", rangeLevel: 1);
        Assert.Null(State.MovableRangeHead);
        State.CurrentSectionKey = Guid.Empty;
        State.Cursor = new CursorInfo(ElementKind.Heading, Book.Root.Key, null, 0);
        State.RangeLevel = 0;
        Assert.Null(State.MovableRangeHead);

        // In the appendix.
        var begriff = Book.Page.AllBlocks().Single(b => b.Content == "Begriff");
        State.Cursor = new CursorInfo(ElementKind.TextBlock, begriff.Key, begriff.Key, 0);
        Assert.Null(State.MovableRangeHead);
        var glossar = Book.Page.AllBlocks().Single(b => b.Content == "# Glossar");
        State.CurrentSectionKey = glossar.Key;
        State.Cursor = new CursorInfo(ElementKind.Heading, glossar.Key, null, 0);
        Assert.Null(State.MovableRangeHead);

        // With an open conflict.
        PutCursorAt("Zweiter Text");
        Assert.NotNull(State.MovableRangeHead);
        var session = State.Session!;
        var path = Path.Combine(vault.Root, BookPath);
        session.Vault.FindPageByPath(path)!.AllBlocks().Single(b => b.Content == "Dritter Text").SetContent("Dritter Text lokal");
        File.WriteAllText(path, vault.Read(BookPath).Replace("\t- Dritter Text\n", "\t- Dritter Text extern\n"));
        session.HandleExternalChange(path);
        Assert.True(session.HasOpenConflict(path));
        Assert.Null(State.MovableRangeHead);

        // A locked editor: the book is read-only.
        await OpenAsync(BookText + "- ```\n  offen\n");
        Assert.True(Book.Page.IsReadOnly);
        PutCursorAt("Zweiter Text");
        Assert.NotNull(State.CurrentRange!.Head);
        Assert.Null(State.MovableRangeHead);
    }

    [Fact]
    public async Task MoveRange_DraggedRange_NotifiesWhenItChanges()
    {
        await OpenAsync();
        var notified = 0;
        State.Changed += () => notified++;
        var head = ElementOf("# Eins");

        State.DraggedRange = head;
        Assert.Equal(head, State.DraggedRange);
        Assert.Equal(1, notified);

        State.DraggedRange = head;
        Assert.Equal(1, notified);

        State.DraggedRange = null;
        Assert.Null(State.DraggedRange);
        Assert.Equal(2, notified);
    }
}
