using Microsoft.Extensions.Localization;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Repetitions;
using NoteEvolution.UI.Resources;

namespace NoteEvolution.UI.State;

/// <summary>How a place of a repetition is named in lists: "Abschnitt › Textanfang" (the assistant and the section bar).</summary>
internal static class RepetitionPlace
{
    public const int ExcerptLength = 60;

    /// <summary>The title of a section, or "(ohne Titel)" for one without a title.</summary>
    public static string TitleOf(OutlineNode section, IStringLocalizer<Strings> l) =>
        section.Title.Length > 0 ? section.Title : l["OutlineUntitled"].Value;

    /// <summary>
    /// "Abschnitt › Textanfang": the block's own section and the start of its text (as <paramref name="set"/> knows it);
    /// just the start for text before the first heading.
    /// </summary>
    public static string Of(Book? book, RepetitionSet set, Guid key, IStringLocalizer<Strings> l)
    {
        var start = set.Block(key)?.Paragraphs[0].Plain is { } plain ? Excerpt.FirstLine(plain, ExcerptLength) : "";
        return book?.FindTextBlock(key)?.Section is { Parent: not null } section
            ? $"{TitleOf(section, l)}{ManuscriptEditor.SectionPathSeparator}{start}"
            : start;
    }
}
