using AngleSharp.Dom;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>The "nicht relevant" and "später" marks of a journal day and the journal's filters for them.</summary>
public class JournalDayMarksTests : UiTestContext
{
    private const string AlphaPath = "pages/Buch - Alpha.md";
    private const string October = "journals/2026_10_01.md";
    private const string September = "journals/2026_09_15.md";
    private const string OctoberText = "- Oktoberidee\n\t- Unterpunkt\n";
    private const string SeptemberText = "title:: Herbst\n\n- Septemberidee\n";

    private string Text(string key) => Services.GetRequiredService<IStringLocalizer<Strings>>()[key].Value;

    private static TestVault Create() => TestVault.Create(
        (AlphaPath, "title:: Alpha\ntype:: book\n\n- # Eins\n"),
        ("pages/Buch - Beta.md", "title:: Beta\ntype:: book\n\n- # Beta\n"),
        (October, OctoberText),
        (September, SeptemberText));

    private async Task<VaultSession> OpenAsync(TestVault vault, string? book = "Buch - Alpha")
    {
        var session = await OpenSessionAsync(vault);
        State.CurrentBook = book is null ? null : session.Vault.FindBook(book);
        return session;
    }

    private IRenderedComponent<NotesPane> RenderJournal()
    {
        var cut = Render<NotesPane>();
        cut.Find(".ne-tab-journal").Click();
        return cut;
    }

    private static IElement Day(IRenderedComponent<NotesPane> cut, string date) =>
        cut.FindAll(".ne-journal-day").Single(d => d.QuerySelector(".ne-journal-day-date")!.TextContent == date);

    private static IElement NotRelevantBox(IRenderedComponent<NotesPane> cut, string date) =>
        Day(cut, date).QuerySelector(".ne-journal-day-head .ne-day-not-relevant")!;

    private static IElement LaterBox(IRenderedComponent<NotesPane> cut, string date) =>
        Day(cut, date).QuerySelector(".ne-journal-day-head .ne-day-later")!;

    private static List<string> Dates(IRenderedComponent<NotesPane> cut) =>
        [.. cut.FindAll(".ne-journal-day-date").Select(e => e.TextContent)];

    private static string? Message(IRenderedComponent<NotesPane> cut, string date) =>
        Day(cut, date).QuerySelector(".ne-note-message")?.TextContent;

    [Fact]
    public async Task Day_ShowsBothMarksInItsHead_WithTheirLabels()
    {
        using var tv = Create();
        await OpenAsync(tv);
        var cut = RenderJournal();

        var head = Day(cut, "01.10.2026").QuerySelector(".ne-journal-day-head")!;
        var labels = head.QuerySelectorAll(".ne-day-mark").Select(l => l.TextContent.Trim()).ToList();
        Assert.Equal(["nicht relevant", "später"], labels);
        Assert.False(NotRelevantBox(cut, "01.10.2026").HasAttribute("checked"));
        Assert.False(LaterBox(cut, "01.10.2026").HasAttribute("checked"));
    }

    [Fact]
    public async Task NotRelevant_CheckWritesTheProperty_UncheckRestoresTheBytes()
    {
        using var tv = Create();
        await OpenAsync(tv);
        var cut = RenderJournal();

        NotRelevantBox(cut, "01.10.2026").Change(true);

        Assert.Equal("not-relevant-for:: [[Buch - Alpha]]\n\n" + OctoberText, tv.Read(October));
        Assert.True(NotRelevantBox(cut, "01.10.2026").HasAttribute("checked"));
        Assert.False(LaterBox(cut, "01.10.2026").HasAttribute("checked"));
        Assert.Null(Message(cut, "01.10.2026"));

        NotRelevantBox(cut, "01.10.2026").Change(false);

        Assert.Equal(OctoberText, tv.Read(October));
        Assert.False(NotRelevantBox(cut, "01.10.2026").HasAttribute("checked"));
        Assert.Equal(SeptemberText, tv.Read(September));
    }

