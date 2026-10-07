using System.Text.RegularExpressions;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Storage;

namespace NoteEvolution.Core.Assistants;

/// <summary>One block of a <see cref="DraftProposal"/>: the block and whether the assistant turns it into a heading.</summary>
public sealed class DraftNode
{
    private bool _isHeading;

    internal DraftNode(Block block, DraftNode? parent, bool isFixed, bool isHeading)
    {
        Block = block;
        Parent = parent;
        IsFixed = isFixed;
        _isHeading = isFixed || isHeading;
    }

    public Block Block { get; }

    /// <summary>The block already starts with <c>#</c> to <c>######</c> and a space; it is never changed.</summary>
    public bool IsFixed { get; }

    /// <summary>
    /// The block is (or will become) a heading. Always <c>true</c> for a fixed node; setting <c>true</c> on it does
    /// nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">Setting <c>false</c> on a fixed node.</exception>
    public bool IsHeading
    {
        get => _isHeading;
        set
        {
            if (IsFixed && !value)
            {
                throw new InvalidOperationException("Ein Block, der schon eine Überschrift ist, bleibt eine Überschrift.");
            }

            _isHeading = value;
        }
    }

    public DraftNode? Parent { get; }

    public IReadOnlyList<DraftNode> Children { get; internal set; } = [];
}

/// <summary>The assistant's proposal for a page: one <see cref="DraftNode"/> per block, mirroring the block tree.</summary>
public sealed class DraftProposal
{
    internal DraftProposal(IReadOnlyList<DraftNode> roots) => Roots = roots;

    public IReadOnlyList<DraftNode> Roots { get; }

    /// <summary>All nodes, depth-first in page order.</summary>
    public IEnumerable<DraftNode> AllNodes() => Walk(Roots);

    /// <summary>The heading level the node has (or would get): 1 plus the number of its ancestors that are headings.</summary>
    public int LevelOf(DraftNode node)
    {
        var level = 1;
        for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor.IsHeading)
            {
                level++;
            }
        }

        return level;
    }

    private static IEnumerable<DraftNode> Walk(IEnumerable<DraftNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Walk(node.Children))
            {
                yield return child;
            }
        }
    }
}

/// <summary>
/// Assistant for an existing plain outline: proposes which blocks are headings, and after the user's changes
/// prefixes those with <c>#</c> marks and sets <c>type:: book</c>. Nothing else in the file changes.
/// </summary>
public sealed partial class DraftConverter(BackupService backups, IPageWriter writer)
{
    private const int MaxHeadingLength = 80;
    private const int MaxLevel = 6;
    private const string SentenceEnds = ".!?…";

    [GeneratedRegex(@"^#{1,6} ")]
    private static partial Regex HeadingStart();

    /// <summary>
    /// Builds the proposal for the page's whole block tree (every depth). A block already starting with
    /// <c>#</c> marks is a fixed heading. Any other block is proposed as a heading if it has children, is a single
    /// line, has 1 to 80 characters (trimmed) and does not end in <c>.</c>, <c>!</c>, <c>?</c> or <c>…</c>.
    /// </summary>
    public DraftProposal Propose(Page page) => new([.. page.Roots.Select(root => Build(root, null))]);

    /// <summary>
    /// Writes the proposal: backup first (<see cref="BackupService.CreateBackup"/>), then <c>"#" * level + " "</c> before
    /// the first line of every non-fixed heading node (further lines stay), then <c>type:: book</c> (an existing
    /// <c>type::</c> with another value is overwritten), then the page is saved. If nothing would change (no new
    /// heading and the type is already <c>book</c>) the page is left alone: no backup, no save.
    /// </summary>
    /// <exception cref="ReadOnlyPageException">The page is read-only; checked before anything else.</exception>
    /// <exception cref="ArgumentException">
    /// The proposal is not for this page, a heading would get a level above 6, or a heading's new content would not
    /// parse as one block (for example a block whose first line opens a code fence); all checked before the backup
    /// and before any change.
    /// </exception>
    public void Apply(Page page, DraftProposal proposal)
    {
        if (page.IsReadOnly)
        {
            throw new ReadOnlyPageException($"Die Datei '{page.FilePath}' ist schreibgeschützt: {page.ParseError}");
        }

        var edits = new List<(Block Block, string Content)>();
        foreach (var node in proposal.AllNodes())
        {
            if (node.Block.Page != page)
            {
                throw new ArgumentException("Der Vorschlag gehört nicht zu dieser Seite.", nameof(proposal));
            }

            if (node.IsHeading && !node.IsFixed)
            {
                var level = proposal.LevelOf(node);
                if (level > MaxLevel)
                {
                    throw new ArgumentException(
                        $"Die Überschrift '{node.Block.Content}' bekäme Ebene {level}; erlaubt sind höchstens {MaxLevel}.",
                        nameof(proposal));
                }

                var content = HeadingText.WithTitle(node.Block.Content, level, FirstLine(node.Block.Content));
                try
                {
                    Block.ValidateContent(content);
                }
                catch (ArgumentException ex)
                {
                    throw new ArgumentException(
                        $"Die Überschrift '{FirstLine(node.Block.Content)}' kann nicht gesetzt werden: {ex.Message}",
                        nameof(proposal),
                        ex);
                }

                edits.Add((node.Block, content));
            }
        }

        var alreadyBook = string.Equals(page.GetPageProperty("type"), "book", StringComparison.OrdinalIgnoreCase);
        if (edits.Count == 0 && alreadyBook)
        {
            return;
        }

        backups.CreateBackup(page.FilePath);

        foreach (var (block, content) in edits)
        {
            block.SetContent(content);
        }

        if (!alreadyBook)
        {
            page.SetPageProperty("type", "book");
        }

        writer.Save(page);
    }

    private static DraftNode Build(Block block, DraftNode? parent)
    {
        var isFixed = HeadingStart().IsMatch(FirstLine(block.Content));
        var node = new DraftNode(block, parent, isFixed, !isFixed && LooksLikeHeading(block));
        node.Children = [.. block.Children.Select(child => Build(child, node))];
        return node;
    }

    private static bool LooksLikeHeading(Block block)
    {
        if (block.Children.Count == 0 || block.Content.Contains('\n'))
        {
            return false;
        }

        var text = block.Content.Trim();
        return text.Length is > 0 and <= MaxHeadingLength && !SentenceEnds.Contains(text[^1]);
    }

    private static string FirstLine(string content) => content.Split('\n', 2)[0];
}
