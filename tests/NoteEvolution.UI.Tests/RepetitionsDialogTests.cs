using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NoteEvolution.AI.Tests;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Components.Dialogs;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>
/// The assistant "Wiederholungen": the list of both groups with filter and switch, the side-by-side view of the selected
/// pair, and its actions (reference, both directions, "Gewollt", "Im Buch öffnen"). The sets come from the real tracker:
/// literal pairs from blocks that share eight words, semantic ones from the fake embedder (equal letters, other order).
/// </summary>
public class RepetitionsDialogTests : UiTestContext
{
    private const string BookPath = "pages/Buch - Alpha.md";

    private const string Head = "title:: Alpha\ntype:: book\n\n";

    private const string RunX = "eins zwei drei vier fuenf sechs sieben acht neun";

    private const string RunY = "rot gruen blau gelb weiss schwarz braun lila";

    private const string IdB = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb2";

    /// <summary>Two literal pairs: alpha~beta in "Eins" and gamma~delta in "Zwei".</summary>
    private const string Simple = Head
        + $"- # Eins\n\t- alpha {RunX}\n\t- beta {RunX}\n"
        + $"- # Zwei\n\t- gamma {RunY}\n\t- delta {RunY}\n";

    /// <summary>zeta (in "Zwei") shares the run with both blocks of "Eins": three pairs touch "Eins".</summary>
    private const string Cross = Head
        + $"- # Eins\n\t- alpha {RunX}\n\t- beta {RunX}\n"
        + $"- # Zwei\n\t- zeta {RunX}\n\t- gamma {RunY}\n\t- delta {RunY}\n";

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

    private async Task<VaultSession> OpenAsync(string bookText = Simple)
    {
        var vault = TestVault.Create((BookPath, bookText));
        _vaults.Add(vault);
        State.Session?.Dispose();
        var session = await OpenSessionAsync(vault);
        await session.RepetitionsIdle();
        return session;
    }

    private IRenderedComponent<RepetitionsDialog> RenderDialog(
        (Guid A, Guid B)? start = null, Action? onClose = null) =>
        Render<RepetitionsDialog>(p =>
        {
            p.Add(c => c.Start, start);
            p.Add(c => c.OnClose, EventCallback.Factory.Create(this, () => onClose?.Invoke()));
        });

    private static IReadOnlyList<string> Items(IRenderedComponent<RepetitionsDialog> cut) =>
        [.. cut.FindAll(".ne-rep-item").Select(item => item.TextContent)];

    private static bool Disabled(AngleSharp.Dom.IElement element) => element.HasAttribute("disabled");

    [Fact]
    public async Task ShowsTwoGroups_WithCounts_AndStrength()
    {
        // alpha~beta share a run; 1..9 and 9..1 have the same letters (digits) in another order: same meaning for the fake.
        var vault = TestVault.Create((BookPath, Head
            + $"- # Eins\n\t- alpha {RunX}\n\t- beta {RunX}\n"
            + "- # Zwei\n\t- 1 2 3 4 5 6 7 8 9\n\t- 9 8 7 6 5 4 3 2 1\n"));
        _vaults.Add(vault);
        var session = await OpenWithAiAsync(vault);
        await session.RepetitionsIdle();

        var cut = RenderDialog();

        cut.WaitForAssertion(() => Assert.Equal(
            ["Wörtlich (1)", "Inhaltlich ähnlich (1)"], cut.FindAll(".ne-rep-group").Select(g => g.TextContent)));
        Assert.Equal(["9 Wörter", "100 %"], cut.FindAll(".ne-rep-strength").Select(s => s.TextContent));
        Assert.Empty(cut.FindAll(".ne-rep-hint-ai"));
        var place = cut.FindAll(".ne-rep-item")[0].QuerySelectorAll(".ne-rep-place").Select(p => p.TextContent).ToList();
        Assert.Equal([$"Eins › alpha {RunX}", $"Eins › beta {RunX}"], place);
    }

