using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components.Dialogs;
using NoteEvolution.UI.Resources;

namespace NoteEvolution.UI.Tests;

/// <summary>The windows "Glossar", "Menschen" and "Quellen": entries are listed, edited, saved with undo and deleted.</summary>
public class AppendixDialogTests : UiTestContext
{
    private const string BookPath = "pages/Buch - Alpha.md";

    private const string AnnaId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1";

    private const string SourceId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb1";

    private const string Head = "title:: Alpha\ntype:: book\n\n- # Eins\n\t- Drei Wörter hier\n";

    private const string GlossaryText =
        Head +
        "- # Glossar\n  anhang:: glossar\n" +
        "\t- Achtsamkeit\n\t  Bewusstes Wahrnehmen.\n\t  id:: cccccccc-cccc-4ccc-8ccc-ccccccccccc1\n" +
        "\t- Resilienz\n\t  id:: cccccccc-cccc-4ccc-8ccc-ccccccccccc2\n";

    private const string PeopleAndSourcesText =
        Head +
        "- # Menschen, die mich inspiriert haben\n  anhang:: menschen\n" +
        $"\t- Anna Berg\n\t  kuerzel:: AB\n\t  id:: {AnnaId}\n" +
        "- # Quellen\n  anhang:: quellen\n" +
        $"\t- Achtsam leben\n\t  art:: buch\n\t  autoren:: (({AnnaId}))\n\t  id:: {SourceId}\n";

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

    private string Text(string key) => Services.GetRequiredService<IStringLocalizer<Strings>>()[key].Value;

    private async Task<TestVault> OpenAsync(string bookText)
    {
        var vault = TestVault.Create((BookPath, bookText));
        _vaults.Add(vault);
        await OpenSessionAsync(vault);
        return vault;
    }

    private IRenderedComponent<AppendixDialog> RenderDialog(
        AppendixKind kind, Action? onClose = null, Action<Guid>? onCreated = null, bool startWithNew = false) =>
        Render<AppendixDialog>(ps =>
        {
            ps.Add(p => p.Kind, kind);
            ps.Add(p => p.StartWithNew, startWithNew);
            ps.Add(p => p.OnClose, () => onClose?.Invoke());
            ps.Add(p => p.OnCreated, (Guid id) => onCreated?.Invoke(id));
        });

    private static async Task CloseAsync(IRenderedComponent<AppendixDialog> cut) =>
        await cut.InvokeAsync(() => cut.Find(".ne-modal-close").Click());

    [Fact]
    public async Task Glossary_CreateEditSave_FileHasEntry_UndoRestores()
    {
        var vault = await OpenAsync(GlossaryText);
        var closed = false;
        var cut = RenderDialog(AppendixKind.Glossary, () => closed = true);
        Assert.Equal(["Achtsamkeit", "Resilienz"], cut.FindAll(".ne-appendix-list button").Select(b => b.TextContent));

        cut.Find(".ne-appendix-new").Click();
        cut.Find(".ne-appendix-term").Change("Gelassenheit");
        cut.Find(".ne-appendix-forms").Change("gelassen, Gelassen");
        cut.Find(".ne-appendix-explanation").Change("Innere Ruhe.");
        await CloseAsync(cut);

        Assert.True(closed);
        var file = vault.Read(BookPath);
        Assert.Contains("\t- Gelassenheit\n", file);
        Assert.Contains("formen:: gelassen, Gelassen", file);
        Assert.Contains("Innere Ruhe.", file);
        Assert.Contains("- Achtsamkeit\n", file);
        Assert.Equal(3, Appendix.Glossary(State.CurrentBook!).Count);

        Assert.Equal(Text("UndoAppendix"), State.Session!.Undo.NextDescription);
        State.Session.Undo.Undo();

        Assert.Equal(GlossaryText, vault.Read(BookPath));
    }

