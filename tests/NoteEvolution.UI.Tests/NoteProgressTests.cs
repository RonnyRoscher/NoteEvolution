using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NoteEvolution.AI.Understanding;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>The status dot of a note card: its state and tooltip from the note's progress (spec 7.2).</summary>
public class NoteProgressTests : UiTestContext
{
    /// <summary>An unused note with the language model and without local AI, no core statement and no placement yet.</summary>
    private static readonly NoteStatusInput Plain = new(
        Used: false, LanguageModel: true, Embedded: null, NeedsCore: false, CoreDone: false, CoreFailed: false,
        Placement: PlacementState.Missing, Running: null, Fit: null);

    private (NoteDot Dot, string Tooltip) Of(NoteStatusInput input) =>
        NoteProgress.Of(input, Services.GetRequiredService<IStringLocalizer<Strings>>());

    [Fact]
    public void Priority_Running_Failed_Current_Stale_Pending()
    {
        var all = Plain with { Running = UnderstandingKind.Placement, CoreFailed = true, NeedsCore = true, Placement = PlacementState.Current };
        Assert.Equal(NoteDot.Running, Of(all).Dot);
        Assert.Equal(NoteDot.Failed, Of(all with { Running = null }).Dot);
        Assert.Equal(NoteDot.Failed, Of(Plain with { Placement = PlacementState.Failed }).Dot);
        Assert.Equal(NoteDot.Current, Of(Plain with { Placement = PlacementState.Current }).Dot);
        Assert.Equal(NoteDot.Stale, Of(Plain with { Placement = PlacementState.Stale }).Dot);
        Assert.Equal(NoteDot.Pending, Of(Plain).Dot);
        Assert.Equal(NoteDot.Running, Of(Plain with { Running = UnderstandingKind.Note, NeedsCore = true }).Dot);
    }

    [Fact]
    public void UsedNote_OrWithoutLanguageModel_None()
    {
        Assert.Equal(NoteDot.None, Of(Plain with { Used = true, Placement = PlacementState.Current }).Dot);
        Assert.Equal(NoteDot.None, Of(Plain with { LanguageModel = false }).Dot);
        Assert.Equal(NoteDot.None, Of(Plain with { LanguageModel = false, Running = UnderstandingKind.Placement }).Dot);
    }

    [Fact]
    public void Tooltip_ListsTheStagesThatApply()
    {
        var full = Plain with { Embedded = true, NeedsCore = true, CoreDone = true };
        Assert.Equal("Embedding fertig · Kernaussage fertig · Zuordnung ausstehend", Of(full).Tooltip);

        // Under 120 characters there is no core statement; without local AI no embedding.
        Assert.Equal("Embedding ausstehend · Zuordnung ausstehend", Of(Plain with { Embedded = false }).Tooltip);
        Assert.Equal("Zuordnung ausstehend", Of(Plain).Tooltip);
        Assert.Equal("Embedding fertig · Kernaussage ausstehend · Zuordnung ausstehend",
            Of(full with { CoreDone = false }).Tooltip);
    }

    [Fact]
    public void Tooltip_Current_WithFit()
    {
        var current = Plain with { Placement = PlacementState.Current, Fit = PlacementFit.Good };
        Assert.Equal("Zuordnung aktuell · Passung gut", Of(current).Tooltip);
        Assert.Equal("Zuordnung aktuell · Passung teilweise", Of(current with { Fit = PlacementFit.Partial }).Tooltip);
        Assert.Equal("Zuordnung aktuell · Passung kaum", Of(current with { Fit = PlacementFit.Poor }).Tooltip);
        Assert.Equal("Embedding fertig · Kernaussage fertig · Zuordnung aktuell · Passung gut",
            Of(current with { Embedded = true, NeedsCore = true, CoreDone = true }).Tooltip);
    }

    [Fact]
    public void Tooltip_Running_Failed_Stale_SentenceFirst_ThenTheOtherStages()
    {
        Assert.Equal("Wird gerade berechnet: Zuordnung", Of(Plain with { Running = UnderstandingKind.Placement }).Tooltip);
        Assert.Equal("Zuordnung veraltet (Notiz oder Buch geändert) · wird neu berechnet",
            Of(Plain with { Placement = PlacementState.Stale }).Tooltip);
        Assert.Equal("Zuordnung fehlgeschlagen, wird nach der nächsten Änderung erneut versucht",
            Of(Plain with { Placement = PlacementState.Failed }).Tooltip);

        var stages = Plain with { Embedded = true, NeedsCore = true, CoreDone = true };
        Assert.Equal("Wird gerade berechnet: Zuordnung · Embedding fertig · Kernaussage fertig",
            Of(stages with { Running = UnderstandingKind.Placement }).Tooltip);
        Assert.Equal("Zuordnung veraltet (Notiz oder Buch geändert) · wird neu berechnet · Embedding fertig · Kernaussage fertig",
            Of(stages with { Placement = PlacementState.Stale }).Tooltip);
        Assert.Equal("Zuordnung fehlgeschlagen, wird nach der nächsten Änderung erneut versucht · Embedding fertig · Kernaussage fertig",
            Of(stages with { Placement = PlacementState.Failed }).Tooltip);

        // The core statement running or given up: its sentence first, the placement stage after the others.
        var core = Plain with { Embedded = true, NeedsCore = true };
        Assert.Equal("Wird gerade berechnet: Kernaussage · Embedding fertig · Zuordnung ausstehend",
            Of(core with { Running = UnderstandingKind.Note }).Tooltip);
        Assert.Equal("Kernaussage fehlgeschlagen, wird nach der nächsten Änderung erneut versucht · Embedding fertig · Zuordnung ausstehend",
            Of(core with { CoreFailed = true }).Tooltip);
    }
}
