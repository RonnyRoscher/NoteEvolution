using System.Text.RegularExpressions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Components.Dialogs;
using NoteEvolution.UI.Resources;

namespace NoteEvolution.UI.Tests;

/// <summary>
/// The assistant „Verknüpfungen der Unterpunkte nachtragen“ (spec 7.2): the preview counts the sub-points per book,
/// „Nachtragen“ writes the links as one undo step, and the guards of the [handled] wizard keep it from writing when the
/// editor holds unsaved text or a conflict is open.
/// </summary>
public class SubPointLinkDialogTests : UiTestContext
{
    private const string BookPath = "pages/Buch - Test.md";
    private const string JournalPath = "journals/2026_03_01.md";
    private const string TextId = "6f1c2a3b-0000-4000-8000-000000000001";
    private const string KernId = "6f1c2a3b-0000-4000-8000-000000000002";

    // An old adoption: only the text block has source::, its details have none.
    private const string BookText =
        "title:: Buch: Test\n" +
        "type:: book\n" +
        "\n" +
        "- # Kapitel\n" +
        "\t- Kern\n" +
        $"\t  id:: {TextId}\n" +
        $"\t  source:: (({KernId}))\n" +
        "\t\t- Unter A\n" +
        "\t\t- Unter B\n";

    private const string JournalText =
        "- Kern\n" +
        $"  id:: {KernId}\n" +
        $"  used-in:: [[Buch - Test]] (({TextId}))\n" +
        "\t- Unter A\n" +
        "\t- Unter B\n";

    private string Text(string key, params object[] arguments) =>
        Services.GetRequiredService<IStringLocalizer<Strings>>()[key, arguments].Value;

    [Fact]
    public async Task Dialog_ShowsCountsPerBook()
    {
        using var vault = TestVault.Create((BookPath, BookText), (JournalPath, JournalText));
        await OpenSessionAsync(vault);

        var cut = Render<SubPointLinkDialog>();

        Assert.Equal(Text("SubPointsSummary", 2, 1, 0), cut.Find(".ne-subpoints-summary").TextContent);
        Assert.Equal([Text("SubPointsBook", "Buch - Test", 2, 0)], cut.FindAll(".ne-subpoints-book").Select(item => item.TextContent));
        Assert.False(cut.Find(".ne-subpoints-apply").HasAttribute("disabled"));
        Assert.Empty(cut.FindAll(".ne-subpoints-none"));
    }

    [Fact]
    public async Task Dialog_NothingToDo()
    {
        using var vault = TestVault.Create((BookPath, BookText.Replace($"\t  source:: (({KernId}))\n", "")), (JournalPath, JournalText));
        await OpenSessionAsync(vault);

        var cut = Render<SubPointLinkDialog>();

        Assert.Equal(Text("SubPointsNone"), cut.Find(".ne-subpoints-none").TextContent);
        Assert.Empty(cut.FindAll(".ne-subpoints-apply"));
        Assert.Empty(cut.FindAll(".ne-subpoints-book"));
    }

    [Fact]
    public async Task Dialog_NoMatchButUnmatched_SaysSo()
    {
        using var vault = TestVault.Create((BookPath, BookText.Replace("- Unter", "- Umformuliert")), (JournalPath, JournalText));
        await OpenSessionAsync(vault);

        var cut = Render<SubPointLinkDialog>();

        Assert.Equal(Text("SubPointsNoneUnmatched", 2), cut.Find(".ne-subpoints-none").TextContent);
        Assert.Equal([Text("SubPointsBook", "Buch - Test", 0, 2)], cut.FindAll(".ne-subpoints-book").Select(item => item.TextContent));
        Assert.Empty(cut.FindAll(".ne-subpoints-apply"));
    }