    [Fact]
    public async Task People_AbbreviationSuggested_TakenIsRefused()
    {
        var vault = await OpenAsync(PeopleAndSourcesText);
        var closed = false;
        var cut = RenderDialog(AppendixKind.People, () => closed = true);

        cut.Find(".ne-appendix-new").Click();
        cut.Find(".ne-appendix-name").Change("Albert Bauer");
        // AB is taken by Anna Berg, so the suggestion counts on.
        Assert.Equal("AB2", cut.Find(".ne-appendix-abbreviation").GetAttribute("value"));

        cut.Find(".ne-appendix-abbreviation").Change("ab");
        await CloseAsync(cut);

        Assert.False(closed);
        Assert.Equal(Text("AppendixAbbreviationTaken"), cut.Find(".ne-dialog-error").TextContent);
        Assert.Equal(PeopleAndSourcesText, vault.Read(BookPath));

        cut.Find(".ne-appendix-abbreviation").Change("Bauer!");
        await CloseAsync(cut);

        Assert.False(closed);
        Assert.Equal(Text("AppendixAbbreviationInvalid"), cut.Find(".ne-dialog-error").TextContent);

        cut.Find(".ne-appendix-abbreviation").Change("AlB");
        await CloseAsync(cut);

        Assert.True(closed);
        var person = Appendix.People(State.CurrentBook!).Single(p => p.Name == "Albert Bauer");
        Assert.Equal("AlB", person.Abbreviation);
        Assert.Contains("kuerzel:: AlB", vault.Read(BookPath));
    }

    [Fact]
    public async Task Sources_UnknownAuthorCreated_KnownLinked()
    {
        var vault = await OpenAsync(PeopleAndSourcesText);
        var cut = RenderDialog(AppendixKind.Sources);

        cut.Find(".ne-appendix-new").Click();
        cut.Find(".ne-appendix-kind").Change("Video");
        cut.Find(".ne-appendix-title").Change("Atmen lernen");
        cut.Find(".ne-appendix-authors").Change("anna berg, Neue Person");
        cut.Find(".ne-appendix-year").Change("2021");
        await CloseAsync(cut);

        var book = State.CurrentBook!;
        var people = Appendix.People(book);
        Assert.Equal(["Anna Berg", "Neue Person"], people.Select(p => p.Name));
        Assert.Equal("NP", people[1].Abbreviation);
        var source = Appendix.Sources(book).Single(s => s.Title == "Atmen lernen");
        Assert.Equal(SourceKind.Video, source.Kind);
        Assert.Equal([people[0].Id, people[1].Id], source.AuthorIds);
        Assert.Equal(Guid.Parse(AnnaId), people[0].Id);
        Assert.Equal("2021", source.Year);
        Assert.Empty(AppendixIndex.Of(book).MissingTargets);
        Assert.Contains("kuerzel:: NP", vault.Read(BookPath));
    }

    [Fact]
    public async Task Delete_Referenced_AsksWithCount_PersonAsAuthor()
    {
        var vault = await OpenAsync(PeopleAndSourcesText);
        var cut = RenderDialog(AppendixKind.People);
        cut.Find(".ne-appendix-list button").Click();

        cut.Find(".ne-appendix-delete").Click();

        Assert.Equal(
            "1 Verweise im Buch werden zu ‚Verweis fehlt‘. Trotzdem löschen?",
            cut.Find(".ne-appendix-confirm span").TextContent);
        Assert.Equal(PeopleAndSourcesText, vault.Read(BookPath));

        cut.Find(".ne-appendix-delete-confirm").Click();

        var book = State.CurrentBook!;
        Assert.Empty(Appendix.People(book));
        var source = Assert.Single(Appendix.Sources(book));
        var missing = Assert.Single(AppendixIndex.Of(book).MissingTargets);
        Assert.Equal((source.Key, Guid.Parse(AnnaId)), missing);
        Assert.DoesNotContain("Anna Berg", vault.Read(BookPath));
        Assert.Contains("Achtsam leben", vault.Read(BookPath));
    }

