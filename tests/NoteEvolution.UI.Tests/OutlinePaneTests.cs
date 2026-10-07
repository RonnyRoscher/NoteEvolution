using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;

namespace NoteEvolution.UI.Tests;

public class OutlinePaneTests : UiTestContext
{
    private const string BookPath = "pages/Buch - Alpha.md";

    private const string Source = "11111111-1111-1111-1111-111111111111";

    private static TestVault Alpha() => TestVault.Create(
        (BookPath,
            "title:: Alpha\ntype:: book\n\n" +
            "- Ein Vorspann aus fünf Wörtern\n" +
            "- # Eins\n" +
            "\t- Erster Text\n" +
            "\t  source:: ((" + Source + "))\n" +
            "\t- ## Eins-A\n" +
            "\t\t- Noch drei Wörter\n" +
            "- # Zwei\n"));

    private static TestVault Plain() => TestVault.Create(
        (BookPath, "title:: Alpha\ntype:: book\n\n- # Eins\n- # Zwei\n\t- ## Zwei-A\n- # Drei\n"));

    private static IElement Item(IRenderedComponent<OutlinePane> cut, string title) =>
        cut.FindAll(".ne-outline-item").First(li => Title(li.QuerySelector(".ne-outline-row")!) == title);

    /// <summary>The title shown in the row; while the row is being renamed, the text of its input.</summary>
    private static string Title(IElement row) =>
        row.QuerySelector(".ne-outline-title")?.TextContent ?? row.QuerySelector(".ne-outline-rename")?.GetAttribute("value") ?? "";

    private static IElement Row(IRenderedComponent<OutlinePane> cut, string title) =>
        Item(cut, title).QuerySelector(".ne-outline-row")!;

    private static IElement Before(IRenderedComponent<OutlinePane> cut, string title) =>
        Item(cut, title).QuerySelector(".ne-drop-before")!;

    private static List<string> Titles(IRenderedComponent<OutlinePane> cut) =>
        [.. cut.FindAll(".ne-outline-row").Select(Title)];

    [Fact]
    public async Task Renders_TitlesWithCounts()
    {
        using var tv = Alpha();
        await OpenSessionAsync(tv);

        var cut = Render<OutlinePane>();

        Assert.Equal(["Vorspann", "Eins", "Eins-A", "Zwei"], Titles(cut));
        var eins = Row(cut, "Eins");
        Assert.Equal("5 W", eins.QuerySelector(".ne-outline-words")!.TextContent);
        Assert.Equal("1 Q", eins.QuerySelector(".ne-outline-sources")!.TextContent);
        Assert.Equal("3 W", Row(cut, "Eins-A").QuerySelector(".ne-outline-words")!.TextContent);
        Assert.Null(Row(cut, "Eins-A").QuerySelector(".ne-outline-sources"));
        Assert.Equal("5 W", Row(cut, "Vorspann").QuerySelector(".ne-outline-words")!.TextContent);
        Assert.Contains("Eins-A", Item(cut, "Eins").QuerySelector("ul")!.TextContent);
    }

    [Fact]
    public async Task NoVorspann_WithoutTextBlocksInTheRoot()
    {
        using var tv = Plain();
        await OpenSessionAsync(tv);

        var cut = Render<OutlinePane>();

        Assert.Equal(["Eins", "Zwei", "Zwei-A", "Drei"], Titles(cut));
    }

    [Fact]
    public async Task Click_SetsCurrentSection()
    {
        using var tv = Alpha();
        await OpenSessionAsync(tv);
        var cut = Render<OutlinePane>();
        var eins = State.CurrentBook!.Root.Children.First(c => c.Title == "Eins");

        Row(cut, "Eins").Click();

        Assert.Equal(eins.Key, State.CurrentSectionKey);
        Assert.Contains("selected", Row(cut, "Eins").ClassList);

        Row(cut, "Vorspann").Click();

        Assert.Equal(Guid.Empty, State.CurrentSectionKey);
    }

