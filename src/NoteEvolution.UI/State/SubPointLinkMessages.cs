using Microsoft.Extensions.Localization;
using NoteEvolution.Core.Links;
using NoteEvolution.UI.Resources;

namespace NoteEvolution.UI.State;

/// <summary>The messages of the sub-point assistant that more than one component shows.</summary>
public static class SubPointLinkMessages
{
    /// <summary>
    /// „Nicht vollständig zurückgenommen, weil nicht schreibbar: …“ for an undo that could not write some pages
    /// (<see cref="PagesNotRestoredException"/>); the pages are named by file name, without folders.
    /// </summary>
    public static string UndoKept(IStringLocalizer<Strings> localizer, PagesNotRestoredException error) =>
        localizer["SubPointsUndoKept", string.Join(", ", error.Paths.Select(Path.GetFileName))].Value;
}
