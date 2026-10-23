using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NoteEvolution.Core.Storage;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components.Dialogs;
using NoteEvolution.UI.Resources;

namespace NoteEvolution.UI.Tests;

/// <summary>
/// The "[handled]" assistant with the AI: the places it suggests in the background and converting with them. The AI keeps
/// the CPU busy, so these run alone, in the collection of the other heavy tests.
/// </summary>
[Collection(ManyNotesCollection.Name)]
public class HandledPlacementTests : UiTestContext
{
    /// <summary>The places are computed with the AI in the background, which can take longer than bUnit's default second.</summary>
    private static readonly TimeSpan HandledWait = TimeSpan.FromSeconds(30);

    private const string NotesPath = "pages/Notizen.md";

    private static readonly UTF8Encoding Utf8 = new(false);

    protected override void Dispose(bool disposing)
    {
        // The session (and its vector cache in the vault) is closed first, then the vault folders are deleted.
        base.Dispose(disposing);
        if (disposing)
        {
            _aiVaults.ForEach(v => v.Dispose());
        }
    }

    private string Text(string key) => Services.GetRequiredService<IStringLocalizer<Strings>>()[key].Value;


    private const string SleepText = "Schlaf und Träume im Alltag";

    private readonly List<TestVault> _aiVaults = [];

    private TestVault PlacementVault()
    {
        var vault = CreatePlacementVault();
        _aiVaults.Add(vault);
        return vault;
    }

    private static TestVault CreatePlacementVault() => TestVault.Create(
        ("pages/Buch - Alpha.md", $"title:: Alpha\ntype:: book\n\n- # Eins\n\t- {SleepText}\n\t- Suppe kochen mit Gemüse\n"),
        ("pages/Buch - Beta.md", "title:: Beta\ntype:: book\n\n- # Zwei\n\t- Musik Gitarre Klavier spielen\n"),
        (NotesPath, "- [handled] Schlaf und Träume\n- [handled] Zwei\n- Drei\n"));

    /// <summary>
    /// Waits up to <see cref="HandledWait"/> until <paramref name="assertion"/> holds for the places the assistant computes
    /// in the background. bUnit can keep the elements of an older render when the render of the places lands while it
    /// reads them (its markup has the places, the elements the assertion finds do not, and it checks no more): every
    /// <see cref="PlaceCheckInterval"/> the elements are compared with the markup, and the component is rendered once
    /// more if they lag behind, so they are read again. A component that did not render the places fails all the same.
    /// </summary>
    private static void WaitForPlaces(IRenderedComponent<HandledWizard> cut, Action assertion)
    {
        var until = DateTime.UtcNow + HandledWait;
        while (true)
        {
            try
            {
                cut.WaitForAssertion(assertion, PlaceCheckInterval);
                return;
            }
            catch (Bunit.Extensions.WaitForHelpers.WaitForFailedException) when (DateTime.UtcNow < until)
            {
                var rendered = new HtmlParser().ParseDocument(cut.Markup).Body!.TextContent;
                if (rendered != string.Concat(cut.Nodes.Select(node => node.TextContent)))
                {
                    cut.Render();
                }
            }
        }
    }

    private static readonly TimeSpan PlaceCheckInterval = TimeSpan.FromMilliseconds(200);

    private static IElement Place(IRenderedComponent<HandledWizard> cut, int item) =>
        cut.FindAll(".ne-handled-item")[item].QuerySelector(".ne-handled-place")!;

    private static string OptionFor(IElement place, string startsWith) =>
        place.QuerySelectorAll("option").First(o => o.TextContent.StartsWith(startsWith, StringComparison.Ordinal)).GetAttribute("value")!;