    [Fact]
    public async Task Apply_LinksAndCloses()
    {
        using var vault = TestVault.Create((BookPath, BookText), (JournalPath, JournalText));
        var session = await OpenSessionAsync(vault);
        var closed = 0;
        var cut = Render<SubPointLinkDialog>(p => p.Add(d => d.OnClose, () => closed++));

        await cut.InvokeAsync(() => cut.Find(".ne-subpoints-apply").Click());

        Assert.Equal(1, closed);
        // The text block's own source stays; both details got one, and the note both entries besides its own.
        Assert.Equal(3, Regex.Matches(vault.Read(BookPath), "source::").Count);
        Assert.Equal(3, Regex.Matches(vault.Read(JournalPath), "used-in::").Count);
        Assert.Equal(Text("UndoSubPoints"), session.Undo.NextDescription);

        // A second run finds nothing.
        var again = Render<SubPointLinkDialog>();
        Assert.Equal(Text("SubPointsNone"), again.Find(".ne-subpoints-none").TextContent);
    }

    [Fact]
    public async Task Apply_OpenConflict_NothingWritten()
    {
        using var vault = TestVault.Create((BookPath, BookText), (JournalPath, JournalText));
        var session = await OpenSessionAsync(vault);
        var cut = Render<SubPointLinkDialog>();

        // A local change collides with an external change of the same line.
        var book = session.Vault.FindBook("Buch - Test")!;
        book.Page.AllBlocks().Single(b => b.Content == "Unter A").SetContent("Unter A lokal");
        var fullPath = Path.Combine(vault.Root, BookPath);
        var external = BookText.Replace("Unter A", "Unter A extern");
        File.WriteAllText(fullPath, external);
        session.HandleExternalChange(fullPath);
        Assert.True(session.HasOpenConflict(fullPath));

        await cut.InvokeAsync(() => cut.Find(".ne-subpoints-apply").Click());

        Assert.Equal(Text("AssistantConflict"), cut.Find(".ne-dialog-error").TextContent);
        Assert.Equal(external, vault.Read(BookPath));
        Assert.Equal(JournalText, vault.Read(JournalPath));
        Assert.False(session.Undo.CanUndo);
    }

    [Fact]
    public async Task Apply_VaultChangedSincePreview_NothingWritten_NewPlanShown()
    {
        using var vault = TestVault.Create((BookPath, BookText), (JournalPath, JournalText));
        var session = await OpenSessionAsync(vault);
        var cut = Render<SubPointLinkDialog>();
        Assert.Equal(Text("SubPointsSummary", 2, 1, 0), cut.Find(".ne-subpoints-summary").TextContent);

        // A detail is reworded after the preview: it has no unique match any more.
        var book = session.Vault.FindBook("Buch - Test")!;
        book.Page.AllBlocks().Single(b => b.Content == "Unter A").SetContent("Umformuliert");

        await cut.InvokeAsync(() => cut.Find(".ne-subpoints-apply").Click());

        Assert.Equal(Text("AssistantStale"), cut.Find(".ne-dialog-error").TextContent);
        Assert.Equal(Text("SubPointsSummary", 1, 1, 1), cut.Find(".ne-subpoints-summary").TextContent);
        Assert.Equal(BookText, vault.Read(BookPath));
        Assert.Equal(JournalText, vault.Read(JournalPath));
        Assert.False(session.Undo.CanUndo);
    }

    [Fact]
    public async Task Apply_NothingLeftSincePreview_ShowsNone_NotClosed()
    {
        using var vault = TestVault.Create((BookPath, BookText), (JournalPath, JournalText));
        var session = await OpenSessionAsync(vault);
        var closed = 0;
        var cut = Render<SubPointLinkDialog>(p => p.Add(d => d.OnClose, () => closed++));
        var book = session.Vault.FindBook("Buch - Test")!;
        foreach (var detail in book.Page.AllBlocks().Where(b => b.Content.StartsWith("Unter ")))
        {
            detail.SetContent("Neu " + detail.Content);
        }

        await cut.InvokeAsync(() => cut.Find(".ne-subpoints-apply").Click());

        Assert.Equal(0, closed);
        Assert.Equal(Text("SubPointsNoneUnmatched", 2), cut.Find(".ne-subpoints-none").TextContent);
        Assert.Empty(cut.FindAll(".ne-subpoints-summary"));
        Assert.Equal(BookText, vault.Read(BookPath));
    }

