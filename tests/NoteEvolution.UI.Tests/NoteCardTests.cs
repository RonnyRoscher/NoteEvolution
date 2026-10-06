using System.Resources;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NoteEvolution.Core.Vaults;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;
using static NoteEvolution.UI.Tests.NotesTestData;

namespace NoteEvolution.UI.Tests;

public class NoteCardTests : UiTestContext
{
    private async Task<VaultSession> OpenAlphaAsync(TestVault vault)
    {
        var session = await OpenSessionAsync(vault);
        State.CurrentBook = session.Vault.FindBook("Buch - Alpha");
        State.CurrentSectionKey = State.CurrentBook!.Root.Children.First().Key;
        return session;
    }

    private IRenderedComponent<NoteCard> RenderCard(NoteBlock note, int? percent = null) =>
        Render<NoteCard>(p => p.Add(c => c.Note, note).Add(c => c.Percent, percent));

    [Fact]
    public async Task NoteCard_UsedShowsCheck_ClickNavigates()
    {
        using var tv = Create();
        var session = await OpenAlphaAsync(tv);
        var note = Note(session, "Gedächtnis wird");
        var cut = RenderCard(note);

        cut.Find(".ne-note-used").Click();

        var beta = session.Vault.FindBook(BetaBook)!;
        Assert.Equal(BetaBook, State.CurrentBook!.LinkName);
        var section = beta.Root.Children.Single().Children.Single();
        Assert.Equal("Beta Unter", section.Title);
        Assert.Equal(section.Key, State.CurrentSectionKey);
        Assert.Null(State.CursorTextBlockKey);
    }

    [Fact]
    public async Task NoteCard_UnusedNote_HasNoCheck()
    {
        using var tv = Create();
        var session = await OpenAlphaAsync(tv);

        var cut = RenderCard(Note(session, "Gedächtnis braucht"));

        Assert.Empty(cut.FindAll(".ne-note-used"));
    }

    [Fact]
    public async Task NoteCard_UsedWithoutBlockReference_SwitchesOnlyTheBook()
    {
        using var tv = Create();
        var session = await OpenAlphaAsync(tv);
        var journalNote = Note(session, "Gedächtnis und Musik");
        var cut = RenderCard(journalNote);

        cut.Find(".ne-note-used").Click();

        Assert.Equal(BetaBook, State.CurrentBook!.LinkName);
        Assert.Equal(State.CurrentBook.Root.Key, State.CurrentSectionKey);
    }

    [Fact]
    public async Task NoteCard_ShowsDateOrPageNameAndScore()
    {
        using var tv = Create();
        var session = await OpenAlphaAsync(tv);

        var journal = RenderCard(Note(session, "Gedächtnis im Alltag"), 87);
        var page = RenderCard(Note(session, "Gedächtnis braucht"));

        Assert.Equal("01.10.2026", journal.Find(".ne-note-origin").TextContent);
        Assert.Equal("87 %", journal.Find(".ne-note-score").TextContent);
        Assert.Equal("Ideen", page.Find(".ne-note-origin").TextContent);
        Assert.Empty(page.FindAll(".ne-note-score"));
    }

    [Fact]
    public async Task NoteCard_AdoptChild_CallsAdoptWithChildKey()
    {
        using var tv = Create();
        var session = await OpenAlphaAsync(tv);
        var cut = RenderCard(Note(session, "Gedächtnis braucht"));

        cut.Find(".ne-note-child-adopt").Click();

        var book = session.Vault.FindBook("Buch - Alpha")!;
        var texts = book.Root.Children.First().TextBlocks.Select(t => t.Text).ToList();
        Assert.Equal(["Erster Text", "Zweiter Text", "Unterpunkt Träume"], texts);
        var adopted = book.Root.Children.First().TextBlocks.Last();
        Assert.Single(adopted.Sources);
        var child = Note(session, "Unterpunkt Träume");
        Assert.Equal(child.Block.Id, adopted.Sources[0]);
        Assert.Contains("used-in:: [[Buch - Alpha]]", tv.Read(NotesPath));
        Assert.Same(book, State.CurrentBook);
    }

