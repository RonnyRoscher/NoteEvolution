using AngleSharp.Dom;
using Bunit;
using NoteEvolution.AI.Embeddings;
using NoteEvolution.AI.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Vaults;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;
using static NoteEvolution.UI.Tests.NotesTestData;

namespace NoteEvolution.UI.Tests;

/// <summary>The "Wohin damit?" buttons of a note card and of its sub-bullets (spec 3).</summary>
public class WhereToTests : UiTestContext
{
    private const string NoteText = "Schlaf und Träume im Alltag";
    private const string OtherText = "Musik Gitarre Klavier Notiz";
    private const string ChildText = "Rosen und Tomaten pflanzen";
    private const string NoteId = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";

    private readonly List<TestVault> _vaults = [];

    protected override void Dispose(bool disposing)
    {
        // The session (and its vector cache in the vault) is closed first, then the vault folders are deleted.
        base.Dispose(disposing);
        if (disposing)
        {
            _vaults.ForEach(v => v.Dispose());
        }
    }

    /// <summary>
    /// A book with a prologue and seven sections (the first only with a sub-section), each with one text block, and
    /// two notes, the second with a sub-bullet. <paramref name="bookExtra"/> goes into the sub-section, after its text
    /// block; <paramref name="noteExtra"/> are property lines of the first note.
    /// </summary>
    private TestVault Vault(string bookExtra = "", string noteExtra = "")
    {
        var vault = TestVault.Create(
            (AlphaPath,
                "title:: Alpha\ntype:: book\n\n" +
                "- Vorwort über Schlaf und Träume\n" +
                "- # Schlafen\n\t- ## Träume\n\t\t- Schlaf und Träume im Alltag\n" + bookExtra +
                "- # Musizieren\n\t- Musik Gitarre Klavier spielen\n" +
                "- # Kochen\n\t- Suppe kochen mit Gemüse\n" +
                "- # Reisen\n\t- Zug und Flugzeug nach Rom\n" +
                "- # Garten\n\t- Rosen und Tomaten pflanzen\n" +
                "- # Sport\n\t- Laufen und Schwimmen im See\n" +
                "- # Lesen\n\t- Romane und Gedichte am Abend\n"),
            (NotesPath, $"- {NoteText}\n{noteExtra}- {OtherText}\n\t- {ChildText}\n"));
        _vaults.Add(vault);
        return vault;
    }

    /// <summary>Every text block below <paramref name="node"/> in outline order.</summary>
    private static IEnumerable<TextBlock> TextBlocks(OutlineNode node) =>
        node.TextBlocks.Concat(node.Children.SelectMany(TextBlocks));

    /// <summary>The text block of the current book whose text is <paramref name="text"/>.</summary>
    private TextBlock TextBlock(string text) => TextBlocks(State.CurrentBook!.Root).Single(tb => tb.Text == text);

    /// <summary>The heading path a hit in <paramref name="textBlock"/> shows: the titles below the book root.</summary>
    private static string PathOf(TextBlock textBlock)
    {
        var titles = new List<string>();
        for (var node = textBlock.Section; node.Level > 0; node = node.Parent!)
        {
            titles.Insert(0, node.Title);
        }

        return string.Join(" / ", titles);
    }

    private static string Excerpt(IElement item) => item.QuerySelector(".ne-note-whereto-excerpt")!.TextContent.Trim();

    private static string PathText(IElement item) => item.QuerySelector(".ne-note-whereto-path")?.TextContent.Trim() ?? "";

    private static int PercentOf(IElement item) =>
        int.Parse(item.QuerySelector(".ne-note-whereto-percent")!.TextContent.Replace("%", "").Trim(), System.Globalization.CultureInfo.InvariantCulture);

    private string Text(string key) => Services.GetRequiredService<IStringLocalizer<Strings>>()[key].Value;

    private IRenderedComponent<NoteCard> RenderCard(VaultSession session) =>
        Render<NoteCard>(p => p.Add(c => c.Note, Note(session, NoteText)));

