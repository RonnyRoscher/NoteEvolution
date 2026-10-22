using Microsoft.Extensions.Localization;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Text;
using NoteEvolution.UI.Resources;

namespace NoteEvolution.UI.Editor;

/// <summary>What the editor shows of a cross reference: its text, its tooltip and whether its target is missing.</summary>
public sealed record RefLabel(string Text, string Title, bool Missing);

/// <summary>The labels of the cross references of a book as the editor shows them (spec 2.3).</summary>
public static class RefLabels
{
    /// <summary>How many characters of a target text block's text the label shows.</summary>
    private const int LabelExcerptLength = 40;

    /// <summary>How many characters of a target text block's text the tooltip shows.</summary>
    internal const int TitleExcerptLength = 60;

    internal const string Separator = " › ";

    /// <summary>
    /// The label of a reference by its target id, from the book's <see cref="CrossRefIndex"/>: "→ Titel" for a heading
    /// (tooltip: its path), "→ Abschnitt: Textanfang …" for a text block (tooltip: the section's path › the start of the
    /// text), "→ Verweis fehlt" with <see cref="RefLabel.Missing"/> for an id neither the book nor
    /// <paramref name="knownElsewhere"/> knows. <c>null</c> for a block known elsewhere that is no target of the book (a
    /// note): that is no cross reference and stays plain text (spec 2.1).
    /// </summary>
    /// <param name="knownElsewhere">Whether the vault has a block with the id; <c>null</c>: none is known elsewhere.</param>
    public static Func<Guid, RefLabel?> Of(Book book, IStringLocalizer<Strings> l, Func<Guid, bool>? knownElsewhere = null)
    {
        var index = CrossRefIndex.Of(book, knownElsewhere);
        var labels = new Dictionary<Guid, RefLabel?>();
        return id =>
        {
            if (!labels.TryGetValue(id, out var label))
            {
                labels[id] = label = index.IsForeign(id) ? null : Label(book, index.Target(id), l);
            }

            return label;
        };
    }

    /// <summary>
    /// The start of the text as the editor shows it (without emphasis markers), on one line, at most
    /// <paramref name="length"/> characters and "…"; a reference in it shows as "→".
    /// </summary>
    internal static string Excerpt(string content, int length)
    {
        var text = string.Concat(InlineMarkdown.Parse(BlockTextEscape.Unescape(content)).Select(run => WithoutRefs(run.Text)))
            .Replace("\\*", "*").Replace("\\_", "_").ReplaceLineEndings(" ").Trim();
        return text.Length <= length ? text : text[..length].TrimEnd() + "…";
    }

    private static RefLabel Label(Book book, CrossRefTarget? target, IStringLocalizer<Strings> l)
    {
        switch (target)
        {
            case { Heading: { } heading }:
                return new RefLabel(l["RefLabelHeading", TitleOf(heading, l)], Path(heading, l), false);
            case { TextBlock: { } textBlock }:
                var section = SectionTitle(book, textBlock.Section, l);
                var path = textBlock.Section.Block is null ? book.Title : Path(textBlock.Section, l);
                return new RefLabel(
                    l["RefLabelText", section, Excerpt(textBlock.Text, LabelExcerptLength)],
                    path + Separator + Excerpt(textBlock.Text, TitleExcerptLength),
                    false);
            default:
                return new RefLabel(l["RefLabelMissing"], l["RefTitleMissing"], true);
        }
    }

    /// <summary>The text with each reference as "→".</summary>
    private static string WithoutRefs(string text)
    {
        foreach (var reference in BlockRefs.Find(text).Reverse())
        {
            text = text[..reference.Start] + "→" + text[(reference.Start + reference.Length)..];
        }

        return text;
    }

    /// <summary>The title of the section a text block of <paramref name="section"/> belongs to; the book's title at the top.</summary>
    internal static string SectionTitle(Book book, OutlineNode section, IStringLocalizer<Strings> l) =>
        section.Block is null ? book.Title : TitleOf(section, l);

    internal static string TitleOf(OutlineNode heading, IStringLocalizer<Strings> l) =>
        heading.Title.Length > 0 ? heading.Title : l["OutlineUntitled"];

    /// <summary>The titles of the heading and the headings above it, from the top.</summary>
    internal static string Path(OutlineNode heading, IStringLocalizer<Strings> l)
    {
        var titles = new List<string>();
        for (var node = heading; node?.Block is not null; node = node.Parent)
        {
            titles.Add(TitleOf(node, l));
        }

        titles.Reverse();
        return string.Join(Separator, titles);
    }
}