    [Fact]
    public async Task DoubleClickEnter_RenamesAndSaves()
    {
        using var tv = Alpha();
        await OpenSessionAsync(tv);
        var cut = Render<OutlinePane>();

        Row(cut, "Zwei").DoubleClick();
        cut.Find(".ne-outline-rename").Input("Zweitens");
        cut.Find(".ne-outline-rename").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        Assert.Equal(["Vorspann", "Eins", "Eins-A", "Zweitens"], Titles(cut));
        Assert.Empty(cut.FindAll(".ne-outline-rename"));
        Assert.Contains("- # Zweitens", tv.Read(BookPath));
        Assert.DoesNotContain("- # Zwei\n", tv.Read(BookPath));
        Assert.Equal("Zweitens", State.CurrentBook!.Root.Children.Last().Title);
    }

    [Fact]
    public async Task OutlineOperations_FlushTheEditorBeforeWriting()
    {
        using var tv = Alpha();
        await OpenSessionAsync(tv);
        var before = tv.Read(BookPath);
        var flushes = new List<bool>();
        State.FlushEditor = () =>
        {
            flushes.Add(tv.Read(BookPath) == before);
            return Task.CompletedTask;
        };
        var cut = Render<OutlinePane>();

        Row(cut, "Zwei").DoubleClick();
        cut.Find(".ne-outline-rename").Input("Zwei neu");
        cut.Find(".ne-outline-rename").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        Assert.Equal([true], flushes);
        Assert.Contains("- # Zwei neu\n", tv.Read(BookPath));

        before = tv.Read(BookPath);
        cut.Find(".ne-outline-add-root").Click();
        Row(cut, "Eins").DragStart();
        cut.Find(".ne-drop-end").Drop();

        Assert.Equal([true, true, false], flushes);
    }

    [Fact]
    public async Task Rename_Escape_CancelsWithoutWriting()
    {
        using var tv = Alpha();
        await OpenSessionAsync(tv);
        var before = tv.Read(BookPath);
        var cut = Render<OutlinePane>();

        Row(cut, "Zwei").DoubleClick();
        cut.Find(".ne-outline-rename").Input("Anders");
        cut.Find(".ne-outline-rename").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        Assert.Empty(cut.FindAll(".ne-outline-rename"));
        Assert.Equal("Zwei", Title(Row(cut, "Zwei")));
        Assert.Equal(before, tv.Read(BookPath));
    }

    [Fact]
    public async Task Rename_EmptyTitle_ShowsErrorAndChangesNothing()
    {
        using var tv = Alpha();
        await OpenSessionAsync(tv);
        var before = tv.Read(BookPath);
        var cut = Render<OutlinePane>();

        Row(cut, "Zwei").DoubleClick();
        cut.Find(".ne-outline-rename").Input("   ");
        cut.Find(".ne-outline-rename").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        Assert.NotEmpty(cut.Find(".ne-outline-error").TextContent);
        Assert.Equal(before, tv.Read(BookPath));
        Assert.False(State.CurrentBook!.Page.IsDirty);
        Assert.Single(cut.FindAll(".ne-outline-rename"));
    }

    [Fact]
    public async Task Add_CreatesSubHeadingInRenameMode_AndSaves()
    {
        using var tv = Plain();
        await OpenSessionAsync(tv);
        var cut = Render<OutlinePane>();

        Row(cut, "Eins").QuerySelector(".ne-outline-add")!.Click();

        Assert.Equal(["Eins", "Neue Überschrift", "Zwei", "Zwei-A", "Drei"], Titles(cut));
        Assert.Contains("\t- ## Neue Überschrift", tv.Read(BookPath));
        Assert.Equal(State.CurrentBook!.Root.Children.First().Children.Single().Key, State.CurrentSectionKey);
        Assert.Single(cut.FindAll(".ne-outline-rename"));
    }