    [Fact]
    public async Task NotRelevant_TwoBooks_KeepEachOthersEntries()
    {
        using var tv = Create();
        var session = await OpenAsync(tv);
        var cut = RenderJournal();
        NotRelevantBox(cut, "15.09.2026").Change(true);

        await cut.InvokeAsync(() =>
        {
            State.CurrentBook = session.Vault.FindBook("Buch - Beta");
            State.Notify();
        });
        cut.WaitForAssertion(() => Assert.False(NotRelevantBox(cut, "15.09.2026").HasAttribute("checked")));
        NotRelevantBox(cut, "15.09.2026").Change(true);
        Assert.Equal("title:: Herbst\nnot-relevant-for:: [[Buch - Alpha]], [[Buch - Beta]]\n\n- Septemberidee\n", tv.Read(September));

        NotRelevantBox(cut, "15.09.2026").Change(false);
        Assert.Equal("title:: Herbst\nnot-relevant-for:: [[Buch - Alpha]]\n\n- Septemberidee\n", tv.Read(September));

        await cut.InvokeAsync(() =>
        {
            State.CurrentBook = session.Vault.FindBook("Buch - Alpha");
            State.Notify();
        });
        cut.WaitForAssertion(() => Assert.True(NotRelevantBox(cut, "15.09.2026").HasAttribute("checked")));
        NotRelevantBox(cut, "15.09.2026").Change(false);
        Assert.Equal(SeptemberText, tv.Read(September));
    }

    [Fact]
    public async Task Later_IsIndependentOfNotRelevant_AndForEveryBook()
    {
        using var tv = Create();
        var session = await OpenAsync(tv);
        var cut = RenderJournal();

        LaterBox(cut, "01.10.2026").Change(true);
        Assert.Equal("later:: true\n\n" + OctoberText, tv.Read(October));
        Assert.False(NotRelevantBox(cut, "01.10.2026").HasAttribute("checked"));

        NotRelevantBox(cut, "01.10.2026").Change(true);
        LaterBox(cut, "01.10.2026").Change(false);
        Assert.Equal("not-relevant-for:: [[Buch - Alpha]]\n\n" + OctoberText, tv.Read(October));
        Assert.True(NotRelevantBox(cut, "01.10.2026").HasAttribute("checked"));

        LaterBox(cut, "01.10.2026").Change(true);
        await cut.InvokeAsync(() =>
        {
            State.CurrentBook = session.Vault.FindBook("Buch - Beta");
            State.Notify();
        });
        cut.WaitForAssertion(() => Assert.False(NotRelevantBox(cut, "01.10.2026").HasAttribute("checked")));
        Assert.True(LaterBox(cut, "01.10.2026").HasAttribute("checked"));
    }

    [Fact]
    public async Task NotRelevant_WithoutBook_IsDisabled()
    {
        using var tv = Create();
        await OpenAsync(tv, book: null);
        var cut = RenderJournal();

        Assert.True(NotRelevantBox(cut, "01.10.2026").HasAttribute("disabled"));
        Assert.False(LaterBox(cut, "01.10.2026").HasAttribute("disabled"));
    }

    [Fact]
    public async Task ReadOnlyDay_ShowsTheMessage_AndNothingChanges()
    {
        const string broken = "- Kaputt\n```\nnicht geschlossen\n";
        using var tv = TestVault.Create(
            (AlphaPath, "title:: Alpha\ntype:: book\n\n- # Eins\n"),
            ("journals/2026_10_02.md", broken));
        await OpenAsync(tv);
        var cut = RenderJournal();

        var later = LaterBox(cut, "02.10.2026");
        later.Change(true);
        Assert.Equal(Text("JournalDayReadOnly"), Message(cut, "02.10.2026"));
        AssertRecreated(later, LaterBox(cut, "02.10.2026"));
        var notRelevant = NotRelevantBox(cut, "02.10.2026");
        notRelevant.Change(true);

        Assert.Equal(Text("JournalDayReadOnly"), Message(cut, "02.10.2026"));
        AssertRecreated(notRelevant, NotRelevantBox(cut, "02.10.2026"));
        Assert.Equal(broken, tv.Read("journals/2026_10_02.md"));
        Assert.False(LaterBox(cut, "02.10.2026").HasAttribute("checked"));
        Assert.False(NotRelevantBox(cut, "02.10.2026").HasAttribute("checked"));
    }

