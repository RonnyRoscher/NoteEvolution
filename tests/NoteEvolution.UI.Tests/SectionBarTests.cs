using System.Resources;
using AngleSharp.Dom;
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

/// <summary>The bar at the marking of the current section (spec 2, 3): the structure commands and the section's sources.</summary>
public class SectionBarTests : UiTestContext
{
    private const string BookPath = "pages/Buch - Alpha.md";

    private const string Early = "11111111-1111-4111-8111-111111111111";

    private const string Late = "22222222-2222-4222-8222-222222222222";

    private const string Undated = "33333333-3333-4333-8333-333333333333";

    private const string Gone = "ffffffff-ffff-4fff-8fff-ffffffffffff";

    private const string BookText =
        "title:: Alpha\ntype:: book\n\n" +
        "- # Eins\n" +
        "\t- Erster Text\n" +
        $"\t  source:: (({Late})), (({Early}))\n" +
        "\t\t- Ein Detail\n" +
        "\t- Zweiter Text\n" +
        $"\t  source:: (({Undated})), (({Early})), (({Gone}))\n" +
        "\t- ## Eins-A\n" +
        "\t\t- ### Drei\n" +
        "\t\t\t- #### Vier\n" +
        "\t\t\t\t- ##### Fünf\n" +
        "\t\t\t\t\t- ###### Sechs\n" +
        "- # Zwei\n" +
        "\t- Dritter Text\n" +
        $"\t  source:: (({Undated}))\n" +
        "- # Leer\n" +
        "\t- Ohne Quelle\n";

    private static readonly string[] AllButtons =
        [".ne-sec-after", ".ne-sec-child", ".ne-sec-outdent-any", ".ne-sec-indent-any", ".ne-sec-remove-heading", ".ne-sec-delete"];

    private readonly TestVault _vault = TestVault.Create(
        (BookPath, BookText),
        ("journals/2026_09_15.md", $"- Frühe Notiz\n  id:: {Early}\n"),
        ("journals/2026_10_01.md", $"- Späte Notiz\n  id:: {Late}\n"),
        ("pages/0 Archiv.md", $"- Seitennotiz\n  id:: {Undated}\n"));

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