    [Fact]
    public async Task Add_AtLevelSix_IsDisabled()
    {
        using var tv = TestVault.Create(
            (BookPath, "title:: Alpha\ntype:: book\n\n- # A\n\t- ## B\n\t\t- ### C\n\t\t\t- #### D\n\t\t\t\t- ##### E\n\t\t\t\t\t- ###### F\n"));
        await OpenSessionAsync(tv);

        var cut = Render<OutlinePane>();

        Assert.True(Row(cut, "F").QuerySelector(".ne-outline-add")!.HasAttribute("disabled"));
        Assert.False(Row(cut, "E").QuerySelector(".ne-outline-add")!.HasAttribute("disabled"));
    }

    [Fact]
    public async Task AddRoot_AppendsTopLevelHeading()
    {
        using var tv = Plain();
        await OpenSessionAsync(tv);
        var cut = Render<OutlinePane>();

        cut.Find(".ne-outline-add-root").Click();

        Assert.Equal("Neue Überschrift", Titles(cut).Last());
        Assert.EndsWith("- # Neue Überschrift\n", tv.Read(BookPath));
    }

    [Fact]
    public async Task Warning_ShowsIcon()
    {
        using var tv = TestVault.Create(
            (BookPath, "title:: Alpha\ntype:: book\n\n- ## Oben\n\t- ## Gleich\n\t- Text\n\t\t- # In Text\n"));
        await OpenSessionAsync(tv);
        Assert.Equal(2, State.CurrentBook!.Warnings.Count);

        var cut = Render<OutlinePane>();

        var gleich = Row(cut, "Gleich").QuerySelector(".ne-outline-warning");
        Assert.NotNull(gleich);
        Assert.Contains("Gleich", gleich.GetAttribute("title"));
        var oben = Row(cut, "Oben").QuerySelector(".ne-outline-warning");
        Assert.NotNull(oben);
        Assert.Contains("In Text", oben.GetAttribute("title"));
    }

    [Fact]
    public async Task Drop_MovesSectionAndSaves()
    {
        using var tv = Plain();
        await OpenSessionAsync(tv);
        var cut = Render<OutlinePane>();

        Row(cut, "Drei").DragStart();
        Row(cut, "Eins").Drop();

        // "Drei" became the last child of "Eins" (one level deeper).
        Assert.Equal(["Eins", "Drei", "Zwei", "Zwei-A"], Titles(cut));
        var content = tv.Read(BookPath);
        Assert.Contains("- # Eins\n\t- ## Drei\n", content);
        Assert.False(State.CurrentBook!.Page.IsDirty);
    }

    [Fact]
    public async Task DropBefore_MovesToIndexInSameParent()
    {
        using var tv = Plain();
        await OpenSessionAsync(tv);
        var cut = Render<OutlinePane>();

        Row(cut, "Drei").DragStart();
        Before(cut, "Zwei").Drop();

        Assert.Equal(["Eins", "Drei", "Zwei", "Zwei-A"], Titles(cut));
        Assert.Equal(["Eins", "Drei", "Zwei"], State.CurrentBook!.Root.Children.Select(c => c.Title));
        Assert.Contains("- # Eins\n- # Drei\n- # Zwei\n", tv.Read(BookPath));
    }

    [Fact]
    public async Task DropBefore_NestedItem_MovesIntoThatParent()
    {
        using var tv = Plain();
        await OpenSessionAsync(tv);
        var cut = Render<OutlinePane>();

        Row(cut, "Eins").DragStart();
        Before(cut, "Zwei-A").Drop();

        Assert.Equal(["Zwei", "Eins", "Zwei-A", "Drei"], Titles(cut));
        Assert.Contains("- # Zwei\n\t- ## Eins\n\t- ## Zwei-A\n", tv.Read(BookPath));
    }

