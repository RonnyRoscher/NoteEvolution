using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Text;
using QuestPDF.Infrastructure;

namespace NoteEvolution.Pdf;

internal abstract record PdfElement
{
    /// <summary>The section id of a referenced text block that starts with this element; null for all others.</summary>
    public string? Anchor { get; init; }
}

internal sealed record HeadingElement(int Level, string Title, string SectionId) : PdfElement;

/// <summary>How a run is set: as text, small and raised (a person's abbreviation, a source number), or as a glossary mark.</summary>
internal enum PdfRunKind
{
    Text,
    Superscript,

    /// <summary>The first occurrence of a glossary term: dotted underline.</summary>
    GlossaryMark,
}

/// <summary>
/// A stretch of a paragraph with uniform emphasis: text, or a cross reference to <see cref="RefTarget"/>.
/// <paramref name="LinkTarget"/> is the id of a section of the PDF the run links to (<c>person-{id}</c>,
/// <c>source-{id}</c>, <c>glossary-{key}</c> by the entry's block key); null for a run without a link.
/// </summary>
internal sealed record PdfRun(
    string Text, bool Bold, bool Italic, Guid? RefTarget = null, PdfRunKind Kind = PdfRunKind.Text, string? LinkTarget = null);

internal sealed record ParagraphElement(IReadOnlyList<PdfRun> Runs) : PdfElement;

internal sealed record ImageElement(Image Image) : PdfElement;

/// <summary>
/// The exportable content of a book (or one outline node of it) as a flat list of headings, paragraphs and images,
/// plus the warnings found while collecting it. Relevant and remark blocks, properties and sources never get in.
/// The appendix (glossary, people, sources) follows the text, with the entries the exported text uses.
/// </summary>
internal sealed partial class PdfContent : IDisposable
{
    private const string MissingRef = "→ (Verweis fehlt)";
    private const string Untitled = "(ohne Titel)";
    private const string MissingEntry = "(Verweis fehlt)";
    private const string ThinSpace = " ";

    /// <summary>Marks around a glossary hit in the text before it is cleaned and parsed: the start carries the hit's number.</summary>
    private const char MarkStart = '';
    private const char MarkEnd = '';

    private readonly Book _book;
    private readonly string _bookDirectory;
    private readonly CrossRefIndex _refs;
    private readonly AppendixIndex _appendix;
    private readonly Dictionary<Guid, PersonEntry> _people = [];
    private readonly Dictionary<Guid, SourceEntry> _sources = [];
    private readonly Dictionary<Guid, GlossaryEntry> _glossary = [];
    private readonly Dictionary<Guid, List<GlossaryHit>> _hitsByElement = [];

    /// <summary>The appendix entries the exported text uses: people and sources by id, glossary entries by block key.</summary>
    private readonly HashSet<Guid> _usedPeople = [];
    private readonly HashSet<Guid> _usedSources = [];
    private readonly HashSet<Guid> _usedGlossary = [];

    private readonly List<PdfElement> _elements = [];
    private readonly List<string> _warnings = [];

    /// <summary>The section ids of the exported reference targets by target id.</summary>
    private readonly Dictionary<Guid, string> _anchors = [];

    /// <summary>The target id of the referenced text block whose first element is still to come.</summary>
    private Guid? _pendingAnchor;

