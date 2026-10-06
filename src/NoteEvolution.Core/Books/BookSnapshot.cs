using System.Globalization;
using NoteEvolution.Core.Text;
using NoteEvolution.Core.Vaults;

namespace NoteEvolution.Core.Books;

/// <summary>A source chip of a text block.</summary>
/// <param name="NoteId">The <c>id::</c> of the note block.</param>
/// <param name="Label">
/// The note's journal date (<c>yyyy-MM-dd</c>) or page name, <c>" – "</c> and the first 40 characters of its first
/// content line; the id itself if the note is not found.
/// </param>
/// <param name="IsBroken">No block in the vault has this id.</param>
public sealed record SourceInfo(Guid NoteId, string Label, bool IsBroken);

/// <summary>What the editor shows of a book: one section, or a whole subtree (manuscript view).</summary>
/// <param name="ScopeKey">The <see cref="OutlineNode.Key"/> of the shown section (<see cref="Guid.Empty"/> = root).</param>
/// <param name="IncludeSubsections"><c>false</c>: section view; <c>true</c>: manuscript view.</param>
/// <param name="Nodes">Headings and text blocks in the editor's order.</param>
public sealed record SectionSnapshot(Guid ScopeKey, bool IncludeSubsections, IReadOnlyList<SnapshotNode> Nodes);

/// <summary>An editor node; <paramref name="Key"/> is the <see cref="Model.Block.Key"/> of its block.</summary>
public abstract record SnapshotNode(Guid Key);

/// <summary>A heading (manuscript view only); <paramref name="Text"/> is the title without <c>#</c>, spaces kept.</summary>
public sealed record SnapshotHeading(Guid Key, int Level, string Text) : SnapshotNode(Key);

/// <summary>A text block. Texts are inline Markdown, line breaks within a block are <c>"\n"</c>.</summary>
/// <param name="Key">The block's key; a key the book does not know yet means a new block.</param>
/// <param name="SplitFrom">For a new block split off in the editor: the key of the block it was split from.</param>
/// <param name="Text">The block's own text.</param>
/// <param name="Paragraphs">The descendant blocks in file order.</param>
/// <param name="Sources">
/// The source chips. The sync never changes the sources of a known block; a new block without
/// <paramref name="SplitFrom"/> is linked to those it finds in the vault (see <see cref="BookSync.Apply"/>).
/// </param>
public sealed record SnapshotTextBlock(
    Guid Key, Guid? SplitFrom, string Text, IReadOnlyList<SnapshotParagraph> Paragraphs, IReadOnlyList<SourceInfo> Sources)
    : SnapshotNode(Key);

/// <summary>A descendant block of a text block; <paramref name="Depth"/> 1 is a direct child.</summary>
/// <param name="Key">The block's key.</param>
/// <param name="Text">The text without the <c>#notiz</c> tag.</param>
/// <param name="Depth">Nesting below the text block.</param>
/// <param name="IsNote">The block carries the <c>#notiz</c> tag.</param>
public sealed record SnapshotParagraph(Guid Key, string Text, int Depth, bool IsNote);

/// <summary>Builds the editor's view of a book; <see cref="BookSync.Apply"/> writes an edited one back.</summary>
public static class BookSnapshot
{
    private const int LabelTextLength = 40;

    /// <summary>
    /// Section view (<paramref name="includeSubsections"/> <c>false</c>): only the text blocks of the node.
    /// Manuscript view: the node's subtree in file order with headings; the node's own heading comes first
    /// (the root has none). Texts are unescaped (<see cref="BlockTextEscape"/>).
    /// </summary>
    /// <exception cref="ArgumentException">No node has <paramref name="scopeKey"/>.</exception>
    public static SectionSnapshot Create(Book book, IVault vault, Guid scopeKey, bool includeSubsections)
    {
        var scope = book.FindNode(scopeKey) ?? throw new ArgumentException("No section has this key.", nameof(scopeKey));
        var nodes = new List<SnapshotNode>();
        if (includeSubsections)
        {
            AddSubtree(nodes, scope, vault);
        }
        else
        {
            nodes.AddRange(scope.TextBlocks.Select(t => ToSnapshot(t, vault)));
        }

        return new SectionSnapshot(scopeKey, includeSubsections, nodes);
    }

    /// <summary>
    /// The text a paragraph block shows in the editor: without <c>#notiz</c> and the one space that
    /// <see cref="NoteTag.Add"/> puts before it (other spaces are kept, so the text round-trips), unescaped.
    /// </summary>
    internal static string ParagraphText(string content) => BlockTextEscape.Unescape(NoteTag.Remove(content));

    private static void AddSubtree(List<SnapshotNode> nodes, OutlineNode node, IVault vault)
    {
        if (node.Block is not null)
        {
            nodes.Add(new SnapshotHeading(node.Key, node.Level, HeadingText.TitleOf(node.Block.Content, node.Level)));
        }

        foreach (var item in node.Items)
        {
            switch (item)
            {
                case OutlineNode child:
                    AddSubtree(nodes, child, vault);
                    break;
                case TextBlock text:
                    nodes.Add(ToSnapshot(text, vault));
                    break;
            }
        }
    }

    private static SnapshotTextBlock ToSnapshot(TextBlock text, IVault vault) =>
        new(
            text.Key,
            null,
            BlockTextEscape.Unescape(text.Text),
            [.. text.Paragraphs.Select(p => new SnapshotParagraph(p.Block.Key, ParagraphText(p.Block.Content), p.Depth, p.IsNote))],
            [.. text.Sources.Distinct().Select(id => Source(id, vault))]);

    private static SourceInfo Source(Guid noteId, IVault vault)
    {
        if (vault.FindBlockById(noteId) is not { } found)
        {
            return new SourceInfo(noteId, noteId.ToString("D"), true);
        }

        var (page, block) = found;
        var origin = NoteRepository.DateOf(page)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? page.Name;
        var firstLine = block.Content.Split('\n', 2)[0].Trim();
        if (firstLine.Length > LabelTextLength)
        {
            // Do not cut a surrogate pair in half.
            firstLine = firstLine[..(char.IsHighSurrogate(firstLine[LabelTextLength - 1]) ? LabelTextLength - 1 : LabelTextLength)];
        }

        return new SourceInfo(noteId, firstLine.Length > 0 ? $"{origin} – {firstLine}" : origin, false);
    }
}