    [Fact]
    public async Task ChapterFilter_KeepsPairsTouchingTheChapter()
    {
        await OpenAsync(Cross);
        var cut = RenderDialog();
        Assert.Equal(4, cut.FindAll(".ne-rep-item").Count);
        Assert.Equal(
            ["Alle", "Eins", "Zwei"], cut.FindAll(".ne-rep-chapter option").Select(o => o.TextContent));

        cut.Find(".ne-rep-chapter").Change(KeyOfHeading("Eins").ToString());

        // alpha~beta, alpha~zeta and beta~zeta touch "Eins"; gamma~delta does not.
        Assert.Equal(3, cut.FindAll(".ne-rep-item").Count);
        Assert.DoesNotContain(Items(cut), item => item.Contains("gamma"));

        cut.Find(".ne-rep-chapter").Change(KeyOfHeading("Zwei").ToString());

        // zeta sits in "Zwei": its two pairs and gamma~delta.
        Assert.Equal(3, cut.FindAll(".ne-rep-item").Count);
        Assert.Contains(Items(cut), item => item.Contains("gamma"));
        Assert.DoesNotContain(Items(cut), item => item.Contains("alpha") && item.Contains("beta"));

        cut.Find(".ne-rep-chapter").Change("");
        Assert.Equal(4, cut.FindAll(".ne-rep-item").Count);
    }

    private Guid KeyOfHeading(string title) => Book.Root.Children.Single(c => c.Title == title).Key;

    [Fact]
    public async Task Accepted_HiddenByDefault_ShownWithSwitch_AsGewollt()
    {
        var session = await OpenAsync();
        Assert.Null(await ManuscriptCommands.SetRepetitionAcceptedAsync(
            State, KeyOf("alpha"), KeyOf("beta"), true, Text("UndoRepetitionAccepted"), NullLogger.Instance));
        await session.RepetitionsIdle();

        var cut = RenderDialog();

        Assert.Single(cut.FindAll(".ne-rep-item"));
        Assert.Contains("gamma", cut.Find(".ne-rep-item").TextContent);
        Assert.Equal("Wörtlich (1)", cut.Find(".ne-rep-group").TextContent);

        cut.Find(".ne-rep-show-accepted").Change(true);

        Assert.Equal(2, cut.FindAll(".ne-rep-item").Count);
        var accepted = cut.Find(".ne-rep-item.ne-rep-accepted");
        Assert.Contains("alpha", accepted.TextContent);
        Assert.Equal("Gewollt", accepted.QuerySelector(".ne-rep-badge")!.TextContent);
        // The count in the heading is that of the pairs that count.
        Assert.Equal("Wörtlich (1)", cut.Find(".ne-rep-group").TextContent);
    }

    [Fact]
    public async Task SelectingAnItem_ShowsBothBlocks_EarlierLeft_WithPaths()
    {
        await OpenAsync(Cross);
        var cut = RenderDialog();

        // The second entry is alpha~zeta (book order); the first one is selected without a click.
        Assert.Contains("ne-rep-selected", cut.FindAll(".ne-rep-item")[0].ClassList);
        cut.FindAll(".ne-rep-item")[1].Click();

        Assert.Contains("ne-rep-selected", cut.FindAll(".ne-rep-item")[1].ClassList);
        var sides = cut.FindAll(".ne-rep-side");
        Assert.Equal(2, sides.Count);
        Assert.Equal("Eins", sides[0].QuerySelector(".ne-rep-path")!.TextContent);
        Assert.Equal("Zwei", sides[1].QuerySelector(".ne-rep-path")!.TextContent);
        Assert.Equal($"alpha {RunX}", sides[0].QuerySelector(".ne-rep-text")!.TextContent);
        Assert.Equal($"zeta {RunX}", sides[1].QuerySelector(".ne-rep-text")!.TextContent);
        Assert.Equal(RunX, Assert.Single(sides[0].QuerySelectorAll(".ne-rep-literal")).TextContent);
        Assert.Equal(RunX, Assert.Single(sides[1].QuerySelectorAll(".ne-rep-literal")).TextContent);
    }

    [Fact]
    public async Task SharedRun_AtDifferentOffsets_IsMarkedInTheRightWordsOfEachColumn()
    {
        // The run starts at word 3 on the left (after three own words) and at word 2 on the right, there in the middle of a
        // bold span: swapping the offsets of the two sides would mark other words.
        await OpenAsync(Head + $"- # Eins\n\t- vorn hier gamma {RunX}\n\t- start **neu eins** zwei drei vier fuenf sechs sieben acht neun\n");

        var cut = RenderDialog();

        var sides = cut.FindAll(".ne-rep-side");
        var left = Assert.Single(sides[0].QuerySelectorAll(".ne-rep-literal"));
        var right = Assert.Single(sides[1].QuerySelectorAll(".ne-rep-literal"));
        Assert.Equal(RunX, left.TextContent);
        Assert.Equal(RunX, right.TextContent);
        Assert.Equal("eins", right.QuerySelector("strong")!.TextContent);
        Assert.Equal(["neu ", "eins"], sides[1].QuerySelectorAll("strong").Select(s => s.TextContent));
        Assert.StartsWith("start neu ", sides[1].QuerySelector(".ne-rep-text")!.TextContent);
    }