    [Fact]
    public async Task DropOnEndZone_MovesToEndOfRoot()
    {
        using var tv = Plain();
        await OpenSessionAsync(tv);
        var cut = Render<OutlinePane>();

        Row(cut, "Zwei-A").DragStart();
        cut.Find(".ne-drop-end").Drop();

        Assert.Equal(["Eins", "Zwei", "Drei", "Zwei-A"], Titles(cut));
        Assert.EndsWith("- # Drei\n- # Zwei-A\n", tv.Read(BookPath));
    }

    [Fact]
    public async Task Drop_IntoOwnSubtree_ShowsErrorAndChangesNothing()
    {
        using var tv = Plain();
        await OpenSessionAsync(tv);
        var before = tv.Read(BookPath);
        var cut = Render<OutlinePane>();

        Row(cut, "Zwei").DragStart();
        Row(cut, "Zwei-A").Drop();

        Assert.NotEmpty(cut.Find(".ne-outline-error").TextContent);
        Assert.Equal(before, tv.Read(BookPath));
        Assert.Equal(["Eins", "Zwei", "Zwei-A", "Drei"], Titles(cut));
    }

    [Fact]
    public async Task Drop_OnItself_DoesNothing()
    {
        using var tv = Plain();
        await OpenSessionAsync(tv);
        var before = tv.Read(BookPath);
        var cut = Render<OutlinePane>();

        Row(cut, "Zwei").DragStart();
        Row(cut, "Zwei").Drop();

        Assert.Empty(cut.FindAll(".ne-outline-error"));
        Assert.Equal(before, tv.Read(BookPath));
    }

    [Fact]
    public async Task Vorspann_IsNotDraggable_OthersAre()
    {
        using var tv = Alpha();
        await OpenSessionAsync(tv);

        var cut = Render<OutlinePane>();

        Assert.NotEqual("true", Row(cut, "Vorspann").GetAttribute("draggable"));
        Assert.Equal("true", Row(cut, "Eins").GetAttribute("draggable"));
    }

