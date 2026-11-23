using System.Resources;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NoteEvolution.AI.Tests;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Repetitions;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Editor;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>
/// The tab „Wiederholungen“ (repetitions tab spec 4): one hit at a time, its origin revealed in the book view, the other
/// side in the reader of the tab; ‹ › step, ⇄ swaps, „Gewollt“ and „Verweis“ act on the hit, the filters narrow the
/// hits. The sets come from the real tracker: literal pairs from blocks sharing eight words, semantic ones from the fake
/// embedder (equal letters, other order).
/// </summary>
public class RepetitionsTabTests : UiTestContext
{
    private const string BookPath = "pages/Buch - Alpha.md";

    private const string Head = "title:: Alpha\ntype:: book\n\n";

    private const string RunX = "eins zwei drei vier fuenf sechs sieben acht neun";

    private const string RunY = "rot gruen blau gelb weiss schwarz braun lila";

    /// <summary>Two literal pairs: alpha~beta in "Eins" and gamma~delta in "Zwei".</summary>
    private const string Simple = Head
        + $"- # Eins\n\t- alpha {RunX}\n\t- beta {RunX}\n"
        + $"- # Zwei\n\t- gamma {RunY}\n\t- delta {RunY}\n";

    private readonly List<TestVault> _vaults = [];

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _vaults.ForEach(v => v.Dispose());
        }
    }

    private Book Book => State.CurrentBook!;

    private VaultSession Session => State.Session!;

    private string Text(string key, params object[] arguments) =>
        Services.GetRequiredService<IStringLocalizer<Strings>>()[key, arguments].Value;

    private Guid KeyOf(string start) => Book.Page.AllBlocks().Single(b => b.Content.StartsWith(start, StringComparison.Ordinal)).Key;

    private async Task<VaultSession> OpenAsync(string bookText = Simple, bool ai = false)
    {
        var vault = TestVault.Create((BookPath, bookText));
        _vaults.Add(vault);
        State.Session?.Dispose();
        var session = ai ? await OpenWithAiAsync(vault) : await OpenSessionAsync(vault);
        await session.RepetitionsIdle();
        return session;
    }

    private IRenderedComponent<RepetitionsTab> RenderTab() => Render<RepetitionsTab>();

    private static bool Disabled(AngleSharp.Dom.IElement element) => element.HasAttribute("disabled");

    /// <summary>The block the reader highlights (the other side of the hit).</summary>
    private static string ReaderSide(IRenderedComponent<RepetitionsTab> cut) =>
        Assert.Single(cut.FindAll(".ne-reader .ne-rep-current-block")).Id!;

    private static string ReaderId(Guid key) => $"ne-reader-{key}";

    /// <summary>Changes the text of the block starting with <paramref name="start"/>, saves it as the editor would and waits for the analysis.</summary>
    private async Task EditAsync(IRenderedComponent<RepetitionsTab> cut, string start, string content)
    {
        Book.Page.AllBlocks().First(b => b.Content.StartsWith(start, StringComparison.Ordinal)).SetContent(content);
        Session.Writer.Save(Book.Page);
        await cut.InvokeAsync(() =>
        {
            State.RefreshBook();
            State.Notify();
        });
        await Session.RepetitionsIdle();
    }

    [Fact]
    public async Task Tab_ShowsCountAndKind_PrevGreyedAtTheStart_NextAtTheEnd()
    {
        await OpenAsync();

        var cut = RenderTab();

        Assert.Equal("1 / 2", cut.Find(".ne-reps-count").TextContent);
        Assert.Equal(Text("RepetitionLiteral"), cut.Find(".ne-reps-kind").TextContent);
        Assert.True(Disabled(cut.Find(".ne-reps-prev")));
        Assert.False(Disabled(cut.Find(".ne-reps-next")));

        cut.Find(".ne-reps-next").Click();

        Assert.Equal("2 / 2", cut.Find(".ne-reps-count").TextContent);
        Assert.False(Disabled(cut.Find(".ne-reps-prev")));
        Assert.True(Disabled(cut.Find(".ne-reps-next")));
        Assert.Equal(Text("RepPrevious"), cut.Find(".ne-reps-prev").GetAttribute("title"));
    }

    [Fact]
    public async Task Next_RevealsTheOriginInTheBook_AndTheReaderShowsTheOther()
    {
        await OpenAsync();

        var cut = RenderTab();

        // Opening the tab shows the first hit: its earlier side in the book view, the other one in the reader.
        Assert.True(State.RepetitionsTabOpen);
        Assert.Equal(new RepetitionFocus(RepetitionKind.Literal, KeyOf("alpha"), KeyOf("beta"), false), State.RepetitionFocus);
        Assert.Equal(KeyOf("alpha"), State.Views[0].PendingReveal);
        Assert.Equal(KeyOf("alpha"), State.Views[0].Cursor!.Key);
        Assert.Equal(ReaderId(KeyOf("beta")), ReaderSide(cut));

        cut.Find(".ne-reps-next").Click();

        Assert.Equal(KeyOf("gamma"), State.Views[0].Cursor!.Key);
        Assert.Equal(ReaderId(KeyOf("delta")), ReaderSide(cut));
        Assert.Equal(KeyOf("gamma"), State.RepetitionFocus!.Origin);

        cut.Find(".ne-reps-prev").Click();

        Assert.Equal(KeyOf("alpha"), State.Views[0].Cursor!.Key);
        Assert.Equal(ReaderId(KeyOf("beta")), ReaderSide(cut));
        Assert.False(State.IsSplit);
    }

    [Fact]
    public async Task Swap_ExchangesOriginAndOther()
    {
        await OpenAsync();
        var cut = RenderTab();

        cut.Find(".ne-reps-swap").Click();

        Assert.True(State.RepetitionFocus!.Swapped);
        Assert.Equal(KeyOf("beta"), State.Views[0].Cursor!.Key);
        Assert.Equal(ReaderId(KeyOf("alpha")), ReaderSide(cut));
        Assert.Equal("1 / 2", cut.Find(".ne-reps-count").TextContent);

        cut.Find(".ne-reps-swap").Click();

        Assert.False(State.RepetitionFocus!.Swapped);
        Assert.Equal(KeyOf("alpha"), State.Views[0].Cursor!.Key);
        Assert.Equal(ReaderId(KeyOf("beta")), ReaderSide(cut));
    }

    [Fact]
    public async Task Request_ShowsThePair_WithTheBlockAsOrigin()
    {
        await OpenAsync();
        var cut = RenderTab();
        var tabs = new List<NotesTab>();
        State.NotesTabRequested += tabs.Add;

        // The pair in either order, delta as the origin (as the section bar asks for it).
        await cut.InvokeAsync(() => State.RequestRepetitions((KeyOf("delta"), KeyOf("gamma")), KeyOf("delta")));

        cut.WaitForAssertion(() => Assert.Equal("2 / 2", cut.Find(".ne-reps-count").TextContent));
        Assert.Equal([NotesTab.Repetitions], tabs);
        Assert.Equal(KeyOf("delta"), State.Views[0].Cursor!.Key);
        Assert.Equal(ReaderId(KeyOf("gamma")), ReaderSide(cut));

        // Only an origin: its first hit.
        await cut.InvokeAsync(() => State.RequestRepetitions(null, KeyOf("beta")));

        cut.WaitForAssertion(() => Assert.Equal("1 / 2", cut.Find(".ne-reps-count").TextContent));
        Assert.Equal(KeyOf("beta"), State.Views[0].Cursor!.Key);
        Assert.Equal(ReaderId(KeyOf("alpha")), ReaderSide(cut));
    }

    [Fact]
    public async Task Request_ClearsAChapterFilterThatHidesThePair()
    {
        await OpenAsync();
        var cut = RenderTab();
        cut.Find(".ne-reps-filter").Click();
        cut.Find(".ne-reps-chapter").Change(Book.Root.Children.Single(c => c.Title == "Eins").Key.ToString());
        Assert.Equal("1 / 1", cut.Find(".ne-reps-count").TextContent);

        await cut.InvokeAsync(() => State.RequestRepetitions((KeyOf("gamma"), KeyOf("delta"))));

        cut.WaitForAssertion(() => Assert.Equal("2 / 2", cut.Find(".ne-reps-count").TextContent));
        Assert.Equal("", cut.Find(".ne-reps-chapter").GetAttribute("value"));
    }

    [Fact]
    public async Task ChapterFilter_AndShowAccepted()
    {
        var session = await OpenAsync();
        Assert.Null(await ManuscriptCommands.SetRepetitionAcceptedAsync(
            State, KeyOf("alpha"), KeyOf("beta"), true, Text("UndoRepetitionAccepted"), NullLogger.Instance));
        await session.RepetitionsIdle();
        var cut = RenderTab();
        Assert.Equal("1 / 1", cut.Find(".ne-reps-count").TextContent);
        Assert.Equal(KeyOf("gamma"), State.RepetitionFocus!.A);

        cut.Find(".ne-reps-filter").Click();
        Assert.Equal(["Alle", "Eins", "Zwei"], cut.FindAll(".ne-reps-chapter option").Select(o => o.TextContent));
        cut.Find(".ne-reps-show-accepted").Change(true);

        // The accepted pair is back and marked in the editors too; the tab stays at its hit.
        Assert.True(State.RepetitionsShowAccepted);
        Assert.Equal("2 / 2", cut.Find(".ne-reps-count").TextContent);
        cut.Find(".ne-reps-prev").Click();
        Assert.Equal(Text("RepUnaccept"), cut.Find(".ne-reps-accept").TextContent);

        // Only "Zwei": the hit shown is not in it, the tab goes to the one that is.
        cut.Find(".ne-reps-chapter").Change(Book.Root.Children.Single(c => c.Title == "Zwei").Key.ToString());
        Assert.Equal("1 / 1", cut.Find(".ne-reps-count").TextContent);
        Assert.Equal(KeyOf("gamma"), State.Views[0].Cursor!.Key);

        cut.Find(".ne-reps-chapter").Change("");
        cut.Find(".ne-reps-show-accepted").Change(false);
        Assert.Equal("1 / 1", cut.Find(".ne-reps-count").TextContent);
        Assert.False(State.RepetitionsShowAccepted);
    }

    [Fact]
    public async Task Accept_GoesToTheNextHit()
    {
        var session = await OpenAsync();
        var cut = RenderTab();
        Assert.Equal(Text("RepAccept"), cut.Find(".ne-reps-accept").TextContent);

        await cut.InvokeAsync(() => cut.Find(".ne-reps-accept").Click());

        cut.WaitForAssertion(() => Assert.Equal(KeyOf("gamma"), State.RepetitionFocus!.A));
        Assert.Equal(KeyOf("gamma"), State.Views[0].Cursor!.Key);
        Assert.Single(session.AcceptedRepetitions.Of(Book.LinkName));
        await session.RepetitionsIdle();
        // Once the new analysis is there, the accepted pair no longer counts; the tab stays at its hit.
        cut.WaitForAssertion(() => Assert.Equal("1 / 1", cut.Find(".ne-reps-count").TextContent));
        Assert.Equal(ReaderId(KeyOf("delta")), ReaderSide(cut));
        Assert.Equal("Wiederholung als gewollt abhaken", session.Undo.NextDescription);
    }

    [Fact]
    public async Task Accept_TheLastHit_GoesBackToTheOneBefore_UnacceptStays()
    {
        var session = await OpenAsync();
        var cut = RenderTab();
        cut.Find(".ne-reps-next").Click();

        await cut.InvokeAsync(() => cut.Find(".ne-reps-accept").Click());
        await session.RepetitionsIdle();

        cut.WaitForAssertion(() => Assert.Equal("1 / 1", cut.Find(".ne-reps-count").TextContent));
        Assert.Equal(KeyOf("alpha"), State.RepetitionFocus!.A);

        // With the accepted ones shown, „Nicht mehr gewollt“ stays at its hit.
        cut.Find(".ne-reps-filter").Click();
        cut.Find(".ne-reps-show-accepted").Change(true);
        cut.Find(".ne-reps-next").Click();
        Assert.Equal(Text("RepUnaccept"), cut.Find(".ne-reps-accept").TextContent);

        await cut.InvokeAsync(() => cut.Find(".ne-reps-accept").Click());
        await session.RepetitionsIdle();

        cut.WaitForAssertion(() => Assert.Equal(Text("RepAccept"), cut.Find(".ne-reps-accept").TextContent));
        Assert.Equal("2 / 2", cut.Find(".ne-reps-count").TextContent);
        Assert.Empty(session.AcceptedRepetitions.Of(Book.LinkName));
    }

    [Fact]
    public async Task References_AsBefore_GreyedOnConflict()
    {
        var session = await OpenAsync();
        var alpha = KeyOf("alpha");
        var beta = KeyOf("beta");
        var cut = RenderTab();
        Assert.Empty(cut.FindAll(".ne-reps-ref-forward"));

        cut.Find(".ne-reps-ref").Click();
        Assert.Equal("true", cut.Find(".ne-reps-ref").GetAttribute("aria-expanded"));
        await cut.InvokeAsync(() => cut.Find(".ne-reps-ref-forward").Click());

        // From the origin (alpha, in the book view) to the other side; the hit stays.
        cut.WaitForAssertion(() => Assert.True(ManuscriptEditor.RefersTo(Book, alpha, beta)));
        Assert.False(ManuscriptEditor.RefersTo(Book, beta, alpha));
        Assert.Equal("Verweis einfügen", session.Undo.NextDescription);
        Assert.Equal("1 / 2", cut.Find(".ne-reps-count").TextContent);
        Assert.Empty(cut.FindAll(".ne-reps-message"));

        cut.Find(".ne-reps-ref").Click();
        Assert.True(Disabled(cut.Find(".ne-reps-ref-forward")));
        Assert.Equal(Text("RepReferenceExists"), cut.Find(".ne-reps-ref-forward").GetAttribute("title"));
        Assert.False(Disabled(cut.Find(".ne-reps-ref-back")));
        Assert.False(Disabled(cut.Find(".ne-reps-ref-both")));

        await cut.InvokeAsync(() => cut.Find(".ne-reps-ref-back").Click());

        cut.WaitForAssertion(() => Assert.True(ManuscriptEditor.RefersTo(Book, beta, alpha)));
        cut.Find(".ne-reps-ref").Click();
        Assert.True(Disabled(cut.Find(".ne-reps-ref-both")));

        // The other pair, both directions in one undo step.
        cut.Find(".ne-reps-next").Click();
        var pushed = 0;
        session.Undo.Pushed += _ => pushed++;
        cut.Find(".ne-reps-ref").Click();
        await cut.InvokeAsync(() => cut.Find(".ne-reps-ref-both").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.True(ManuscriptEditor.RefersTo(Book, KeyOf("gamma"), KeyOf("delta")));
            Assert.True(ManuscriptEditor.RefersTo(Book, KeyOf("delta"), KeyOf("gamma")));
        });
        Assert.Equal(1, pushed);

        // An open conflict greys out the writing buttons; stepping still works.
        var path = Path.Combine(_vaults[^1].Root, BookPath);
        OutlineEditor.Rename(Book, Book.Root.Children.First(c => c.Title == "Zwei").Key, "Zwei lokal");
        File.WriteAllText(path, Simple.Replace("- # Zwei\n", "- # Zwei extern\n"));
        Session.HandleExternalChange(path);
        Assert.True(Session.HasOpenConflict(path));
        cut.Render();

        Assert.True(Disabled(cut.Find(".ne-reps-accept")));
        Assert.True(Disabled(cut.Find(".ne-reps-ref")));
        Assert.False(Disabled(cut.Find(".ne-reps-prev")));
    }

    [Fact]
    public async Task Typing_KeepsThePair_OrMovesToTheNext()
    {
        await OpenAsync();
        var cut = RenderTab();
        Assert.Equal(KeyOf("alpha"), State.RepetitionFocus!.A);

        // Another block changes: the tab stays at alpha~beta.
        await EditAsync(cut, "delta", $"delta neu {RunY}");

        cut.WaitForAssertion(() => Assert.True(Session.RepetitionsOf(Book).IsFor(Book)));
        Assert.Equal("1 / 2", cut.Find(".ne-reps-count").TextContent);
        Assert.Equal(KeyOf("alpha"), State.RepetitionFocus!.A);

        // alpha loses the shared run: the pair is gone, the tab goes to the next hit after alpha's place.
        var cursor = State.Views[0].Cursor;
        await EditAsync(cut, "alpha", "alpha ist jetzt ganz anders");

        cut.WaitForAssertion(() => Assert.Equal("1 / 1", cut.Find(".ne-reps-count").TextContent));
        Assert.Equal(new RepetitionFocus(RepetitionKind.Literal, KeyOf("gamma"), KeyOf("delta"), false), State.RepetitionFocus);
        Assert.Equal(ReaderId(KeyOf("delta")), ReaderSide(cut));
        // Typing never moves the cursor of the book view.
        Assert.Equal(cursor, State.Views[0].Cursor);
    }

    [Fact]
    public async Task NewSet_ReachesTheReader()
    {
        await OpenAsync();
        var cut = RenderTab();
        Assert.Single(cut.FindAll($"#{ReaderId(KeyOf("gamma"))} .ne-rep-literal"));

        // The hit and its side stay; only the set is new.
        await EditAsync(cut, "gamma", "gamma ist jetzt ganz anders");

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll($"#{ReaderId(KeyOf("gamma"))} .ne-rep-literal")));
        Assert.Contains("gamma ist jetzt ganz anders", cut.Find($"#{ReaderId(KeyOf("gamma"))}").TextContent);
        Assert.Equal(ReaderId(KeyOf("beta")), ReaderSide(cut));
    }

    [Fact]
    public async Task SwitchOff_ReaderUnderlinesNothing_ButHighlightsTheHit()
    {
        await OpenAsync();
        var cut = RenderTab();
        Assert.NotEmpty(cut.FindAll(".ne-reader .ne-rep-literal"));

        Settings.ShowRepetitions = false;
        await cut.InvokeAsync(State.Notify);

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-reader .ne-rep-literal")));
        Assert.NotEmpty(cut.FindAll(".ne-reader .ne-rep-current"));
        Assert.Equal(ReaderId(KeyOf("beta")), ReaderSide(cut));
    }

    [Fact]
    public async Task DeletedBlock_ShowsRepetitionGone_NavigationStillWorks()
    {
        await OpenAsync();
        var cut = RenderTab();
        var gamma = KeyOf("gamma");
        // The book is read again from its file: its blocks have new keys, the tab still has the last analysis.
        await cut.InvokeAsync(() =>
        {
            State.CurrentBook = Book.Load(LogseqParser.Parse(Book.Page.FilePath, System.Text.Encoding.UTF8.GetBytes(Simple)));
            State.Notify();
        });

        cut.Find(".ne-reps-next").Click();

        cut.WaitForAssertion(() => Assert.Equal(Text("RepetitionGone"), cut.Find(".ne-reps-message").TextContent));
        Assert.Equal("alert", cut.Find(".ne-reps-message").GetAttribute("role"));
        Assert.Equal(gamma, State.RepetitionFocus!.A);
        Assert.Equal("2 / 2", cut.Find(".ne-reps-count").TextContent);

        cut.Find(".ne-reps-prev").Click();

        Assert.Equal("1 / 2", cut.Find(".ne-reps-count").TextContent);
        Assert.Equal(Text("RepetitionGone"), cut.Find(".ne-reps-message").TextContent);
    }

    [Fact]
    public async Task SplitView_RevealsInTheActiveView()
    {
        await OpenAsync();
        State.SetSplit(true);
        await State.ActivateAsync(State.Views[1]);

        var cut = RenderTab();

        Assert.Equal(KeyOf("alpha"), State.Views[1].PendingReveal);
        Assert.Null(State.Views[0].PendingReveal);

        cut.Find(".ne-reps-next").Click();

        Assert.True(State.IsSplit);
        Assert.Same(State.Views[1], State.ActiveView);
        Assert.Equal(KeyOf("gamma"), State.Views[1].Cursor!.Key);
        Assert.Null(State.Views[0].PendingReveal);
    }

    [Fact]
    public async Task NoModelHint_And_Empty()
    {
        // Without AI: the hint, the literal hits, no progress.
        await OpenAsync();
        var cut = RenderTab();
        Assert.Equal(Text("RepNeedsAi"), cut.Find(".ne-reps-nomodel").TextContent);
        Assert.Empty(cut.FindAll(".ne-reps-progress"));
        Assert.Equal("1 / 2", cut.Find(".ne-reps-count").TextContent);

        // No hits: the message instead of the head row and the reader; the filter stays.
        await OpenAsync(Head + "- # Eins\n\t- Nur ein kurzer Text\n");
        cut = RenderTab();
        Assert.Equal("Keine Wiederholungen gefunden.", cut.Find(".ne-reps-empty").TextContent);
        Assert.Empty(cut.FindAll(".ne-reps-count"));
        Assert.Empty(cut.FindAll(".ne-reader"));
        Assert.Single(cut.FindAll(".ne-reps-filter"));
        Assert.Null(State.RepetitionFocus);
    }

    [Fact]
    public async Task Progress_WhileTheAnalysisRuns()
    {
        // While the semantic part is analysed: the known hits and the progress.
        var vault = TestVault.Create((BookPath, Simple));
        _vaults.Add(vault);
        var embedder = new FakeEmbedder();
        var session = await OpenWithAiAsync(vault, embedder);
        await session.RepetitionsIdle();
        using var gate = new ManualResetEventSlim();
        try
        {
            embedder.OnEmbed = (_, ct) => gate.Wait(ct);
            Book.Page.AllBlocks().First(b => b.Content.StartsWith("alpha", StringComparison.Ordinal)).SetContent($"alpha neu {RunX}");
            session.Writer.Save(Book.Page);
            State.RefreshBook();

            var cut = RenderTab();

            cut.WaitForAssertion(
                () =>
                {
                    var set = session.RepetitionsOf(Book);
                    Assert.True(set.IsFor(Book) && !set.Complete);
                    Assert.Equal(Text("RepProgress", set.Embedded, set.Total), cut.Find(".ne-reps-progress").TextContent);
                },
                TimeSpan.FromSeconds(20));
            Assert.StartsWith("inhaltlich: ", cut.Find(".ne-reps-progress").TextContent);
            Assert.Empty(cut.FindAll(".ne-reps-nomodel"));
            Assert.Equal("1 / 2", cut.Find(".ne-reps-count").TextContent);
        }
        finally
        {
            gate.Set();
        }

        await session.RepetitionsIdle();
    }

    [Fact]
    public async Task OpeningAndClosingTheTab_TogglesTheSemanticMarks()
    {
        // The fake embedder gives texts with the same letters the same vector: sonne… and schnee… mean the same.
        await OpenAsync(Head
            + "- # Eins\n\t- sonne mond sterne himmel wolken regen wind schnee\n"
            + "\t- schnee wind regen wolken himmel sterne mond sonne\n", ai: true);
        WaitFor(() => Assert.Contains(Session.RepetitionsOf(Book).All, r => r.Kind == RepetitionKind.Semantic));
        var editor = Render<EditorPane>();
        editor.WaitForAssertion(() => Assert.NotEmpty(Editor.Documents));
        var pane = Render<NotesPane>();
        Assert.DoesNotContain(Editor.RepetitionMarks.SelectMany(marks => marks), m => m.Kind == RepetitionMarkKind.Semantic);

        pane.Find(".ne-tab-repetitions").Click();

        Assert.True(State.RepetitionsTabOpen);
        Assert.Single(pane.FindAll(".ne-reps"));
        Assert.Equal(Text("RepetitionSemantic"), pane.Find(".ne-reps-kind").TextContent);
        WaitFor(() => Assert.Contains(Editor.RepetitionMarks[^1], m => m.Kind == RepetitionMarkKind.Semantic));
        WaitFor(() => Assert.Equal(
            KeyOf("sonne"), Assert.Single(Editor.RepetitionMarks[^1], m => m.Kind == RepetitionMarkKind.Current).ParagraphKey));

        pane.Find(".ne-tab-journal").Click();

        Assert.False(State.RepetitionsTabOpen);
        Assert.Empty(pane.FindAll(".ne-reps"));
        WaitFor(() => Assert.DoesNotContain(Editor.RepetitionMarks[^1], m => m.Kind is RepetitionMarkKind.Semantic or RepetitionMarkKind.Current));
    }

    [Fact]
    public async Task HeaderBar_RepetitionsEntry_OpensTheTab_DisabledWithoutBook()
    {
        await OpenAsync();
        var tabs = new List<NotesTab>();
        State.NotesTabRequested += tabs.Add;
        var header = Render<HeaderBar>(p => p.Add(c => c.OnSubPointLinks, () => { }));
        header.Find(".ne-assistants-toggle").Click();

        Assert.Equal("Wiederholungen", header.Find(".ne-assistant-repetitions").TextContent);
        header.Find(".ne-assistant-repetitions").Click();
        Assert.Equal([NotesTab.Repetitions], tabs);

        State.CurrentBook = null;
        header.Render();
        header.Find(".ne-assistants-toggle").Click();
        Assert.True(Disabled(header.Find(".ne-assistant-repetitions")));
    }

    [Fact]
    public async Task NoGermanLiterals_InRepetitionsTab()
    {
        // Every UI text comes out as its resource key; the book's text (reader, chapter names) is data.
        Services.AddSingleton<IStringLocalizer<Strings>>(new KeyLocalizer());
        await OpenAsync();
        Assert.Null(await ManuscriptCommands.SetRepetitionAcceptedAsync(
            State, KeyOf("alpha"), KeyOf("beta"), true, "x", NullLogger.Instance));
        await Session.RepetitionsIdle();
        var resources = new ResourceManager(typeof(Strings));
        var cut = RenderTab();

        cut.Find(".ne-reps-filter").Click();
        cut.Find(".ne-reps-show-accepted").Change(true);
        cut.Find(".ne-reps-ref").Click();
        cut.Find(".ne-reps-ref-forward").Click();
        // The tab stays at gamma~delta when the accepted pair comes back.
        cut.WaitForAssertion(() => Assert.True(ManuscriptEditor.RefersTo(Book, KeyOf("gamma"), KeyOf("delta"))));
        cut.Find(".ne-reps-ref").Click();
        Assert.Single(cut.FindAll(".ne-reps-ref-menu"));
        Assert.Single(cut.FindAll(".ne-reps-nomodel"));

        foreach (var text in UiTexts(cut.Find(".ne-reps")))
        {
            Assert.True(
                !text.Any(char.IsLetter) || resources.GetString(text, System.Globalization.CultureInfo.InvariantCulture) is not null,
                $"'{text}' is neither a resource key nor free of letters");
        }

        // The message line and the empty tab.
        await cut.InvokeAsync(() =>
        {
            State.CurrentBook = Book.Load(LogseqParser.Parse(Book.Page.FilePath, System.Text.Encoding.UTF8.GetBytes(Simple)));
            State.Notify();
        });
        cut.Find(".ne-reps-prev").Click();
        cut.WaitForAssertion(() => Assert.Equal("RepetitionGone", cut.Find(".ne-reps-message").TextContent));
        await cut.InvokeAsync(() =>
        {
            State.CurrentBook = null;
            State.Notify();
        });
        cut.WaitForAssertion(() => Assert.Equal("RepNone", cut.Find(".ne-reps-empty").TextContent));
    }

    /// <summary>The texts, titles and labels of <paramref name="root"/>, without the book's own text.</summary>
    private static IEnumerable<string> UiTexts(AngleSharp.Dom.IElement root)
    {
        foreach (var element in root.QuerySelectorAll("*").Prepend(root))
        {
            if (element.Closest(".ne-reader") is not null
                || (element.Closest(".ne-reps-chapter option") is { } option && option.GetAttribute("value") is { Length: > 0 }))
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

    private static void WaitFor(Action assertion)
    {
        var until = DateTime.UtcNow + RealTimeWait;
        while (true)
        {
            try
            {
                assertion();
                return;
            }
            catch (Exception) when (DateTime.UtcNow < until)
            {
                Thread.Sleep(10);
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
