using System.Resources;
using System.Text;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Links;
using NoteEvolution.Core.Storage;
using NoteEvolution.Pdf;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Components.Dialogs;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

public class DialogTests : UiTestContext
{
    private const string JournalPath = "journals/2026_03_01.md";
    private const string NotesPath = "pages/Notizen.md";
    private const string IdoenPath = "pages/Ideen.md";
    private const string NoteId = "11111111-1111-4111-8111-111111111111";
    private const string BlockOne = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb1";
    private const string BlockTwo = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb2";
    private const string OrphanBlock = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb9";
    private const string OrphanNote = "22222222-2222-4222-8222-222222222222";
    private const string MissingNote = "33333333-3333-4333-8333-333333333333";
    private const string GoneNote = "44444444-4444-4444-8444-444444444444";

    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly FakePdfExporter _exporter = new();
    private readonly TempDir _out = new();

    public DialogTests()
    {
        Services.AddSingleton<IPdfExporter>(_exporter);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _out.Dispose();
        }

        base.Dispose(disposing);
    }

    private string Text(string key) => Services.GetRequiredService<IStringLocalizer<Strings>>()[key].Value;

    private static TestVault JournalVault() => TestVault.Create(
        ("pages/Buch - Alpha.md", "title:: Alpha\ntype:: book\n\n- # Eins\n\t- Erster Text\n"),
        (JournalPath, $"- Vertrauen wächst\n  id:: {NoteId}\n- Gedanke über Wolken\n"));

    private static string JournalFile(TestVault tv) => Path.Combine(tv.Root, JournalPath);

    /// <summary>The first journal block is changed locally and the file differently: a conflict is reported.</summary>
    private static async Task RaiseConflictAsync(IRenderedComponent<ConflictDialog>? dialog, VaultSession session, string path, string localText, string externalContent)
    {
        session.Vault.FindPageByPath(path)!.Roots[1].SetContent(localText);
        File.WriteAllText(path, externalContent, Utf8);
        if (dialog is null)
        {
            session.HandleExternalChange(path);
            return;
        }

        await dialog.InvokeAsync(() => session.HandleExternalChange(path));
    }

    private static string External(string text) => $"- Vertrauen wächst\n  id:: {NoteId}\n- {text}\n";

    // ---- ConflictDialog ----

    [Fact]
    public async Task ConflictDialog_ChoiceBoth_CallsResolveWithBoth()
    {
        using var tv = JournalVault();
        var session = await OpenSessionAsync(tv);
        var cut = Render<ConflictDialog>();
        Assert.Empty(cut.FindAll(".ne-conflict"));

        await RaiseConflictAsync(cut, session, JournalFile(tv), "Lokal geändert", External("Extern geändert"));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-conflict")));
        Assert.Equal("Lokal geändert", cut.Find(".ne-conflict-mine pre").TextContent);
        Assert.Equal("Extern geändert", cut.Find(".ne-conflict-theirs pre").TextContent);
        Assert.Equal(3, cut.FindAll(".ne-conflict-choice").Count);
        cut.Find("input[value='Both']").Change(true);

        cut.Find(".ne-conflict-confirm").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-conflict")));
        var saved = tv.Read(JournalPath);
        Assert.Contains("Lokal geändert", saved);
        Assert.Contains("Extern geändert", saved);
        Assert.False(session.HasOpenConflict(JournalFile(tv)));
        Assert.False(session.Vault.FindPageByPath(JournalFile(tv))!.IsDirty);
    }

    [Fact]
    public async Task ConflictDialog_ChoiceTheirs_TakesTheExternalVersion()
    {
        using var tv = JournalVault();
        var session = await OpenSessionAsync(tv);
        var cut = Render<ConflictDialog>();
        await RaiseConflictAsync(cut, session, JournalFile(tv), "Lokal geändert", External("Extern geändert"));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-conflict")));

        cut.Find("input[value='Theirs']").Change(true);
        cut.Find(".ne-conflict-confirm").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-conflict")));
        Assert.Equal(External("Extern geändert"), tv.Read(JournalPath));
        Assert.False(session.HasOpenConflict(JournalFile(tv)));
    }

    [Fact]
    public async Task ConflictDialog_DefaultChoiceIsMine_KeepsLocalText()
    {
        using var tv = JournalVault();
        var session = await OpenSessionAsync(tv);
        var cut = Render<ConflictDialog>();
        await RaiseConflictAsync(cut, session, JournalFile(tv), "Lokal geändert", External("Extern geändert"));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-conflict")));

        cut.Find(".ne-conflict-confirm").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-conflict")));
        Assert.Contains("Lokal geändert", tv.Read(JournalPath));
        Assert.DoesNotContain("Extern geändert", tv.Read(JournalPath));
    }

    [Fact]
    public async Task ConflictDialog_ExternalDelete_ShowsMissingExternalVersion_MineRecreatesFile()
    {
        using var tv = JournalVault();
        var session = await OpenSessionAsync(tv);
        var cut = Render<ConflictDialog>();
        session.Vault.FindPageByPath(JournalFile(tv))!.Roots[1].SetContent("Lokal geändert");
        File.Delete(JournalFile(tv));

        await cut.InvokeAsync(() => session.HandleExternalChange(JournalFile(tv)));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-conflict")));
        Assert.Equal(Text("ConflictExternalMissing"), cut.Find(".ne-conflict-theirs .ne-conflict-missing").TextContent);
        Assert.False(File.Exists(JournalFile(tv)));

        cut.Find(".ne-conflict-confirm").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-conflict")));
        Assert.Contains("Lokal geändert", tv.Read(JournalPath));
    }

    [Fact]
    public async Task ConflictDialog_LocalRemovedBlock_ShowsLocalRemoved()
    {
        using var tv = JournalVault();
        var session = await OpenSessionAsync(tv);
        var cut = Render<ConflictDialog>();
        var page = session.Vault.FindPageByPath(JournalFile(tv))!;
        page.RemoveBlock(page.Roots[1]);
        File.WriteAllText(JournalFile(tv), External("Extern geändert"), Utf8);

        await cut.InvokeAsync(() => session.HandleExternalChange(JournalFile(tv)));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-conflict")));
        Assert.Equal(Text("ConflictLocalRemoved"), cut.Find(".ne-conflict-mine .ne-conflict-missing").TextContent);
        Assert.Equal("Extern geändert", cut.Find(".ne-conflict-theirs pre").TextContent);
    }

    [Fact]
    public async Task ConflictDialog_FileChangedAgain_ShowsTheNewConflict()
    {
        using var tv = JournalVault();
        var session = await OpenSessionAsync(tv);
        var cut = Render<ConflictDialog>();
        await RaiseConflictAsync(cut, session, JournalFile(tv), "Lokal geändert", External("Extern geändert"));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-conflict")));
        File.WriteAllText(JournalFile(tv), External("Noch einmal extern"), Utf8);

        cut.Find(".ne-conflict-confirm").Click();

        cut.WaitForAssertion(() => Assert.Equal("Noch einmal extern", cut.Find(".ne-conflict-theirs pre").TextContent));
        Assert.Single(cut.FindAll(".ne-conflict-again"));
        Assert.True(session.HasOpenConflict(JournalFile(tv)));
        Assert.Equal(External("Noch einmal extern"), tv.Read(JournalPath));
    }

    [Fact]
    public async Task ConflictDialog_ConflictClosedElsewhere_DialogDisappears()
    {
        using var tv = JournalVault();
        var session = await OpenSessionAsync(tv);
        var cut = Render<ConflictDialog>();
        await RaiseConflictAsync(cut, session, JournalFile(tv), "Lokal geändert", External("Extern geändert"));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-conflict")));

        // The file takes the local text again: the next handling merges cleanly and the conflict is gone.
        File.WriteAllText(JournalFile(tv), External("Lokal geändert"), Utf8);
        await cut.InvokeAsync(() => session.HandleExternalChange(JournalFile(tv)));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-conflict")));
        Assert.False(session.HasOpenConflict(JournalFile(tv)));
    }

    // ---- LinkCheckDialog ----

    private static TestVault LinkVault() => TestVault.Create(
        ("pages/Buch - Alpha.md",
            "title:: Alpha\ntype:: book\n\n" +
            "- # Eins\n" +
            "\t- Erster Text\n" +
            $"\t  id:: {BlockOne}\n" +
            $"\t  source:: (({MissingNote}))\n" +
            "\t- ## Eins-A\n" +
            "\t\t- Zweiter Text\n" +
            $"\t\t  id:: {BlockTwo}\n" +
            $"\t\t  source:: (({GoneNote}))\n"),
        (IdoenPath,
            "- Waise\n" +
            $"  id:: {OrphanNote}\n" +
            $"  used-in:: [[Buch - Fehlt]] (({OrphanBlock}))\n" +
            "- Ohne Verweis\n" +
            $"  id:: {MissingNote}\n"));

    [Fact]
    public async Task LinkCheckDialog_AddsMissingUsages_AndListsTheRest()
    {
        using var tv = LinkVault();
        await OpenSessionAsync(tv);

        var cut = Render<LinkCheckDialog>();

        Assert.Contains("1", cut.Find(".ne-linkcheck-fixed").TextContent);
        Assert.Contains($"(({BlockOne}))", tv.Read(IdoenPath));
        Assert.Single(cut.FindAll(".ne-linkcheck-orphan"));
        Assert.Equal("Waise", cut.Find(".ne-linkcheck-note").TextContent);
        Assert.Single(cut.FindAll(".ne-linkcheck-goto"));
        Assert.Empty(cut.FindAll(".ne-linkcheck-missing"));
    }

    [Fact]
    public async Task LinkCheckDialog_MarkUnknown_CallsResolve()
    {
        using var tv = LinkVault();
        await OpenSessionAsync(tv);
        var cut = Render<LinkCheckDialog>();

        cut.Find(".ne-linkcheck-unknown").Click();

        var notes = tv.Read(IdoenPath);
        Assert.Contains("used-in:: [[Buch - Fehlt]]\n", notes);
        Assert.DoesNotContain(OrphanBlock, notes);
        // Still listed (the book does not exist) but nothing is left to mark.
        Assert.Single(cut.FindAll(".ne-linkcheck-orphan"));
        Assert.Empty(cut.FindAll(".ne-linkcheck-unknown"));
    }

    [Fact]
    public async Task LinkCheckDialog_NothingToReport_SaysSo()
    {
        using var tv = JournalVault();
        await OpenSessionAsync(tv);

        var cut = Render<LinkCheckDialog>();

        Assert.Single(cut.FindAll(".ne-linkcheck-clean"));
        Assert.Empty(cut.FindAll(".ne-linkcheck-fixed"));
    }

    [Fact]
    public async Task LinkCheckDialog_Remove_RemovesTheEntry()
    {
        using var tv = LinkVault();
        await OpenSessionAsync(tv);
        var cut = Render<LinkCheckDialog>();

        cut.Find(".ne-linkcheck-remove").Click();

        Assert.DoesNotContain("Buch - Fehlt", tv.Read(IdoenPath));
        Assert.Empty(cut.FindAll(".ne-linkcheck-orphan"));
    }

    [Fact]
    public async Task LinkCheckDialog_BrokenSource_ClickJumpsToTheSection_AndCloses()
    {
        using var tv = LinkVault();
        var session = await OpenSessionAsync(tv);
        var closed = 0;
        var flushed = 0;
        State.FlushEditor = () =>
        {
            flushed++;
            return Task.CompletedTask;
        };
        var cut = Render<LinkCheckDialog>(p => p.Add(c => c.OnClose, EventCallback.Factory.Create(this, () => closed++)));

        cut.Find(".ne-linkcheck-goto").Click();

        var section = session.Vault.FindBook("Buch - Alpha")!.Root.Children.First().Children.First();
        Assert.Equal("Eins-A", section.Title);
        cut.WaitForAssertion(() => Assert.Equal(section.Key, State.CurrentSectionKey));
        Assert.Equal(1, flushed);
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task LinkCheckDialog_OpenConflictOnNote_RefusesToResolve()
    {
        using var tv = LinkVault();
        var session = await OpenSessionAsync(tv);
        var cut = Render<LinkCheckDialog>();
        var path = Path.Combine(tv.Root, IdoenPath);
        session.Vault.FindPageByPath(path)!.Roots[1].SetContent("Lokal geändert");
        File.WriteAllText(path, Utf8.GetString(File.ReadAllBytes(path)).Replace("Ohne Verweis", "Extern geändert"), Utf8);
        session.HandleExternalChange(path);
        Assert.True(session.HasOpenConflict(path));
        var before = tv.Read(IdoenPath);

        cut.Find(".ne-linkcheck-remove").Click();

        Assert.Equal(before, tv.Read(IdoenPath));
        Assert.Equal(Text("LinkCheckConflict"), cut.Find(".ne-dialog-error").TextContent);
    }

    [Fact]
    public void Shell_OpeningAVaultWithLinkProblems_ShowsTheLinkCheck()
    {
        using var tv = LinkVault();
        Settings.LastVault = tv.Root;

        var cut = Render<Shell>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-linkcheck-dialog")));
        Assert.Single(cut.FindAll(".ne-linkcheck-orphan"));
        cut.Find(".ne-linkcheck-close").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-linkcheck-dialog")));
    }

    // ---- HandledWizard ----

    private static TestVault HandledVault() => TestVault.Create(
        ("pages/Buch - Alpha.md", "title:: Alpha\ntype:: book\n\n- # Eins\n"),
        ("pages/Buch - Beta.md", "title:: Beta\ntype:: book\n\n- # Zwei\n"),
        (NotesPath, "- [handled] Eins\n- [handled] Zwei\n- Drei\n"));

    [Fact]
    public async Task HandledWizard_DeselectedItemsNotConverted()
    {
        using var tv = HandledVault();
        var session = await OpenSessionAsync(tv);
        var flushed = 0;
        State.FlushEditor = () =>
        {
            flushed++;
            return Task.CompletedTask;
        };
        var cut = Render<HandledWizard>();
        var boxes = cut.FindAll(".ne-handled-item input[type=checkbox]");
        Assert.Equal(2, boxes.Count);
        Assert.All(boxes, box => Assert.True(box.HasAttribute("checked")));

        boxes[1].Change(false);
        cut.Find(".ne-handled-convert").Click();
        Assert.Single(cut.FindAll(".ne-handled-confirm-text"));
        Assert.Equal("- [handled] Eins\n- [handled] Zwei\n- Drei\n", tv.Read(NotesPath));
        cut.Find(".ne-handled-yes").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-handled-done")));
        Assert.Equal("- Eins\n  used-in:: [[Buch - Alpha]]\n- [handled] Zwei\n- Drei\n", tv.Read(NotesPath));
        Assert.Equal(1, flushed);
        Assert.Single(session.Handled.Find());
    }

    [Fact]
    public async Task HandledWizard_BookSelection_ChoosesTheBook()
    {
        using var tv = HandledVault();
        await OpenSessionAsync(tv);
        var cut = Render<HandledWizard>();

        cut.Find(".ne-handled-book-select").Change("Buch - Beta");
        cut.Find(".ne-handled-convert").Click();
        cut.Find(".ne-handled-yes").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-handled-done")));
        Assert.Equal("- Eins\n  used-in:: [[Buch - Beta]]\n- Zwei\n  used-in:: [[Buch - Beta]]\n- Drei\n", tv.Read(NotesPath));
    }

    [Fact]
    public async Task HandledWizard_NothingSelected_CannotConvert()
    {
        using var tv = HandledVault();
        await OpenSessionAsync(tv);
        var cut = Render<HandledWizard>();

        for (var i = 0; i < 2; i++)
        {
            cut.FindAll(".ne-handled-item input[type=checkbox]")[i].Change(false);
        }

        Assert.True(cut.Find(".ne-handled-convert").HasAttribute("disabled"));
    }

    [Fact]
    public async Task HandledWizard_ReadOnlyNotePage_IsListedButNotConverted()
    {
        using var tv = TestVault.Create(
            ("pages/Buch - Alpha.md", "title:: Alpha\ntype:: book\n\n- # Eins\n"),
            (NotesPath, "- [handled] Eins\n"),
            ("pages/Kaputt.md", "- [handled] Kaputt\n```\nnicht geschlossen\n"));
        var session = await OpenSessionAsync(tv);
        Assert.True(session.Vault.FindPageByPath(Path.Combine(tv.Root, "pages/Kaputt.md"))!.IsReadOnly);

        var cut = Render<HandledWizard>();

        Assert.Equal(2, cut.FindAll(".ne-handled-item").Count);
        Assert.Single(cut.FindAll(".ne-handled-readonly"));
        cut.Find(".ne-handled-convert").Click();
        cut.Find(".ne-handled-yes").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-handled-done")));
        Assert.Equal("- Eins\n  used-in:: [[Buch - Alpha]]\n", tv.Read(NotesPath));
    }

    [Fact]
    public async Task HandledWizard_OpenConflictOnTheNotePage_IsRefused()
    {
        using var tv = HandledVault();
        var session = await OpenSessionAsync(tv);
        var path = Path.Combine(tv.Root, NotesPath);
        session.Vault.FindPageByPath(path)!.Roots[0].SetContent("[handled] Lokal");
        const string external = "- [handled] Extern\n- [handled] Zwei\n- Drei\n";
        File.WriteAllText(path, external, Utf8);
        session.HandleExternalChange(path);
        Assert.True(session.HasOpenConflict(path));
        var cut = Render<HandledWizard>();

        cut.Find(".ne-handled-convert").Click();
        cut.Find(".ne-handled-yes").Click();

        cut.WaitForAssertion(() => Assert.Equal(Text("AssistantConflict"), cut.Find(".ne-dialog-error").TextContent));
        Assert.Equal(external, tv.Read(NotesPath));
    }

    // ---- DraftWizard ----

    private static TestVault DraftVault() => TestVault.Create(
        ("pages/Buch - Alpha.md", "title:: Alpha\ntype:: book\n\n- # Eins\n"),
        ("pages/Entwurf.md", "- Kapitel\n\t- Abschnitt\n\t\t- Ein Satz hier.\n"));

    private static IEnumerable<string> Levels(IRenderedComponent<DraftWizard> cut) =>
        cut.FindAll(".ne-draft-level").Select(e => e.TextContent);

    [Fact]
    public async Task DraftWizard_ToggleUpdatesLevels()
    {
        using var tv = DraftVault();
        await OpenSessionAsync(tv);
        var cut = Render<DraftWizard>();
        Assert.Equal(["Entwurf"], cut.FindAll(".ne-draft-page").Select(e => e.TextContent));

        cut.Find(".ne-draft-page").Click();

        Assert.Equal(["H1", "H2"], Levels(cut));
        cut.FindAll(".ne-draft-toggle")[0].Click();
        Assert.Equal(["H1"], Levels(cut));
        cut.FindAll(".ne-draft-toggle")[2].Click();
        Assert.Equal(["H1", "H2"], Levels(cut));
        Assert.Equal("false", cut.FindAll(".ne-draft-toggle")[0].GetAttribute("aria-pressed"));
    }

    [Fact]
    public async Task DraftWizard_OnlyPlainPagesCanBeChosen()
    {
        using var tv = TestVault.Create(
            ("pages/Buch - Alpha.md", "title:: Alpha\ntype:: book\n\n- # Eins\n"),
            ("pages/Entwurf.md", "- Kapitel\n\t- Text\n"),
            ("pages/Kaputt.md", "- Kapitel\n```\nnicht geschlossen\n"));
        await OpenSessionAsync(tv);

        var cut = Render<DraftWizard>();

        Assert.Equal(["Entwurf"], cut.FindAll(".ne-draft-page").Select(e => e.TextContent));
    }

    [Fact]
    public async Task DraftWizard_Convert_WritesHeadings_AndTheBookAppearsInTheSelection()
    {
        using var tv = DraftVault();
        var session = await OpenSessionAsync(tv);
        var flushed = 0;
        State.FlushEditor = () =>
        {
            flushed++;
            return Task.CompletedTask;
        };
        var header = Render<HeaderBar>();
        Assert.Equal(["Alpha"], header.FindAll(".ne-book-select option").Select(o => o.TextContent));
        var cut = Render<DraftWizard>();
        cut.Find(".ne-draft-page").Click();

        cut.Find(".ne-draft-convert").Click();
        Assert.Single(cut.FindAll(".ne-draft-confirm-text"));
        Assert.DoesNotContain("# Kapitel", tv.Read("pages/Entwurf.md"));
        cut.Find(".ne-draft-yes").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-draft-done")));
        var saved = tv.Read("pages/Entwurf.md");
        Assert.Contains("- # Kapitel", saved);
        Assert.Contains("- ## Abschnitt", saved);
        Assert.Contains("Ein Satz hier.", saved);
        Assert.Contains("type:: book", saved);
        Assert.Equal(1, flushed);
        Assert.NotNull(session.Vault.FindBook("Entwurf"));
        header.WaitForAssertion(() => Assert.Equal(["Alpha", "Entwurf"], header.FindAll(".ne-book-select option").Select(o => o.TextContent)));
    }

    [Fact]
    public async Task DraftWizard_TooDeepOutline_ShowsErrorAndChangesNothing()
    {
        const string deep = "- a\n\t- b\n\t\t- c\n\t\t\t- d\n\t\t\t\t- e\n\t\t\t\t\t- f\n\t\t\t\t\t\t- g\n\t\t\t\t\t\t\t- Ein Satz hier.\n";
        using var tv = TestVault.Create(
            ("pages/Buch - Alpha.md", "title:: Alpha\ntype:: book\n\n- # Eins\n"),
            ("pages/Tief.md", deep));
        await OpenSessionAsync(tv);
        var cut = Render<DraftWizard>();
        cut.Find(".ne-draft-page").Click();
        Assert.Contains("H7", Levels(cut));

        cut.Find(".ne-draft-convert").Click();
        cut.Find(".ne-draft-yes").Click();

        cut.WaitForAssertion(() => Assert.Equal(Text("DraftInvalid"), cut.Find(".ne-dialog-error").TextContent));
        Assert.Equal(deep, tv.Read("pages/Tief.md"));
        Assert.Empty(cut.FindAll(".ne-draft-done"));
    }

    // ---- PdfExportDialog ----

    private async Task<(VaultSession Session, string Target)> PdfSetupAsync(TestVault tv)
    {
        var session = await OpenSessionAsync(tv);
        var target = Path.Combine(_out.Path, "Alpha.pdf");
        Platform.SaveFileToPick = target;
        return (session, target);
    }

    private static TestVault PdfVault() => TestVault.Create(
        ("pages/Buch - Alpha.md", "title:: Alpha\ntype:: book\n\n- # Eins\n\t- Erster Text\n- # Zwei\n"));

    [Fact]
    public async Task PdfExportDialog_ShowsWarningsAfterExport()
    {
        using var tv = PdfVault();
        var (_, target) = await PdfSetupAsync(tv);
        _exporter.Warnings = ["Ein Bild konnte nicht eingebettet werden."];
        var cut = Render<PdfExportDialog>();

        cut.Find(".ne-pdf-export").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-pdf-saved")));
        Assert.Equal(["Ein Bild konnte nicht eingebettet werden."], cut.FindAll(".ne-pdf-warnings li").Select(e => e.TextContent));
        Assert.True(File.Exists(target));
        Assert.Equal("Alpha.pdf", Platform.LastSuggestedName);
        var call = Assert.Single(_exporter.Calls);
        Assert.Null(call.Scope);
        Assert.Equal(PdfPageSize.A4, call.Options.Size);
    }

    [Fact]
    public async Task PdfExportDialog_NoWarnings_SaysSo()
    {
        using var tv = PdfVault();
        await PdfSetupAsync(tv);
        var cut = Render<PdfExportDialog>();

        cut.Find(".ne-pdf-export").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-pdf-nowarnings")));
        Assert.Empty(cut.FindAll(".ne-pdf-warnings"));
    }

    [Fact]
    public async Task PdfExportDialog_SectionScopeAndA5_ArePassedOn()
    {
        using var tv = PdfVault();
        var (session, _) = await PdfSetupAsync(tv);
        var section = session.Vault.FindBook("Buch - Alpha")!.Root.Children.First();
        State.CurrentSectionKey = section.Key;
        var cut = Render<PdfExportDialog>();
        Assert.True(cut.Find("input[value='section']").HasAttribute("checked"));

        cut.Find("input[value='A5']").Change(true);
        cut.Find(".ne-pdf-export").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-pdf-saved")));
        var call = Assert.Single(_exporter.Calls);
        Assert.Equal(section.Key, call.Scope);
        Assert.Equal(PdfPageSize.A5, call.Options.Size);
    }

    [Fact]
    public async Task PdfExportDialog_BookRootSelected_SectionScopeIsNotOffered()
    {
        using var tv = PdfVault();
        await PdfSetupAsync(tv);

        var cut = Render<PdfExportDialog>();

        Assert.True(cut.Find("input[value='section']").HasAttribute("disabled"));
        Assert.True(cut.Find("input[value='book']").HasAttribute("checked"));
    }

    [Fact]
    public async Task PdfExportDialog_CancelledPicker_ExportsNothing()
    {
        using var tv = PdfVault();
        await OpenSessionAsync(tv);
        Platform.SaveFileToPick = null;
        var cut = Render<PdfExportDialog>();

        cut.Find(".ne-pdf-export").Click();

        Assert.Empty(_exporter.Calls);
        Assert.Empty(cut.FindAll(".ne-pdf-saved"));
        Assert.Empty(cut.FindAll(".ne-dialog-error"));
    }

    [Fact]
    public async Task PdfExportDialog_ExporterFails_ShowsErrorAndRemovesTheIncompleteFile()
    {
        using var tv = PdfVault();
        var (_, target) = await PdfSetupAsync(tv);
        _exporter.Failure = new InvalidOperationException("kaputt");
        var cut = Render<PdfExportDialog>();

        cut.Find(".ne-pdf-export").Click();

        cut.WaitForAssertion(() => Assert.Equal(Text("PdfFailed"), cut.Find(".ne-dialog-error").TextContent));
        Assert.False(File.Exists(target));
        Assert.Empty(cut.FindAll(".ne-pdf-saved"));
    }

    // ---- SettingsDialog ----

    [Fact]
    public async Task SettingsDialog_FontSizeClampedAndSaved()
    {
        using var tv = JournalVault();
        await OpenSessionAsync(tv);
        var closed = 0;
        var cut = Render<SettingsDialog>(p => p.Add(c => c.OnClose, EventCallback.Factory.Create(this, () => closed++)));

        cut.Find(".ne-settings-font").Change("99");
        cut.Find(".ne-settings-width").Change("10");
        cut.Find(".ne-settings-theme").Change(nameof(ThemeChoice.Dark));
        cut.Find(".ne-settings-save").Click();

        Assert.Equal(UiSettings.MaxFontSizePt, Settings.FontSizePt);
        Assert.Equal(UiSettings.MinLineWidthCh, Settings.LineWidthCh);
        Assert.Equal(ThemeChoice.Dark, Settings.Theme);
        var saved = UiSettings.Load(Platform.UserDataDirectory);
        Assert.Equal(20, saved.FontSizePt);
        Assert.Equal(50, saved.LineWidthCh);
        Assert.Equal(ThemeChoice.Dark, saved.Theme);
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task SettingsDialog_TooSmallFontSize_ClampsToMinimum()
    {
        using var tv = JournalVault();
        await OpenSessionAsync(tv);
        var cut = Render<SettingsDialog>();

        cut.Find(".ne-settings-font").Change("2");
        cut.Find(".ne-settings-save").Click();

        Assert.Equal(UiSettings.MinFontSizePt, UiSettings.Load(Platform.UserDataDirectory).FontSizePt);
    }

    [Fact]
    public async Task SettingsDialog_ChangedFolders_AreSavedInTheVault_AndReloadIsRequested()
    {
        using var tv = JournalVault();
        await OpenSessionAsync(tv);
        var flushed = 0;
        State.FlushEditor = () =>
        {
            flushed++;
            return Task.CompletedTask;
        };
        var reloads = 0;
        var cut = Render<SettingsDialog>(p => p.Add(c => c.OnNoteFoldersChanged, EventCallback.Factory.Create(this, () => reloads++)));
        Assert.Equal("journals\npages", cut.Find(".ne-settings-folders").GetAttribute("value")!.Replace("\r", ""));

        cut.Find(".ne-settings-folders").Input("journals\r\n\r\npages\nextra\njournals");
        cut.Find(".ne-settings-save").Click();

        Assert.Equal(["journals", "pages", "extra"], Core.Vaults.VaultSettings.Load(tv.Root).NoteFolders);
        Assert.Equal(1, reloads);
        Assert.Equal(1, flushed);
    }

    [Fact]
    public async Task SettingsDialog_UnchangedFolders_DoNotReload()
    {
        using var tv = JournalVault();
        await OpenSessionAsync(tv);
        var reloads = 0;
        var cut = Render<SettingsDialog>(p => p.Add(c => c.OnNoteFoldersChanged, EventCallback.Factory.Create(this, () => reloads++)));

        cut.Find(".ne-settings-save").Click();

        Assert.Equal(0, reloads);
        Assert.False(File.Exists(Path.Combine(tv.Root, ".noteevolution", "settings.json")));
    }

    [Fact]
    public async Task SettingsDialog_FoldersWhileConflictOpen_AreRefused()
    {
        using var tv = JournalVault();
        var session = await OpenSessionAsync(tv);
        await RaiseConflictAsync(null, session, JournalFile(tv), "Lokal geändert", External("Extern geändert"));
        Assert.True(session.HasAnyOpenConflict);
        var reloads = 0;
        var cut = Render<SettingsDialog>(p => p.Add(c => c.OnNoteFoldersChanged, EventCallback.Factory.Create(this, () => reloads++)));

        cut.Find(".ne-settings-folders").Input("pages");
        cut.Find(".ne-settings-save").Click();

        Assert.Equal(Text("SettingsConflict"), cut.Find(".ne-dialog-error").TextContent);
        Assert.Equal(0, reloads);
        Assert.False(File.Exists(Path.Combine(tv.Root, ".noteevolution", "settings.json")));
    }

    [Fact]
    public void Shell_HeaderButtons_OpenTheirDialogs()
    {
        using var tv = JournalVault();
        Settings.LastVault = tv.Root;
        var cut = Render<Shell>();
        // The vault opens in the background; the header buttons are enabled once the book is shown.
        cut.WaitForAssertion(() => Assert.False(cut.Find(".ne-pdf").HasAttribute("disabled")));

        OpenAndClose(cut, ".ne-settings", ".ne-settings-dialog", ".ne-settings-cancel");
        OpenAndClose(cut, ".ne-pdf", ".ne-pdf-dialog", ".ne-pdf-close");
        OpenAndClose(cut, ".ne-linkcheck", ".ne-linkcheck-dialog", ".ne-linkcheck-close");

        cut.Find(".ne-assistants-toggle").Click();
        OpenAndClose(cut, ".ne-assistant-handled", ".ne-handled", ".ne-handled-close");
        cut.Find(".ne-assistants-toggle").Click();
        OpenAndClose(cut, ".ne-assistant-draft", ".ne-draft", ".ne-draft-close");
    }

    private static void OpenAndClose(IRenderedComponent<Shell> cut, string button, string dialog, string close)
    {
        cut.Find(button).Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(dialog)));
        cut.Find(close).Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(dialog)));
    }

    [Fact]
    public void Shell_ChangedNoteFolders_ReopensTheVault()
    {
        using var tv = TestVault.Create(
            ("pages/Buch - Alpha.md", "title:: Alpha\ntype:: book\n\n- # Eins\n"),
            ("extra/Neu.md", "- Neue Notiz\n"));
        Settings.LastVault = tv.Root;
        var cut = Render<Shell>();
        cut.WaitForAssertion(() => Assert.NotNull(State.Session));
        var first = State.Session;
        Assert.Empty(first!.Notes.All());

        cut.Find(".ne-settings").Click();
        cut.Find(".ne-settings-folders").Input("pages\nextra");
        cut.Find(".ne-settings-save").Click();

        cut.WaitForAssertion(() => Assert.NotSame(first, State.Session));
        Assert.Equal(["Neue Notiz"], State.Session!.Notes.All().Select(n => n.Block.Content));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-settings-dialog")));
    }

    // ---- UndoToast ----

    [Fact]
    public async Task UndoToast_ClickUndo_CallsUndoManager()
    {
        using var tv = JournalVault();
        var session = await OpenSessionAsync(tv);
        var action = new RecordingUndo("Übernehmen");
        var flushedFirst = false;
        State.FlushEditor = () =>
        {
            flushedFirst = !action.Undone;
            return Task.CompletedTask;
        };
        var cut = Render<UndoToast>();
        Assert.Empty(cut.FindAll(".ne-toast"));

        await cut.InvokeAsync(() => session.Undo.Push(action));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-toast")));
        Assert.Contains("Übernehmen", cut.Find(".ne-toast-text").TextContent);
        cut.Find(".ne-toast-undo").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-toast")));
        Assert.True(action.Undone);
        Assert.True(flushedFirst);
        Assert.False(session.Undo.CanUndo);
    }

    [Fact]
    public async Task UndoToast_DisappearsAfterEightSeconds()
    {
        using var tv = JournalVault();
        var session = await OpenSessionAsync(tv);
        var cut = Render<UndoToast>();
        await cut.InvokeAsync(() => session.Undo.Push(new RecordingUndo("Löschen")));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-toast")));

        Time.Advance(TimeSpan.FromSeconds(7));
        Assert.Single(cut.FindAll(".ne-toast"));
        Time.Advance(TimeSpan.FromSeconds(1));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-toast")));
        Assert.True(session.Undo.CanUndo);
    }

    [Fact]
    public async Task UndoToast_AnotherActionRestartsTheTime()
    {
        using var tv = JournalVault();
        var session = await OpenSessionAsync(tv);
        var cut = Render<UndoToast>();
        await cut.InvokeAsync(() => session.Undo.Push(new RecordingUndo("Übernehmen")));
        Time.Advance(TimeSpan.FromSeconds(6));
        await cut.InvokeAsync(() => session.Undo.Push(new RecordingUndo("Löschen")));

        Time.Advance(TimeSpan.FromSeconds(6));

        cut.WaitForAssertion(() => Assert.Contains("Löschen", cut.Find(".ne-toast-text").TextContent));
        Time.Advance(TimeSpan.FromSeconds(2));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-toast")));
    }

    [Fact]
    public async Task UndoToast_OpenConflict_RefusesAndShowsWhy()
    {
        using var tv = JournalVault();
        var session = await OpenSessionAsync(tv);
        var action = new RecordingUndo("Übernehmen");
        var cut = Render<UndoToast>();
        await cut.InvokeAsync(() => session.Undo.Push(action));
        await RaiseConflictAsync(null, session, JournalFile(tv), "Lokal geändert", External("Extern geändert"));
        Assert.True(session.HasAnyOpenConflict);

        cut.Find(".ne-toast-undo").Click();

        cut.WaitForAssertion(() => Assert.Equal(Text("UndoConflict"), cut.Find(".ne-toast-text").TextContent));
        Assert.False(action.Undone);
        Assert.True(session.Undo.CanUndo);
    }

    [Fact]
    public async Task UndoToast_HeaderUndoOfItsAction_HidesTheToast_ButtonNeverUndoesTheOlderAction()
    {
        using var tv = JournalVault();
        var session = await OpenSessionAsync(tv);
        var older = new RecordingUndo("Löschen");
        var newer = new RecordingUndo("Übernehmen");
        var cut = Render<UndoToast>();
        await cut.InvokeAsync(() => session.Undo.Push(older));
        await cut.InvokeAsync(() => session.Undo.Push(newer));
        cut.WaitForAssertion(() => Assert.Contains("Übernehmen", cut.Find(".ne-toast-text").TextContent));

        // The header's undo reverses the toast's action.
        await cut.InvokeAsync(() => session.TryUndo(out _));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-toast")));
        Assert.True(newer.Undone);
        Assert.False(older.Undone);
        Assert.True(session.Undo.CanUndo);
    }

    [Fact]
    public async Task UndoToast_ActionNoLongerOnTop_ClickHidesWithoutUndoing()
    {
        using var tv = JournalVault();
        var session = await OpenSessionAsync(tv);
        var older = new RecordingUndo("Löschen");
        var newer = new RecordingUndo("Übernehmen");
        var cut = Render<UndoToast>();
        await cut.InvokeAsync(() => session.Undo.Push(older));
        await cut.InvokeAsync(() => session.Undo.Push(newer));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-toast")));
        // While the click waits for the editor's flush, the toast's action is undone elsewhere.
        State.FlushEditor = () =>
        {
            session.Undo.Undo();
            return Task.CompletedTask;
        };

        cut.Find(".ne-toast-undo").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-toast")));
        Assert.True(newer.Undone);
        Assert.False(older.Undone);
        Assert.True(session.Undo.CanUndo);
    }

    [Fact]
    public async Task UndoToast_FailingUndo_ShowsTheFailure()
    {
        using var tv = JournalVault();
        var session = await OpenSessionAsync(tv);
        var cut = Render<UndoToast>();
        await cut.InvokeAsync(() => session.Undo.Push(new RecordingUndo("Löschen") { Failure = new IOException("gesperrt") }));

        cut.Find(".ne-toast-undo").Click();

        cut.WaitForAssertion(() => Assert.Equal(Text("UndoFailed"), cut.Find(".ne-toast-text").TextContent));
    }

    // ---- Localization ----

    [Fact]
    public async Task Dialogs_NoGermanLiterals()
    {
        // Every UI text comes out as its resource key; data (note and book text, names, page sizes) is excluded.
        Services.AddSingleton<IStringLocalizer<Strings>>(new KeyLocalizer());
        using var tv = LinkVault();
        var session = await OpenSessionAsync(tv);
        var resources = new ResourceManager(typeof(Strings));
        Platform.SaveFileToPick = Path.Combine(_out.Path, "Alpha.pdf");
        _exporter.Warnings = ["Hinweis"];

        var link = Render<LinkCheckDialog>();
        AssertKeys(link, resources);
        var settings = Render<SettingsDialog>();
        AssertKeys(settings, resources);
        var pdf = Render<PdfExportDialog>();
        pdf.Find(".ne-pdf-export").Click();
        pdf.WaitForAssertion(() => Assert.Single(pdf.FindAll(".ne-pdf-saved")));
        AssertKeys(pdf, resources);
        var handled = Render<HandledWizard>();
        AssertKeys(handled, resources);
        var toast = Render<UndoToast>();
        await toast.InvokeAsync(() => session.Undo.Push(new RecordingUndo("Aktion")));
        AssertKeys(toast, resources);

        // The conflict dialog and the draft wizard need their own vaults.
        var conflictDialog = Render<ConflictDialog>();
        var page = session.Vault.FindPageByPath(Path.Combine(tv.Root, IdoenPath))!;
        page.Roots[1].SetContent("Lokal");
        File.WriteAllText(page.FilePath, "- Waise\n  id:: " + OrphanNote + "\n- Extern\n", Utf8);
        await conflictDialog.InvokeAsync(() => session.HandleExternalChange(page.FilePath));
        conflictDialog.WaitForAssertion(() => Assert.Single(conflictDialog.FindAll(".ne-conflict")));
        AssertKeys(conflictDialog, resources);
    }

    [Fact]
    public async Task DraftWizard_NoGermanLiterals()
    {
        Services.AddSingleton<IStringLocalizer<Strings>>(new KeyLocalizer());
        using var tv = DraftVault();
        await OpenSessionAsync(tv);
        var resources = new ResourceManager(typeof(Strings));

        var cut = Render<DraftWizard>();
        AssertKeys(cut, resources);
        cut.Find(".ne-draft-page").Click();
        AssertKeys(cut, resources);
        cut.Find(".ne-draft-convert").Click();
        AssertKeys(cut, resources);
    }

    private static void AssertKeys<T>(IRenderedComponent<T> cut, ResourceManager resources)
        where T : IComponent
    {
        foreach (var text in UiTexts(cut.Find(".ne-modal, .ne-toast")))
        {
            Assert.True(
                !text.Any(char.IsLetter) || resources.GetString(text, System.Globalization.CultureInfo.InvariantCulture) is not null,
                $"'{text}' is neither a resource key nor free of letters");
        }
    }

    private static readonly string[] DataSelectors =
    [
        ".ne-conflict-version pre", ".ne-linkcheck-note", ".ne-linkcheck-block", ".ne-linkcheck-book", ".ne-handled-text",
        ".ne-handled-page", ".ne-draft-text", ".ne-draft-page", ".ne-draft-level", ".ne-pdf-size label", ".ne-pdf-warnings li",
        ".ne-handled-skipped li", ".ne-handled-book-select option", "textarea",
    ];

    private static IEnumerable<string> UiTexts(IElement root)
    {
        foreach (var element in root.QuerySelectorAll("*").Prepend(root))
        {
            if (DataSelectors.Any(selector => element.Matches(selector) || element.Closest(selector) is not null))
            {
                continue;
            }

            foreach (var name in new[] { "title", "aria-label", "placeholder" })
            {
                if (element.GetAttribute(name) is { Length: > 0 } value)
                {
                    yield return value;
                }
            }

            foreach (var text in element.ChildNodes.OfType<IText>().Select(t => t.Data.Trim()).Where(t => t.Length > 0))
            {
                yield return text;
            }
        }
    }

    private sealed class KeyLocalizer : IStringLocalizer<Strings>
    {
        public LocalizedString this[string name] => new(name, name);

        public LocalizedString this[string name, params object[] arguments] => new(name, name);

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    private sealed class RecordingUndo(string description) : IUndoAction
    {
        public bool Undone { get; private set; }

        public Exception? Failure { get; init; }

        public string Description => description;

        public void Undo()
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Undone = true;
        }
    }

    private sealed class FakePdfExporter : IPdfExporter
    {
        public List<(Guid? Scope, PdfOptions Options)> Calls { get; } = [];

        public IReadOnlyList<string> Warnings { get; set; } = [];

        public Exception? Failure { get; set; }

        public PdfExportReport Export(Book book, Guid? scopeKey, PdfOptions options, Stream output)
        {
            output.Write("%PDF-fake"u8);
            if (Failure is not null)
            {
                throw Failure;
            }

            Calls.Add((scopeKey, options));
            return new PdfExportReport(Warnings);
        }
    }
}