    [Fact]
    public async Task ReadOnlyBook_ShowsOutlineButDisablesEditing()
    {
        // Invalid UTF-8 makes the page not safely parseable (read-only), but its blocks are still there.
        using var tv = TestVault.Create((BookPath, "x"));
        File.WriteAllBytes(
            Path.Combine(tv.Root, BookPath),
            [.. "title:: Alpha\ntype:: book\n\n- # Eins\n- # Zwei\n- Text ".Select(c => (byte)c), 0xC3, 0x28, (byte)'\n']);
        await OpenSessionAsync(tv);
        var page = State.CurrentBook!.Page;
        Assert.True(page.IsReadOnly);
        var before = File.ReadAllBytes(Path.Combine(tv.Root, BookPath));

        var cut = Render<OutlinePane>();
        Row(cut, "Eins").DoubleClick();
        Row(cut, "Zwei").DragStart();
        Row(cut, "Eins").Drop();

        Assert.Equal(["Vorspann", "Eins", "Zwei"], Titles(cut));
        Assert.Empty(cut.FindAll(".ne-outline-rename"));
        Assert.All(cut.FindAll(".ne-outline-add, .ne-outline-add-root"), b => Assert.True(b.HasAttribute("disabled")));
        Assert.NotEqual("true", Row(cut, "Zwei").GetAttribute("draggable"));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(tv.Root, BookPath)));
    }

    [Fact]
    public async Task ExternalChangeBeforeSave_KeepsExternalText_AndOutlineShowsFileState()
    {
        using var tv = Plain();
        var session = await OpenSessionAsync(tv);
        var cut = Render<OutlinePane>();
        var path = Path.Combine(tv.Root, BookPath);
        File.WriteAllText(path, "title:: Alpha\ntype:: book\n\n- # Eins\n- # Zwei\n\t- ## Zwei-A\n- # Drei\n- # Extern\n");

        Row(cut, "Drei").DoubleClick();
        cut.Find(".ne-outline-rename").Input("Dritte");
        cut.Find(".ne-outline-rename").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        // The session took the external change in (merged and saved, or reported as a conflict); nothing was lost.
        var content = tv.Read(BookPath);
        Assert.Contains("- # Extern", content);
        Assert.Empty(cut.FindAll(".ne-outline-error"));
        Assert.Contains("Extern", Titles(cut));
        Assert.Same(session, State.Session);
    }

    [Fact]
    public async Task OpenConflict_DisablesEditing()
    {
        using var tv = Plain();
        var session = await OpenSessionAsync(tv);
        var path = Path.Combine(tv.Root, BookPath);
        // A local, unsaved change plus a different external change of the same block is a conflict.
        var book = State.CurrentBook!;
        Core.Books.OutlineEditor.Rename(book, book.Root.Children.First().Key, "Lokal");
        File.WriteAllText(path, "title:: Alpha\ntype:: book\n\n- # ExternEins\n- # Zwei\n\t- ## Zwei-A\n- # Drei\n");
        session.HandleExternalChange(path);
        Assert.True(session.HasOpenConflict(path));

        var cut = Render<OutlinePane>();

        Assert.All(cut.FindAll(".ne-outline-add, .ne-outline-add-root"), b => Assert.True(b.HasAttribute("disabled")));
        var before = tv.Read(BookPath);
        Row(cut, "Zwei").DoubleClick();
        Assert.Empty(cut.FindAll(".ne-outline-rename"));
        Assert.Equal(before, tv.Read(BookPath));
    }

    [Fact]
    public async Task SaveFailure_ReloadsPageFromDisk_AndShowsError()
    {
        using var tv = Plain();
        await OpenSessionAsync(tv);
        var path = Path.Combine(tv.Root, BookPath);
        var cut = Render<OutlinePane>();
        var before = tv.Read(BookPath);

        // A read-only file cannot be replaced: the write fails after its retries.
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Row(cut, "Drei").DoubleClick();
            cut.Find(".ne-outline-rename").Input("Dritte");
            cut.Find(".ne-outline-rename").KeyDown(new KeyboardEventArgs { Key = "Enter" });
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Assert.NotEmpty(cut.Find(".ne-outline-error").TextContent);
        Assert.Equal(before, tv.Read(BookPath));
        Assert.Equal(["Eins", "Zwei", "Zwei-A", "Drei"], Titles(cut));
        Assert.False(State.CurrentBook!.Page.IsDirty);
        Assert.Equal("Drei", State.CurrentBook.Root.Children.Last().Title);
    }

    [Fact]
    public void NoBook_RendersPlaceholder()
    {
        var cut = Render<OutlinePane>();

        Assert.Empty(cut.FindAll(".ne-outline-row"));
        Assert.Single(cut.FindAll(".ne-placeholder"));
    }

    [Fact]
    public void Shell_ShowsOutlinePane_InsteadOfPlaceholder()
    {
        using var tv = Plain();
        Settings.LastVault = tv.Root;
        Settings.LogseqHintShown = true;

        var cut = Render<Shell>();

        cut.WaitForAssertion(() => Assert.Equal(["Eins", "Zwei", "Zwei-A", "Drei"], cut.FindAll(".ne-outline .ne-outline-title").Select(e => e.TextContent)));
        Assert.Empty(cut.FindAll(".ne-outline .ne-placeholder"));
    }

    [Fact]
    public async Task PagesChanged_RebuildsTheOutline()
    {
        using var tv = Plain();
        var session = await OpenSessionAsync(tv);
        var cut = Render<OutlinePane>();
        var path = Path.Combine(tv.Root, BookPath);

        File.WriteAllText(path, "title:: Alpha\ntype:: book\n\n- # Eins\n- # Neu\n");
        session.HandleExternalChange(path);
        State.RefreshBook();
        State.Notify();

        cut.WaitForAssertion(() => Assert.Equal(["Eins", "Neu"], Titles(cut)));
    }
}