    [Fact]
    public async Task Delete_Unreferenced_NoQuestion()
    {
        var vault = await OpenAsync(GlossaryText);
        var cut = RenderDialog(AppendixKind.Glossary);
        cut.FindAll(".ne-appendix-list button")[1].Click();

        cut.Find(".ne-appendix-delete").Click();

        Assert.Empty(cut.FindAll(".ne-appendix-confirm"));
        Assert.DoesNotContain("Resilienz", vault.Read(BookPath));
    }

    [Fact]
    public async Task ReadOnlyBook_RefusesSave()
    {
        var text = GlossaryText + "- ```\n  offen\n";
        var vault = await OpenAsync(text);
        Assert.True(State.CurrentBook!.Page.IsReadOnly);
        var closed = false;
        var cut = RenderDialog(AppendixKind.Glossary, () => closed = true);

        cut.Find(".ne-appendix-new").Click();
        cut.Find(".ne-appendix-term").Change("Gelassenheit");
        await CloseAsync(cut);

        Assert.False(closed);
        Assert.Equal(Text("EditorReadOnly"), cut.Find(".ne-dialog-error").TextContent);
        Assert.Equal(text, vault.Read(BookPath));
    }

    [Fact]
    public async Task SwitchingEntries_SavesThePrevious()
    {
        var vault = await OpenAsync(GlossaryText);
        var cut = RenderDialog(AppendixKind.Glossary);

        cut.FindAll(".ne-appendix-list button")[0].Click();
        Assert.Equal("Achtsamkeit", cut.Find(".ne-appendix-term").GetAttribute("value"));
        cut.Find(".ne-appendix-term").Change("Achtsamkeit (Praxis)");
        cut.FindAll(".ne-appendix-list button")[1].Click();

        Assert.Contains("\t- Achtsamkeit (Praxis)\n", vault.Read(BookPath));
        Assert.Equal("Resilienz", cut.Find(".ne-appendix-term").GetAttribute("value"));
        Assert.Equal(["Achtsamkeit (Praxis)", "Resilienz"], cut.FindAll(".ne-appendix-list button").Select(b => b.TextContent));
    }

    [Fact]
    public async Task SwitchingEntries_InvalidPrevious_StaysAndShowsTheText()
    {
        var vault = await OpenAsync(GlossaryText);
        var cut = RenderDialog(AppendixKind.Glossary);
        cut.FindAll(".ne-appendix-list button")[0].Click();
        cut.Find(".ne-appendix-term").Change("");

        cut.FindAll(".ne-appendix-list button")[1].Click();

        Assert.Equal(Text("AppendixTermEmpty"), cut.Find(".ne-dialog-error").TextContent);
        Assert.Equal("", cut.Find(".ne-appendix-term").GetAttribute("value"));
        Assert.Equal(GlossaryText, vault.Read(BookPath));
    }

    [Fact]
    public async Task NewEntry_OnCreated_GetsTheKeyAfterTheFirstSave()
    {
        await OpenAsync(GlossaryText);
        var created = new List<Guid>();
        var cut = RenderDialog(AppendixKind.Glossary, onCreated: created.Add, startWithNew: true);

        cut.Find(".ne-appendix-term").Change("Gelassenheit");
        await CloseAsync(cut);

        var entry = Appendix.Glossary(State.CurrentBook!).Single(e => e.Term == "Gelassenheit");
        Assert.NotEqual(Guid.Empty, entry.Id);
        Assert.Equal([entry.Key], created);
    }

