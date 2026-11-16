using AngleSharp.Dom;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NoteEvolution.AI.Tests;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>
/// The repetition counts in the outline rows and the switch next to the coverage toggle (repetitions spec 6): the not
/// accepted pairs with a block in the section and its sub-sections, shown while the switch is on.
/// </summary>
public class OutlineRepetitionTests : UiTestContext
{
    private const string BookPath = "pages/Buch - Alpha.md";

    private const string RunX = "eins zwei drei vier fuenf sechs sieben acht neun";

    /// <summary>One literal pair (alpha in "Eins", beta in its sub-section "Eins-A"); "Zwei" has none.</summary>
    private const string Nested =
        "title:: Alpha\ntype:: book\n\n"
        + $"- # Eins\n\t- alpha {RunX}\n\t- ## Eins-A\n\t\t- beta {RunX}\n"
        + "- # Zwei\n\t- gamma ohne Wiederholung\n";

    private readonly List<TestVault> _vaults = [];

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _vaults.ForEach(v => v.Dispose());
        }
    }

    private string Text(string key, params object[] arguments) =>
        Services.GetRequiredService<IStringLocalizer<Strings>>()[key, arguments].Value;

    private Guid KeyOf(string start) =>
        State.CurrentBook!.Page.AllBlocks().Single(b => b.Content.StartsWith(start, StringComparison.Ordinal)).Key;

    private async Task<VaultSession> OpenAsync(string bookText = Nested)
    {
        var vault = TestVault.Create((BookPath, bookText));
        _vaults.Add(vault);
        var session = await OpenSessionAsync(vault);
        await session.RepetitionsIdle();
        return session;
    }

    private static IElement Row(IRenderedComponent<OutlinePane> cut, string title) =>
        cut.FindAll(".ne-outline-row").First(r => r.QuerySelector(".ne-outline-title")?.TextContent == title);

    private static string? Reps(IRenderedComponent<OutlinePane> cut, string title) =>
        Row(cut, title).QuerySelector(".ne-outline-reps")?.TextContent.Trim();

    [Fact]
    public async Task Row_ShowsCountOfSectionWithSubSections()
    {
        await OpenAsync();

        var cut = Render<OutlinePane>();

        // The pair has one block in "Eins" itself and one in its sub-section: both rows count it once.
        Assert.Equal("≈ 1", Reps(cut, "Eins"));
        Assert.Equal("≈ 1", Reps(cut, "Eins-A"));
        Assert.Equal(Text("BlockRepetitionsTitle", 1), Row(cut, "Eins").QuerySelector(".ne-outline-reps")!.GetAttribute("title"));
        Assert.Equal("1 Wiederholungen", Row(cut, "Eins").QuerySelector(".ne-outline-reps")!.GetAttribute("title"));
        Assert.NotNull(Row(cut, "Eins").QuerySelector(".ne-outline-meta .ne-outline-reps"));
    }

    [Fact]
    public async Task Row_NoCount_WhenZero()
    {
        await OpenAsync();

        var cut = Render<OutlinePane>();

        Assert.Null(Reps(cut, "Zwei"));
        Assert.Equal(2, cut.FindAll(".ne-outline-reps").Count);
    }

    [Fact]
    public async Task SwitchOff_HidesCounts_AndIsRemembered()
    {
        await OpenAsync();
        var cut = Render<OutlinePane>();
        Assert.Equal("true", cut.Find(".ne-outline-repetitions-toggle").GetAttribute("aria-pressed"));
        Assert.Equal(Text("OutlineRepetitionsToggle"), cut.Find(".ne-outline-repetitions-toggle").TextContent.Trim());
        Assert.Equal("Wiederholungen anzeigen", cut.Find(".ne-outline-repetitions-toggle").TextContent.Trim());
        var notified = 0;
        State.Changed += () => notified++;

        cut.Find(".ne-outline-repetitions-toggle").Click();

        Assert.Empty(cut.FindAll(".ne-outline-reps"));
        Assert.Equal("false", cut.Find(".ne-outline-repetitions-toggle").GetAttribute("aria-pressed"));
        Assert.False(Settings.ShowRepetitions);
        Assert.False(UiSettings.Load(Platform.UserDataDirectory).ShowRepetitions);
        Assert.True(notified > 0, "The editor panes are told to resend their marks.");

        cut.Find(".ne-outline-repetitions-toggle").Click();

        Assert.Equal("≈ 1", Reps(cut, "Eins"));
        Assert.True(UiSettings.Load(Platform.UserDataDirectory).ShowRepetitions);
    }

    [Fact]
    public async Task Incomplete_ShowsEllipsis()
    {
        var vault = TestVault.Create((BookPath, Nested));
        _vaults.Add(vault);
        var embedder = new FakeEmbedder();
        var session = await OpenWithAiAsync(vault, embedder);
        await session.RepetitionsIdle();
        using var gate = new ManualResetEventSlim();
        try
        {
            // The edited block's vector waits: the literal pair is there, the set is not complete.
            embedder.OnEmbed = (_, ct) => gate.Wait(ct);
            State.CurrentBook!.Page.AllBlocks().First(b => b.Content.StartsWith("alpha", StringComparison.Ordinal)).SetContent($"alpha neu {RunX}");
            session.Writer.Save(State.CurrentBook!.Page);
            State.RefreshBook();

            var cut = Render<OutlinePane>();

            cut.WaitForAssertion(() => Assert.Equal("≈ 1 …", Reps(cut, "Eins")), TimeSpan.FromSeconds(20));
            Assert.Equal("≈ 1 …", Reps(cut, "Eins-A"));
        }
        finally
        {
            gate.Set();
        }

        await session.RepetitionsIdle();
    }

    [Fact]
    public async Task Saving_KeepsCounts_WithEllipsis_UntilTheNewSnapshotIsAnalysed()
    {
        var session = await OpenAsync();
        var cut = Render<OutlinePane>();
        Assert.Equal("≈ 1", Reps(cut, "Eins"));
        using var gate = new ManualResetEventSlim();
        try
        {
            // The analysis of the saved snapshot waits: the old set belongs to other nodes, but its block keys still count.
            session.BeforeRepetitionsPublished = () => gate.Wait();
            State.CurrentBook!.Page.AllBlocks().First(b => b.Content.StartsWith("alpha", StringComparison.Ordinal)).SetContent($"alpha neu {RunX}");
            session.Writer.Save(State.CurrentBook!.Page);
            await cut.InvokeAsync(() =>
            {
                State.RefreshBook();
                State.Notify();
            });

            cut.WaitForAssertion(() => Assert.True(session.RepetitionsShown(State.CurrentBook).Pending));
            cut.WaitForAssertion(() => Assert.Equal("≈ 1 …", Reps(cut, "Eins")));
            Assert.Equal("≈ 1 …", Reps(cut, "Eins-A"));
        }
        finally
        {
            session.BeforeRepetitionsPublished = null;
            gate.Set();
        }

        await session.RepetitionsIdle();
        cut.WaitForAssertion(() => Assert.Equal("≈ 1", Reps(cut, "Eins")));
    }

    [Fact]
    public async Task Rerenders_OnRepetitionsChanged()
    {
        var session = await OpenAsync();
        var cut = Render<OutlinePane>();
        Assert.Equal("≈ 1", Reps(cut, "Eins"));

        Assert.Null(await cut.InvokeAsync(() => ManuscriptCommands.SetRepetitionAcceptedAsync(
            State, KeyOf("alpha"), KeyOf("beta"), true, Text("UndoRepetitionAccepted"), NullLogger.Instance)));
        await session.RepetitionsIdle();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-outline-reps")));
    }
}
