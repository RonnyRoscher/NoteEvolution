using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NoteEvolution.AI.Tests;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Editor;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>
/// "Wiederholungen: n" in the section bar (repetitions spec 6): the pairs with a block in the marked range, shown whenever
/// there are some, and the list of their counterparts that opens the assistant at the pair.
/// </summary>
public class SectionBarRepetitionTests : UiTestContext
{
    private const string BookPath = "pages/Buch - Alpha.md";

    private const string Head = "title:: Alpha\ntype:: book\n\n";

    private const string RunX = "eins zwei drei vier fuenf sechs sieben acht neun";

    private const string RunY = "rot gruen blau gelb weiss schwarz braun lila";

    /// <summary>One literal pair in "Eins" (alpha~beta) and one in "Zwei" (gamma~delta).</summary>
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

    private string Text(string key, params object[] arguments) =>
        Services.GetRequiredService<IStringLocalizer<Strings>>()[key, arguments].Value;

    private Guid KeyOf(string start) => Book.Page.AllBlocks().Single(b => b.Content.StartsWith(start, StringComparison.Ordinal)).Key;

    private async Task<IRenderedComponent<EditorPane>> RenderAsync(string bookText = Simple)
    {
        var vault = TestVault.Create((BookPath, bookText));
        _vaults.Add(vault);
        var session = await OpenSessionAsync(vault);
        await session.RepetitionsIdle();
        return await RenderPaneAsync();
    }