    [Fact]
    public async Task CommandMessage_ShowsInTheErrorLine_AndTheDialogStaysOpen()
    {
        await OpenAsync();
        var closed = 0;
        var cut = RenderDialog(onClose: () => closed++);
        // The book is read again from its file: its blocks have new keys, the dialog still lists the old ones.
        State.CurrentBook = Book.Load(LogseqParser.Parse(Book.Page.FilePath, System.Text.Encoding.UTF8.GetBytes(Simple)));

        await cut.InvokeAsync(() => cut.Find(".ne-rep-open").Click());

        cut.WaitForAssertion(() => Assert.Equal("Diese Stelle gibt es nicht mehr.", cut.Find(".ne-dialog-error").TextContent));
        Assert.Equal("alert", cut.Find(".ne-dialog-error").GetAttribute("role"));
        Assert.Equal(0, closed);
    }

    [Fact]
    public async Task SelectedEntry_IsMarkedCurrent()
    {
        await OpenAsync();

        var cut = RenderDialog();

        var items = cut.FindAll(".ne-rep-item");
        Assert.Equal("true", items[0].GetAttribute("aria-current"));
        Assert.Null(items[1].GetAttribute("aria-current"));
    }

    [Fact]
    public async Task SubSection_ShowsTheWholePath()
    {
        await OpenAsync(Head
            + $"- # Eins\n\t- ## Unter\n\t\t- alpha {RunX}\n\t\t- beta {RunX}\n");
        var cut = RenderDialog();

        Assert.Equal("Eins › Unter", cut.FindAll(".ne-rep-path")[0].TextContent);
        Assert.Equal($"Unter › alpha {RunX}", cut.FindAll(".ne-rep-item .ne-rep-place")[0].TextContent);
    }

    [Fact]
    public async Task ReferenceButton_CallsCommand_AndKeepsThePairSelected()
    {
        var session = await OpenAsync();
        var alpha = KeyOf("alpha");
        var beta = KeyOf("beta");
        var cut = RenderDialog();
        Assert.False(Disabled(cut.FindAll(".ne-rep-ref")[0]));

        await cut.InvokeAsync(() => cut.FindAll(".ne-rep-ref")[0].Click());

        cut.WaitForAssertion(() =>
        {
            Assert.True(ManuscriptEditor.RefersTo(Book, alpha, beta));
            Assert.False(ManuscriptEditor.RefersTo(Book, beta, alpha));
            Assert.True(Disabled(cut.FindAll(".ne-rep-ref")[0]));
            Assert.Equal("Verweis besteht", cut.FindAll(".ne-rep-ref")[0].GetAttribute("title"));
            Assert.False(Disabled(cut.FindAll(".ne-rep-ref")[1]));
        });
        // A reference does not take the pair out of the list: it stays selected, the dialog stays open.
        Assert.Contains("alpha", cut.Find(".ne-rep-item.ne-rep-selected").TextContent);
        Assert.Empty(cut.FindAll(".ne-dialog-error"));
        Assert.Equal("Verweis einfügen", session.Undo.NextDescription);
    }

    [Fact]
    public async Task ReferenceExists_ButtonDisabled()
    {
        await OpenAsync(Head
            + $"- # Eins\n\t- alpha {RunX} (({IdB}))\n\t- beta {RunX}\n\t  id:: {IdB}\n");
        var cut = RenderDialog();

        Assert.True(Disabled(cut.FindAll(".ne-rep-ref")[0]));
        Assert.Equal("Verweis besteht", cut.FindAll(".ne-rep-ref")[0].GetAttribute("title"));
        Assert.False(Disabled(cut.FindAll(".ne-rep-ref")[1]));
        Assert.Null(cut.FindAll(".ne-rep-ref")[1].GetAttribute("title"));
    }

