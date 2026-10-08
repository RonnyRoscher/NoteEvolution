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

/// <summary>Adopting a note at the cursor, after or below the current element (spec 4).</summary>
public class AdoptVariantTests : UiTestContext
{
    private const string BookPath = "pages/Buch - Alpha.md";

    private const string NotePath = "pages/Notiz.md";

    private const string ErsterId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1";

    private const string NoteId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb1";

    private const string ChildId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb2";

    private const string BookText =
        "title:: Alpha\ntype:: book\n\n" +
        "- # Eins\n" +
        "\t- Erster Text\n" +
        $"\t  id:: {ErsterId}\n" +
        "\t\t- Ein Detail\n" +
        "\t\t\t- Tiefes Detail\n" +
        "\t\t- Zweites Detail\n" +
        "\t- Zweiter Text\n" +
        "- # Zwei\n" +
        "\t- Dritter Text\n";

    private const string NoteText =
        "- Eine Notiz\n" +
        $"  id:: {NoteId}\n" +
        "\t- Unterpunkt\n" +
        $"\t  id:: {ChildId}\n";

    /// <summary>The note's file after it was adopted into the text block with the id <paramref name="textBlockId"/>.</summary>
    private static string NoteUsedIn(Guid? textBlockId) =>
        "- Eine Notiz\n" +
        $"  id:: {NoteId}\n" +
        $"  used-in:: [[Buch - Alpha]] (({textBlockId}))\n" +
        "\t- Unterpunkt\n" +
        $"\t  id:: {ChildId}\n";

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

    private Book Book => State.CurrentBook!;

    private string Text(string key) => Services.GetRequiredService<IStringLocalizer<Strings>>()[key].Value;

    private Guid KeyOf(string content) => Book.Page.AllBlocks().Single(b => b.Content == content).Key;

    private Guid NoteKey(string content) => State.Session!.Notes.All().Single(n => n.Block.Content == content).Key;

    private Task<AdoptMessage?> AdoptAsync(AdoptVariant variant, string note = "Eine Notiz") =>
        NoteAdoption.AdoptAsync(State, NoteKey(note), variant, NullLogger.Instance);

