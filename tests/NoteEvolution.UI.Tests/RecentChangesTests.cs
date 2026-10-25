using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Editor;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>The "Zuletzt geändert" list (spec "Blockdaten" 4.1).</summary>
public class RecentChangesTests : UiTestContext
{
    private const string BookPath = "pages/Buch - Alpha.md";

    private readonly List<TestVault> _vaults = [];

    private IStringLocalizer<Strings> L => Services.GetRequiredService<IStringLocalizer<Strings>>();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _vaults.ForEach(v => v.Dispose());
        }
    }

    private TestVault Vault(string body)
    {
        var vault = TestVault.Create((BookPath, "title:: Alpha\ntype:: book\n\n" + body));
        _vaults.Add(vault);
        return vault;
    }

    private static string Block(string text, string? updated) =>
        $"- {text}\n" + (updated is null ? "" : $"  updated:: {updated}\n");

    private static string Indented(string blocks) => string.Concat(blocks.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => "\t" + l + "\n"));

    private static Book BookOf(TestVault vault) => vault.Open().Books.Single();

    private static IReadOnlyList<RecentChange> Of(Book book, Func<OutlineNode, string?>? summary = null, int limit = 20) =>
        RecentChanges.Of(book, summary ?? (_ => null), limit);

    [Fact]
    public void Of_NewestFirst_AtMost20()
    {
        // One day each: blocks of a section changed on the same day would form a single entry.
        var blocks = string.Concat(Enumerable.Range(0, 25).Select(i => Block($"Text {i}", $"2026-09-{i + 1:00}T10:00")));
        var book = BookOf(Vault("- # Eins\n" + Indented(blocks)));

        var changes = Of(book);

        Assert.Equal(20, changes.Count);
        Assert.Equal("Text 24", changes[0].Excerpt);
        Assert.Equal("Text 5", changes[^1].Excerpt);
        Assert.Equal(changes.OrderByDescending(c => c.Updated), changes);
        Assert.Equal(3, Of(book, limit: 3).Count);
    }

    [Fact]
    public void Of_SkipsBlocksWithoutUpdated()
    {
        var book = BookOf(Vault("- # Eins\n" + Indented(Block("Ohne", null) + Block("Mit", "2026-10-01T10:00"))));

        var change = Assert.Single(Of(book));

        Assert.Equal("Mit", change.Excerpt);
        Assert.Equal(new DateTime(2026, 10, 1, 10, 0, 0), change.Updated);
    }

    [Fact]
    public void Of_SummaryFirstSentence_ElseExcerpt()
    {
        var book = BookOf(Vault("- # Eins\n" + Indented(Block("Ein Text im Abschnitt.", "2026-10-01T10:00"))
            + "- # Zwei\n" + Indented(Block("Zweiter Text", "2026-10-01T09:00"))));
        var asked = new List<string>();

        var changes = Of(book, node =>
        {
            asked.Add(node.Title);
            return node.Title == "Eins" ? "Es geht z. B. um Schlaf. Dann um Träume." : "  ";
        });

        Assert.Equal("Es geht z. B. um Schlaf.", changes[0].Summary);
        Assert.Equal("Ein Text im Abschnitt.", changes[0].Excerpt);
        Assert.Equal("Zweiter Text", changes[1].Summary);
        Assert.Equal(["Eins", "Zwei"], asked);
    }

    [Fact]
    public void Of_ExcerptCutAt80()
    {
        var book = BookOf(Vault("- # Eins\n" + Indented(Block(new string('x', 100), "2026-10-01T10:00"))));

        Assert.Equal(80, Assert.Single(Of(book)).Excerpt.Length);
    }

    [Fact]
    public void Of_SectionPath()
    {
        var book = BookOf(Vault("- Vorwort\n  updated:: 2026-10-01T08:00\n- # Eins\n\t- ## Eins-A\n" + Indented(Indented(Block("Tief", "2026-10-01T10:00")))));

        var changes = Of(book);

        Assert.Equal("Eins › Eins-A", changes[0].SectionPath);
        Assert.Equal("Alpha", changes[1].SectionPath);
    }

    [Fact]
    public void Of_BlocksOfOneSectionOnOneDay_FormOneEntry()
    {
        var book = BookOf(Vault("- # Eins\n" + Indented(
            Block("Erster Text", "2026-10-01T09:00") + Block("Zweiter Text", "2026-10-01T11:30") + Block("Dritter", "2026-10-02T08:00"))));

        var changes = Of(book);

        Assert.Equal(2, changes.Count);
        Assert.Equal(1, changes[0].Count);
        Assert.Equal("Dritter", changes[0].Excerpt);
        Assert.Equal(changes[0].TextBlockKey, changes[0].RevealKey);
        Assert.Equal(2, changes[1].Count);
        Assert.Equal(new DateTime(2026, 10, 1, 11, 30, 0), changes[1].Updated);
        Assert.Equal("Erster Text", changes[1].Excerpt);
        Assert.Equal(book.Root.Children.Single().Key, changes[1].RevealKey);
    }

    [Fact]
    public void Of_SameBlocksOnDifferentDays_StaySeparate()
    {
        var book = BookOf(Vault("- # Eins\n" + Indented(Block("A", "2026-10-01T23:50") + Block("B", "2026-10-02T00:10"))));

        var changes = Of(book);

        Assert.Equal(2, changes.Count);
        Assert.All(changes, c => Assert.Equal(1, c.Count));
    }

    [Fact]
    public void Of_DifferentSections_StaySeparate_AncestorsDoNotGroup()
    {
        var book = BookOf(Vault(
            "- # Eins\n" + Indented(Block("A", "2026-10-01T09:00"))
            + "\t- ## Eins-A\n" + Indented(Indented(Block("B", "2026-10-01T10:00")))
            + "- # Zwei\n" + Indented(Block("C", "2026-10-01T11:00"))));

        var changes = Of(book);

        Assert.Equal(3, changes.Count);
        Assert.All(changes, c => Assert.Equal(1, c.Count));
    }

    [Fact]
    public void Of_Limit_CountsEntriesNotBlocks()
    {
        var blocks = string.Concat(Enumerable.Range(0, 30).Select(i => Block($"Text {i}", $"2026-10-01T10:{i:00}")));
        var other = string.Concat(Enumerable.Range(2, 25).Select(d => Block($"Tag {d}", $"2026-09-{d:00}T10:00")));
        var book = BookOf(Vault("- # Eins\n" + Indented(blocks) + "- # Zwei\n" + Indented(other)));

        var changes = Of(book);

        Assert.Equal(20, changes.Count);
        Assert.Equal(30, changes[0].Count);
        Assert.Equal(19, changes.Skip(1).Count(c => c.Count == 1));
    }

    [Fact]
    public void Of_GroupSummary_FirstSentence_ElseFirstBlocksExcerpt()
    {
        var book = BookOf(Vault("- # Eins\n" + Indented(Block("Erster", "2026-10-01T09:00") + Block("Zweiter", "2026-10-01T10:00"))));

        var plain = Assert.Single(Of(book));
        var summarized = Assert.Single(Of(book, _ => "Es geht um Schlaf. Mehr."));

        Assert.Equal("Erster", plain.Summary);
        Assert.Equal("Es geht um Schlaf.", summarized.Summary);
    }

    [Fact]
    public void Of_PrologueGroup_UsesBookTitle_RevealsFirstBlock()
    {
        var book = BookOf(Vault(Block("Vor A", "2026-10-01T09:00") + Block("Vor B", "2026-10-01T10:00") + "- # Eins\n"));

        var change = Assert.Single(Of(book));

        Assert.Equal(2, change.Count);
        Assert.Equal("Alpha", change.SectionPath);
        Assert.Equal(book.Root.TextBlocks.First().Key, change.RevealKey);
    }

    [Fact]
    public async Task List_GroupShowsCount_ClickRevealsHeading()
    {
        using var tv = Vault("- # Eins\n" + Indented(Block("Erster Text", "2026-10-03T10:00") + Block("Zweiter Text", "2026-10-03T12:00")));
        await OpenSessionAsync(tv);
        var cut = Render<OutlinePane>();

        var item = Assert.Single(cut.FindAll(".ne-recent-item"));

        Assert.Contains("2 Blöcke", item.TextContent);
        item.Click();

        Assert.Equal(State.CurrentBook!.Root.Children.Single().Key, State.PendingReveal);
    }

    [Fact]
    public void When_Today_Yesterday_Date()
    {
        var now = new DateTime(2026, 10, 19, 15, 0, 0);

        Assert.Equal("heute 14:32", RecentChanges.When(new DateTime(2026, 10, 19, 14, 32, 0), now, L));
        Assert.Equal("gestern 09:10", RecentChanges.When(new DateTime(2026, 10, 18, 9, 10, 0), now, L));
        Assert.Equal("17.10.2026", RecentChanges.When(new DateTime(2026, 10, 17, 9, 10, 0), now, L));
    }

    [Fact]
    public void When_Midnight()
    {
        Assert.Equal(
            "gestern 23:59", RecentChanges.When(new DateTime(2026, 10, 19, 23, 59, 0), new DateTime(2026, 10, 20, 0, 1, 0), L));
    }

    [Fact]
    public async Task List_ShowsEntries_ClickReveals()
    {
        using var tv = Vault("- # Eins\n" + Indented(Block("Erster Text", "2026-10-03T10:00") + Block("Zweiter Text", "2026-10-06T08:30")));
        await OpenSessionAsync(tv);
        var cut = Render<OutlinePane>();

        var items = cut.FindAll(".ne-recent-item");

        Assert.Equal(2, items.Count);
        Assert.Contains("Zweiter Text", items[0].TextContent);
        Assert.Contains("heute 08:30", items[0].TextContent);
        Assert.Contains("Eins", items[0].TextContent);
        Assert.Contains("03.10.2026", items[1].TextContent);
        // Date, summary, then the section path small below (spec 4.1).
        Assert.Equal(
            ["ne-recent-when", "ne-recent-summary", "ne-recent-path"], items[0].Children.Select(child => child.ClassName));

        items[1].Click();

        Assert.Equal(State.CurrentBook!.Root.Children.Single().TextBlocks.First().Key, State.PendingReveal);
    }

    [Fact]
    public async Task List_Empty_Hint()
    {
        using var tv = Vault("- # Eins\n" + Indented(Block("Ohne Datum", null)));
        await OpenSessionAsync(tv);

        var cut = Render<OutlinePane>();

        Assert.Equal("Noch keine Änderungen mit Datum", cut.Find(".ne-recent-empty").TextContent);
        Assert.Empty(cut.FindAll(".ne-recent-item"));
    }

    [Fact]
    public void List_NoBook_Hidden()
    {
        var cut = Render<RecentChangesList>();

        Assert.Empty(cut.FindAll(".ne-recent"));
    }

    [Fact]
    public async Task List_UpdatesAfterSave()
    {
        using var tv = Vault("- # Eins\n" + Indented(Block("Erster Text", "2026-10-05T10:00")));
        var session = await OpenSessionAsync(tv);
        var cut = Render<OutlinePane>();
        Assert.Single(cut.FindAll(".ne-recent-item"));
        var path = Path.Combine(tv.Root, BookPath);

        File.WriteAllText(path, "title:: Alpha\ntype:: book\n\n- # Eins\n" + Indented(Block("Erster Text", "2026-10-05T10:00") + Block("Neuer Text", Stamp)));
        session.HandleExternalChange(path);
        State.RefreshBook();
        State.Notify();

        cut.WaitForAssertion(() =>
        {
            var items = cut.FindAll(".ne-recent-item");
            Assert.Equal(2, items.Count);
            Assert.Contains("Neuer Text", items[0].TextContent);
        });
    }

    [Fact]
    public async Task List_CursorMoves_DoNotRecompute_NewBookInstanceDoes()
    {
        using var tv = Vault("- # Eins\n" + Indented(Block("Erster Text", "2026-10-05T10:00")));
        var session = await OpenSessionAsync(tv);
        var cut = Render<RecentChangesList>();
        var computed = cut.Instance.Recomputes;

        State.Cursor = new CursorInfo(ElementKind.Heading, State.CurrentBook!.Root.Children.Single().Key, null, 0);
        State.Notify();
        State.Notify();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-recent-item")));
        Assert.Equal(computed, cut.Instance.Recomputes);

        var path = Path.Combine(tv.Root, BookPath);
        File.WriteAllText(path, "title:: Alpha\ntype:: book\n\n- # Eins\n" + Indented(Block("Erster Text", "2026-10-05T10:00") + Block("Neuer Text", Stamp)));
        session.HandleExternalChange(path);
        State.RefreshBook();
        State.Notify();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".ne-recent-item").Count));
        Assert.True(cut.Instance.Recomputes > computed);
    }

    [Fact]
    public void RecentChanges_IgnoresAppendixEntries()
    {
        var book = BookOf(Vault(
            "- # Eins\n" + Indented(Block("Buchtext", "2026-09-01T10:00")) +
            "- # Glossar\n  anhang:: glossar\n" + Indented(Block("Achtsamkeit", "2026-09-20T10:00"))));

        var changes = Of(book);

        Assert.Equal(["Buchtext"], changes.Select(c => c.Excerpt));
    }
}
