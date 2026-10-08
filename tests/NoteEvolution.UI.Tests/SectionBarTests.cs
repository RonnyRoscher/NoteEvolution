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
        [".ne-sec-after", ".ne-sec-child", ".ne-sec-indent", ".ne-sec-outdent", ".ne-sec-remove-heading", ".ne-sec-delete"];

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

    /// <summary>Opens the vault, renders the editor and lets it report a visible marking box.</summary>
    private async Task<IRenderedComponent<EditorPane>> RenderAsync()
    {
        await OpenSessionAsync(_vault);
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
            ["SectionInsertAfter", "SectionInsertChild", "SectionIndent", "SectionOutdent", "SectionRemoveHeading", "SectionDelete"],
            AllButtons.Select(selector => cut.Find(selector).TextContent.Trim()).Select(text => Keys[text]));

        await MoveToAsync(cut, "Erster Text");
        cut.WaitForAssertion(() => Assert.Equal([".ne-sec-after", ".ne-sec-child", ".ne-sec-delete"], ShownButtons(cut)));

        await MoveToAsync(cut, "Ein Detail");
        cut.WaitForAssertion(() => Assert.Equal([".ne-sec-after", ".ne-sec-child", ".ne-sec-delete"], ShownButtons(cut)));
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
        Assert.True(cut.Find(".ne-sec-outdent").HasAttribute("disabled"));
        Assert.All(AllButtons.Where(s => s != ".ne-sec-outdent"), s => Assert.False(cut.Find(s).HasAttribute("disabled")));

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

        Assert.Equal(BookText.Replace("\t\t- Ein Detail\n", "\t\t- Ein Detail\n\t-\n"), _vault.Read(BookPath));
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
        var entries = cut.Find(".ne-sec-source-list").Children;
        Assert.Equal(
            ["2026-09-15 – Frühe Notiz", "2026-10-01 – Späte Notiz", "0 Archiv – Seitennotiz", Text("SectionSourceMissing")],
            entries.Select(e => e.TextContent.Trim()));
        Assert.Equal(["BUTTON", "BUTTON", "BUTTON", "SPAN"], entries.Select(e => e.TagName));
        Assert.Equal(3, cut.FindAll("button.ne-sec-source").Count);
        Assert.Single(cut.FindAll("span.ne-sec-source-broken"));
        Assert.Empty(cut.FindAll("button.ne-sec-source-broken"));

        cut.Find(".ne-sec-sources").Click();

        Assert.Equal(Text("SectionSources", 4), cut.Find(".ne-sec-sources").TextContent.Trim());
        Assert.Empty(cut.FindAll(".ne-sec-source-list"));
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
    }

    /// <summary>The resource key of each German button text.</summary>
    private Dictionary<string, string> Keys =>
        new[] { "SectionInsertAfter", "SectionInsertChild", "SectionIndent", "SectionOutdent", "SectionRemoveHeading", "SectionDelete" }
            .ToDictionary(key => Text(key));

    /// <summary>Every text, title and label in <paramref name="root"/> is a resource key; the sources' labels are data.</summary>
    private static void AssertKeys(IElement root, ResourceManager resources)
    {
        foreach (var element in root.QuerySelectorAll("*").Prepend(root))
        {
            if (element.Closest("button.ne-sec-source") is not null)
            {
                continue;
            }

            var texts = new[] { "title", "aria-label", "placeholder" }
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