    [Fact]
    public async Task Message_GoesWhenTheEntriesAreReadAgain()
    {
        using var tv = TestVault.Create(
            (AlphaPath, "title:: Alpha\ntype:: book\n\n- # Eins\n"),
            ("journals/2026_10_02.md", "- Kaputt\n```\nnicht geschlossen\n"),
            (October, OctoberText));
        var session = await OpenAsync(tv);
        var cut = RenderJournal();
        LaterBox(cut, "02.10.2026").Change(true);
        Assert.Equal(Text("JournalDayReadOnly"), Message(cut, "02.10.2026"));

        // Another program changes another day: the pane reads the entries again for the new revision.
        var path = Path.Combine(tv.Root, October.Replace('/', Path.DirectorySeparatorChar));
        File.AppendAllText(path, "- Von außen\n");
        await cut.InvokeAsync(() => session.HandleExternalChange(path));

        cut.WaitForAssertion(() => Assert.Null(Message(cut, "02.10.2026")));
    }

    /// <summary>A toggle that was not saved as clicked recreates the input (so the DOM cannot keep the clicked state).</summary>
    private static void AssertRecreated(IElement before, IElement after) =>
        Assert.NotEqual(before.GetAttribute("data-mark-refresh"), after.GetAttribute("data-mark-refresh"));

    [Fact]
    public async Task OpenConflict_RefusesWithoutChanges()
    {
        using var tv = Create();
        var session = await OpenAsync(tv);
        var path = Path.Combine(tv.Root, October.Replace('/', Path.DirectorySeparatorChar));
        var page = session.Vault.FindPageByPath(path)!;
        page.Roots[0].SetContent("Lokal geändert");
        File.WriteAllText(path, "- Extern geändert\n\t- Unterpunkt\n");
        session.HandleExternalChange(page.FilePath);
        Assert.True(session.HasOpenConflict(page.FilePath));
        var cut = RenderJournal();
        var later = LaterBox(cut, "01.10.2026");

        later.Change(true);

        Assert.Equal(Text("NoteAdoptConflict"), Message(cut, "01.10.2026"));
        AssertRecreated(later, LaterBox(cut, "01.10.2026"));
        Assert.Equal("- Extern geändert\n\t- Unterpunkt\n", tv.Read(October));
        Assert.Null(session.Vault.FindPageByPath(path)!.GetPageProperty("later"));
    }

    [Fact]
    public async Task ExternalChange_IsMergedByTheSession_WithTheMessage()
    {
        using var tv = Create();
        await OpenAsync(tv);
        var cut = RenderJournal();
        File.AppendAllText(Path.Combine(tv.Root, October.Replace('/', Path.DirectorySeparatorChar)), "- Von außen ergänzt\n");

        LaterBox(cut, "01.10.2026").Change(true);

        cut.WaitForAssertion(() => Assert.Equal(Text("JournalDayExternalChange"), Message(cut, "01.10.2026")));
        Assert.Equal("later:: true\n\n" + OctoberText + "- Von außen ergänzt\n", tv.Read(October));
    }

    [Fact]
    public async Task FailedWrite_ShowsTheMessage_AndTheDayShowsTheFileAgain()
    {
        using var tv = Create();
        await OpenAsync(tv);
        var cut = RenderJournal();
        var path = Path.Combine(tv.Root, October.Replace('/', Path.DirectorySeparatorChar));

        // A read-only file cannot be replaced: the write fails after its retries.
        var later = LaterBox(cut, "01.10.2026");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            later.Change(true);
            AssertRecreated(later, LaterBox(cut, "01.10.2026"));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Assert.Equal(Text("JournalDayFailed"), Message(cut, "01.10.2026"));
        Assert.Equal(OctoberText, tv.Read(October));
        Assert.False(LaterBox(cut, "01.10.2026").HasAttribute("checked"));
        Assert.Null(State.Session!.Vault.FindPageByPath(path)!.GetPageProperty("later"));
    }

