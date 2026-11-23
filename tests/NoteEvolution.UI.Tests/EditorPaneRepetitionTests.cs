using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NoteEvolution.AI.Tests;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Repetitions;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Editor;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>
/// The repetition marks of the editor (repetitions tab spec 3): the pane sends the underlines after a document and when
/// the set or the repetitions tab changes, nothing when the switch is off, the same to both halves of a split view.
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

    private IReadOnlyList<RepetitionTextMark> Last => Editor.RepetitionMarks[^1];

    /// <summary>The paragraphs the last marks are in, each once, in order.</summary>
    private List<Guid> MarkedBlocks => [.. Last.Select(m => m.ParagraphKey).Distinct()];

    private async Task<IRenderedComponent<EditorPane>> RenderAsync(TestVault? vault = null, bool ai = false)
    {
        var session = ai ? await OpenWithAiAsync(vault ?? _vault) : await OpenSessionAsync(vault ?? _vault);
        await session.RepetitionsIdle();
        var cut = Render<EditorPane>();
        cut.WaitForAssertion(() => Assert.NotEmpty(Editor.Documents));
        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionBoxMoved(120.5, true));
        return cut;
    }

    [Fact]
    public async Task SendsUnderlines_AfterDocument_AndOnChange()
    {
        var cut = await RenderAsync();

        WaitFor(() => Assert.NotEmpty(Editor.RepetitionMarks));
        // Only blocks with repetitions are marked, each at its shared run; epsilon has none.
        Assert.Equal([KeyOf("alpha"), KeyOf("beta"), KeyOf("gamma"), KeyOf("delta")], MarkedBlocks);
        Assert.All(Last, m => Assert.Equal(RepetitionMarkKind.Literal, m.Kind));
        var alpha = Last.Single(m => m.ParagraphKey == KeyOf("alpha"));
        Assert.Equal((6, RunX.Length), (alpha.Start, alpha.Length));
        // The tooltip is localized here, not in the script, and names the other place.
        Assert.Equal(Text("RepetitionAlsoIn", $"Eins{ManuscriptEditor.SectionPathSeparator}beta {RunX}"), alpha.Title);
        var sent = Editor.RepetitionMarks.Count;

        // „Gewollt“ for alpha~beta changes the set: the new marks are sent.
        Assert.Null(await cut.InvokeAsync(() => ManuscriptCommands.SetRepetitionAcceptedAsync(
            State, KeyOf("alpha"), KeyOf("beta"), true, Text("UndoRepetitionAccepted"), NullLogger.Instance)));
        await State.Session!.RepetitionsIdle();

        WaitFor(() => Assert.Equal([KeyOf("gamma"), KeyOf("delta")], MarkedBlocks));
        Assert.True(Editor.RepetitionMarks.Count > sent);
    }

    [Fact]
    public async Task SendsNothingAgain_WhenNothingChanged()
    {
        var cut = await RenderAsync();
        WaitFor(() => Assert.NotEmpty(Editor.RepetitionMarks));
        var sent = Editor.RepetitionMarks.Count;

        State.Notify();
        await cut.InvokeAsync(() => Editor.Callbacks!.OnCursorChanged(new CursorInfo(ElementKind.TextBlock, KeyOf("beta"), KeyOf("beta"), 0)));
        // Opening the tab without semantic repetitions or a focus changes no mark either.
        await cut.InvokeAsync(() => State.SetRepetitionsTabOpen(true));

        Assert.Equal(sent, Editor.RepetitionMarks.Count);
    }

    [Fact]
    public async Task Pane_SwitchOff_SendsNoMarks()
    {
        await RenderAsync();
        WaitFor(() => Assert.NotEmpty(Last));

        Settings.ShowRepetitions = false;
        State.Notify();

        WaitFor(() => Assert.Empty(Last));

        // Nor with the tab open and a hit in focus.
        State.SetRepetitionsTabOpen(true);
        State.SetRepetitionFocus(new RepetitionFocus(RepetitionKind.Literal, KeyOf("alpha"), KeyOf("beta"), false));
        WaitFor(() => Assert.Empty(Last));

        Settings.ShowRepetitions = true;
        State.Notify();

        WaitFor(() => Assert.Equal(4, MarkedBlocks.Count));
    }

    [Fact]
    public async Task Pane_FocusWithTheTab_HighlightsTheOrigin()
    {
        await RenderAsync();
        WaitFor(() => Assert.NotEmpty(Last));

        State.SetRepetitionFocus(new RepetitionFocus(RepetitionKind.Literal, KeyOf("alpha"), KeyOf("beta"), Swapped: true));
        Assert.DoesNotContain(Last, m => m.Kind == RepetitionMarkKind.Current);

        State.SetRepetitionsTabOpen(true);

        WaitFor(() => Assert.Equal(KeyOf("beta"), Assert.Single(Last, m => m.Kind == RepetitionMarkKind.Current).ParagraphKey));

        State.SetRepetitionsTabOpen(false);

        WaitFor(() => Assert.DoesNotContain(Last, m => m.Kind == RepetitionMarkKind.Current));
    }

    [Fact]
    public async Task Pane_TabOpened_SendsTheSemanticMarksToo()
    {
        // The fake embedder gives texts with the same letters the same vector: sonne… and schnee… mean the same.
        using var vault = TestVault.Create((BookPath, "title:: Alpha\ntype:: book\n\n"
            + "- # Eins\n\t- sonne mond sterne himmel wolken regen wind schnee\n"
            + "\t- schnee wind regen wolken himmel sterne mond sonne\n"));
        await RenderAsync(vault, ai: true);
        WaitFor(() => Assert.Contains(State.Session!.RepetitionsOf(Book).All, r => r.Kind == RepetitionKind.Semantic));
        // Without the tab there is nothing to mark: no literal repetitions here.
        Assert.DoesNotContain(Editor.RepetitionMarks.SelectMany(marks => marks), m => m.Kind == RepetitionMarkKind.Semantic);

        State.SetRepetitionsTabOpen(true);

        WaitFor(() => Assert.Equal(
            [KeyOf("sonne"), KeyOf("schnee")], Last.Where(m => m.Kind == RepetitionMarkKind.Semantic).Select(m => m.ParagraphKey)));

        State.SetRepetitionsTabOpen(false);

        WaitFor(() => Assert.DoesNotContain(Last, m => m.Kind == RepetitionMarkKind.Semantic));
    }

    [Fact]
    public async Task SplitView_BothPanesGetTheSameMarks()
    {
        UseEditorPerPane();
        var session = await OpenSessionAsync(_vault);
        await session.RepetitionsIdle();
        State.SetSplit(true);
        var upper = Render<EditorPane>(p => p.Add(e => e.View, State.Views[0]));
        var lower = Render<EditorPane>(p => p.Add(e => e.View, State.Views[1]));
        upper.WaitForAssertion(() => Assert.NotEmpty(Editors[0].Documents));
        lower.WaitForAssertion(() => Assert.NotEmpty(Editors[1].Documents));

        WaitFor(() => Assert.NotEmpty(Editors[0].RepetitionMarks));
        WaitFor(() => Assert.NotEmpty(Editors[1].RepetitionMarks));
        Assert.Equal(Editors[0].RepetitionMarks[^1], Editors[1].RepetitionMarks[^1]);

        State.SetRepetitionFocus(new RepetitionFocus(RepetitionKind.Literal, KeyOf("gamma"), KeyOf("delta"), false));
        State.SetRepetitionsTabOpen(true);

        WaitFor(() => Assert.Contains(Editors[0].RepetitionMarks[^1], m => m.Kind == RepetitionMarkKind.Current));
        WaitFor(() => Assert.Equal(Editors[0].RepetitionMarks[^1], Editors[1].RepetitionMarks[^1]));
    }

    [Fact]
    public async Task Marks_AreNoEdit_NothingIsSaved()
    {
        await RenderAsync();
        WaitFor(() => Assert.NotEmpty(Editor.RepetitionMarks));
        var documents = Editor.Documents.Count;

        State.SetRepetitionFocus(new RepetitionFocus(RepetitionKind.Literal, KeyOf("alpha"), KeyOf("beta"), false));
        State.SetRepetitionsTabOpen(true);
        WaitFor(() => Assert.Contains(Last, m => m.Kind == RepetitionMarkKind.Current));

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
