using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NoteEvolution.Core.Text;

namespace NoteEvolution.Core.Books;

/// <summary>A glossary term found in the text.</summary>
/// <param name="EntryId">The <see cref="GlossaryEntry.Id"/> of the entry.</param>
/// <param name="ParagraphKey">The key of the text block or detail that holds the hit.</param>
/// <param name="Start">
/// The offset in the text as the editor shows it (a block reference or source citation counts as one unit; see
/// <see cref="ManuscriptEditor.ContentOffset"/>).
/// </param>
/// <param name="Length">The length of the hit in the same units.</param>
public sealed record GlossaryHit(Guid EntryId, Guid ParagraphKey, int Start, int Length);

/// <summary>
/// What the appendix of a book derives from its text, computed from a snapshot of the book: the reading order of the
/// PDF, the source numbers, the first hit of each glossary term, how often an entry is referred to and the references
/// whose appendix target is missing. The appendix itself is no part of the text.
/// </summary>
public sealed partial class AppendixIndex
{
    private const char Placeholder = '￼';
    private static readonly CompareInfo German = new CultureInfo("de-DE").CompareInfo;

    private readonly List<Guid> _readingOrder = [];
    private readonly Dictionary<Guid, int> _sourceNumbers = [];
    private readonly List<GlossaryHit> _glossaryHits = [];
    private readonly Dictionary<Guid, int> _referenceCounts = [];
    private readonly List<(Guid ElementKey, Guid MissingId)> _missingTargets = [];

    /// <summary>A heading, text block or detail of the book's text, with the text the editor shows for it.</summary>
    private sealed record Element(Guid Key, string Shown, bool IsHeading, bool Exported);

    [GeneratedRegex(@"\[\[[^\]\r\n]*\]\]|\[[^\]\r\n]*\]\([^)\r\n]*\)")]
    private static partial Regex LinkRegex();

    private AppendixIndex()
    {
    }

    /// <summary>
    /// The keys of the headings, text blocks and details of the PDF in reading order: book order, without blocks
    /// whose status is relevant or remark (with their details) and without the appendix. This is the rule of the
    /// PDF export (<see cref="BlockStatuses.IsExcluded"/>, <see cref="TextBlock.ExportedParagraphs"/>).
    /// </summary>
    public IReadOnlyList<Guid> ReadingOrder => _readingOrder;

    /// <summary>The first hit of every glossary term (or one of its forms) that occurs in the text.</summary>
    public IReadOnlyList<GlossaryHit> GlossaryHits => _glossaryHits;

    /// <summary>
    /// The references that need a target in the appendix and have none: a <c>{{quelle}}</c> whose source is no
    /// entry (<c>ElementKey</c> is the element with the macro), and an author of a source who is no person
    /// (<c>ElementKey</c> is the source entry). A <c>((id))</c> without target in the text is a cross reference
    /// (<see cref="CrossRefIndex.Missing"/>), not listed here.
    /// </summary>
    public IReadOnlyList<(Guid ElementKey, Guid MissingId)> MissingTargets => _missingTargets;

    public static AppendixIndex Of(Book book)
    {
        var index = new AppendixIndex();
        var people = Appendix.People(book).Where(p => p.Id != Guid.Empty).Select(p => p.Id).ToHashSet();
        var sources = Appendix.Sources(book);
        var sourceIds = sources.Where(s => s.Id != Guid.Empty).Select(s => s.Id).ToHashSet();

        var elements = new List<Element>();
        Collect(book.Root, elements);

        foreach (var element in elements)
        {
            if (element.Exported)
            {
                index._readingOrder.Add(element.Key);
            }

            foreach (var citation in SourceCitations.Find(element.Shown))
            {
                if (sourceIds.Contains(citation.Id))
                {
                    index.Count(citation.Id);
                    if (element.Exported)
                    {
                        index._sourceNumbers.TryAdd(citation.Id, index._sourceNumbers.Count + 1);
                    }
                }
                else
                {
                    index._missingTargets.Add((element.Key, citation.Id));
                }
            }

            foreach (var reference in BlockRefs.Find(element.Shown).Where(r => people.Contains(r.Id)))
            {
                index.Count(reference.Id);
            }
        }

        foreach (var source in sources)
        {
            foreach (var author in source.AuthorIds)
            {
                if (people.Contains(author))
                {
                    index.Count(author);
                }
                else
                {
                    index._missingTargets.Add((source.Key, author));
                }
            }
        }

        index.FindGlossaryHits(Appendix.Glossary(book), elements.Where(e => e is { IsHeading: false, Exported: true }));
        return index;
    }

