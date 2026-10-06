using System.Text.RegularExpressions;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Text;
using QuestPDF.Infrastructure;

namespace NoteEvolution.Pdf;

internal abstract record PdfElement;

internal sealed record HeadingElement(int Level, string Title, string SectionId) : PdfElement;

internal sealed record ParagraphElement(IReadOnlyList<InlineRun> Runs) : PdfElement;

internal sealed record ImageElement(Image Image) : PdfElement;

/// <summary>
/// The exportable content of a book (or one outline node of it) as a flat list of headings, paragraphs and images,
/// plus the warnings found while collecting it. Notes, properties and sources never get in.
/// </summary>
internal sealed partial class PdfContent : IDisposable
{
    private readonly string _bookDirectory;
    private readonly List<PdfElement> _elements = [];
    private readonly List<string> _warnings = [];

    [GeneratedRegex(@"!\[[^\]\n]*\]\((?<path>[^)\n]*)\)(\{[^}\n]*\})?")]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]*://")]
    private static partial Regex RemoteRegex();

    private PdfContent(Book book)
    {
        Title = book.Title;
        Dedication = book.Dedication;
        _bookDirectory = Path.GetDirectoryName(Path.GetFullPath(book.Page.FilePath)) ?? "";
    }

    public string Title { get; }

    public string? Dedication { get; }

    public IReadOnlyList<PdfElement> Elements => _elements;

    public IReadOnlyList<string> Warnings => _warnings;

    /// <exception cref="ArgumentException"><paramref name="scopeKey"/> names no outline node of the book.</exception>
    public static PdfContent Build(Book book, Guid? scopeKey)
    {
        var scope = book.FindNode(scopeKey ?? Guid.Empty)
            ?? throw new ArgumentException($"Kein Gliederungsknoten mit dem Schlüssel {scopeKey}.", nameof(scopeKey));
        var content = new PdfContent(book);
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
                case TextBlock text:
                    AddBlockText(text.Text);
                    foreach (var paragraph in text.Paragraphs.Where(p => !p.IsNote))
                    {
                        AddBlockText(paragraph.Text);
                    }

                    break;
            }
        }
    }

    private void AddHeading(OutlineNode node) =>
        _elements.Add(new HeadingElement(node.Level, ExportText.Clean(node.Title), $"h{_elements.Count}"));

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
        var clean = ExportText.Clean(text);
        if (clean.Length > 0)
        {
            _elements.Add(new ParagraphElement(InlineMarkdown.Parse(clean)));
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
            _elements.Add(new ImageElement(Image.FromBinaryData(File.ReadAllBytes(file))));
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
        _elements.Add(new ParagraphElement([new InlineRun($"[Bild fehlt: {path}]", false, false)]));
    }
}