    [Fact]
    public async Task Handled_PlacementChosen_FullLinkWritten()
    {
        var tv = PlacementVault();
        var session = await OpenWithAiAsync(tv);
        var cut = Render<HandledWizard>();
        WaitForPlaces(cut, () => Assert.Equal(2, cut.FindAll(".ne-handled-place").Count));
        var place = Place(cut, 0);
        var options = place.QuerySelectorAll("option").ToList();
        Assert.Equal(3, options.Count);
        Assert.Equal("", options[0].GetAttribute("value"));
        Assert.Equal(Text("HandledPlaceUnknown"), options[0].TextContent);
        Assert.All(options.Skip(1), o => Assert.Matches(@" – \d{1,3} %$", o.TextContent));

        place.Change(OptionFor(place, SleepText));
        DialogTests.CheckAll(cut);
        cut.Find(".ne-handled-convert").Click();
        cut.WaitForElement(".ne-handled-yes").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-handled-done")), HandledWait);
        var notes = tv.Read(NotesPath);
        Assert.DoesNotContain("[handled] Schlaf", notes);
        Assert.Matches(@"- Schlaf und Träume\n  id:: [0-9a-f-]{36}\n  used-in:: \[\[Buch - Alpha\]\] \(\([0-9a-f-]{36}\)\)\n", notes);
        Assert.Contains("- Zwei\n  used-in:: [[Buch - Alpha]]\n", notes);
        Assert.Contains("source:: ((", tv.Read("pages/Buch - Alpha.md"));
        Assert.Same(session.Vault.FindBook("Buch - Alpha"), State.CurrentBook);
        Assert.NotNull(State.CurrentBook!.Root.Children.Single().TextBlocks.First().Block.Id);
    }

    [Fact]
    public async Task Handled_ChoosingAPlaceChecksTheBox_UnknownUnchecksIt_ManualToggleStaysFree()
    {
        var tv = PlacementVault();
        await OpenWithAiAsync(tv);
        var cut = Render<HandledWizard>();
        WaitForPlaces(cut, () => Assert.Equal(2, cut.FindAll(".ne-handled-place").Count));
        bool Checked(int item) => cut.FindAll(".ne-handled-item input[type=checkbox]")[item].HasAttribute("checked");
        Assert.False(Checked(0));

        var place = Place(cut, 0);
        place.Change(OptionFor(place, SleepText));
        Assert.True(Checked(0));
        Assert.False(Checked(1));

        cut.FindAll(".ne-handled-item input[type=checkbox]")[0].Change(false);
        Assert.False(Checked(0));

        cut.FindAll(".ne-handled-item input[type=checkbox]")[0].Change(true);
        Assert.True(Checked(0));

        Place(cut, 0).Change("");
        Assert.False(Checked(0));
    }

    [Fact]
    public async Task Handled_ConvertingSomeNotes_KeepsTheChoicesOfTheOthers()
    {
        var tv = PlacementVault();
        await OpenWithAiAsync(tv);
        var cut = Render<HandledWizard>();
        WaitForPlaces(cut, () => Assert.Equal(2, cut.FindAll(".ne-handled-place").Count));
        var first = Place(cut, 0);
        first.Change(OptionFor(first, SleepText));
        cut.FindAll(".ne-handled-item input[type=checkbox]")[0].Change(false);
        var secondPlace = Place(cut, 1);
        secondPlace.Change(secondPlace.QuerySelectorAll("option").Skip(1).First().GetAttribute("value")!);

        cut.Find(".ne-handled-convert").Click();
        cut.WaitForElement(".ne-handled-yes").Click();

        cut.WaitForAssertion(() => Assert.Equal("1 umgewandelt", cut.Find(".ne-handled-done").TextContent), HandledWait);
        Assert.Single(cut.FindAll(".ne-handled-item"));
        WaitForPlaces(cut, () => Assert.Contains(SleepText, cut.Find(".ne-handled-preview-text").TextContent));
        Assert.False(cut.Find(".ne-handled-item input[type=checkbox]").HasAttribute("checked"));
    }

    [Fact]
    public async Task Handled_ShowsTheFullNoteWithSubPoints()
    {
        using var tv = TestVault.Create(
            ("pages/Buch - Alpha.md", "title:: Alpha\ntype:: book\n\n- # Eins\n"),
            (NotesPath, "- [handled] Hauptpunkt\n\t- Unterpunkt A\n\t\t- Tiefer B\n- Drei\n"));
        await OpenSessionAsync(tv);
        var cut = Render<HandledWizard>();

        var note = cut.Find(".ne-handled-note");
        Assert.True(note.HasAttribute("open"));
        Assert.Contains("Hauptpunkt", note.TextContent);
        Assert.Contains("Unterpunkt A", note.TextContent);
        Assert.Contains("Tiefer B", note.TextContent);
        Assert.Empty(cut.FindAll(".ne-handled-preview"));
    }

    [Fact]
    public async Task Handled_ChosenPlace_ShowsItsTextAndPath()
    {
        var tv = PlacementVault();
        await OpenWithAiAsync(tv);
        var cut = Render<HandledWizard>();
        WaitForPlaces(cut, () => Assert.Equal(2, cut.FindAll(".ne-handled-place").Count));
        Assert.Empty(cut.FindAll(".ne-handled-preview"));

        var place = Place(cut, 0);
        place.Change(OptionFor(place, SleepText));

        var preview = cut.Find(".ne-handled-preview");
        Assert.Equal(SleepText, preview.QuerySelector(".ne-handled-preview-text")!.TextContent);
        Assert.Equal("Eins", preview.QuerySelector(".ne-handled-preview-path")!.TextContent);
    }

