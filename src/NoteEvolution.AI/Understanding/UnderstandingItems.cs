using System.Security.Cryptography;
using System.Text;
using NoteEvolution.AI.Embeddings;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Text;
using NoteEvolution.Core.Vaults;

namespace NoteEvolution.AI.Understanding;

/// <summary>
/// One unit of work for the language model: a heading (summary of its own text and its sub-headings' summaries) or a note
/// (core statement). <paramref name="Fingerprint"/> covers everything the answer depends on, <paramref name="Identity"/>
/// names the thing across edits, <paramref name="Key"/> is the session key (<c>OutlineNode.Key</c> / <c>NoteBlock.Key</c>).
/// </summary>
public sealed record UnderstandingItem(UnderstandingKind Kind, string Fingerprint, string Identity, string Title,
    string OwnText, IReadOnlyList<string> ChildFingerprints, Guid Key);

/// <summary>Turns a book snapshot and notes into <see cref="UnderstandingItem"/>s. Pure; reads the model it is given.</summary>
public static class UnderstandingItems
{
    public const int HeadingInputLimit = 6000;
    public const int NoteInputLimit = 4000;
    public const int NoteMinLength = 120;
    public const int PromptVersion = 1;

    private const string FieldSeparator = "\u001f";
    private const int NoteIdentityLineLimit = 80;

    /// <summary>All headings of the book, children before their parents (the model needs the children's summaries first); the root is not included.</summary>
    public static IReadOnlyList<UnderstandingItem> ForBook(Book book, string chatModel)
    {
        var items = new List<UnderstandingItem>();
        AddChildren(book, book.Root, [], chatModel, items);
        return items;
    }

    /// <summary>The note's item, or <c>null</c> when its text is shorter than <see cref="NoteMinLength"/>.</summary>
    public static UnderstandingItem? ForNote(NoteBlock note, string chatModel) =>
        ForNote(note.Key, NoteText(note), note.Block.Content, note.Page.FilePath, chatModel);

    /// <summary>
    /// The same from a snapshot taken on the UI thread (the note's key, its <see cref="NoteText"/>, its block's content and
    /// its page's file path), so it can be computed on any thread.
    /// </summary>
    public static UnderstandingItem? ForNote(Guid key, string noteText, string content, string filePath, string chatModel)
    {
        var text = LimitNote(noteText);
        if (text.Length < NoteMinLength)
        {
            return null;
        }

        var firstLine = content.Split('\n', 2)[0].Trim();
        if (firstLine.Length > NoteIdentityLineLimit)
        {
            firstLine = firstLine[..NoteIdentityLineLimit];
        }

        return new UnderstandingItem(UnderstandingKind.Note, NoteFingerprint(text, chatModel),
            "note:" + RelativePagePath(filePath) + "#" + firstLine, firstLine, text, [], key);
    }

    /// <summary>
    /// The text the model sees for a note before it is limited: journal date, parent line, the note's own text and its
    /// sub-points (as <see cref="EmbeddingText.ForNote"/> builds it, without the role marker). Call it on the UI thread.
    /// </summary>
    public static string NoteText(NoteBlock note) => EmbeddingText.ForNote(note)[EmbeddingText.PassagePrefix.Length..];

    /// <summary>Fingerprint of a note from its <see cref="NoteText"/> (limited to <see cref="NoteInputLimit"/> after trimming); equals the one of <see cref="ForNote"/>.</summary>
    public static string NoteFingerprint(string noteText, string chatModel) =>
        Hash([PromptVersion.ToString(), chatModel, LimitNote(noteText)]);

    /// <summary>The note text as the model sees it: trimmed and cut to <see cref="NoteInputLimit"/> characters.</summary>
    internal static string LimitNote(string noteText)
    {
        var text = noteText.Trim();
        return text.Length > NoteInputLimit ? text[..NoteInputLimit].TrimEnd() : text;
    }

    private static void AddChildren(Book book, OutlineNode parent, List<string> path, string chatModel,
        List<UnderstandingItem> items)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var child in parent.Children)
        {
            var index = seen.GetValueOrDefault(child.Title);
            seen[child.Title] = index + 1;
            path.Add(child.Title);
            items.Add(BuildHeading(book, child, path, index, chatModel, items));
            path.RemoveAt(path.Count - 1);
        }
    }

    /// <summary>Adds the heading's descendants to <paramref name="items"/> first, then returns the heading's own item.</summary>
    private static UnderstandingItem BuildHeading(Book book, OutlineNode node, List<string> path, int index,
        string chatModel, List<UnderstandingItem> items)
    {
        var before = items.Count;
        AddChildren(book, node, path, chatModel, items);
        var childFingerprints = new List<string>();
        foreach (var child in node.Children)
        {
            // Children were added just before; look them up by key.
            var item = items.Skip(before).First(i => i.Key == child.Key);
            childFingerprints.Add(item.Fingerprint);
        }

        var ownText = OwnText(node);
        var identity = "book:" + book.LinkName + "/" + string.Join(" › ", path) + "#" + index;
        var fingerprint = Hash([PromptVersion.ToString(), chatModel, node.Title, ownText, .. childFingerprints]);
        return new UnderstandingItem(UnderstandingKind.Heading, fingerprint, identity, node.Title, ownText,
            childFingerprints, node.Key);
    }

    /// <summary>
    /// Direct text blocks that are not remarks (relevant ones count), each with its non-remark paragraphs. Over the limit
    /// every block is shortened to the same length, keeping its start.
    /// </summary>
    private static string OwnText(OutlineNode node)
    {
        const char Newline = (char)10;
        var blocks = node.TextBlocks.Where(tb => tb.Status != BlockStatus.Remark)
            .Select(tb => string.Join(Newline, tb.Paragraphs.Where(p => p.Status != BlockStatus.Remark).Select(p => p.Text)
                .Prepend(tb.Text).Select(BlockTextEscape.Unescape).Where(line => !string.IsNullOrWhiteSpace(line))))
            .Where(text => text.Length > 0)
            .ToList();
        var joined = string.Join(Newline, blocks);
        if (joined.Length <= HeadingInputLimit)
        {
            return joined.Trim();
        }

        var budget = (HeadingInputLimit - (blocks.Count - 1)) / blocks.Count;
        return string.Join(Newline, blocks.Select(b => b.Length > budget ? b[..budget] : b)).Trim();
    }

    private static string RelativePagePath(string filePath)
    {
        // The vault root is not known here; the folder (pages / journals) and the file name identify the page.
        var segments = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Length >= 2 ? segments[^2] + "/" + segments[^1] : segments[^1];
    }

    private static string Hash(IEnumerable<string> fields) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(FieldSeparator, fields))));
}
