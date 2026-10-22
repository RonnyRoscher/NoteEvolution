using System.Text.RegularExpressions;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Text;
using QuestPDF.Infrastructure;

namespace NoteEvolution.Pdf;

internal abstract record PdfElement
{
    /// <summary>The section id of a referenced text block that starts with this element; null for all others.</summary>
    public string? Anchor { get; init; }
}

internal sealed record HeadingElement(int Level, string Title, string SectionId) : PdfElement;

/// <summary>A stretch of a paragraph with uniform emphasis: text, or a cross reference to <see cref="RefTarget"/>.</summary>
internal sealed record PdfRun(string Text, bool Bold, bool Italic, Guid? RefTarget = null);

internal sealed record ParagraphElement(IReadOnlyList<PdfRun> Runs) : PdfElement;

internal sealed record ImageElement(Image Image) : PdfElement;

/// <summary>
/// The exportable content of a book (or one outline node of it) as a flat list of headings, paragraphs and images,
/// plus the warnings found while collecting it. Relevant and remark blocks, properties and sources never get in.
/// </summary>
internal sealed partial class PdfContent : IDisposable
{
    private const string MissingRef = "→ (Verweis fehlt)";
    private const string Untitled = "(ohne Titel)";

    private readonly Book _book;
    private readonly string _bookDirectory;
    private readonly CrossRefIndex _refs;
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

    private PdfContent(Book book, Func<Guid, bool>? knownElsewhere)
    {
        _book = book;
        _refs = CrossRefIndex.Of(book, knownElsewhere);
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
        var content = new PdfContent(book, knownElsewhere);
        try
        {
            if (scope.Level > 0)
            {
                content.AddHeading(scope);
            }

            content.AddItems(scope);
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
        ExportText.Clean(heading.Title) is { Length: > 0 } title ? title : Untitled;

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
                case OutlineNode child:
                    AddHeading(child);
                    AddItems(child);
                    break;
                case TextBlock { Status: var status } text when !BlockStatuses.IsExcluded(status):
                    // A relevant or remark block stays out with all its details, whatever their status;
                    // inside an exported block, an excluded detail takes its whole subtree with it.
                    _pendingAnchor = IsReferencedTarget(text) ? text.Block.Id : null;
                    AddBlockText(text.Text);
                    foreach (var paragraph in text.ExportedParagraphs)
                    {
                        AddBlockText(paragraph.Text);
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

        _elements.Add(new HeadingElement(node.Level, ExportText.Clean(node.Title), sectionId));
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

    /// <summary>Text between images becomes paragraphs; each image becomes an image (or a placeholder).</summary>
    private void AddBlockText(string blockText)
    {
        var text = BlockTextEscape.Unescape(blockText);
        var position = 0;
        foreach (var match in ImageRegex().Matches(text).Cast<Match>())
        {
            AddParagraph(text[position..match.Index]);
            AddImage(match.Groups["path"].Value.Trim());
            position = match.Index + match.Length;
        }

        AddParagraph(text[position..]);
    }

    private void AddParagraph(string text)
    {
        // A reference to a block outside the cross references (a note) goes, as before them (spec 2.1).
        var clean = ExportText.Clean(text, id => !_refs.IsForeign(id));
        if (clean.Length > 0)
        {
            Add(new ParagraphElement([.. InlineMarkdown.Parse(clean).SelectMany(SplitRefs)]));
        }
    }

    /// <summary>The run as text runs and one run per block reference in it, all with the run's emphasis.</summary>
    private static IEnumerable<PdfRun> SplitRefs(InlineRun run)
    {
        var position = 0;
        foreach (var reference in BlockRefs.Find(run.Text))
        {
            if (reference.Start > position)
            {
                yield return new PdfRun(run.Text[position..reference.Start], run.Bold, run.Italic);
            }

            yield return new PdfRun("", run.Bold, run.Italic, reference.Id);
            position = reference.Start + reference.Length;
        }

        if (position < run.Text.Length)
        {
            yield return new PdfRun(run.Text[position..], run.Bold, run.Italic);
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