    [Fact]
    public async Task Handled_ChangingThePlace_UpdatesThePreview_UnknownClearsIt()
    {
        var tv = PlacementVault();
        await OpenWithAiAsync(tv);
        var cut = Render<HandledWizard>();
        WaitForPlaces(cut, () => Assert.Equal(2, cut.FindAll(".ne-handled-place").Count));

        Place(cut, 0).Change(OptionFor(Place(cut, 0), SleepText));
        Assert.Equal(SleepText, cut.Find(".ne-handled-preview-text").TextContent);

        Place(cut, 0).Change(OptionFor(Place(cut, 0), "Suppe kochen"));
        Assert.Equal("Suppe kochen mit Gemüse", cut.Find(".ne-handled-preview-text").TextContent);

        Place(cut, 0).Change("");
        Assert.Empty(cut.FindAll(".ne-handled-preview"));
    }

    [Fact]
    public async Task Handled_LongPlaceText_IsCutAt400()
    {
        var tv = TestVault.Create(
            ("pages/Buch - Alpha.md", $"title:: Alpha\ntype:: book\n\n- # Eins\n\t- {SleepText} {new string('x', 600)}\n"),
            (NotesPath, "- [handled] Schlaf und Träume\n- Drei\n"));
        _aiVaults.Add(tv);
        await OpenWithAiAsync(tv);
        var cut = Render<HandledWizard>();
        WaitForPlaces(cut, () => Assert.Single(cut.FindAll(".ne-handled-place")));

        Place(cut, 0).Change(OptionFor(Place(cut, 0), SleepText));

        var text = cut.Find(".ne-handled-preview-text").TextContent;
        Assert.Equal(400, text.Length);
        Assert.EndsWith("…", text);
    }

    [Fact]
    public async Task Handled_DefaultUnknownPlace_AsBefore()
    {
        var tv = PlacementVault();
        await OpenWithAiAsync(tv);
        var cut = Render<HandledWizard>();
        WaitForPlaces(cut, () => Assert.Equal(2, cut.FindAll(".ne-handled-place").Count));

        DialogTests.CheckAll(cut);
        cut.Find(".ne-handled-convert").Click();
        cut.WaitForElement(".ne-handled-yes").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-handled-done")), HandledWait);
        Assert.Equal(
            "- Schlaf und Träume\n  used-in:: [[Buch - Alpha]]\n- Zwei\n  used-in:: [[Buch - Alpha]]\n- Drei\n", tv.Read(NotesPath));
        Assert.DoesNotContain("source::", tv.Read("pages/Buch - Alpha.md"));
    }

    [Fact]
    public async Task Handled_WithoutAi_NoPlacementColumn()
    {
        var tv = PlacementVault();
        await OpenSessionAsync(tv);
        var cut = Render<HandledWizard>();

        Assert.Equal(2, cut.FindAll(".ne-handled-item").Count);
        Assert.Empty(cut.FindAll(".ne-handled-place"));
    }

    [Fact]
    public async Task Handled_BookChange_RecomputesPlacementsAndForgetsTheChoice()
    {
        var tv = PlacementVault();
        await OpenWithAiAsync(tv);
        var cut = Render<HandledWizard>();
        WaitForPlaces(cut, () => Assert.Equal(2, cut.FindAll(".ne-handled-place").Count));
        var place = Place(cut, 0);
        place.Change(OptionFor(place, SleepText));

        cut.Find(".ne-handled-book-select").Change("Buch - Beta");

        WaitForPlaces(
            cut, () => Assert.Contains(Place(cut, 0).QuerySelectorAll("option"), o => o.TextContent.StartsWith("Musik Gitarre", StringComparison.Ordinal)));
        Assert.DoesNotContain(Place(cut, 0).QuerySelectorAll("option"), o => o.TextContent.StartsWith(SleepText, StringComparison.Ordinal));
        DialogTests.CheckAll(cut);
        cut.Find(".ne-handled-convert").Click();
        cut.WaitForElement(".ne-handled-yes").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-handled-done")), HandledWait);
        Assert.Equal(
            "- Schlaf und Träume\n  used-in:: [[Buch - Beta]]\n- Zwei\n  used-in:: [[Buch - Beta]]\n- Drei\n", tv.Read(NotesPath));
    }