    [Fact]
    public async Task Both_IsOneUndoStep()
    {
        var session = await OpenAsync();
        var alpha = KeyOf("alpha");
        var beta = KeyOf("beta");
        var cut = RenderDialog();

        await cut.InvokeAsync(() => cut.Find(".ne-rep-both").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.True(ManuscriptEditor.RefersTo(Book, alpha, beta));
            Assert.True(ManuscriptEditor.RefersTo(Book, beta, alpha));
            Assert.True(Disabled(cut.FindAll(".ne-rep-ref")[0]));
            Assert.True(Disabled(cut.FindAll(".ne-rep-ref")[1]));
        });
        Assert.Equal("Verweis einfügen", session.Undo.NextDescription);
        Assert.True(session.TryUndo(out var error), error?.ToString());
        Assert.False(session.Undo.CanUndo);
    }

    [Fact]
    public async Task Gewollt_RemovesFromList_AndBack()
    {
        var session = await OpenAsync();
        var cut = RenderDialog();
        Assert.Equal("Gewollt", cut.Find(".ne-rep-accept").TextContent);

        await cut.InvokeAsync(() => cut.Find(".ne-rep-accept").Click());

        // The pair leaves the list; the next one (gamma~delta) is selected.
        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".ne-rep-item"));
            Assert.Contains("gamma", cut.Find(".ne-rep-item.ne-rep-selected").TextContent);
        });
        Assert.Single(session.AcceptedRepetitions.Of(Book.LinkName));

        cut.Find(".ne-rep-show-accepted").Change(true);
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".ne-rep-item").Count));
        cut.Find(".ne-rep-item.ne-rep-accepted").Click();
        Assert.Equal("Nicht mehr gewollt", cut.Find(".ne-rep-accept").TextContent);

        await cut.InvokeAsync(() => cut.Find(".ne-rep-accept").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(session.AcceptedRepetitions.Of(Book.LinkName));
            Assert.Empty(cut.FindAll(".ne-rep-item.ne-rep-accepted"));
            Assert.Equal(2, cut.FindAll(".ne-rep-item").Count);
        });
        Assert.Equal("Gewollt", cut.Find(".ne-rep-accept").TextContent);
    }

    [Fact]
    public async Task OpenInBook_ClosesAndSplits()
    {
        await OpenAsync();
        var closed = 0;
        var cut = RenderDialog(onClose: () => closed++);

        await cut.InvokeAsync(() => cut.Find(".ne-rep-open").Click());

        cut.WaitForAssertion(() => Assert.Equal(1, closed));
        Assert.True(State.IsSplit);
        Assert.Equal(KeyOf("alpha"), State.Views[0].Cursor!.Key);
        Assert.Equal(KeyOf("beta"), State.Views[1].Cursor!.Key);
    }

    [Fact]
    public async Task Conflict_DisablesWritingButtons()
    {
        await OpenAsync();
        var cut = RenderDialog();
        Assert.False(Disabled(cut.Find(".ne-rep-both")));

        var path = Path.Combine(_vaults[^1].Root, BookPath);
        OutlineEditor.Rename(Book, Book.Root.Children.First(c => c.Title == "Zwei").Key, "Zwei lokal");
        File.WriteAllText(path, Simple.Replace("- # Zwei\n", "- # Zwei extern\n"));
        Session.HandleExternalChange(path);
        Assert.True(Session.HasOpenConflict(path));
        cut.Render();

        Assert.True(Disabled(cut.Find(".ne-rep-both")));
        Assert.True(Disabled(cut.Find(".ne-rep-accept")));
        Assert.All(cut.FindAll(".ne-rep-ref"), button => Assert.True(Disabled(button)));
        Assert.False(Disabled(cut.Find(".ne-rep-open")));
    }

    [Fact]
    public async Task Incomplete_ShowsProgress()
    {
        var vault = TestVault.Create((BookPath, Simple));
        _vaults.Add(vault);
        var embedder = new FakeEmbedder();
        var session = await OpenWithAiAsync(vault, embedder);
        await session.RepetitionsIdle();
        using var gate = new ManualResetEventSlim();
        try
        {
            // Newly embedded text blocks wait: the literal pairs are there, the vectors are not.
            embedder.OnEmbed = (_, ct) => gate.Wait(ct);
            Book.Page.AllBlocks().First(b => b.Content.StartsWith("alpha", StringComparison.Ordinal)).SetContent($"alpha neu {RunX}");
            session.Writer.Save(Book.Page);
            State.RefreshBook();

            var cut = RenderDialog();

            // The three unchanged blocks have their vectors cached; the edited one waits.
            cut.WaitForAssertion(
                () => Assert.Equal("Wiederholungen: 3/4 Blöcke …", cut.Find(".ne-rep-hint-progress").TextContent),
                TimeSpan.FromSeconds(20));
            Assert.Equal("Wörtlich (2 …)", cut.Find(".ne-rep-group").TextContent);
        }
        finally
        {
            gate.Set();
        }

        await session.RepetitionsIdle();
    }

    [Fact]
    public async Task NoAi_ShowsHint()
    {
        await OpenAsync();

        var cut = RenderDialog();

        Assert.Equal("Inhaltlich ähnliche Stellen brauchen die lokale KI.", cut.Find(".ne-rep-hint-ai").TextContent);
        Assert.Equal(2, cut.FindAll(".ne-rep-item").Count);
    }

    [Fact]
    public async Task Empty_ShowsMessage()
    {
        await OpenAsync(Head + "- # Eins\n\t- Nur ein kurzer Text\n");

        var cut = RenderDialog();

        Assert.Equal("Keine Wiederholungen gefunden.", cut.Find(".ne-rep-empty").TextContent);
        Assert.Empty(cut.FindAll(".ne-rep-item"));
        Assert.Empty(cut.FindAll(".ne-rep-side"));
        Assert.True(Disabled(cut.Find(".ne-rep-accept")));
    }

    [Fact]
    public async Task Start_SelectsThatPair()
    {
        await OpenAsync();

        // The pair may be given in either order.
        var cut = RenderDialog((KeyOf("delta"), KeyOf("gamma")));

        Assert.Contains("gamma", cut.Find(".ne-rep-item.ne-rep-selected").TextContent);
        Assert.Equal("Zwei", cut.FindAll(".ne-rep-path")[0].TextContent);
        Assert.StartsWith("gamma", cut.FindAll(".ne-rep-text")[0].TextContent);
        Assert.StartsWith("delta", cut.FindAll(".ne-rep-text")[1].TextContent);
    }

    [Fact]
    public async Task SameBookInBothHalves_ReferenceRefreshesBoth()
    {
        await OpenAsync();
        State.SetSplit(true);
        var alpha = KeyOf("alpha");
        var beta = KeyOf("beta");
        var cut = RenderDialog();

        await cut.InvokeAsync(() => cut.Find(".ne-rep-both").Click());

        cut.WaitForAssertion(() => Assert.True(Disabled(cut.FindAll(".ne-rep-ref")[1])));
        // Both halves show State.CurrentBook; after the command it is the saved snapshot with both references.
        Assert.Equal(2, State.Views.Count);
        Assert.True(ManuscriptEditor.RefersTo(State.CurrentBook!, alpha, beta));
        Assert.True(ManuscriptEditor.RefersTo(State.CurrentBook!, beta, alpha));
        Assert.Same(Session.Vault.FindBook(Book.LinkName), State.CurrentBook);
    }

    [Fact]
    public async Task HeaderBar_RepetitionsEntry_DisabledWithoutBook()
    {
        var vault = TestVault.Create((BookPath, Simple));
        _vaults.Add(vault);
        await OpenSessionAsync(vault);
        var opened = 0;
        var header = Render<HeaderBar>(p => p.Add(c => c.OnRepetitions, () => opened++));
        header.Find(".ne-assistants-toggle").Click();

        Assert.Equal("Wiederholungen", header.Find(".ne-assistant-repetitions").TextContent);
        Assert.False(Disabled(header.Find(".ne-assistant-repetitions")));
        header.Find(".ne-assistant-repetitions").Click();
        Assert.Equal(1, opened);

        State.CurrentBook = null;
        header.Render();
        header.Find(".ne-assistants-toggle").Click();
        Assert.True(Disabled(header.Find(".ne-assistant-repetitions")));
    }

    [Fact]
    public async Task Shell_RequestRepetitions_OpensTheDialogAtThePair()
    {
        var vault = TestVault.Create((BookPath, Simple));
        _vaults.Add(vault);
        Settings.LastVault = vault.Root;
        var cut = Render<Shell>();
        WaitForVaultOpened(cut);
        cut.WaitForAssertion(() => Assert.NotEmpty(State.Session!.RepetitionsOf(State.CurrentBook).All));
        var gamma = KeyOf("gamma");
        var delta = KeyOf("delta");

        State.RequestRepetitions((gamma, delta));

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".ne-rep"));
            Assert.Contains("gamma", cut.Find(".ne-rep-item.ne-rep-selected").TextContent);
        });
        cut.Find(".ne-rep-close").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-rep")));

        // Without a pair the dialog opens with the first entry selected.
        State.RequestRepetitions(null);
        cut.WaitForAssertion(() => Assert.Contains("alpha", cut.Find(".ne-rep-item.ne-rep-selected").TextContent));
    }
}