    /// <summary>Opens the vault (<see cref="_vault"/> by default), renders the editor and lets it report a visible marking box.</summary>
    private async Task<IRenderedComponent<EditorPane>> RenderAsync(TestVault? vault = null)
    {
        await OpenSessionAsync(vault ?? _vault);
        var cut = Render<EditorPane>();
        cut.WaitForAssertion(() => Assert.NotEmpty(Editor.Documents));
        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionBoxMoved(120.5, true));
        return cut;
    }

    /// <summary>Moves the cursor (as the editor reports it) to the element whose block has <paramref name="content"/>.</summary>
    private Task MoveToAsync(IRenderedComponent<EditorPane> cut, string content) =>
        cut.InvokeAsync(() => Editor.Callbacks!.OnCursorChanged(CursorOf(content)));

    private static IEnumerable<string> ShownButtons(IRenderedComponent<EditorPane> cut) =>
        AllButtons.Where(selector => cut.FindAll($".ne-section-bar {selector}").Count == 1);

    [Fact]
    public async Task Buttons_FollowElementKind()
    {
        var cut = await RenderAsync();

        // The bar sits at the position the editor reports and is hidden while the marking is out of view.
        await MoveToAsync(cut, "# Eins");
        cut.WaitForAssertion(() => Assert.Contains("top: 120.5px", cut.Find(".ne-section-bar").GetAttribute("style")));
        Assert.False(cut.Find(".ne-section-bar").HasAttribute("hidden"));
        Assert.Equal(AllButtons, ShownButtons(cut));
        Assert.Equal(
            ["SectionInsertAfter", "SectionInsertChild", "SectionOutdent", "SectionIndent", "SectionRemoveHeading", "SectionDelete"],
            AllButtons.Select(selector => cut.Find(selector).TextContent.Trim()).Select(text => Keys[text]));

        await MoveToAsync(cut, "Erster Text");
        cut.WaitForAssertion(() => Assert.Equal([".ne-sec-after", ".ne-sec-child", ".ne-sec-outdent-any", ".ne-sec-indent-any", ".ne-sec-delete"], ShownButtons(cut)));

        await MoveToAsync(cut, "Ein Detail");
        cut.WaitForAssertion(() => Assert.Equal([".ne-sec-after", ".ne-sec-child", ".ne-sec-outdent-any", ".ne-sec-indent-any", ".ne-sec-delete"], ShownButtons(cut)));
        Assert.False(cut.Find(".ne-sec-delete").HasAttribute("disabled"));

        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionBoxMoved(0, false));
        cut.WaitForAssertion(() => Assert.True(cut.Find(".ne-section-bar").HasAttribute("hidden")));
    }

    [Fact]
    public async Task Buttons_DisabledOnConflict()
    {
        var cut = await RenderAsync();
        var session = State.Session!;
        await MoveToAsync(cut, "# Zwei");
        cut.WaitForAssertion(() => Assert.False(cut.Find(".ne-sec-after").HasAttribute("disabled")));

        // Ausrücken is not possible on level 1; everything else is.
        Assert.True(cut.Find(".ne-sec-outdent-any").HasAttribute("disabled"));
        Assert.All(AllButtons.Where(s => s != ".ne-sec-outdent-any"), s => Assert.False(cut.Find(s).HasAttribute("disabled")));

        // A local change of the heading collides with an external change of the same line.
        var path = Path.Combine(_vault.Root, BookPath);
        await cut.InvokeAsync(() =>
        {
            OutlineEditor.Rename(Book, KeyOf("# Zwei"), "Zwei lokal");
            File.WriteAllText(path, BookText.Replace("- # Zwei\n", "- # Zwei extern\n"));
            session.HandleExternalChange(path);
        });
        Assert.True(session.HasOpenConflict(path));

        cut.WaitForAssertion(() => Assert.All(AllButtons, s => Assert.True(cut.Find(s).HasAttribute("disabled"), s)));
    }

    [Fact]
    public async Task InsertChild_DisabledOnLevel6()
    {
        var cut = await RenderAsync();

        await MoveToAsync(cut, "##### Fünf");
        cut.WaitForAssertion(() => Assert.False(cut.Find(".ne-sec-child").HasAttribute("disabled")));

        await MoveToAsync(cut, "###### Sechs");
        cut.WaitForAssertion(() => Assert.True(cut.Find(".ne-sec-child").HasAttribute("disabled")));
        Assert.False(cut.Find(".ne-sec-after").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Click_RunsCommand()
    {
        var cut = await RenderAsync();
        var session = State.Session!;
        await MoveToAsync(cut, "Erster Text");

        await cut.InvokeAsync(() => cut.Find(".ne-sec-after").Click());

        Assert.Equal(BookText.Replace("\t\t- Ein Detail\n", "\t\t- Ein Detail\n\t-\n\t  created:: " + Stamp + "\n\t  updated:: " + Stamp + "\n"), _vault.Read(BookPath));
        var added = KeyOf("");
        cut.WaitForAssertion(() => Assert.Equal(added, Assert.Single(Editor.Reveals)));
        Assert.Equal(Text("UndoStructure"), session.Undo.NextDescription);
        Assert.Empty(cut.FindAll(".ne-editor-error"));
    }

    [Fact]
    public async Task Sources_CountDistinct_PerKind()
    {
        var cut = await RenderAsync();
        (string Cursor, string Expected)[] cases =
        [
            ("# Eins", Text("SectionSources", 4)),
            ("Erster Text", Text("SectionSources", 2)),
            ("Ein Detail", Text("SectionSources", 2)),
            ("Zweiter Text", Text("SectionSources", 3)),
            ("# Zwei", Text("SectionSourcesOne")),
            ("Dritter Text", Text("SectionSourcesOne")),
        ];

        foreach (var (cursor, expected) in cases)
        {
            await MoveToAsync(cut, cursor);
            cut.WaitForAssertion(() => Assert.Equal(expected, cut.Find(".ne-sec-sources").TextContent.Trim()));
        }
    }

    [Fact]
    public async Task Sources_Expand_SortedByDate_MissingNotClickable()
    {
        var cut = await RenderAsync();
        await MoveToAsync(cut, "# Eins");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-sec-sources")));
        Assert.Empty(cut.FindAll(".ne-sec-source-list"));

        cut.Find(".ne-sec-sources").Click();

        Assert.Equal(Text("SectionSourcesOpen", 4), cut.Find(".ne-sec-sources").TextContent.Trim());
        // One row per linked block: „Frühe Notiz“ is a source of both text blocks of the section.
        var entries = cut.FindAll(".ne-sec-source-list > .ne-sec-source-row").Select(row => row.FirstElementChild!).ToList();
        Assert.Equal(
            ["2026-09-15 – Frühe Notiz", "2026-09-15 – Frühe Notiz", "2026-10-01 – Späte Notiz", "0 Archiv – Seitennotiz",
                Text("SectionSourceMissing")],
            entries.Select(e => e.TextContent.Trim()));
        Assert.Equal(["BUTTON", "BUTTON", "BUTTON", "BUTTON", "SPAN"], entries.Select(e => e.TagName));
        Assert.Equal(4, cut.FindAll("button.ne-sec-source").Count);
        Assert.Single(cut.FindAll("span.ne-sec-source-broken"));
        Assert.Empty(cut.FindAll("button.ne-sec-source-broken"));

        cut.Find(".ne-sec-sources").Click();

        Assert.Equal(Text("SectionSources", 4), cut.Find(".ne-sec-sources").TextContent.Trim());
        Assert.Empty(cut.FindAll(".ne-sec-source-list"));
    }

    [Fact]
    public async Task Sources_ChoosingASource_ClosesTheList()
    {
        var cut = await RenderAsync();
        await MoveToAsync(cut, "Erster Text");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-sec-sources")));
        cut.Find(".ne-sec-sources").Click();
        Assert.Single(cut.FindAll(".ne-sec-source-list"));

        cut.FindAll("button.ne-sec-source")[0].Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-sec-source-list")));
        Assert.Equal(Text("SectionSources", 2), cut.Find(".ne-sec-sources").TextContent.Trim());
    }

    [Fact]
    public async Task HeadingAfter_OnTextBlockAndDetail_NotOnHeading_SplitsTheSection()
    {
        var cut = await RenderAsync();
        await MoveToAsync(cut, "# Eins");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-sec-after")));
        Assert.Empty(cut.FindAll(".ne-sec-heading-after"));

        await MoveToAsync(cut, "Ein Detail");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-sec-heading-after")));
        await MoveToAsync(cut, "Erster Text");
        cut.WaitForAssertion(() => Assert.Equal(Text("SectionInsertHeadingAfter"), cut.Find(".ne-sec-heading-after").TextContent.Trim()));

        await cut.InvokeAsync(() => cut.Find(".ne-sec-heading-after").Click());

        cut.WaitForAssertion(() => Assert.Contains("\t\t- Ein Detail\n- # \n\t- Zweiter Text\n", _vault.Read(BookPath)));
        var heading = Book.Root.Children.Single(node => node.Title == "");
        Assert.Equal(["Zweiter Text"], heading.TextBlocks.Select(t => t.Text));
        Assert.Equal(["Eins-A"], heading.Children.Select(c => c.Title));
    }

    [Fact]
    public async Task Sources_ExpandedStateSurvivesCursorMove()
    {
        var cut = await RenderAsync();
        await MoveToAsync(cut, "Dritter Text");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-sec-sources")));
        cut.Find(".ne-sec-sources").Click();
        Assert.Equal(Text("SectionSourcesOneOpen"), cut.Find(".ne-sec-sources").TextContent.Trim());

        await MoveToAsync(cut, "Erster Text");
        cut.WaitForAssertion(() => Assert.Equal(Text("SectionSourcesOpen", 2), cut.Find(".ne-sec-sources").TextContent.Trim()));
        Assert.Equal(
            ["2026-09-15 – Frühe Notiz", "2026-10-01 – Späte Notiz"],
            cut.FindAll(".ne-sec-source").Select(e => e.TextContent.Trim()));

        // Nothing to list, but the list opens again at the next section with sources.
        await MoveToAsync(cut, "Ohne Quelle");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-sec-source-list")));
        await MoveToAsync(cut, "Zweiter Text");
        cut.WaitForAssertion(() => Assert.Equal(Text("SectionSourcesOpen", 3), cut.Find(".ne-sec-sources").TextContent.Trim()));
        Assert.Single(cut.FindAll(".ne-sec-source-list"));
    }

    [Fact]
    public async Task SourceClick_RaisesRevealNote()
    {
        var cut = await RenderAsync();
        var revealed = new List<Guid>();
        State.NoteRevealRequested += revealed.Add;
        await MoveToAsync(cut, "Zweiter Text");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-sec-sources")));
        cut.Find(".ne-sec-sources").Click();

        cut.FindAll(".ne-sec-source")[1].Click();

        Assert.Equal([Guid.Parse(Undated)], revealed);
    }

    [Fact]
    public async Task NoSources_ShowsKeineQuellen()
    {
        var cut = await RenderAsync();

        await MoveToAsync(cut, "# Leer");

        cut.WaitForAssertion(() => Assert.Equal(Text("SectionNoSources"), cut.Find(".ne-sec-no-sources").TextContent.Trim()));
        Assert.Equal("SPAN", cut.Find(".ne-sec-no-sources").TagName);
        Assert.Empty(cut.FindAll(".ne-sec-sources"));
        Assert.Empty(cut.FindAll(".ne-sec-source-list"));
    }

    private const string LinkedTextId = "44444444-4444-4444-8444-444444444444";

    private const string LinkedDetailId = "55555555-5555-4555-8555-555555555555";

    private const string LongLine = "Ein Textblock mit einer recht langen ersten Zeile, die über sechzig Zeichen hinausgeht";

    /// <summary>„Frühe Notiz“ on a text block and on its detail, the detail also names a missing note.</summary>
    private const string LinkedBook =
        "title:: Alpha\ntype:: book\n\n" +
        "- # Eins\n" +
        "\t- " + LongLine + "\n" +
        "\t  id:: " + LinkedTextId + "\n" +
        $"\t  source:: (({Early}))\n" +
        "\t\t- Ein Detail\n" +
        "\t\t  id:: " + LinkedDetailId + "\n" +
        $"\t\t  source:: (({Early})), (({Gone}))\n" +
        "- # Leer\n" +
        "\t- Ohne Quelle\n";

    private const string LinkedJournalPath = "journals/2026_09_15.md";

    private const string LinkedJournal =
        $"- Frühe Notiz\n  id:: {Early}\n  used-in:: [[Buch - Alpha]] (({LinkedTextId})), [[Buch - Alpha]] (({LinkedDetailId}))\n";

    private static TestVault Linked() => TestVault.Create((BookPath, LinkedBook), (LinkedJournalPath, LinkedJournal));

    /// <summary>Renders the editor on <see cref="Linked"/> with the cursor in the detail and the list of sources open.</summary>
    private async Task<IRenderedComponent<EditorPane>> RenderLinkedOpenAsync(TestVault vault)
    {
        var cut = await RenderAsync(vault);
        await MoveToAsync(cut, "Ein Detail");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-sec-sources")));
        cut.Find(".ne-sec-sources").Click();
        return cut;
    }

    private static List<string> RowTexts(IRenderedComponent<EditorPane> cut) =>
        [.. cut.FindAll(".ne-sec-source-row").Select(row => row.FirstElementChild!.TextContent.Trim())];

    [Fact]
    public async Task Sources_ListsDetailLinks_OneRowPerBlock()
    {
        using var vault = Linked();
        var cut = await RenderLinkedOpenAsync(vault);

        // The count names the distinct notes (as the outline does); the list has a row per linked block.
        Assert.Equal(Text("SectionSourcesOpen", 2), cut.Find(".ne-sec-sources").TextContent.Trim());
        Assert.Equal(["2026-09-15 – Frühe Notiz", "2026-09-15 – Frühe Notiz", Text("SectionSourceMissing")], RowTexts(cut));
        var loosen = cut.FindAll(".ne-sec-source-row > button.ne-sec-source-loosen");
        Assert.Equal(3, loosen.Count);
        var longExcerpt = LongLine[..60].TrimEnd() + "…";
        Assert.Equal(
            [Text("SectionSourceLoosenTitle", longExcerpt), Text("SectionSourceLoosenTitle", "Ein Detail"),
                Text("SectionSourceLoosenTitle", "Ein Detail")],
            loosen.Select(button => button.GetAttribute("title")));
        Assert.All(loosen, button => Assert.Equal(Text("SectionSourceLoosen"), button.GetAttribute("aria-label")));
    }

    [Fact]
    public async Task Sources_Loosen_RemovesThere_ListStaysOpen()
    {
        using var vault = Linked();
        var cut = await RenderLinkedOpenAsync(vault);

        // The second row: „Frühe Notiz“ on the detail.
        await cut.InvokeAsync(() => cut.FindAll(".ne-sec-source-loosen")[1].Click());

        cut.WaitForAssertion(() => Assert.Contains($"\t\t  id:: {LinkedDetailId}\n\t\t  source:: (({Gone}))\n", vault.Read(BookPath)));
        Assert.Contains($"\t  source:: (({Early}))\n", vault.Read(BookPath));
        Assert.Equal(
            $"- Frühe Notiz\n  id:: {Early}\n  used-in:: [[Buch - Alpha]] (({LinkedTextId}))\n", vault.Read(LinkedJournalPath));
        cut.WaitForAssertion(() => Assert.Equal(["2026-09-15 – Frühe Notiz", Text("SectionSourceMissing")], RowTexts(cut)));
        Assert.Single(cut.FindAll(".ne-sec-source-list"));
        Assert.Equal(Text("SectionSourcesOpen", 2), cut.Find(".ne-sec-sources").TextContent.Trim());
    }

    [Fact]
    public async Task Sources_LoosenLast_ShowsNoSources()
    {
        using var vault = Linked();
        var cut = await RenderLinkedOpenAsync(vault);

        for (var left = 3; left > 0; left--)
        {
            cut.WaitForAssertion(() => Assert.Equal(left, cut.FindAll(".ne-sec-source-loosen").Count));
            await cut.InvokeAsync(() => cut.Find(".ne-sec-source-loosen").Click());
        }

        cut.WaitForAssertion(() => Assert.Equal(Text("SectionNoSources"), cut.Find(".ne-sec-no-sources").TextContent.Trim()));
        Assert.Empty(cut.FindAll(".ne-sec-source-list"));
        Assert.DoesNotContain("source::", vault.Read(BookPath));
        Assert.DoesNotContain("used-in::", vault.Read(LinkedJournalPath));
    }

    [Fact]
    public async Task Sources_BrokenSource_HasLoosen()
    {
        using var vault = Linked();
        var cut = await RenderLinkedOpenAsync(vault);
        var broken = cut.Find(".ne-sec-source-row:last-child");
        Assert.Equal(Text("SectionSourceMissing"), broken.QuerySelector(".ne-sec-source-broken")!.TextContent.Trim());

        await cut.InvokeAsync(() => cut.Find(".ne-sec-source-row:last-child .ne-sec-source-loosen").Click());

        cut.WaitForAssertion(() => Assert.Contains($"\t\t  id:: {LinkedDetailId}\n\t\t  source:: (({Early}))\n", vault.Read(BookPath)));
        Assert.Equal(LinkedJournal, vault.Read(LinkedJournalPath));
        cut.WaitForAssertion(() => Assert.Equal(["2026-09-15 – Frühe Notiz", "2026-09-15 – Frühe Notiz"], RowTexts(cut)));
    }

    [Fact]
    public async Task Loosen_Undo_Restores()
    {
        using var vault = Linked();
        var cut = await RenderLinkedOpenAsync(vault);
        var session = State.Session!;
        await cut.InvokeAsync(() => cut.FindAll(".ne-sec-source-loosen")[1].Click());
        cut.WaitForAssertion(() => Assert.NotEqual(LinkedBook, vault.Read(BookPath)));

        Assert.Equal("Verknüpfung lösen", session.Undo.NextDescription);
        await cut.InvokeAsync(() => Assert.True(session.TryUndo(out _)));

        Assert.Equal(LinkedBook, vault.Read(BookPath));
        Assert.Equal(LinkedJournal, vault.Read(LinkedJournalPath));
    }

    [Fact]
    public async Task Loosen_OpenConflict_ChangesNothing()
    {
        using var vault = Linked();
        var cut = await RenderLinkedOpenAsync(vault);
        var session = State.Session!;

        // A local change of the note collides with an external change of the same line: a conflict for the note page.
        var path = Path.Combine(vault.Root, LinkedJournalPath);
        await cut.InvokeAsync(() =>
        {
            var note = session.Vault.FindBlockById(Guid.Parse(Early))!.Value;
            note.Block.SetContent("Frühe Notiz, lokal");
            File.WriteAllText(path, LinkedJournal.Replace("- Frühe Notiz\n", "- Frühe Notiz, extern\n"));
            session.HandleExternalChange(path);
        });
        Assert.True(session.HasOpenConflict(path));

        await cut.InvokeAsync(() => cut.FindAll(".ne-sec-source-loosen")[1].Click());

        cut.WaitForAssertion(() => Assert.Equal(Text("EditorChipConflict"), cut.Find(".ne-editor-error").TextContent.Trim()));
        Assert.Equal(LinkedBook, vault.Read(BookPath));
        Assert.Equal(3, cut.FindAll(".ne-sec-source-row").Count);
    }

    [Fact]
    public async Task NoGermanLiterals_InSectionBar()
    {
        Services.AddSingleton<IStringLocalizer<Strings>>(new KeyLocalizer());
        var resources = new ResourceManager(typeof(Strings));
        var cut = await RenderAsync();
        await MoveToAsync(cut, "# Eins");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-sec-sources")));
        cut.Find(".ne-sec-sources").Click();
        Assert.Single(cut.FindAll(".ne-sec-source-broken"));
        AssertKeys(cut.Find(".ne-section-bar"), resources);

        await MoveToAsync(cut, "# Leer");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-sec-no-sources")));
        AssertKeys(cut.Find(".ne-section-bar"), resources);

        // The range buttons, the label of each kind of range head (also without sub-elements) and the range commands.
        Assert.Single(cut.FindAll(".ne-sec-merge"));
        Assert.Single(cut.FindAll(".ne-sec-wrap"));
        await MoveToAsync(cut, "Ein Detail");
        cut.WaitForAssertion(() => Assert.Equal("RangeDetail", cut.Find(".ne-range-label").TextContent));
        AssertKeys(cut.Find(".ne-section-bar"), resources);

        // The status selection with its five options and „Bemerkung anfügen“.
        Assert.Equal("SectionStatus", cut.Find("select.ne-sec-status").GetAttribute("title"));
        Assert.Equal(
            ["StatusDraft", "StatusRelevant", "StatusRemark", "StatusWording", "StatusDone"],
            cut.FindAll("select.ne-sec-status option").Select(option => option.TextContent.Trim()));
        Assert.Equal("SectionAddRemark", cut.Find(".ne-sec-remark").TextContent.Trim());
        cut.Find(".ne-range-down").Click();
        cut.WaitForAssertion(() => Assert.Equal("RangeDetailRangeOwnOnly", cut.Find(".ne-range-label").TextContent));
        AssertKeys(cut.Find(".ne-section-bar"), resources);
        foreach (var label in new[] { "RangeDetail", "RangeTextBlock", "RangeHeading", "RangeBook" })
        {
            cut.Find(".ne-range-up").Click();
            cut.WaitForAssertion(() => Assert.Equal(label, cut.Find(".ne-range-label").TextContent));
            AssertKeys(cut.Find(".ne-section-bar"), resources);
        }
    }

    /// <summary>The resource key of each German button text.</summary>
    private Dictionary<string, string> Keys =>
        new[] { "SectionInsertAfter", "SectionInsertChild", "SectionIndent", "SectionOutdent", "SectionRemoveHeading", "SectionDelete" }
            .ToDictionary(key => Text(key));

    /// <summary>
    /// Every text, title and label in <paramref name="root"/> is a resource key; the sources' labels are data. The range
    /// label's title is its full text, made of the texts of its parts (each checked on its own).
    /// </summary>
    private static void AssertKeys(IElement root, ResourceManager resources)
    {
        foreach (var element in root.QuerySelectorAll("*").Prepend(root))
        {
            if (element.Closest("button.ne-sec-source") is not null)
            {
                continue;
            }

            var rangeLabel = element.ClassList.Contains("ne-range-label");
            if (rangeLabel)
            {
                Assert.Equal(element.TextContent, element.GetAttribute("title"));
            }

            var texts = new[] { rangeLabel ? null : "title", "aria-label", "placeholder" }
                .OfType<string>()
                .Select(element.GetAttribute)
                .OfType<string>()
                .Concat(element.ChildNodes.OfType<IText>().Select(t => t.Data.Trim()))
                .Where(text => text.Length > 0);
            foreach (var text in texts)
            {
                Assert.True(
                    !text.Any(char.IsLetter) || resources.GetString(text, System.Globalization.CultureInfo.InvariantCulture) is not null,
                    $"'{text}' is neither a resource key nor free of letters");
            }
        }
    }

    private sealed class KeyLocalizer : IStringLocalizer<Strings>
    {
        public LocalizedString this[string name] => new(name, name);

        public LocalizedString this[string name, params object[] arguments] => new(name, name);

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