    [Fact]
    public async Task Handled_PlacementChosen_OpenConflictOnTheBookPage_IsRefused()
    {
        var tv = PlacementVault();
        var session = await OpenWithAiAsync(tv);
        var cut = Render<HandledWizard>();
        WaitForPlaces(cut, () => Assert.Equal(2, cut.FindAll(".ne-handled-place").Count));
        var place = Place(cut, 0);
        place.Change(OptionFor(place, SleepText));
        var path = Path.Combine(tv.Root, "pages/Buch - Alpha.md");
        session.Vault.FindPageByPath(path)!.Roots[0].SetContent("# Lokal");
        const string external = "title:: Alpha\ntype:: book\n\n- # Extern\n\t- Ein Text\n";
        File.WriteAllText(path, external, Utf8);
        session.HandleExternalChange(path);
        Assert.True(session.HasOpenConflict(path));
        var before = tv.Read(NotesPath);

        DialogTests.CheckAll(cut);
        cut.Find(".ne-handled-convert").Click();
        cut.WaitForElement(".ne-handled-yes").Click();

        cut.WaitForAssertion(() => Assert.Equal(Text("AssistantConflict"), cut.Find(".ne-dialog-error").TextContent));
        Assert.Equal(before, tv.Read(NotesPath));
        Assert.Equal(external, tv.Read("pages/Buch - Alpha.md"));
    }

    [Fact]
    public async Task Handled_PlacementVanished_ShowsErrorAndStaysOpen()
    {
        var tv = PlacementVault();
        var session = await OpenWithAiAsync(tv);
        var cut = Render<HandledWizard>();
        WaitForPlaces(cut, () => Assert.Equal(2, cut.FindAll(".ne-handled-place").Count));
        var place = Place(cut, 0);
        place.Change(OptionFor(place, SleepText));
        var path = Path.Combine(tv.Root, "pages/Buch - Alpha.md");
        const string external = "title:: Alpha\ntype:: book\n\n- # Eins\n\t- Etwas ganz anderes\n";
        File.WriteAllText(path, external, Utf8);
        session.HandleExternalChange(path);
        DialogTests.CheckAll(cut);
        cut.Find(".ne-handled-convert").Click();
        cut.WaitForElement(".ne-handled-yes").Click();

        cut.WaitForAssertion(() => Assert.Equal(Text("HandledPlacementFailed"), cut.Find(".ne-dialog-error").TextContent));
        // The failure is per note: the one without a place is converted, only the one whose place vanished stays.
        Assert.Equal("1 umgewandelt, 1 fehlgeschlagen", cut.Find(".ne-handled-done").TextContent);
        Assert.Single(cut.FindAll(".ne-handled-item"));
        Assert.Contains("Schlaf", cut.Find(".ne-handled-item").TextContent);
        Assert.Equal("- [handled] Schlaf und Träume\n- Zwei\n  used-in:: [[Buch - Alpha]]\n- Drei\n", tv.Read(NotesPath));

        // The places are suggested again for the book as it is now; the choice is back to "Stelle unbekannt".
        WaitForPlaces(cut, () =>
        {
            var options = Place(cut, 0).QuerySelectorAll("option").Select(o => o.TextContent).ToList();
            Assert.DoesNotContain(options, o => o.StartsWith(SleepText, StringComparison.Ordinal));
            Assert.Contains(options, o => o.StartsWith("Etwas ganz anderes", StringComparison.Ordinal));
        });
        cut.Find(".ne-handled-item input[type=checkbox]").Change(true);
        cut.Find(".ne-handled-convert").Click();
        cut.WaitForElement(".ne-handled-yes").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-handled-done")), HandledWait);
        Assert.Equal(
            "- Schlaf und Träume\n  used-in:: [[Buch - Alpha]]\n- Zwei\n  used-in:: [[Buch - Alpha]]\n- Drei\n", tv.Read(NotesPath));
    }