    [Fact]
    public async Task HideNotRelevantFilter_WithoutBook_IsDisabled()
    {
        using var tv = Create();
        var session = await OpenAsync(tv, book: null);
        var cut = RenderJournal();

        Assert.True(cut.Find(".ne-hide-not-relevant").HasAttribute("disabled"));
        Assert.False(cut.Find(".ne-hide-later").HasAttribute("disabled"));

        await cut.InvokeAsync(() =>
        {
            State.CurrentBook = session.Vault.FindBook("Buch - Beta");
            State.Notify();
        });
        cut.WaitForAssertion(() => Assert.False(cut.Find(".ne-hide-not-relevant").HasAttribute("disabled")));
    }

    [Fact]
    public async Task Filters_HideMarkedDays_AndArePersisted()
    {
        using var tv = Create();
        var session = await OpenAsync(tv);
        var cut = RenderJournal();
        NotRelevantBox(cut, "01.10.2026").Change(true);
        LaterBox(cut, "15.09.2026").Change(true);
        Assert.Equal(["01.10.2026", "15.09.2026"], Dates(cut));

        var filters = cut.Find(".ne-notes-filters");
        Assert.Equal("‚nicht relevant' ausblenden", filters.QuerySelector(".ne-hide-not-relevant")!.ParentElement!.TextContent.Trim());
        Assert.Equal("‚später' ausblenden", filters.QuerySelector(".ne-hide-later")!.ParentElement!.TextContent.Trim());

        cut.Find(".ne-hide-not-relevant").Change(true);
        Assert.Equal(["15.09.2026"], Dates(cut));
        Assert.True(Settings.HideNotRelevant);
        Assert.True(UiSettings.Load(Platform.UserDataDirectory).HideNotRelevant);

        // "nicht relevant" counts for the current book only.
        await cut.InvokeAsync(() =>
        {
            State.CurrentBook = session.Vault.FindBook("Buch - Beta");
            State.Notify();
        });
        cut.WaitForAssertion(() => Assert.Equal(["01.10.2026", "15.09.2026"], Dates(cut)));

        cut.Find(".ne-hide-later").Change(true);
        Assert.Equal(["01.10.2026"], Dates(cut));
        Assert.True(UiSettings.Load(Platform.UserDataDirectory).HideLater);

        cut.Find(".ne-hide-not-relevant").Change(false);
        cut.Find(".ne-hide-later").Change(false);
        Assert.Equal(["01.10.2026", "15.09.2026"], Dates(cut));
        var loaded = UiSettings.Load(Platform.UserDataDirectory);
        Assert.False(loaded.HideNotRelevant);
        Assert.False(loaded.HideLater);
    }

    [Fact]
    public async Task Filters_AreRestoredOnStart_AndHideADayMarkedNow()
    {
        using var tv = Create();
        await OpenAsync(tv);
        Settings.HideLater = true;
        var cut = RenderJournal();

        Assert.True(cut.Find(".ne-hide-later").HasAttribute("checked"));
        Assert.False(cut.Find(".ne-hide-not-relevant").HasAttribute("checked"));
        LaterBox(cut, "15.09.2026").Change(true);

        Assert.Equal(["01.10.2026"], Dates(cut));
    }

    [Fact]
    public async Task Filters_OnlyOnTheJournalTab_AndNotForSearch()
    {
        using var tv = Create();
        await OpenAsync(tv);
        var cut = RenderJournal();
        LaterBox(cut, "15.09.2026").Change(true);
        cut.Find(".ne-hide-later").Change(true);

        cut.Find(".ne-tab-search").Click();
        Assert.Empty(cut.FindAll(".ne-hide-later, .ne-hide-not-relevant"));
        cut.Find(".ne-search-input").Input("Septemberidee");
        Time.Advance(TimeSpan.FromMilliseconds(300));

        cut.WaitForAssertion(() => Assert.Contains("Septemberidee",
            cut.FindAll(".ne-note-card .ne-note-text").Select(e => e.TextContent.Trim())));
    }

    [Fact]
    public async Task FromAndTo_AreNoLongerRendered()
    {
        using var tv = Create();
        await OpenAsync(tv);
        var cut = RenderJournal();

        Assert.Empty(cut.FindAll(".ne-filter-from, .ne-filter-to"));
        Assert.Single(cut.FindAll(".ne-notes-filters .ne-journal-jump"));
        Assert.Single(cut.FindAll(".ne-notes-filters .ne-hide-used"));
    }
}