    private async Task<IRenderedComponent<EditorPane>> RenderPaneAsync()
    {
        var cut = Render<EditorPane>();
        cut.WaitForAssertion(() => Assert.NotEmpty(Editor.Documents));
        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionBoxMoved(120.5, true));
        return cut;
    }

    private Task MoveToAsync(IRenderedComponent<EditorPane> cut, string content)
    {
        var key = content.StartsWith("# ", StringComparison.Ordinal)
            ? Book.Root.Children.Single(c => c.Title == content[2..]).Key
            : KeyOf(content);
        var element = BookElements.Find(Book, key)!;
        var cursor = new CursorInfo(element.Kind, element.Key, BookElements.TextBlockOf(Book, element)?.Key, 0);
        return cut.InvokeAsync(() => Editor.Callbacks!.OnCursorChanged(cursor));
    }

    [Fact]
    public async Task NoRepetitions_NoButton()
    {
        var cut = await RenderAsync(Head + "- # Eins\n\t- alpha\n\t- beta\n");

        await MoveToAsync(cut, "alpha");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".ne-sec-sources-area")));
        Assert.Empty(cut.FindAll(".ne-sec-reps"));
    }

    [Fact]
    public async Task Block_ShowsCount()
    {
        var cut = await RenderAsync();

        await MoveToAsync(cut, "alpha");

        cut.WaitForAssertion(() => Assert.Equal(Text("SectionRepetitions", 1), cut.Find(".ne-sec-reps").TextContent.Trim()));
        Assert.Equal("Wiederholungen: 1", cut.Find(".ne-sec-reps").TextContent.Trim());
        Assert.Equal("false", cut.Find(".ne-sec-reps").GetAttribute("aria-expanded"));
        Assert.Empty(cut.FindAll(".ne-sec-rep-list"));
    }

    [Fact]
    public async Task Counter_IsShownWhateverTheSwitchSays()
    {
        Settings.ShowRepetitions = false;
        var cut = await RenderAsync();

        await MoveToAsync(cut, "alpha");

        cut.WaitForAssertion(() => Assert.Equal("Wiederholungen: 1", cut.Find(".ne-sec-reps").TextContent.Trim()));
    }

    [Fact]
    public async Task Section_CountsPairInsideOnce()
    {
        var cut = await RenderAsync(Cross);

        await MoveToAsync(cut, "# Eins");

        // alpha~beta lies inside "Eins" (once), alpha~zeta and beta~zeta touch it; gamma~delta does not.
        cut.WaitForAssertion(() => Assert.Equal("Wiederholungen: 3", cut.Find(".ne-sec-reps").TextContent.Trim()));
        cut.Find(".ne-sec-reps").Click();
        Assert.Equal(3, cut.FindAll(".ne-sec-rep").Count);
    }

    [Fact]
    public async Task AcceptedPairs_NotCounted()
    {
        var cut = await RenderAsync();
        await MoveToAsync(cut, "alpha");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-sec-reps")));

        Assert.Null(await cut.InvokeAsync(() => ManuscriptCommands.SetRepetitionAcceptedAsync(
            State, KeyOf("alpha"), KeyOf("beta"), true, Text("UndoRepetitionAccepted"), NullLogger.Instance)));
        await State.Session!.RepetitionsIdle();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-sec-reps")));
    }

    [Fact]
    public async Task Incomplete_ShowsEllipsis()
    {
        var vault = TestVault.Create((BookPath, Simple));
        _vaults.Add(vault);
        var embedder = new FakeEmbedder();
        var session = await OpenWithAiAsync(vault, embedder);
        await session.RepetitionsIdle();
        using var gate = new ManualResetEventSlim();
        try
        {
            // The edited block's vector waits: the literal pairs are there, the set is not complete.
            embedder.OnEmbed = (_, ct) => gate.Wait(ct);
            Book.Page.AllBlocks().First(b => b.Content.StartsWith("alpha", StringComparison.Ordinal)).SetContent($"alpha neu {RunX}");
            session.Writer.Save(Book.Page);
            State.RefreshBook();
            var cut = await RenderPaneAsync();

            await MoveToAsync(cut, "alpha");

            cut.WaitForAssertion(
                () => Assert.Equal(Text("SectionRepetitions", "1 …"), cut.Find(".ne-sec-reps").TextContent.Trim()),
                TimeSpan.FromSeconds(20));
            Assert.Equal("Wiederholungen: 1 …", cut.Find(".ne-sec-reps").TextContent.Trim());
        }
        finally
        {
            gate.Set();
        }

        await session.RepetitionsIdle();
    }

    [Fact]
    public async Task Expanded_ListsCounterparts_ClickOpensAssistantAtPair()
    {
        var cut = await RenderAsync(Cross);
        await MoveToAsync(cut, "beta");
        cut.WaitForAssertion(() => Assert.Equal("Wiederholungen: 2", cut.Find(".ne-sec-reps").TextContent.Trim()));
        (Guid A, Guid B)? requested = null;
        State.RepetitionsRequested += pair => requested = pair;

        cut.Find(".ne-sec-reps").Click();

        Assert.Equal("true", cut.Find(".ne-sec-reps").GetAttribute("aria-expanded"));
        var rows = cut.FindAll(".ne-sec-rep");
        Assert.Equal(2, rows.Count);
        // Book order of the pairs: alpha~beta, then beta~zeta; each with its kind and both places.
        Assert.Equal(
            [Text("RepetitionLiteral"), $"Eins › alpha {RunX}", $"Eins › beta {RunX}"],
            rows[0].Children.Select(c => c.TextContent.Trim()));
        Assert.Equal(
            [Text("RepetitionLiteral"), $"Eins › beta {RunX}", $"Zwei › zeta {RunX}"],
            rows[1].Children.Select(c => c.TextContent.Trim()));

        rows[1].Click();

        Assert.Equal((KeyOf("beta"), KeyOf("zeta")), requested);
    }

    [Fact]
    public async Task List_ClosesWhenTheRangeChanges()
    {
        var cut = await RenderAsync();
        await MoveToAsync(cut, "alpha");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-sec-reps")));
        cut.Find(".ne-sec-reps").Click();
        Assert.Single(cut.FindAll(".ne-sec-rep-list"));

        await MoveToAsync(cut, "gamma");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-sec-reps")));
        Assert.Empty(cut.FindAll(".ne-sec-rep-list"));
        Assert.Equal("false", cut.Find(".ne-sec-reps").GetAttribute("aria-expanded"));
    }
}
