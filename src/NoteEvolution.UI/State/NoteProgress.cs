using Microsoft.Extensions.Localization;
using NoteEvolution.AI.Understanding;
using NoteEvolution.UI.Resources;

namespace NoteEvolution.UI.State;

/// <summary>The status dot of a note card (spec 7.2); <see cref="None"/> draws no dot.</summary>
public enum NoteDot { None, Running, Failed, Current, Stale, Pending }

/// <summary>
/// What a note's status is decided from: whether it is used, whether the language model works for the vault, whether its
/// embedding is there (<c>null</c> without local AI), whether it needs a core statement (long enough) and whether that is
/// done or given up, the state of its placement check in the current book, the kind of request running for it, and the
/// placement's fit while it is current.
/// </summary>
public sealed record NoteStatusInput(bool Used, bool LanguageModel, bool? Embedded, bool NeedsCore, bool CoreDone,
    bool CoreFailed, PlacementState Placement, UnderstandingKind? Running, PlacementFit? Fit);

/// <summary>Decides a note's status dot and its tooltip (spec 7.2); pure.</summary>
public static class NoteProgress
{
    private const string Separator = " · ";

    /// <summary>
    /// The dot by priority: a request running for the note, the core statement or placement given up, the placement
    /// current, stale, else pending; none for a used note or without the language model. The tooltip lists the stages
    /// that apply (embedding with local AI, core statement for a long enough note, placement); for a running, failed or
    /// stale state a sentence about it comes first and stands for its own stage.
    /// </summary>
    public static (NoteDot Dot, string Tooltip) Of(NoteStatusInput input, IStringLocalizer<Strings> l)
    {
        if (input.Used || !input.LanguageModel)
        {
            return (NoteDot.None, string.Empty);
        }

        var coreFailed = input.NeedsCore && input.CoreFailed && !input.CoreDone;
        var (dot, sentence, about) = input switch
        {
            { Running: UnderstandingKind.Note } => (NoteDot.Running, l["NoteDotRunning", l["NoteKindCore"].Value].Value, Stage.Core),
            { Running: { } } => (NoteDot.Running, l["NoteDotRunning", l["NoteKindPlacement"].Value].Value, Stage.Placement),
            _ when coreFailed => (NoteDot.Failed, l["NoteDotCoreFailed"].Value, Stage.Core),
            { Placement: PlacementState.Failed } => (NoteDot.Failed, l["NoteDotPlacementFailed"].Value, Stage.Placement),
            { Placement: PlacementState.Current } => (NoteDot.Current, null, Stage.None),
            { Placement: PlacementState.Stale } => (NoteDot.Stale, l["NoteDotStale"].Value, Stage.Placement),
            _ => (NoteDot.Pending, null, Stage.None),
        };

        var parts = new List<string>();
        if (sentence is not null)
        {
            parts.Add(sentence);
        }

        if (input.Embedded is { } embedded)
        {
            parts.Add(l[embedded ? "NoteStageEmbeddingDone" : "NoteStageEmbeddingPending"]);
        }

        if (input.NeedsCore && about != Stage.Core)
        {
            parts.Add(l[input.CoreDone ? "NoteStageCoreDone" : coreFailed ? "NoteStageCoreFailed" : "NoteStageCorePending"]);
        }

        if (about != Stage.Placement)
        {
            parts.Add(l[input.Placement switch
            {
                PlacementState.Current => "NoteStagePlacementCurrent",
                PlacementState.Stale => "NoteStagePlacementStale",
                PlacementState.Failed => "NoteStagePlacementFailed",
                _ => "NoteStagePlacementPending",
            }]);
            if (input is { Placement: PlacementState.Current, Fit: { } fit })
            {
                parts.Add(l[fit switch
                {
                    PlacementFit.Good => "NoteFitGood",
                    PlacementFit.Poor => "NoteFitPoor",
                    _ => "NoteFitPartial",
                }]);
            }
        }

        return (dot, string.Join(Separator, parts));
    }

    /// <summary>The stage the first sentence of the tooltip is about (it is not listed again).</summary>
    private enum Stage { None, Core, Placement }
}
