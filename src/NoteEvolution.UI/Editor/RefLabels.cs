using System.Globalization;
using System.Text;
using Microsoft.Extensions.Localization;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Text;
using NoteEvolution.UI.Resources;

namespace NoteEvolution.UI.Editor;

/// <summary>
/// What the editor shows of a reference: its text, its tooltip, whether its target is missing and whether it is foreign
/// (a block outside the book, no cross reference: it shows its reference text like plain text and goes nowhere).
/// </summary>
public sealed record RefLabel(string Text, string Title, bool Missing, bool Foreign = false)
{
    /// <summary>The label of a foreign reference: the reference text, no tooltip.</summary>
    public static RefLabel ForeignOf(Guid id) => new(BlockRefs.Format(id), "", false, true);
}

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
    /// <paramref name="knownElsewhere"/> knows. A block known elsewhere that is no target of the book (a note) is no cross
    /// reference (spec 2.1): <see cref="RefLabel.ForeignOf"/>, still one unit of the shown text.
    /// </summary>
    /// <param name="knownElsewhere">Whether the vault has a block with the id; <c>null</c>: none is known elsewhere.</param>
    public static Func<Guid, RefLabel> Of(Book book, IStringLocalizer<Strings> l, Func<Guid, bool>? knownElsewhere = null)
    {
        var index = CrossRefIndex.Of(book, knownElsewhere);
        var labels = new Dictionary<Guid, RefLabel>();
        return id =>
        {
            if (!labels.TryGetValue(id, out var label))
            {
                labels[id] = label = index.IsForeign(id) ? RefLabel.ForeignOf(id) : Label(book, index.Target(id), l);
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

/// <summary>
/// What the editor shows of the appendix (appendix spec 3): the labels of the people and sources the text refers to and
/// the glossary terms it marks.
/// </summary>
public static class AppendixLabels
{
    private const string Dash = " — ";

    /// <summary>
    /// The label of each person with an <c>id::</c>: the abbreviation (the name if it has none), and as tooltip the name
    /// and the topics.
    /// </summary>
    public static Dictionary<Guid, RefLabel> People(Book book) =>
        Appendix.People(book).Where(p => p.Id != Guid.Empty).DistinctBy(p => p.Id).ToDictionary(
            p => p.Id,
            p => new RefLabel(
                p.Abbreviation.Length > 0 ? p.Abbreviation : p.Name,
                p.Topics.Count > 0 ? p.Name + Dash + string.Join(", ", p.Topics) : p.Name,
                false));

    /// <summary>
    /// The label of each source with an <c>id::</c>: its number in the PDF (<see cref="AppendixIndex.SourceNumber"/>),
    /// "–" for a source no exported text cites, and as tooltip the full reference (<see cref="SourceTitle"/>).
    /// <see cref="CitationLabel"/> puts it in brackets with the locator.
    /// </summary>
    public static Dictionary<Guid, RefLabel> Sources(Book book, AppendixIndex index) =>
        Appendix.Sources(book).Where(s => s.Id != Guid.Empty).DistinctBy(s => s.Id).ToDictionary(
            s => s.Id,
            s => new RefLabel(index.SourceNumber(s.Id)?.ToString(CultureInfo.InvariantCulture) ?? "–", SourceTitle(book, s), false));

    /// <summary>The label of a citation whose source is no entry of the appendix: "Verweis fehlt", red.</summary>
    public static RefLabel MissingSource(IStringLocalizer<Strings> l) => new(l["CiteLabelMissing"], l["CiteTitleMissing"], true);

    /// <summary>What a citation shows: "[3]", with a locator "[3, S. 12]"; a missing source its missing label.</summary>
    public static string CitationLabel(RefLabel source, string? locator) =>
        source.Missing ? source.Text
        : string.IsNullOrWhiteSpace(locator) ? $"[{source.Text}]"
        : $"[{source.Text}, {locator.Trim()}]";

    /// <summary>
    /// The full reference of a source: "Autor; Autor: Titel. Herausgeber, Jahr. URL"; missing parts are left out with
    /// their separators, and an author who is no person any more too.
    /// </summary>
    public static string SourceTitle(Book book, SourceEntry source)
    {
        var authors = string.Join("; ", source.AuthorIds.Select(id => Appendix.PersonById(book, id)?.Name).OfType<string>().Where(n => n.Length > 0));
        var text = new StringBuilder();
        if (authors.Length > 0)
        {
            text.Append(authors).Append(": ");
        }

        text.Append(source.Title);
        var published = string.Join(", ", new[] { source.Publisher, source.Year }.Where(p => !string.IsNullOrWhiteSpace(p)));
        if (published.Length > 0)
        {
            text.Append(". ").Append(published);
        }

        if (!string.IsNullOrWhiteSpace(source.Url))
        {
            text.Append(". ").Append(source.Url);
        }

        return text.ToString().Trim();
    }

    /// <summary>
    /// The glossary terms to mark: the first hit of each (<see cref="AppendixIndex.GlossaryHits"/>), with the term and its
    /// explanation as tooltip and the entry's key for the glossary window.
    /// </summary>
    public static List<GlossaryMark> Marks(Book book, AppendixIndex index)
    {
        var entries = Appendix.Glossary(book).Where(g => g.Id != Guid.Empty).DistinctBy(g => g.Id).ToDictionary(g => g.Id);
        var marks = new List<GlossaryMark>();
        foreach (var hit in index.GlossaryHits)
        {
            if (entries.TryGetValue(hit.EntryId, out var entry))
            {
                var explanation = entry.Explanation.ReplaceLineEndings(" ").Trim();
                marks.Add(new GlossaryMark(
                    hit.ParagraphKey, hit.Start, hit.Length, explanation.Length > 0 ? entry.Term + Dash + explanation : entry.Term, entry.Key));
            }
        }

        return marks;
    }
}
