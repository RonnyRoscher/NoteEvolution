using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Editor;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>
/// The repetition marks of the editor (repetitions spec 6): the pane sends the number of repetitions per text block after
/// a document and when the set changes, nothing when the switch is off, and a click on a mark shows the block and opens
/// the bar's list.
/// </summary>
public class EditorPaneRepetitionTests : UiTestContext
{
    private const string BookPath = "pages/Buch - Alpha.md";

    private const string RunX = "eins zwei drei vier fuenf sechs sieben acht neun";

    private const string RunY = "rot gruen blau gelb weiss schwarz braun lila";

    private const string BookText = "title:: Alpha\ntype:: book\n\n"
        + $"- # Eins\n\t- alpha {RunX}\n\t- beta {RunX}\n"
        + $"- # Zwei\n\t- gamma {RunY}\n\t- delta {RunY}\n\t- epsilon ohne Wiederholung\n";

    private readonly TestVault _vault = TestVault.Create((BookPath, BookText));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _vault.Dispose();
        }
    }

    private Book Book => State.CurrentBook!;

    private string Text(string key, params object[] arguments) =>
        Services.GetRequiredService<IStringLocalizer<Strings>>()[key, arguments].Value;

    private Guid KeyOf(string start) => Book.Page.AllBlocks().Single(b => b.Content.StartsWith(start, StringComparison.Ordinal)).Key;

    private IReadOnlyDictionary<Guid, int> LastCounts => Editor.BlockRepetitions[^1].ToDictionary(m => m.Key, m => m.Value.Count);

    private async Task<IRenderedComponent<EditorPane>> RenderAsync()
    {
        var session = await OpenSessionAsync(_vault);
        await session.RepetitionsIdle();
        var cut = Render<EditorPane>();
        cut.WaitForAssertion(() => Assert.NotEmpty(Editor.Documents));
        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionBoxMoved(120.5, true));
        return cut;
    }

    [Fact]
    public async Task SendsCounts_AfterDocument_AndOnChange()
    {
        var cut = await RenderAsync();

        WaitFor(() => Assert.NotEmpty(Editor.BlockRepetitions));
        // Only blocks with repetitions are in the map; epsilon has none.
        Assert.Equal(
            new Dictionary<Guid, int> { [KeyOf("alpha")] = 1, [KeyOf("beta")] = 1, [KeyOf("gamma")] = 1, [KeyOf("delta")] = 1 },
            LastCounts);
        // The tooltip is localized here, not in the script.
        Assert.Equal(Text("BlockRepetitionsTitle", 1), Editor.BlockRepetitions[^1][KeyOf("alpha")].Title);
        var sent = Editor.BlockRepetitions.Count;

        // „Gewollt“ for alpha~beta changes the set: the new counts are sent.
        Assert.Null(await cut.InvokeAsync(() => ManuscriptCommands.SetRepetitionAcceptedAsync(
            State, KeyOf("alpha"), KeyOf("beta"), true, Text("UndoRepetitionAccepted"), NullLogger.Instance)));
        await State.Session!.RepetitionsIdle();

        WaitFor(() => Assert.Equal(
            new Dictionary<Guid, int> { [KeyOf("gamma")] = 1, [KeyOf("delta")] = 1 }, LastCounts));
        Assert.True(Editor.BlockRepetitions.Count > sent);
    }

    [Fact]
    public async Task SendsNothingAgain_WhenNothingChanged()
    {
        var cut = await RenderAsync();
        WaitFor(() => Assert.NotEmpty(Editor.BlockRepetitions));
        var sent = Editor.BlockRepetitions.Count;

        State.Notify();
        await cut.InvokeAsync(() => Editor.Callbacks!.OnCursorChanged(new CursorInfo(ElementKind.TextBlock, KeyOf("beta"), KeyOf("beta"), 0)));

        Assert.Equal(sent, Editor.BlockRepetitions.Count);
    }

    [Fact]
    public async Task SwitchOff_SendsEmpty()
    {
        await RenderAsync();
        WaitFor(() => Assert.NotEmpty(LastCounts));

        Settings.ShowRepetitions = false;
        State.Notify();

        WaitFor(() => Assert.Empty(LastCounts));

        Settings.ShowRepetitions = true;
        State.Notify();

        WaitFor(() => Assert.Equal(4, LastCounts.Count));
    }

    [Fact]
    public async Task MarkClick_RevealsBlock_AndExpandsBarList()
    {
        var cut = await RenderAsync();
        WaitFor(() => Assert.NotEmpty(Editor.BlockRepetitions));
        Assert.Empty(cut.FindAll(".ne-sec-rep-list"));
        var beta = KeyOf("beta");

        await cut.InvokeAsync(() => Editor.Callbacks!.OnRepetitionMarkClicked(beta));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-sec-rep-list")));
        Assert.Equal(beta, State.Cursor!.Key);
        Assert.Contains(beta, Editor.Reveals);
        Assert.Equal("true", cut.Find(".ne-sec-reps").GetAttribute("aria-expanded"));
        Assert.Single(cut.FindAll(".ne-sec-rep"));
    }

    [Fact]
    public async Task Marks_AreNoEdit_NothingIsSaved()
    {
        var cut = await RenderAsync();
        WaitFor(() => Assert.NotEmpty(Editor.BlockRepetitions));
        var documents = Editor.Documents.Count;

        await cut.InvokeAsync(() => Editor.Callbacks!.OnRepetitionMarkClicked(KeyOf("beta")));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-sec-rep-list")));

        // No document was loaded again for the marks and the file is as it was.
        Assert.Equal(documents, Editor.Documents.Count);
        Assert.Equal(BookText, _vault.Read(BookPath));
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
}