    [Fact]
    public async Task Apply_UnsavedEditorText_Refused()
    {
        using var vault = TestVault.Create((BookPath, BookText), (JournalPath, JournalText));
        var session = await OpenSessionAsync(vault);
        State.FlushEditor = () => throw new IOException("disk full");
        var cut = Render<SubPointLinkDialog>();

        await cut.InvokeAsync(() => cut.Find(".ne-subpoints-apply").Click());

        Assert.Equal(Text("AssistantFlushFailed"), cut.Find(".ne-dialog-error").TextContent);
        Assert.Equal(BookText, vault.Read(BookPath));
        Assert.Equal(JournalText, vault.Read(JournalPath));
        Assert.False(session.Undo.CanUndo);
    }

    [Fact]
    public async Task Undo_FromHeader()
    {
        using var vault = TestVault.Create((BookPath, BookText), (JournalPath, JournalText));
        await OpenSessionAsync(vault);
        var header = Render<HeaderBar>();
        var cut = Render<SubPointLinkDialog>();
        await cut.InvokeAsync(() => cut.Find(".ne-subpoints-apply").Click());
        Assert.NotEqual(BookText, vault.Read(BookPath));
        header.WaitForAssertion(() => Assert.Equal(Text("UndoSubPoints"), header.Find(".ne-undo").GetAttribute("title")));

        await header.InvokeAsync(() => header.Find(".ne-undo").Click());

        Assert.Equal(BookText, vault.Read(BookPath));
        Assert.Equal(JournalText, vault.Read(JournalPath));
        Assert.Empty(header.FindAll(".ne-undo-error"));
    }

    [Fact]
    public async Task Undo_ChangedNotePage_TakesBackTheEntries_KeepsTheChange()
    {
        using var vault = TestVault.Create((BookPath, BookText), (JournalPath, JournalText));
        var session = await OpenSessionAsync(vault);
        var header = Render<HeaderBar>();
        var cut = Render<SubPointLinkDialog>();
        await cut.InvokeAsync(() => cut.Find(".ne-subpoints-apply").Click());
        header.WaitForAssertion(() => Assert.False(header.Find(".ne-undo").HasAttribute("disabled")));

        // The note page was changed after the run: it keeps the change and loses only the run's entries.
        var path = Path.Combine(vault.Root, JournalPath);
        File.WriteAllText(path, File.ReadAllText(path) + "- Neu\n");
        session.HandleExternalChange(path);

        await header.InvokeAsync(() => header.Find(".ne-undo").Click());

        Assert.Empty(header.FindAll(".ne-undo-error"));
        Assert.Equal(BookText, vault.Read(BookPath));
        Assert.Equal(
            JournalText + "- Neu\n",
            Regex.Replace(vault.Read(JournalPath), @"^\t[\t ]*id:: [^\n]*\n", "", RegexOptions.Multiline));
    }

    [Fact]
    public async Task Undo_ChangedBook_Refused_NothingChanged()
    {
        using var vault = TestVault.Create((BookPath, BookText), (JournalPath, JournalText));
        var session = await OpenSessionAsync(vault);
        var header = Render<HeaderBar>();
        var cut = Render<SubPointLinkDialog>();
        await cut.InvokeAsync(() => cut.Find(".ne-subpoints-apply").Click());
        header.WaitForAssertion(() => Assert.False(header.Find(".ne-undo").HasAttribute("disabled")));
        var path = Path.Combine(vault.Root, BookPath);
        var changed = File.ReadAllText(path) + "- Neu\n";
        File.WriteAllText(path, changed);
        session.HandleExternalChange(path);
        var journal = vault.Read(JournalPath);

        await header.InvokeAsync(() => header.Find(".ne-undo").Click());

        Assert.Equal(Text("UndoFailed"), header.Find(".ne-undo-error").TextContent);
        Assert.Equal(changed, vault.Read(BookPath));
        Assert.Equal(journal, vault.Read(JournalPath));
    }
}