    /// <summary>Puts the cursor (as the editor reports it) into the element whose block has <paramref name="content"/>.</summary>
    private void CursorAt(string content, int offset = 0)
    {
        var element = BookElements.Find(Book, KeyOf(content))!;
        State.Cursor = new CursorInfo(element.Kind, element.Key, BookElements.TextBlockOf(Book, element)?.Key, offset);
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

    /// <summary>The new text block with <paramref name="text"/> is revealed: the cursor is at its start.</summary>
    private Guid AssertRevealedNewTextBlock(string text)
    {
        var added = Book.FindTextBlock(KeyOf(text))!;
        Assert.Equal(added.Key, State.PendingReveal);
        Assert.Equal((ElementKind.TextBlock, added.Key, 0), (State.Cursor!.Kind, State.Cursor.Key, State.Cursor.Offset));
        return added.Key;
    }

    [Fact]
    public async Task Adopt_AtCursor_TextBlockMidText()
    {
        await OpenSessionAsync(_vault);

        // The target is read after the editor saved, from the cursor it reports then.
        State.FlushEditor = () =>
        {
            CursorAt("Erster Text", 7);
            return Task.CompletedTask;
        };
        var message = await AdoptAsync(AdoptVariant.AtCursor);

        Assert.Null(message);
        AssertBook(
            BookText.Replace(
                "\t- Erster Text\n" + $"\t  id:: {ErsterId}\n",
                "\t- Erster Eine NotizText\n" + $"\t  id:: {ErsterId}\n" + $"\t  source:: (({NoteId}))\n" + "\t\t- Unterpunkt\n"),
            ["\t- Erster Text"],
            ["\t- Erster Eine NotizText", $"\t  source:: (({NoteId}))", "\t\t- Unterpunkt"]);
        Assert.Equal(NoteUsedIn(Guid.Parse(ErsterId)), _vault.Read(NotePath));
        Assert.Null(State.PendingReveal);
    }

    [Fact]
    public async Task Adopt_After_TextBlock_NewBlock()
    {
        await OpenSessionAsync(_vault);
        CursorAt("Erster Text", 7);

        Assert.Null(await AdoptAsync(AdoptVariant.After));

        var key = AssertRevealedNewTextBlock("Eine Notiz");
        var id = Book.FindTextBlock(key)!.Block.Id;
        AssertBook(
            BookText.Replace(
                "\t- Zweiter Text\n",
                "\t- Eine Notiz\n" + $"\t  id:: {id}\n" + $"\t  source:: (({NoteId}))\n" + "\t\t- Unterpunkt\n" + "\t- Zweiter Text\n"),
            [],
            ["\t- Eine Notiz", $"\t  id:: {id}", $"\t  source:: (({NoteId}))", "\t\t- Unterpunkt"]);
        Assert.Equal(NoteUsedIn(id), _vault.Read(NotePath));
    }

    [Fact]
    public async Task Adopt_Below_TextBlock_FirstDetail()
    {
        await OpenSessionAsync(_vault);
        CursorAt("Erster Text", 7);

        Assert.Null(await AdoptAsync(AdoptVariant.Below));

        AssertBook(
            BookText.Replace(
                $"\t  id:: {ErsterId}\n",
                $"\t  id:: {ErsterId}\n" + $"\t  source:: (({NoteId}))\n" + "\t\t- Eine Notiz\n" + "\t\t\t- Unterpunkt\n"),
            [],
            [$"\t  source:: (({NoteId}))", "\t\t- Eine Notiz", "\t\t\t- Unterpunkt"]);
        Assert.Equal(NoteUsedIn(Guid.Parse(ErsterId)), _vault.Read(NotePath));
        Assert.Null(State.PendingReveal);
    }

    [Fact]
    public async Task Adopt_After_Detail()
    {
        await OpenSessionAsync(_vault);
        CursorAt("Ein Detail", 3);

        Assert.Null(await AdoptAsync(AdoptVariant.After));

        AssertBook(
            BookText
                .Replace($"\t  id:: {ErsterId}\n", $"\t  id:: {ErsterId}\n" + $"\t  source:: (({NoteId}))\n")
                .Replace("\t\t- Zweites Detail\n", "\t\t- Eine Notiz\n\t\t\t- Unterpunkt\n\t\t- Zweites Detail\n"),
            [],
            [$"\t  source:: (({NoteId}))", "\t\t- Eine Notiz", "\t\t\t- Unterpunkt"]);
        Assert.Equal(NoteUsedIn(Guid.Parse(ErsterId)), _vault.Read(NotePath));
    }

    [Fact]
    public async Task Adopt_Below_Detail()
    {
        await OpenSessionAsync(_vault);
        CursorAt("Ein Detail", 3);

        Assert.Null(await AdoptAsync(AdoptVariant.Below));

        AssertBook(
            BookText
                .Replace($"\t  id:: {ErsterId}\n", $"\t  id:: {ErsterId}\n" + $"\t  source:: (({NoteId}))\n")
                .Replace("\t\t\t- Tiefes Detail\n", "\t\t\t- Eine Notiz\n\t\t\t\t- Unterpunkt\n\t\t\t- Tiefes Detail\n"),
            [],
            [$"\t  source:: (({NoteId}))", "\t\t\t- Eine Notiz", "\t\t\t\t- Unterpunkt"]);
        Assert.Equal(NoteUsedIn(Guid.Parse(ErsterId)), _vault.Read(NotePath));
    }

    [Fact]
    public async Task Adopt_AtCursor_Detail()
    {
        await OpenSessionAsync(_vault);
        CursorAt("Ein Detail", 4);

        Assert.Null(await AdoptAsync(AdoptVariant.AtCursor));

        AssertBook(
            BookText
                .Replace($"\t  id:: {ErsterId}\n", $"\t  id:: {ErsterId}\n" + $"\t  source:: (({NoteId}))\n")
                .Replace("\t\t- Ein Detail\n", "\t\t- Ein Eine NotizDetail\n\t\t\t- Unterpunkt\n"),
            ["\t\t- Ein Detail"],
            [$"\t  source:: (({NoteId}))", "\t\t- Ein Eine NotizDetail", "\t\t\t- Unterpunkt"]);
    }

    [Theory]
    [InlineData(AdoptVariant.AtCursor)]
    [InlineData(AdoptVariant.After)]
    [InlineData(AdoptVariant.Below)]
    public async Task Adopt_AnyVariant_OnHeading_FirstTextBlockOfSection(AdoptVariant variant)
    {
        await OpenSessionAsync(_vault);
        CursorAt("# Zwei", 2);

        Assert.Null(await AdoptAsync(variant));

        var key = AssertRevealedNewTextBlock("Eine Notiz");
        var id = Book.FindTextBlock(key)!.Block.Id;
        Assert.Equal(KeyOf("# Zwei"), Book.FindTextBlock(key)!.Section.Key);
        AssertBook(
            BookText.Replace(
                "- # Zwei\n",
                "- # Zwei\n" + "\t- Eine Notiz\n" + $"\t  id:: {id}\n" + $"\t  source:: (({NoteId}))\n" + "\t\t- Unterpunkt\n"),
            [],
            ["\t- Eine Notiz", $"\t  id:: {id}", $"\t  source:: (({NoteId}))", "\t\t- Unterpunkt"]);
    }

    [Theory]
    [InlineData(AdoptVariant.AtCursor)]
    [InlineData(AdoptVariant.After)]
    [InlineData(AdoptVariant.Below)]
    public async Task Adopt_NoElement_EndOfBook(AdoptVariant variant)
    {
        await OpenSessionAsync(_vault);
        Assert.Null(State.Cursor);
        Assert.Equal(Book.Root.Key, State.CurrentSectionKey);

        Assert.Null(await AdoptAsync(variant));

        var key = AssertRevealedNewTextBlock("Eine Notiz");
        var id = Book.FindTextBlock(key)!.Block.Id;
        AssertBook(
            BookText + "\t- Eine Notiz\n" + $"\t  id:: {id}\n" + $"\t  source:: (({NoteId}))\n" + "\t\t- Unterpunkt\n",
            [],
            ["\t- Eine Notiz", $"\t  id:: {id}", $"\t  source:: (({NoteId}))", "\t\t- Unterpunkt"]);
    }

    [Fact]
    public async Task Adopt_AtCursor_UnknownCursorKey_AfterFallbackTextBlock()
    {
        await OpenSessionAsync(_vault);

        // A detail the book does not know (yet): its offset must not be applied to its text block.
        State.Cursor = new CursorInfo(ElementKind.Detail, Guid.NewGuid(), KeyOf("Erster Text"), 3);
        Assert.Equal(new BookElement(ElementKind.TextBlock, KeyOf("Erster Text")), State.CurrentElement);

        Assert.Null(await AdoptAsync(AdoptVariant.AtCursor));

        var key = AssertRevealedNewTextBlock("Eine Notiz");
        var id = Book.FindTextBlock(key)!.Block.Id;
        AssertBook(
            BookText.Replace(
                "\t- Zweiter Text\n",
                "\t- Eine Notiz\n" + $"\t  id:: {id}\n" + $"\t  source:: (({NoteId}))\n" + "\t\t- Unterpunkt\n" + "\t- Zweiter Text\n"),
            [],
            ["\t- Eine Notiz", $"\t  id:: {id}", $"\t  source:: (({NoteId}))", "\t\t- Unterpunkt"]);
    }

    [Fact]
    public async Task Adopt_SubBullet_AtCursor()
    {
        await OpenSessionAsync(_vault);
        CursorAt("Zweiter Text", 8);

        Assert.Null(await AdoptAsync(AdoptVariant.AtCursor, "Unterpunkt"));

        var id = Book.FindTextBlock(KeyOf("Zweiter UnterpunktText"))!.Block.Id;
        Assert.NotNull(id);
        AssertBook(
            BookText.Replace(
                "\t- Zweiter Text\n",
                "\t- Zweiter UnterpunktText\n" + $"\t  id:: {id}\n" + $"\t  source:: (({ChildId}))\n"),
            ["\t- Zweiter Text"],
            ["\t- Zweiter UnterpunktText", $"\t  id:: {id}", $"\t  source:: (({ChildId}))"]);
        Assert.Equal(NoteText + $"\t  used-in:: [[Buch - Alpha]] (({id}))\n", _vault.Read(NotePath));
    }

    [Fact]
    public async Task SplitButton_MenuShowsThreeVariants()
    {
        var session = await OpenSessionAsync(_vault);
        CursorAt("Erster Text", 7);
        var cut = Render<NoteCard>(p => p.Add(c => c.Note, session.Notes.All().Single(n => n.Block.Content == "Eine Notiz")));
        Assert.Empty(cut.FindAll(".ne-adopt-menu"));
        Assert.Equal(Text("NoteAdoptMore"), cut.Find(".ne-note-adopt-more").GetAttribute("title"));
        Assert.Equal(Text("NoteAdoptMore"), cut.Find(".ne-note-child-adopt-more").GetAttribute("title"));

        cut.Find(".ne-note-adopt-more").Click();

        var menu = cut.Find(".ne-adopt-menu");
        Assert.Equal(
            [Text("AdoptAtCursor"), Text("AdoptAfter"), Text("AdoptBelow")],
            menu.QuerySelectorAll("button").Select(b => b.TextContent.Trim()));
        Assert.Equal(Text("AdoptAtCursor"), cut.Find(".ne-adopt-at-cursor").TextContent);
        Assert.Equal(Text("AdoptAfter"), cut.Find(".ne-adopt-after").TextContent);
        Assert.Equal(Text("AdoptBelow"), cut.Find(".ne-adopt-below").TextContent);

        // A variant of the menu adopts that way and closes the menu.
        cut.Find(".ne-adopt-below").Click();

        Assert.Empty(cut.FindAll(".ne-adopt-menu"));
        Assert.Contains("\t\t- Eine Notiz\n\t\t\t- Unterpunkt\n\t\t- Ein Detail\n", _vault.Read(BookPath));

        // The sub-bullet's arrow opens the menu for the sub-bullet.
        cut.Find(".ne-note-child-adopt-more").Click();
        cut.Find(".ne-adopt-after").Click();

        Assert.Contains($"source:: (({ChildId}))", _vault.Read(BookPath));
        Assert.Single(Book.Page.AllBlocks(), b => b.Content == "Unterpunkt" && Book.FindTextBlock(b.Key) is not null);
    }

    [Fact]
    public async Task SplitButton_MainClick_IsAtCursor()
    {
        var session = await OpenSessionAsync(_vault);
        CursorAt("Zweiter Text", 8);
        var cut = Render<NoteCard>(p => p.Add(c => c.Note, session.Notes.All().Single(n => n.Block.Content == "Eine Notiz")));
        Assert.Equal(Text("NoteAdopt"), cut.Find(".ne-note-adopt").TextContent);

        cut.Find(".ne-note-adopt").Click();

        Assert.Contains("\t- Zweiter Eine NotizText\n", _vault.Read(BookPath));

        // The sub-bullet's main button adopts it at the cursor as well.
        CursorAt("Dritter Text", 8);
        cut.Find(".ne-note-child-adopt").Click();

        Assert.Contains("\t- Dritter UnterpunktText\n", _vault.Read(BookPath));
    }

    [Theory]
    [InlineData(AdoptVariant.AtCursor)]
    [InlineData(AdoptVariant.After)]
    [InlineData(AdoptVariant.Below)]
    public async Task Adopt_ConflictOpen_Refused(AdoptVariant variant)
    {
        var session = await OpenSessionAsync(_vault);
        CursorAt("Erster Text", 7);

        // A local change collides with an external change of the same line.
        Book.FindTextBlock(KeyOf("Erster Text"))!.Block.SetContent("Erster lokal");
        var fullPath = Path.Combine(_vault.Root, BookPath);
        var external = BookText.Replace("\t- Erster Text\n", "\t- Erster extern\n");
        File.WriteAllText(fullPath, external);
        session.HandleExternalChange(fullPath);
        Assert.True(session.HasOpenConflict(fullPath));

        var message = await AdoptAsync(variant);

        Assert.Equal(new AdoptMessage("NoteAdoptConflict", true), message);
        Assert.Equal(external, _vault.Read(BookPath));
        Assert.Equal(NoteText, _vault.Read(NotePath));
        Assert.False(session.Undo.CanUndo);
    }
}