    [GeneratedRegex(@"!\[[^\]\n]*\]\((?<path>[^)\n]*)\)(\{[^}\n]*\})?")]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]*://")]
    private static partial Regex RemoteRegex();

    private PdfContent(Book book, OutlineNode scope, Func<Guid, bool>? knownElsewhere)
    {
        _book = book;
        _refs = CrossRefIndex.Of(book, knownElsewhere);
        _appendix = AppendixIndex.Of(book);
        foreach (var person in Appendix.People(book).Where(p => p.Id != Guid.Empty))
        {
            _people.TryAdd(person.Id, person);
        }

        foreach (var source in Appendix.Sources(book).Where(s => s.Id != Guid.Empty))
        {
            _sources.TryAdd(source.Id, source);
        }

        // By block key: an entry written in Logseq without an id:: is marked and listed too.
        foreach (var entry in Appendix.Glossary(book))
        {
            _glossary.TryAdd(entry.Key, entry);
        }

        // Terms are marked at their first occurrence in the exported part; the source numbers stay those of the book.
        foreach (var hit in _appendix.GlossaryHitsIn(scope))
        {
            if (!_hitsByElement.TryGetValue(hit.ParagraphKey, out var hits))
            {
                _hitsByElement[hit.ParagraphKey] = hits = [];
            }

            hits.Add(hit);
        }

        Title = book.Title;
        Dedication = book.Dedication;
        _bookDirectory = Path.GetDirectoryName(Path.GetFullPath(book.Page.FilePath)) ?? "";
    }

    public string Title { get; }

    public string? Dedication { get; }

    public IReadOnlyList<PdfElement> Elements => _elements;

    public IReadOnlyList<string> Warnings => _warnings;

    /// <exception cref="ArgumentException"><paramref name="scopeKey"/> names no outline node of the book.</exception>
    /// <param name="knownElsewhere">Whether an id names a block in the vault; see <see cref="CrossRefIndex.Of"/>.</param>
    public static PdfContent Build(Book book, Guid? scopeKey, Func<Guid, bool>? knownElsewhere = null)
    {
        var scope = book.FindNode(scopeKey ?? Guid.Empty)
            ?? throw new ArgumentException($"Kein Gliederungsknoten mit dem Schlüssel {scopeKey}.", nameof(scopeKey));
        if (scope.AppendixKind != AppendixKind.None)
        {
            throw new ArgumentException($"Der Anhang ist kein Teil des Buchtexts: {scopeKey}.", nameof(scopeKey));
        }

        var content = new PdfContent(book, scope, knownElsewhere);
        try
        {
            if (scope.Level > 0)
            {
                content.AddHeading(scope);
            }

            content.AddItems(scope);
            content.AddAppendix();
            return content;
        }
        catch
        {
            content.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Spec 2.5: what a reference to <paramref name="targetId"/> shows, "→ Titel" (a text block shows the title of its
    /// section), and the section id to link to; null if the target is not in this export or missing.
    /// </summary>
    public (string Label, string? SectionId) Ref(Guid targetId) =>
        _refs.Target(targetId) switch
        {
            { Heading: { } heading } => ("→ " + TitleOf(heading), _anchors.GetValueOrDefault(targetId)),
            { TextBlock: { } textBlock } => (
                "→ " + (textBlock.Section.Block is null ? _book.Title : TitleOf(textBlock.Section)),
                _anchors.GetValueOrDefault(targetId)),
            _ => (MissingRef, null),
        };

    private static string TitleOf(OutlineNode heading) =>
        CleanTitle(heading.Title) is { Length: > 0 } title ? title : Untitled;

    /// <summary>A heading's title as reading text; a source citation has no place in it (the source is still listed).</summary>
    private static string CleanTitle(string title) =>
        ExportText.Clean(SourceCitations.Remove(title, SourceCitations.Find(title).Select(c => c.Id).ToHashSet()));

    public void Dispose()
    {
        foreach (var image in _elements.OfType<ImageElement>())
        {
            image.Image.Dispose();
        }
    }

    private void AddItems(OutlineNode node)
    {
        foreach (var item in node.Items)
        {
            switch (item)
            {
                case OutlineNode { AppendixKind: not AppendixKind.None }:
                    // The appendix is no part of the book's text; its used entries come at the end (AddAppendix).
                    break;
                case OutlineNode child:
                    AddHeading(child);
                    AddItems(child);
                    break;
                case TextBlock { Status: var status } text when !BlockStatuses.IsExcluded(status):
                    // A relevant or remark block stays out with all its details, whatever their status;
                    // inside an exported block, an excluded detail takes its whole subtree with it.
                    _pendingAnchor = IsReferencedTarget(text) ? text.Block.Id : null;
                    AddBlockText(text.Text, text.Block);
                    foreach (var paragraph in text.ExportedParagraphs)
                    {
                        AddBlockText(paragraph.Text, paragraph.Block);
                    }

                    _pendingAnchor = null;
                    break;
            }
        }
    }

    private void AddHeading(OutlineNode node)
    {
        var sectionId = $"h{_elements.Count}";
        if (node.Block?.Id is { } id && _refs.Target(id)?.Heading == node)
        {
            _anchors[id] = sectionId;
        }

        foreach (var citation in SourceCitations.Find(node.Title).Where(c => _sources.ContainsKey(c.Id)))
        {
            _usedSources.Add(citation.Id);
        }

        _elements.Add(new HeadingElement(node.Level, CleanTitle(node.Title), sectionId));
    }

    /// <summary>Only referenced text blocks get a section of their own; the first block with an id is its target.</summary>
    private bool IsReferencedTarget(TextBlock text) =>
        text.Block.Id is { } id && _refs.Target(id)?.TextBlock == text && _refs.To(id).Count > 0;

    /// <summary>Adds a body element; the first one of a referenced text block carries the block's section.</summary>
    private void Add(PdfElement element)
    {
        if (_pendingAnchor is { } id)
        {
            var sectionId = $"t{_elements.Count}";
            _anchors[id] = sectionId;
            element = element with { Anchor = sectionId };
            _pendingAnchor = null;
        }

        _elements.Add(element);
    }

    /// <summary>
    /// Text between images becomes paragraphs; each image becomes an image (or a placeholder). The first occurrence of
    /// a glossary term in the block (<paramref name="block"/> is where its hit is) is marked in the text first, so it
    /// survives the cleaning and the parsing into runs.
    /// </summary>
    private void AddBlockText(string blockText, Block block)
    {
        var hits = UsedHits(block.Key);
        var marked = hits.Count > 0 && blockText == block.Content ? WithMarks(blockText, block.Key, hits) : blockText;
        var text = BlockTextEscape.Unescape(marked);
        var position = 0;
        foreach (var match in ImageRegex().Matches(text).Cast<Match>())
        {
            AddParagraph(text[position..match.Index], hits);
            AddImage(match.Groups["path"].Value.Trim());
            position = match.Index + match.Length;
        }

        AddParagraph(text[position..], hits);
    }

    /// <summary>The glossary hits of an exported element in text order.</summary>
    private List<GlossaryHit> UsedHits(Guid elementKey)
    {
        return _hitsByElement.GetValueOrDefault(elementKey)?.OrderBy(hit => hit.Start).ToList() ?? [];
    }

    /// <summary>
    /// <paramref name="content"/> with a start mark (carrying the number of the hit in <paramref name="hits"/>) and an
    /// end mark around each hit. The hits are offsets in the text as the editor shows it, mapped to the content's
    /// Markdown. A hit that overlaps the one before, or has no place in the Markdown, stays unmarked and its entry unlisted;
    /// a marked hit makes its entry used.
    /// </summary>
    private string WithMarks(string content, Guid elementKey, List<GlossaryHit> hits)
    {
        var places = new List<(int Start, int End, int Number)>();
        for (var number = 0; number < hits.Count; number++)
        {
            var hit = hits[number];
            if (ManuscriptEditor.ContentOffset(_book, elementKey, hit.Start) is { } start
                && ManuscriptEditor.ContentOffset(_book, elementKey, hit.Start + hit.Length) is { } end
                && end > start && (places.Count == 0 || start >= places[^1].End))
            {
                places.Add((start, end, number));
                _usedGlossary.Add(hit.EntryKey);
            }
        }

        var sb = new StringBuilder(content);
        foreach (var (start, end, number) in Enumerable.Reverse(places))
        {
            sb.Insert(end, MarkEnd).Insert(start, (char)(MarkStart + number));
        }

        return sb.ToString();
    }

    private void AddParagraph(string text, List<GlossaryHit> hits)
    {
        // A reference to a block outside the cross references (a note) goes, as before them (spec 2.1); a person is
        // no cross reference and stays.
        var clean = ExportText.Clean(text, id => _people.ContainsKey(id) || !_refs.IsForeign(id));
        if (clean.Length > 0)
        {
            var runs = InlineMarkdown.Parse(clean).SelectMany(SplitRefs);
            Add(new ParagraphElement([.. hits.Count > 0 ? ApplyMarks(runs, hits) : runs]));
        }
    }

    /// <summary>
    /// The run as text runs, one run per block reference (a person's abbreviation or a cross reference) and per source
    /// citation in it (the number, the locator behind it), all with the run's emphasis.
    /// </summary>
    private IEnumerable<PdfRun> SplitRefs(InlineRun run)
    {
        var found = BlockRefs.Find(run.Text).Select(r => (r.Start, r.Length, r.Id, Citation: (SourceCitation?)null))
            .Concat(SourceCitations.Find(run.Text).Select(c => (c.Start, c.Length, c.Id, Citation: (SourceCitation?)c)))
            .OrderBy(item => item.Start);
        var position = 0;
        foreach (var (start, length, id, citation) in found)
        {
            if (start > position)
            {
                yield return new PdfRun(run.Text[position..start], run.Bold, run.Italic);
            }

            foreach (var part in citation is { } c ? CitationRuns(run, c) : ReferenceRuns(run, id))
            {
                yield return part;
            }

            position = start + length;
        }

        if (position < run.Text.Length)
        {
            yield return new PdfRun(run.Text[position..], run.Bold, run.Italic);
        }
    }

    private IEnumerable<PdfRun> ReferenceRuns(InlineRun run, Guid id)
    {
        if (!_people.TryGetValue(id, out var person))
        {
            yield return new PdfRun("", run.Bold, run.Italic, id);
            yield break;
        }

        _usedPeople.Add(id);
        yield return new PdfRun(MarkOf(person), run.Bold, run.Italic, Kind: PdfRunKind.Superscript, LinkTarget: Anchor("person", id));
    }

    /// <summary>What the text shows of a person: the abbreviation, the name if it has none (as the editor does).</summary>
    private static string MarkOf(PersonEntry person) =>
        person.Abbreviation.Length > 0 ? person.Abbreviation : ExportText.Clean(person.Name);

    private IEnumerable<PdfRun> CitationRuns(InlineRun run, SourceCitation citation)
    {
        if (!_sources.ContainsKey(citation.Id))
        {
            yield return new PdfRun(MissingEntry, run.Bold, run.Italic);
            yield break;
        }

        _usedSources.Add(citation.Id);
        var number = _appendix.SourceNumber(citation.Id)?.ToString(CultureInfo.InvariantCulture) ?? "–";
        yield return new PdfRun(number, run.Bold, run.Italic, Kind: PdfRunKind.Superscript, LinkTarget: Anchor("source", citation.Id));
        if (citation.Locator is { } locator)
        {
            yield return new PdfRun(ThinSpace + locator, run.Bold, run.Italic);
        }
    }

    /// <summary>The marks in the text runs become glossary marks, a mark running over run boundaries.</summary>
    private static IEnumerable<PdfRun> ApplyMarks(IEnumerable<PdfRun> runs, List<GlossaryHit> hits)
    {
        string? link = null;
        foreach (var run in runs)
        {
            if (run.RefTarget is not null || run.Kind != PdfRunKind.Text)
            {
                yield return run;
                continue;
            }

            var from = 0;
            for (var i = 0; i <= run.Text.Length; i++)
            {
                var isMark = i < run.Text.Length
                    && (run.Text[i] == MarkEnd || run.Text[i] >= MarkStart && run.Text[i] < MarkStart + hits.Count);
                if (i < run.Text.Length && !isMark)
                {
                    continue;
                }

                if (i > from)
                {
                    yield return run with
                    {
                        Text = run.Text[from..i],
                        Kind = link is null ? PdfRunKind.Text : PdfRunKind.GlossaryMark,
                        LinkTarget = link,
                    };
                }

                if (isMark)
                {
                    link = run.Text[i] == MarkEnd ? null : Anchor("glossary", hits[run.Text[i] - MarkStart].EntryKey);
                }

                from = i + 1;
            }
        }
    }

    private static string Anchor(string kind, Guid id) => $"{kind}-{id:D}";

    /// <summary>
    /// The appendix after the book text: glossary, people, sources, each with a heading of level 1 (so on a page of its
    /// own and in the table of contents), only with the entries the exported text uses. The authors of a used source
    /// count as used.
    /// </summary>
    private void AddAppendix()
    {
        foreach (var id in _usedSources.SelectMany(id => _sources[id].AuthorIds).Where(_people.ContainsKey))
        {
            _usedPeople.Add(id);
        }

        var german = StringComparer.Create(new CultureInfo("de-DE"), ignoreCase: true);

        var glossary = _glossary.Values.Where(entry => _usedGlossary.Contains(entry.Key)).OrderBy(entry => entry.Term, german);
        AddAppendixPart(AppendixKind.Glossary, "Glossar", glossary.Select(entry => (Anchor("glossary", entry.Key), GlossaryRuns(entry))));

        var people = _people.Values.Where(person => _usedPeople.Contains(person.Id)).OrderBy(person => person.Name, german);
        AddAppendixPart(
            AppendixKind.People, "Menschen, die mich inspiriert haben", people.Select(person => (Anchor("person", person.Id), PersonRuns(person))));

        var sources = _sources.Values.Where(source => _usedSources.Contains(source.Id))
            .OrderBy(source => _appendix.SourceNumber(source.Id) ?? int.MaxValue);
        AddAppendixPart(AppendixKind.Sources, "Quellen", sources.Select(source => (Anchor("source", source.Id), SourceRuns(source))));
    }

    private void AddAppendixPart(AppendixKind kind, string defaultTitle, IEnumerable<(string Anchor, IEnumerable<PdfRun> Runs)> entries)
    {
        var list = entries.ToList();
        if (list.Count == 0)
        {
            return;
        }

        var title = _book.AppendixSection(kind) is { } section && CleanTitle(section.Title) is { Length: > 0 } own ? own : defaultTitle;
        _elements.Add(new HeadingElement(1, title, $"appendix-{kind.ToString().ToLowerInvariant()}"));
        foreach (var (anchor, runs) in list)
        {
            _elements.Add(new ParagraphElement([.. runs]) { Anchor = anchor });
        }
    }

    /// <summary>The term in bold, the explanation behind it.</summary>
    private static IEnumerable<PdfRun> GlossaryRuns(GlossaryEntry entry)
    {
        yield return new PdfRun(ExportText.Clean(entry.Term), true, false);
        foreach (var run in Lines(entry.Explanation))
        {
            yield return run;
        }
    }

    /// <summary>Free text of an entry (explanation, description) as runs behind a line break; none for an empty text.</summary>
    private static IEnumerable<PdfRun> Lines(string text)
    {
        if (ExportText.Clean(text) is not { Length: > 0 } clean)
        {
            yield break;
        }

        yield return new PdfRun("\n", false, false);
        foreach (var run in InlineMarkdown.Parse(clean))
        {
            yield return new PdfRun(run.Text, run.Bold, run.Italic);
        }
    }

    /// <summary>"Name (Kürzel)" ("Name" without an abbreviation), the topics in italics, the description.</summary>
    private static IEnumerable<PdfRun> PersonRuns(PersonEntry person)
    {
        yield return new PdfRun(ExportText.Clean(person.Name), true, false);
        if (person.Abbreviation.Length > 0)
        {
            yield return new PdfRun($" ({person.Abbreviation})", false, false);
        }

        if (person.Topics.Count > 0)
        {
            yield return new PdfRun("\n" + string.Join(", ", person.Topics), false, true);
        }

        foreach (var run in Lines(person.Description))
        {
            yield return run;
        }
    }

    /// <summary>"[n] Autor; Autor: Titel (italic). Herausgeber, Jahr. URL"; a missing part goes with its separator.</summary>
    private IEnumerable<PdfRun> SourceRuns(SourceEntry source)
    {
        yield return new PdfRun($"[{_appendix.SourceNumber(source.Id)}] ", false, false);
        var authors = source.AuthorIds.Where(_people.ContainsKey).ToList();
        for (var i = 0; i < authors.Count; i++)
        {
            if (i > 0)
            {
                yield return new PdfRun("; ", false, false);
            }

            yield return new PdfRun(ExportText.Clean(_people[authors[i]].Name), false, false, LinkTarget: Anchor("person", authors[i]));
        }

        if (authors.Count > 0)
        {
            yield return new PdfRun(": ", false, false);
        }

        yield return new PdfRun(ExportText.Clean(source.Title), false, true);
        var publisherAndYear = string.Join(
            ", ", new[] { source.Publisher, source.Year }.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim()));
        foreach (var part in new[] { publisherAndYear, source.Url?.Trim() ?? "" }.Where(part => part.Length > 0))
        {
            yield return new PdfRun(". " + part, false, false);
        }
    }

    private void AddImage(string path)
    {
        if (RemoteRegex().IsMatch(path))
        {
            Missing(path, $"Bild aus dem Netz wird nicht geladen: {path}");
            return;
        }

        var file = Resolve(path);
        if (file is null)
        {
            Missing(path, $"Bild nicht gefunden: {path}");
            return;
        }

        try
        {
            Add(new ImageElement(Image.FromBinaryData(File.ReadAllBytes(file))));
        }
        catch (Exception)
        {
            // Unreadable file or a format QuestPDF cannot decode: the export goes on with a placeholder.
            Missing(path, $"Bild nicht lesbar: {path}");
        }
    }

    private string? Resolve(string path)
    {
        // Logseq writes spaces and the like percent-encoded.
        foreach (var candidate in new[] { path, SafeUnescape(path) }.Distinct())
        {
            var file = Path.GetFullPath(Path.Combine(_bookDirectory, candidate));
            if (File.Exists(file))
            {
                return file;
            }
        }

        return null;
    }

    private static string SafeUnescape(string path)
    {
        try
        {
            return Uri.UnescapeDataString(path);
        }
        catch (UriFormatException)
        {
            return path;
        }
    }

    private void Missing(string path, string warning)
    {
        _warnings.Add(warning);
        Add(new ParagraphElement([new PdfRun($"[Bild fehlt: {path}]", false, false)]));
    }
}