    [Fact]
    public async Task WhereTo_Note_ShowsFiveTextBlocks_PathExcerptPercent()
    {
        var tv = Vault();
        var session = await OpenWithAiAsync(tv);
        var cut = RenderCard(session);

        cut.Find(".ne-note-whereto").Click();

        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".ne-note-whereto-item").Count));
        var items = cut.FindAll(".ne-note-whereto-item");
        var percents = items.Select(PercentOf).ToList();
        Assert.Equal(100, percents[0]);
        Assert.Equal(percents.OrderByDescending(p => p), percents);

        // The best hit is the text block with the note's own text, in a sub-section.
        Assert.Equal(NoteText, Excerpt(items[0]));
        Assert.Equal("Schlafen / Träume", PathText(items[0]));
        var excerpts = items.Select(Excerpt).ToList();
        Assert.Equal(excerpts.Count, excerpts.Distinct().Count());
        Assert.All(items, item => Assert.Equal(PathOf(TextBlock(Excerpt(item))), PathText(item)));

        // The prologue is among the hits and has no heading path.
        var prologue = items.Single(item => Excerpt(item) == "Vorwort über Schlaf und Träume");
        Assert.Equal("", PathText(prologue));
        Assert.Empty(cut.FindAll(".ne-note-whereto-used"));
    }

    [Fact]
    public async Task WhereTo_SubBullet_UsesItsOwnText()
    {
        var tv = Vault();
        var embedder = new FakeEmbedder();
        var session = await OpenWithAiAsync(tv, embedder);
        var note = Note(session, OtherText);
        var child = note.Block.Children.Single();
        var cut = Render<NoteCard>(p => p.Add(c => c.Note, note));

        cut.Find(".ne-note-child-whereto").Click();

        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".ne-note-body .ne-note-whereto-item").Count));
        // The sub-bullet's text (with its descendants, as a passage) was searched, not the note's.
        Assert.Contains(EmbeddingText.ForNoteBlock(child), embedder.Texts);
        var first = cut.FindAll(".ne-note-body .ne-note-whereto-item")[0];
        Assert.Equal(ChildText, Excerpt(first));
        Assert.Equal("Garten", PathText(first));
        Assert.Empty(cut.FindAll(".ne-note-card > .ne-note-whereto-results"));

        // The note's own list opens beside it.
        cut.Find(".ne-note-whereto").Click();
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".ne-note-card > .ne-note-whereto-results .ne-note-whereto-item").Count));
        Assert.Equal(10, cut.FindAll(".ne-note-whereto-item").Count);

        // The sub-bullet's toggle closes only its own list.
        cut.Find(".ne-note-child-whereto").Click();
        Assert.Empty(cut.FindAll(".ne-note-body .ne-note-whereto-item"));
        Assert.Equal(5, cut.FindAll(".ne-note-whereto-item").Count);
    }

    [Fact]
    public async Task WhereTo_Click_Reveals_ListStaysOpen()
    {
        var tv = Vault();
        var session = await OpenWithAiAsync(tv);
        var book = State.CurrentBook!;
        var flushed = 0;
        State.FlushEditor = () =>
        {
            flushed++;
            return Task.CompletedTask;
        };
        var cut = RenderCard(session);
        cut.Find(".ne-note-whereto").Click();
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".ne-note-whereto-item").Count));
        var target = TextBlock(Excerpt(cut.FindAll(".ne-note-whereto-item")[1]));

        cut.FindAll(".ne-note-whereto-item")[1].Click();

        cut.WaitForAssertion(() => Assert.Equal(target.Key, State.PendingReveal));
        Assert.Equal(1, flushed);
        Assert.Equal((ElementKind.TextBlock, target.Key, 0), (State.Cursor!.Kind, State.Cursor.Key, State.Cursor.Offset));
        Assert.Equal(target.Section.Key, State.CurrentSectionKey);
        Assert.Same(book, State.CurrentBook);
        Assert.Equal(5, cut.FindAll(".ne-note-whereto-item").Count);

        // A second hit can be looked at the same way.
        var second = TextBlock(Excerpt(cut.FindAll(".ne-note-whereto-item")[2]));
        cut.FindAll(".ne-note-whereto-item")[2].Click();
        cut.WaitForAssertion(() => Assert.Equal(second.Key, State.PendingReveal));
        Assert.Equal(2, flushed);
        Assert.Equal(5, cut.FindAll(".ne-note-whereto-item").Count);
    }

    [Fact]
    public async Task WhereTo_AlreadySource_ShowsCheck()
    {
        var tv = Vault(
            bookExtra: $"\t\t- Suppe aus Schlaf und Träume\n\t\t  source:: (({NoteId}))\n",
            noteExtra: $"  id:: {NoteId}\n");
        var session = await OpenWithAiAsync(tv);
        var cut = RenderCard(session);

        cut.Find(".ne-note-whereto").Click();

        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".ne-note-whereto-item").Count));
        var used = cut.FindAll(".ne-note-whereto-item").Single(item => Excerpt(item) == "Suppe aus Schlaf und Träume");
        Assert.Equal("✓", used.QuerySelector(".ne-note-whereto-used")!.TextContent.Trim());
        Assert.Single(cut.FindAll(".ne-note-whereto-used"));
    }

    [Fact]
    public async Task WhereTo_AdoptAfterJump_UsesHitAsCursor()
    {
        var tv = Vault();
        var session = await OpenWithAiAsync(tv);
        var cut = RenderCard(session);
        cut.Find(".ne-note-whereto").Click();
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".ne-note-whereto-item").Count));
        var hitText = Excerpt(cut.FindAll(".ne-note-whereto-item")[1]);

        // The cursor was in another block before the jump.
        SetCursorInTextBlock(TextBlock(Excerpt(cut.FindAll(".ne-note-whereto-item")[2])).Key);
        cut.FindAll(".ne-note-whereto-item")[1].Click();

        cut.Find(".ne-note-adopt-more").Click();
        cut.Find(".ne-adopt-after").Click();

        // The note became a new text block right after the hit.
        cut.WaitForAssertion(() =>
        {
            var blocks = TextBlock(hitText).Section.TextBlocks.ToList();
            var index = blocks.FindIndex(tb => tb.Text == hitText);
            Assert.Equal(NoteText, blocks[index + 1].Text);
            Assert.Contains(Note(session, NoteText).Block.Id!.Value, blocks[index + 1].Sources);
        });
        Assert.Equal(5, cut.FindAll(".ne-note-whereto-item").Count);
    }


    [Fact]
    public async Task WhereTo_CardReusedForAnotherNote_ClosesTheListAndDropsALateResult()
    {
        var tv = Vault();
        var embedder = new FakeEmbedder();
        var session = await OpenWithAiAsync(tv, embedder);
        using var gate = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var cut = RenderCard(session);
        try
        {
            // The request for the first note is running (its sections are embedded) when the card gets another note.
            embedder.OnEmbed = (_, _) =>
            {
                entered.Set();
                gate.Wait(TimeSpan.FromSeconds(10));
            };
            cut.Find(".ne-note-whereto").Click();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            cut.Render(p => p.Add(c => c.Note, Note(session, OtherText)));
            Assert.Empty(cut.FindAll(".ne-note-whereto-busy"));

            gate.Set();
            await Task.Delay(300);
            cut.Render();

            Assert.Empty(cut.FindAll(".ne-note-whereto-item"));
            Assert.Empty(cut.FindAll(".ne-note-whereto-busy"));
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public async Task WhereTo_FocusedNoteChanges_NoListForTheOtherNote()
    {
        var tv = Vault();
        var session = await OpenWithAiAsync(tv);
        State.FocusedNoteKey = Note(session, NoteText).Key;
        var cut = Render<NotesPane>();
        cut.Find(".ne-note-focused .ne-note-whereto").Click();
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".ne-note-focused .ne-note-whereto-item").Count));

        State.FocusedNoteKey = Note(session, OtherText).Key;
        cut.WaitForState(() => { State.Notify(); return cut.Find(".ne-note-focused .ne-note-text").TextContent.Contains("Musik", StringComparison.Ordinal); });

        Assert.Empty(cut.FindAll(".ne-note-focused .ne-note-whereto-item"));
    }

    [Fact]
    public async Task WhereTo_BookChanged_ListCloses()
    {
        var tv = TestVault.Create(
            (AlphaPath, "title:: Alpha\ntype:: book\n\n- # Eins\n\t- Schlaf und Träume\n"),
            ("pages/Buch - Beta.md", "title:: Beta\ntype:: book\n\n- # Zwei\n\t- Musik Gitarre\n"),
            (NotesPath, $"- {NoteText}\n\t- {ChildText}\n"));
        _vaults.Add(tv);
        var session = await OpenWithAiAsync(tv);
        var cut = RenderCard(session);
        cut.Find(".ne-note-whereto").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-note-whereto-item")));
        cut.Find(".ne-note-child-whereto").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".ne-note-whereto-item").Count));

        State.CurrentBook = session.Vault.FindBook("Buch - Beta");
        cut.Render();

        Assert.Empty(cut.FindAll(".ne-note-whereto-item"));
        Assert.Empty(cut.FindAll(".ne-note-whereto-results"));
    }
    [Fact]
    public async Task WhereTo_NoHits_ShowsHint()
    {
        // The embedder fails after the indexing (the sections need embedding): the AI answers with nothing.
        var tv = Vault();
        var embedder = new FakeEmbedder();
        var session = await OpenWithAiAsync(tv, embedder);
        embedder.Throws = true;
        var cut = RenderCard(session);

        cut.Find(".ne-note-whereto").Click();

        cut.WaitForAssertion(() => Assert.Equal(Text("NoteWhereToNone"), cut.Find(".ne-note-whereto-none").TextContent.Trim()));
        Assert.Empty(cut.FindAll(".ne-note-whereto-item"));
    }

    [Fact]
    public async Task WhereTo_HiddenWithoutAi()
    {
        var tv = Vault();
        var session = await OpenSessionAsync(tv);

        var off = Render<NoteCard>(p => p.Add(c => c.Note, Note(session, OtherText)));

        Assert.Empty(off.FindAll(".ne-note-whereto"));
        Assert.Empty(off.FindAll(".ne-note-child-whereto"));
        Assert.Single(off.FindAll(".ne-note-child-adopt"));
    }

    [Fact]
    public async Task WhereTo_HiddenWithoutBook()
    {
        var tv = TestVault.Create((NotesPath, $"- {NoteText}\n"));
        _vaults.Add(tv);
        var session = await OpenWithAiAsync(tv);
        Assert.Null(State.CurrentBook);

        var cut = RenderCard(session);

        Assert.Empty(cut.FindAll(".ne-note-whereto"));
    }

    [Fact]
    public async Task WhereTo_AiModelMissing_Hidden()
    {
        var tv = Vault();
        var session = await OpenSessionAsync(tv, UseAi(installed: false, () => new FakeEmbedder()));

        var cut = RenderCard(session);

        Assert.Empty(cut.FindAll(".ne-note-whereto"));
    }
}