    [Fact]
    public async Task NoteCard_AdoptWithCursor_InsertsAfterCursorBlock()
    {
        using var tv = Create();
        var session = await OpenAlphaAsync(tv);
        var first = State.CurrentBook!.Root.Children.First().TextBlocks.First();
        State.CursorTextBlockKey = first.Key;
        var cut = RenderCard(Note(session, "Gedächtnis braucht"));

        cut.Find(".ne-note-adopt").Click();

        var texts = State.CurrentBook!.Root.Children.First().TextBlocks.Select(t => t.Text).ToList();
        Assert.Equal(["Erster Text", "Gedächtnis braucht Schlaf", "Zweiter Text"], texts);
    }

    [Fact]
    public async Task NoteCard_AdoptUsedNote_DoesNotAskAndShowsTwoUsages()
    {
        using var tv = Create();
        var session = await OpenAlphaAsync(tv);
        var cut = RenderCard(Note(session, "Gedächtnis wird"));
        Assert.Empty(cut.FindAll(".ne-note-used-count"));

        cut.Find(".ne-note-adopt").Click();

        Assert.Equal("2", cut.Find(".ne-note-used-count").TextContent);
        Assert.Equal(2, Note(session, "Gedächtnis wird").Usages.Count);
    }

    [Fact]
    public async Task NoteCard_AdoptRefreshesSearchIndexAndNotifies()
    {
        using var tv = Create();
        var session = await OpenAlphaAsync(tv);
        var notified = 0;
        State.Changed += () => notified++;
        var cut = RenderCard(Note(session, "Gedächtnis braucht"));

        cut.Find(".ne-note-adopt").Click();

        Assert.True(notified > 0);
        var hits = session.Search.Search(new NoteEvolution.AI.Search.SearchQuery("Schlaf", new NoteFilter(HideUsed: true, null, null)));
        Assert.Empty(hits);
    }

    [Fact]
    public async Task NoteCard_SearchIndexFailsAfterAdopt_BookIsStillRefreshed()
    {
        using var tv = Create();
        var session = await OpenAlphaAsync(tv);
        var staleBook = State.CurrentBook!;
        var note = Note(session, "Gedächtnis braucht");
        var cut = RenderCard(note);
        ((IDisposable)session.Search).Dispose();
        Assert.ThrowsAny<Exception>(() => session.Search.UpdatePage(session.Notes, note.Page.FilePath));

        cut.Find(".ne-note-adopt").Click();

        Assert.NotSame(staleBook, State.CurrentBook);
        Assert.Contains("Gedächtnis braucht Schlaf", State.CurrentBook!.Root.Children.First().TextBlocks.Select(t => t.Text));
        Assert.Empty(cut.FindAll(".ne-note-error"));
    }

    [Fact]
    public async Task NoteCard_FlushFails_ShowsGenericMessageAndAdoptsNothing()
    {
        using var tv = Create();
        var session = await OpenAlphaAsync(tv);
        State.FlushEditor = () => throw new InvalidOperationException("editor broke");
        var before = tv.Read(AlphaPath);
        var cut = RenderCard(Note(session, "Gedächtnis braucht"));

        cut.Find(".ne-note-adopt").Click();

        var localizer = Services.GetRequiredService<IStringLocalizer<Strings>>();
        Assert.Equal(localizer["NoteAdoptFailed"].Value, cut.Find(".ne-note-error").TextContent);
        Assert.Equal(before, tv.Read(AlphaPath));
    }

    [Fact]
    public async Task NoteCard_AdoptFlushesTheEditorFirst()
    {
        using var tv = Create();
        var session = await OpenAlphaAsync(tv);
        var flushed = false;
        State.FlushEditor = () =>
        {
            flushed = !File.ReadAllText(Path.Combine(tv.Root, "pages", "Ideen.md")).Contains("used-in:: [[Buch - Alpha]]");
            return Task.CompletedTask;
        };
        var cut = RenderCard(Note(session, "Gedächtnis braucht"));

        cut.Find(".ne-note-adopt").Click();

        Assert.True(flushed, "the flush hook runs before the adoption writes anything");
    }