    [Fact]
    public async Task List_GlossaryAndPeopleAlphabetical_SourcesByNumberThenTitle()
    {
        const string ZId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb2";
        const string MId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb3";
        // In the text Z is cited first (number 1), then M (2); the other sources are not cited.
        var text =
            "title:: Alpha\ntype:: book\n\n- # Eins\n" +
            $"\t- Erst {{{{quelle (({ZId}))}}}} dann {{{{quelle (({MId}))}}}}\n" +
            "- # Glossar\n  anhang:: glossar\n\t- Zuversicht\n\t- Übermut\n\t- achtsamkeit\n\t- Mut\n" +
            "- # Menschen, die mich inspiriert haben\n  anhang:: menschen\n\t- Zoe Zeh\n\t- Ömer Öz\n\t- Anna Berg\n" +
            "- # Quellen\n  anhang:: quellen\n" +
            "\t- Wenig zitiert\n" +
            $"\t- Mitte\n\t  id:: {MId}\n" +
            "\t- Anfang ohne Zitat\n" +
            $"\t- Zuerst zitiert\n\t  id:: {ZId}\n";
        await OpenAsync(text);

        Assert.Equal(
            ["achtsamkeit", "Mut", "Übermut", "Zuversicht"],
            RenderDialog(AppendixKind.Glossary).FindAll(".ne-appendix-list button").Select(b => b.TextContent));
        Assert.Equal(
            ["Anna Berg", "Ömer Öz", "Zoe Zeh"],
            RenderDialog(AppendixKind.People).FindAll(".ne-appendix-list button").Select(b => b.TextContent));
        Assert.Equal(
            ["Zuerst zitiert", "Mitte", "Anfang ohne Zitat", "Wenig zitiert"],
            RenderDialog(AppendixKind.Sources).FindAll(".ne-appendix-list button").Select(b => b.TextContent));
    }

    [Fact]
    public async Task Sources_AuthorWithoutId_IsLinkedByName_GetsItsId_NoDuplicate()
    {
        var text = Head +
                   "- # Menschen, die mich inspiriert haben\n  anhang:: menschen\n\t- Anna Berg\n\t  kuerzel:: AB\n";
        var vault = await OpenAsync(text);
        var cut = RenderDialog(AppendixKind.Sources);

        cut.Find(".ne-appendix-new").Click();
        cut.Find(".ne-appendix-title").Change("Atmen lernen");
        cut.Find(".ne-appendix-authors").Change("Anna Berg");
        await CloseAsync(cut);

        var book = State.CurrentBook!;
        var anna = Assert.Single(Appendix.People(book));
        Assert.NotEqual(Guid.Empty, anna.Id);
        Assert.Equal([anna.Id], Appendix.Sources(book).Single().AuthorIds);
        Assert.Empty(AppendixIndex.Of(book).MissingTargets);
        Assert.Contains($"\t- Anna Berg\n\t  kuerzel:: AB\n\t  id:: {anna.Id:D}\n", vault.Read(BookPath));

        // One undo takes the source and the person's new id back.
        State.Session!.Undo.Undo();
        Assert.Equal(text, vault.Read(BookPath));
    }

    [Fact]
    public async Task NewEntry_LeftEmpty_IsNotSaved()
    {
        var vault = await OpenAsync(GlossaryText);
        var closed = false;
        var cut = RenderDialog(AppendixKind.Glossary, () => closed = true, startWithNew: true);

        await CloseAsync(cut);

        Assert.True(closed);
        Assert.Equal(GlossaryText, vault.Read(BookPath));
    }

    [Fact]
    public async Task Search_FiltersTheList()
    {
        await OpenAsync(GlossaryText);
        var cut = RenderDialog(AppendixKind.Glossary);

        cut.Find(".ne-appendix-search").Input("resi");

        Assert.Equal(["Resilienz"], cut.FindAll(".ne-appendix-list button").Select(b => b.TextContent));
    }

    [Fact]
    public async Task Shell_ShowsTheDialogOnRequest()
    {
        await OpenAsync(GlossaryText);
        var cut = Render<NoteEvolution.UI.Components.Shell>();

        State.RequestAppendixDialog(AppendixKind.Glossary);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".ne-appendix-dialog")));
    }
}