    /// <summary>The number of the source in the PDF; <c>null</c> if no text of the PDF cites it.</summary>
    public int? SourceNumber(Guid sourceId) => _sourceNumbers.TryGetValue(sourceId, out var number) ? number : null;

    /// <summary>
    /// How many references name the entry: people in the text (block references), people as author of a source and
    /// sources in the text (citations), in the whole text including blocks the PDF leaves out. 0 for a glossary entry.
    /// </summary>
    public int ReferenceCount(Guid entryId) => _referenceCounts.GetValueOrDefault(entryId);

    private void Count(Guid id) => _referenceCounts[id] = _referenceCounts.GetValueOrDefault(id) + 1;

    /// <summary>Every heading, text block and detail outside the appendix in book order; a detail is exported with its block.</summary>
    private static void Collect(OutlineNode node, List<Element> elements)
    {
        foreach (var item in node.Items)
        {
            switch (item)
            {
                case OutlineNode { AppendixKind: not AppendixKind.None }:
                    break;
                case OutlineNode child:
                    elements.Add(new Element(child.Key, child.Title, IsHeading: true, Exported: true));
                    Collect(child, elements);
                    break;
                case TextBlock text:
                    var exported = !BlockStatuses.IsExcluded(text.Status);
                    elements.Add(new Element(text.Key, BlockTextEscape.Unescape(text.Text), IsHeading: false, exported));
                    var exportedDetails = exported ? text.ExportedParagraphs.ToHashSet() : [];
                    foreach (var paragraph in text.Paragraphs)
                    {
                        elements.Add(new Element(
                            paragraph.Block.Key, BookSnapshot.ParagraphText(paragraph.Block.Content), IsHeading: false,
                            exportedDetails.Contains(paragraph)));
                    }

                    break;
            }
        }
    }

    private void FindGlossaryHits(IReadOnlyList<GlossaryEntry> glossary, IEnumerable<Element> texts)
    {
        var pending = glossary
            .Where(entry => entry.Id != Guid.Empty && entry.Term.Length > 0)
            .ToDictionary(entry => entry, entry => new[] { entry.Term }.Concat(entry.Forms).ToList());
        foreach (var element in texts)
        {
            if (pending.Count == 0)
            {
                break;
            }

            var shown = ShownText(element.Shown);
            foreach (var (entry, terms) in pending.ToList())
            {
                if (EarliestHit(shown, terms) is { } hit)
                {
                    _glossaryHits.Add(new GlossaryHit(entry.Id, element.Key, hit.Start, hit.Length));
                    pending.Remove(entry);
                }
            }
        }
    }

    /// <summary>
    /// The text as the editor shows it, one character per unit: a block reference or citation is one unit, and so is
    /// every character of a link; those units are <see cref="Placeholder"/>, which neither matches a term nor counts as part of a word.
    /// </summary>
    private static string ShownText(string editorText)
    {
        var sb = new StringBuilder();
        foreach (var (shown, _, _) in InlineMarkdown.ShownChars(editorText))
        {
            sb.Append(shown.Length > 2 ? Placeholder : shown[0]);
        }

        foreach (Match link in LinkRegex().Matches(sb.ToString()))
        {
            sb.Remove(link.Index, link.Length).Insert(link.Index, new string(Placeholder, link.Length));
        }

        return sb.ToString();
    }

    /// <summary>The earliest whole-word hit of one of the terms; at the same start the longest.</summary>
    private static (int Start, int Length)? EarliestHit(string shown, List<string> terms)
    {
        (int Start, int Length)? best = null;
        foreach (var term in terms)
        {
            if (FirstWholeWord(shown, term.Trim()) is { } hit
                && (best is null || hit.Start < best.Value.Start || hit.Start == best.Value.Start && hit.Length > best.Value.Length))
            {
                best = hit;
            }
        }

        return best;
    }

    private static (int Start, int Length)? FirstWholeWord(string shown, string term)
    {
        for (var from = 0; term.Length > 0 && from < shown.Length;)
        {
            var at = German.IndexOf(shown.AsSpan(from), term, CompareOptions.IgnoreCase, out var length);
            if (at < 0)
            {
                return null;
            }

            var start = from + at;
            var end = start + length;
            if (length > 0
                && (start == 0 || !char.IsLetterOrDigit(shown[start - 1]))
                && (end == shown.Length || !char.IsLetterOrDigit(shown[end])))
            {
                return (start, length);
            }

            from = start + 1;
        }

        return null;
    }
}