    [Fact]
    public async Task NoteCard_NoteOnReadOnlyPage_AdoptDisabledWithHint()
    {
        using var tv = TestVault.Create(
            (AlphaPath, "title:: Alpha\ntype:: book\n\n- # Eins\n"),
            ("pages/Kaputt.md", "- Gedächtnis kaputt\n\t- unten\n  ```\n  offener Codeblock\n"));
        var session = await OpenAlphaAsync(tv);
        var broken = session.Vault.Pages.Single(p => p.IsReadOnly);
        Assert.Equal("Kaputt", broken.Name);
        var cut = RenderCard(session.Notes.All().First(n => n.Page == broken));

        Assert.All(cut.FindAll(".ne-note-adopt, .ne-note-child-adopt"), b => Assert.True(b.HasAttribute("disabled")));
        Assert.NotEmpty(cut.FindAll(".ne-note-hint"));
    }

    [Fact]
    public async Task NoteCard_OpenConflict_RefusesWithoutChanges()
    {
        using var tv = Create();
        var session = await OpenAlphaAsync(tv);
        var book = State.CurrentBook!;
        book.Root.Children.First().TextBlocks.First().Block.SetContent("Lokal geändert");
        File.WriteAllText(Path.Combine(tv.Root, AlphaPath.Replace('/', Path.DirectorySeparatorChar)),
            "title:: Alpha\ntype:: book\n\n- # Eins\n\t- Extern geändert\n\t  id:: " + AlphaFirstId + "\n");
        session.HandleExternalChange(book.Page.FilePath);
        Assert.True(session.HasOpenConflict(book.Page.FilePath));
        var before = tv.Read(AlphaPath);
        var notesBefore = tv.Read(NotesPath);
        var cut = RenderCard(Note(session, "Gedächtnis braucht"));

        cut.Find(".ne-note-adopt").Click();

        Assert.NotEmpty(cut.FindAll(".ne-note-error"));
        Assert.Equal(before, tv.Read(AlphaPath));
        Assert.Equal(notesBefore, tv.Read(NotesPath));
    }

