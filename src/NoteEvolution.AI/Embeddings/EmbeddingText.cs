using System.Security.Cryptography;
using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Text;
using NoteEvolution.Core.Vaults;

namespace NoteEvolution.AI.Embeddings;

/// <summary>
/// Decides exactly which text each embedding sees. e5 models expect a prefix: <c>passage: </c> for the texts
/// that are searched (notes), <c>query: </c> for the texts that search (book sections, cursor, search box).
/// </summary>
public static class EmbeddingText
{
    public const string PassagePrefix = "passage: ";
    public const string QueryPrefix = "query: ";

    /// <summary>Journal date, first line of the parent block, own text and the text of all descendants (depth first).</summary>
    public static string ForNote(NoteBlock note)
    {
        var lines = new List<string>();
        if (note.Date is { } date)
        {
            lines.Add(date.ToString("yyyy-MM-dd"));
        }

        if (note.Block.Parent is { } parent)
        {
            lines.Add(parent.Content.Split('\n', 2)[0].Trim());
        }

        AddContent(lines, note.Block);
        return PassagePrefix + Join(lines);
    }

    /// <summary>Titles from the book's top level down to <paramref name="node"/>; for the root the book title.</summary>
    public static string ForHeadingPath(Book book, OutlineNode node) => QueryPrefix + HeadingPath(book, node);

    /// <summary>The block's text and its paragraphs that are not notes, as the user wrote it.</summary>
    public static string ForTextBlock(TextBlock tb) => QueryPrefix + TextOf(tb);

    /// <summary>Heading path of the cursor's section plus the previous, the cursor's and the next text block of that section.</summary>
    public static string ForCursor(Book book, TextBlock cursor)
    {
        var blocks = cursor.Section.TextBlocks.ToList();
        var index = blocks.FindIndex(b => b.Key == cursor.Key);
        var lines = new List<string> { HeadingPath(book, cursor.Section) };
        if (index > 0)
        {
            lines.Add(TextOf(blocks[index - 1]));
        }

        lines.Add(TextOf(cursor));
        if (index >= 0 && index < blocks.Count - 1)
        {
            lines.Add(TextOf(blocks[index + 1]));
        }

        return QueryPrefix + Join(lines);
    }

    public static string ForSearch(string userText) => QueryPrefix + userText.Trim();

    /// <summary>Lowercase hex SHA-256 over <c>modelId + "\n" + text</c>; changes when the model or the text changes.</summary>
    public static string Hash(string modelId, string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(modelId + "\n" + text)));

    private static void AddContent(List<string> lines, Block block)
    {
        lines.Add(block.Content);
        foreach (var child in block.Children)
        {
            AddContent(lines, child);
        }
    }

    private static string HeadingPath(Book book, OutlineNode node)
    {
        var titles = new List<string>();
        for (var current = node; current is { Level: > 0 }; current = current.Parent)
        {
            titles.Add(current.Title);
        }

        if (titles.Count == 0)
        {
            return book.Title;
        }

        titles.Reverse();
        return string.Join(" / ", titles);
    }

    private static string TextOf(TextBlock tb) =>
        Join(tb.Paragraphs.Where(p => !p.IsNote).Select(p => p.Text).Prepend(tb.Text)
            .Select(BlockTextEscape.Unescape));

    private static string Join(IEnumerable<string> lines) =>
        string.Join("\n", lines.Where(line => !string.IsNullOrWhiteSpace(line)));
}