    [Fact]
    public async Task Handled_ConvertWaitsForTheCarriedOverPlaces_ThenKeepsTheChoice()
    {
        var tv = PlacementVault();
        var session = await OpenWithAiAsync(tv);
        using var gate = new GatedRelevance(session.Relevance);
        session.WrapRelevance = _ => gate;
        var cut = Render<HandledWizard>();
        WaitForPlaces(cut, () => Assert.Equal(2, cut.FindAll(".ne-handled-place").Count));
        var first = Place(cut, 0);
        first.Change(OptionFor(first, SleepText));
        cut.FindAll(".ne-handled-item input[type=checkbox]")[0].Change(false);
        var second = Place(cut, 1);
        second.Change(second.QuerySelectorAll("option").Skip(1).First().GetAttribute("value")!);

        // The places computed again after the run are held back until the gate opens.
        gate.Hold();
        cut.Find(".ne-handled-convert").Click();
        cut.WaitForElement(".ne-handled-yes").Click();
        cut.WaitForAssertion(() => Assert.Equal("1 umgewandelt", cut.Find(".ne-handled-done").TextContent), HandledWait);
        cut.WaitForAssertion(() => Assert.True(gate.Waiting > 0), HandledWait);

        // Check the remaining note: while its places are computed again nothing can be converted.
        cut.Find(".ne-handled-item input[type=checkbox]").Change(true);
        cut.WaitForAssertion(() => Assert.True(cut.Find(".ne-handled-convert").HasAttribute("disabled")));
        Assert.True(gate.Waiting > 0);

        // Once they are back, "Umwandeln" can be used and the choice made before the run is kept.
        gate.Release();
        WaitForPlaces(cut, () => Assert.False(cut.Find(".ne-handled-convert").HasAttribute("disabled")));
        Assert.Contains(SleepText, cut.Find(".ne-handled-preview-text").TextContent);
        Assert.Single(cut.FindAll(".ne-handled-place"));
    }

    [Fact]
    public async Task Handled_FailedBookSave_KeysCarriedOver_NextNoteIsTriedAgainstTheReloadedPage()
    {
        const string second = "pages/Zweit.md";
        var tv = TestVault.Create(
            ("pages/Buch - Alpha.md", $"title:: Alpha\ntype:: book\n\n- # Eins\n	- {SleepText}\n	- Suppe kochen mit Gemüse\n"),
            (NotesPath, "- [handled] Schlaf und Träume\n"),
            (second, "- [handled] Suppe kochen\n"));
        _aiVaults.Add(tv);
        var session = await OpenWithAiAsync(tv);
        var cut = Render<HandledWizard>();
        WaitForPlaces(cut, () => Assert.Equal(2, cut.FindAll(".ne-handled-place").Count));
        foreach (var item in new[] { 0, 1 })
        {
            var place = Place(cut, item);
            place.Change(place.QuerySelectorAll("option").Skip(1).First().GetAttribute("value")!);
        }

        // The book file changed behind the app's back: the first save of the book is refused (and the page replaced),
        // the next note must be linked to the book as it is now.
        var bookPath = Path.Combine(tv.Root, "pages", "Buch - Alpha.md");
        File.AppendAllText(bookPath, "\n", Utf8);
        cut.Find(".ne-handled-convert").Click();
        cut.WaitForElement(".ne-handled-yes").Click();

        // The first save of the book is refused and its page taken from the file again, its blocks keeping their keys:
        // the second note's place is still valid and it is tried (spec 7), linked to the book as it is now, not to a
        // detached page. Vault and disk agree.
        cut.WaitForAssertion(() => Assert.Equal("1 umgewandelt, 1 fehlgeschlagen", cut.Find(".ne-handled-done").TextContent), HandledWait);
        var book = session.Vault.FindBook("Buch - Alpha")!;
        var bookText = tv.Read("pages/Buch - Alpha.md");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(bookText, "source::"));
        Assert.Matches(@"\t- Suppe kochen mit Gemüse\n\t  id:: [0-9a-f-]{36}\n\t  source:: \(\([0-9a-f-]{36}\)\)\n", bookText);
        Assert.Same(book.Page, session.Vault.FindPageByPath(bookPath));
        Assert.Contains(book.Page.Roots.SelectMany(Flatten), b => b.Content == "Suppe kochen mit Gemüse" && b.Id is not null);
        Assert.Equal("- [handled] Schlaf und Träume\n", tv.Read(NotesPath));
        Assert.DoesNotContain("[handled]", tv.Read(second));
        Assert.Single(cut.FindAll(".ne-handled-item"));
        Assert.Contains("Schlaf", cut.Find(".ne-handled-item").TextContent);
    }

    private static IEnumerable<NoteEvolution.Core.Model.Block> Flatten(NoteEvolution.Core.Model.Block block) =>
        new[] { block }.Concat(block.Children.SelectMany(Flatten));
}