    [Fact]
    public async Task NoteCard_ExternalChangeDuringAdopt_IsHandedToTheSession()
    {
        using var tv = Create();
        var session = await OpenAlphaAsync(tv);
        var bookPath = Path.Combine(tv.Root, AlphaPath.Replace('/', Path.DirectorySeparatorChar));
        File.AppendAllText(bookPath, "\t- Von außen ergänzt\n");
        var changed = new List<string>();
        session.PagesChanged += changed.Add;
        var cut = RenderCard(Note(session, "Gedächtnis braucht"));

        cut.Find(".ne-note-adopt").Click();

        Assert.Contains(changed, p => string.Equals(p, session.Vault.FindBook("Buch - Alpha")!.Page.FilePath, StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(cut.FindAll(".ne-note-error"));
        Assert.DoesNotContain("Schlaf", tv.Read(AlphaPath));
    }

    [Fact]
    public async Task NoteCard_LongText_CollapsedAfterThreeLines()
    {
        using var tv = TestVault.Create(
            (AlphaPath, "title:: Alpha\ntype:: book\n\n- # Eins\n"),
            (NotesPath, "- Zeile eins\n  Zeile zwei\n  Zeile drei\n  Zeile vier\n  Zeile fünf\n- Kurz\n"));
        var session = await OpenAlphaAsync(tv);
        var cut = RenderCard(Note(session, "Zeile eins"));

        Assert.Contains("collapsed", cut.Find(".ne-note-body").ClassList);
        cut.Find(".ne-note-more").Click();
        Assert.DoesNotContain("collapsed", cut.Find(".ne-note-body").ClassList);

        var shortCard = RenderCard(Note(session, "Kurz"));
        Assert.Empty(shortCard.FindAll(".ne-note-more"));
        Assert.DoesNotContain("collapsed", shortCard.Find(".ne-note-body").ClassList);
    }

    [Fact]
    public async Task NoteCard_SubBulletsCountTowardsTheThreeLines()
    {
        using var tv = TestVault.Create(
            (AlphaPath, "title:: Alpha\ntype:: book\n\n- # Eins\n"),
            (NotesPath, "- Eins\n\t- Zwei\n\t\t- Drei\n\t\t\t- Vier\n"));
        var session = await OpenAlphaAsync(tv);

        var cut = RenderCard(Note(session, "Eins"));

        Assert.Equal(3, cut.FindAll(".ne-note-child").Count);
        Assert.Contains("collapsed", cut.Find(".ne-note-body").ClassList);
    }

    [Fact]
    public async Task NoteCard_LogseqLink_UsesBlockIdOrPage()
    {
        using var tv = Create();
        var session = await OpenAlphaAsync(tv);
        var folder = Path.GetFileName(tv.Root);

        RenderCard(Note(session, "Gedächtnis wird")).Find(".ne-note-logseq").Click();
        RenderCard(Note(session, "Gedächtnis braucht")).Find(".ne-note-logseq").Click();

        Assert.Equal(
            [
                $"logseq://graph/{Uri.EscapeDataString(folder)}?block-id={UsedNoteId}",
                $"logseq://graph/{Uri.EscapeDataString(folder)}?page=Ideen",
            ],
            Platform.OpenedUris);
    }

    [Fact]
    public async Task NoteCard_LogseqLink_EncodesPageName()
    {
        using var tv = TestVault.Create(
            (AlphaPath, "title:: Alpha\ntype:: book\n\n- # Eins\n"),
            ("pages/Große Ideen & mehr.md", "- Etwas\n"));
        var session = await OpenAlphaAsync(tv);

        RenderCard(Note(session, "Etwas")).Find(".ne-note-logseq").Click();

        Assert.EndsWith("?page=Gro%C3%9Fe%20Ideen%20%26%20mehr", Platform.OpenedUris.Single());
    }

    [Fact]
    public async Task NoteCard_DragStartAndEnd_SetAndClearDraggedKey()
    {
        using var tv = Create();
        var session = await OpenAlphaAsync(tv);
        var note = Note(session, "Gedächtnis braucht");
        var cut = RenderCard(note);
        var card = cut.Find(".ne-note-card");

        Assert.Equal("true", card.GetAttribute("draggable"));
        Assert.Equal(note.Key.ToString(), card.GetAttribute("data-note-key"));
        card.DragStart();
        Assert.Equal(note.Key, State.DraggedNoteKey);
        card.DragEnd();
        Assert.Null(State.DraggedNoteKey);
    }

    [Fact]
    public async Task NoteCard_NoGermanLiterals()
    {
        // Every UI text comes out as its resource key; note data (text, date, page name, context) is excluded.
        Services.AddSingleton<IStringLocalizer<Strings>>(new KeyLocalizer());
        using var tv = Create();
        var session = await OpenAlphaAsync(tv);
        var resources = new ResourceManager(typeof(Strings));

        foreach (var startsWith in new[] { "Gedächtnis wird", "Gedächtnis braucht", "Gedächtnis und Musik" })
        {
            var cut = RenderCard(Note(session, startsWith), 55);
            foreach (var text in UiTexts(cut.Find(".ne-note-card")))
            {
                Assert.True(
                    !text.Any(char.IsLetter) || resources.GetString(text, System.Globalization.CultureInfo.InvariantCulture) is not null,
                    $"'{text}' is neither a resource key nor free of letters");
            }
        }
    }

    private static IEnumerable<string> UiTexts(AngleSharp.Dom.IElement root)
    {
        foreach (var element in root.QuerySelectorAll("*").Prepend(root))
        {
            if (element.ClosestOrSelfIsData())
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

            foreach (var text in element.ChildNodes.OfType<AngleSharp.Dom.IText>().Select(t => t.Data.Trim()).Where(t => t.Length > 0))
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
}

internal static class ElementExtensions
{
    /// <summary>The element or one of its ancestors is note data (text, origin, context).</summary>
    public static bool ClosestOrSelfIsData(this AngleSharp.Dom.IElement element)
    {
        for (var e = element; e is not null; e = e.ParentElement)
        {
            if (e.ClassList.Contains("ne-note-text") || e.ClassList.Contains("ne-note-child-text")
                || e.ClassList.Contains("ne-note-origin") || e.ClassList.Contains("ne-note-context"))
            {
                return true;
            }
        }

        return false;
    }
}
