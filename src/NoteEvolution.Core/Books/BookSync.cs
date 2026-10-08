using NoteEvolution.Core.Format;
using NoteEvolution.Core.Links;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Storage;
using NoteEvolution.Core.Text;
using NoteEvolution.Core.Vaults;

namespace NoteEvolution.Core.Books;

/// <summary>A consequence of <see cref="BookSync.Apply"/> for the linked notes; see <see cref="ILinkService.ApplySyncEffects"/>.</summary>
public abstract record SyncEffect;

/// <summary>A linked block was deleted in the editor (itself or within a removed subtree); its notes lose the usage.</summary>
public sealed record BlockDeleted(Guid BookBlockId, IReadOnlyList<Guid> Sources) : SyncEffect
{
    /// <summary>
    /// The removed top block with its subtree and lines unchanged, so that undo can put it back. Effects for linked
    /// blocks within the same removed subtree carry the same instance.
    /// </summary>
    internal Block? Removed { get; init; }

    /// <summary><see cref="Block.Key"/> of the parent the block was removed from; <c>null</c> = root level.</summary>
    internal Guid? ParentKey { get; init; }

    /// <summary>The block's index among its siblings when it was removed.</summary>
    internal int Index { get; init; }
}

/// <summary>
/// A text block got its own id and the sources: split off a linked block in the editor, or a linked block the editor put
/// back after its deletion was saved (its sources that are still in the vault). Each source note gains the usage.
/// </summary>
public sealed record BlockSplit(Guid NewBookBlockId, IReadOnlyList<Guid> Sources) : SyncEffect;

/// <param name="Changed">The page's content changed; the caller has to save it.</param>
/// <param name="Effects">What the linked notes need, in the order it happened.</param>
public sealed record SyncResult(bool Changed, IReadOnlyList<SyncEffect> Effects);

/// <summary>Writes an edited <see cref="SectionSnapshot"/> back into the book page with as few line changes as possible.</summary>
public static class BookSync
{
    /// <summary>
    /// Reconciles <paramref name="snapshot"/> into <c>book.Page</c>: changed texts are written (escaped with
    /// <see cref="BlockTextEscape"/>), new blocks are inserted with the snapshot key as <see cref="Block.Key"/>,
    /// moved blocks are moved, blocks missing from the snapshot are removed. Blocks the user did not change keep
    /// their lines and their place. Headings are only renamed; the outline itself is never changed here.
    /// <para>
    /// A new text block split off a linked block gets an id and the same <c>source::</c> (<see cref="BlockSplit"/>).
    /// A new text block without <see cref="SnapshotTextBlock.SplitFrom"/> but with sources (the editor put a deleted
    /// linked block back, e.g. its undo after the deletion was saved) is linked again the same way with those of its
    /// sources that <paramref name="vault"/> finds; without a vault, or if none is found, it stays unlinked.
    /// </para>
    /// <para>
    /// The page is not saved, and <paramref name="book"/> is stale afterwards (rebuild it with <see cref="Book.Load"/>).
    /// If <see cref="SyncResult.Changed"/>, the caller saves <c>book.Page</c> with <see cref="IPageWriter.Save"/>,
    /// then passes the effects to <see cref="ILinkService.ApplySyncEffects"/>, then calls
    /// <see cref="ILinkService.RetryPending"/>. If the save fails, the effects are dropped and the book is taken
    /// from the vault again; the in-memory page must not be saved later.
    /// </para>
    /// <para>
    /// Line endings in the snapshot texts (<c>"\r\n"</c>, <c>"\r"</c>) are read as <c>"\n"</c>. Every text to be
    /// written is validated before the first change, so the exceptions listed here leave the page untouched. On
    /// any other exception the page may be partly changed: the caller must reload it from disk.
    /// </para>
    /// </summary>
    /// <exception cref="ReadOnlyPageException">The book page is read-only.</exception>
    /// <exception cref="ArgumentException">
    /// Unknown scope, an inconsistent snapshot (empty or duplicate keys, a text block or paragraph with the key
    /// of a heading), or a text that would change the block tree (see <see cref="Block.SetContent"/>); nothing is changed.
    /// </exception>
    public static SyncResult Apply(Book book, SectionSnapshot snapshot, IVault? vault = null)
    {
        var page = book.Page;
        if (page.IsReadOnly)
        {
            throw new ReadOnlyPageException($"Die Datei '{page.FilePath}' ist schreibgeschützt: {page.ParseError}");
        }

        var before = PageSerializer.Serialize(page);
        var run = new Run(book, NormalizeLineEndings(snapshot), vault);
        run.Execute();
        var changed = !before.AsSpan().SequenceEqual(PageSerializer.Serialize(page));
        return new SyncResult(changed, run.Effects);
    }

    private static SectionSnapshot NormalizeLineEndings(SectionSnapshot snapshot)
    {
        static string Lf(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

        return snapshot with
        {
            Nodes =
            [
                .. snapshot.Nodes.Select(node => node switch
                {
                    SnapshotHeading heading => heading with { Text = Lf(heading.Text) },
                    SnapshotTextBlock text => text with
                    {
                        Text = Lf(text.Text),
                        Paragraphs = [.. text.Paragraphs.Select(p => p with { Text = Lf(p.Text) })],
                    },
                    _ => node,
                }),
            ],
        };
    }

    /// <summary>The state of one <see cref="Apply"/> call. All lookups go through the page's blocks by key.</summary>
    private sealed class Run
    {
        private readonly Page _page;
        private readonly Book _book;
        private readonly IVault? _vault;
        private readonly SectionSnapshot _snapshot;
        private readonly OutlineNode _scope;

        /// <summary>Keys of all heading blocks; Apply never adds, removes or moves headings.</summary>
        private readonly HashSet<Guid> _headings = [];

        /// <summary>Every key in the snapshot (headings, text blocks, paragraphs).</summary>
        private readonly HashSet<Guid> _listed = [];

        /// <summary>Keys of the known headings in a manuscript snapshot: fixed points of the order.</summary>
        private readonly HashSet<Guid> _listedHeadings = [];

        private readonly Dictionary<Guid, Block> _blocks = [];
        private readonly List<Block> _textBlocks = [];

        /// <summary>The content to write, by key: for new blocks and for blocks whose text changed. Validated up front.</summary>
        private readonly Dictionary<Guid, string> _contents = [];

        /// <summary>Checks the snapshot and plans and validates every text change; the page is not touched yet.</summary>
        public Run(Book book, SectionSnapshot snapshot, IVault? vault)
        {
            _page = book.Page;
            _vault = vault;
            _book = book;
            _snapshot = snapshot;
            _scope = book.FindNode(snapshot.ScopeKey)
                     ?? throw new ArgumentException("No section has this key.", nameof(snapshot));
            CollectHeadings(book.Root);
            Validate();
            foreach (var block in _page.AllBlocks())
            {
                _blocks.TryAdd(block.Key, block);
            }

            PlanContents();
        }

        public List<SyncEffect> Effects { get; } = [];

        private bool Manuscript => _snapshot.IncludeSubsections;

        public void Execute()
        {
            var section = _scope;
            var predecessor = Manuscript ? _scope.Block : null;
            foreach (var node in _snapshot.Nodes)
            {
                switch (node)
                {
                    case SnapshotHeading heading when Manuscript && _book.FindNode(heading.Key) is { Block: { } block } known:
                        WritePlanned(block);
                        section = known;
                        predecessor = block;
                        break;
                    case SnapshotTextBlock text:
                        predecessor = PlaceTextBlock(text, section, predecessor);
                        break;
                }
            }

            RemoveMissing();
        }

        private void CollectHeadings(OutlineNode node)
        {
            foreach (var child in node.Children)
            {
                _headings.Add(child.Key);
                CollectHeadings(child);
            }
        }

        private void Validate()
        {
            foreach (var node in _snapshot.Nodes)
            {
                List(node.Key);
                if (node is SnapshotHeading)
                {
                    if (Manuscript && _headings.Contains(node.Key))
                    {
                        _listedHeadings.Add(node.Key);
                    }

                    continue;
                }

                if (_headings.Contains(node.Key))
                {
                    throw new ArgumentException("A text block has the key of a heading.", "snapshot");
                }

                foreach (var paragraph in ((SnapshotTextBlock)node).Paragraphs)
                {
                    List(paragraph.Key);
                    if (_headings.Contains(paragraph.Key))
                    {
                        throw new ArgumentException("A paragraph has the key of a heading.", "snapshot");
                    }
                }
            }
        }

        private void List(Guid key)
        {
            if (key == Guid.Empty || !_listed.Add(key))
            {
                throw new ArgumentException($"The snapshot key {key} is empty or not unique.", "snapshot");
            }
        }

        /// <summary>
        /// Records the content of every renamed heading, changed or new text block and changed or new paragraph,
        /// and validates it as <see cref="Block.SetContent"/> would (ruling R17).
        /// </summary>
        private void PlanContents()
        {
            foreach (var node in _snapshot.Nodes)
            {
                switch (node)
                {
                    case SnapshotHeading heading when Manuscript && _book.FindNode(heading.Key) is { Block: not null } known:
                        if (RenamedContent(known, heading.Text) is { } renamed)
                        {
                            Plan(heading.Key, renamed);
                        }

                        break;
                    case SnapshotTextBlock text:
                        if (!_blocks.TryGetValue(text.Key, out var block) || BlockTextEscape.Unescape(block.Content) != text.Text)
                        {
                            Plan(text.Key, BlockTextEscape.Escape(text.Text));
                        }

                        foreach (var paragraph in text.Paragraphs)
                        {
                            if (!_blocks.TryGetValue(paragraph.Key, out var existing)
                                || BookSnapshot.ParagraphText(existing.Content) != paragraph.Text
                                || NoteTag.Has(existing.Content) != paragraph.IsNote)
                            {
                                Plan(paragraph.Key, ParagraphContent(paragraph));
                            }
                        }

                        break;
                }
            }
        }

        private void Plan(Guid key, string content)
        {
            Block.ValidateContent(content);
            _contents[key] = content;
        }

        /// <summary>Writes the planned content, if any, into a present block.</summary>
        private void WritePlanned(Block block)
        {
            if (_contents.TryGetValue(block.Key, out var content))
            {
                block.SetContent(content);
            }
        }

        /// <summary>
        /// The heading's content with the title replaced (the <c>#</c> prefix and any further lines kept), or
        /// <c>null</c> if the title is the same. Spaces are kept, as <see cref="BookSnapshot.Create"/> reports them.
        /// </summary>
        private static string? RenamedContent(OutlineNode node, string text)
        {
            var title = text.ReplaceLineEndings(" ");
            if (title == HeadingText.TitleOf(node.Block!.Content, node.Level))
            {
                return null;
            }

            return HeadingText.WithTitle(node.Block!.Content, node.Level, title);
        }

        private Block PlaceTextBlock(SnapshotTextBlock text, OutlineNode section, Block? predecessor)
        {
            if (_blocks.TryGetValue(text.Key, out var block))
            {
                WritePlanned(block);
            }
            else
            {
                block = Create(text);
            }

            if (!StaysInView(block, section, predecessor))
            {
                if (predecessor is not null && !_headings.Contains(predecessor.Key))
                {
                    Put(block, predecessor.Parent, IndexAmongSiblings(predecessor, without: block) + 1);
                }
                else
                {
                    Put(block, section.Block, FirstIndex(section.Block, block));
                }
            }

            _textBlocks.Add(block);
            ReconcileParagraphs(block, text.Paragraphs);
            return block;
        }

        /// <summary>
        /// A new detached text block. Split off a linked block, it gets an id and the same <c>source::</c>; put back
        /// with sources (no split), it gets an id and those sources that are in the vault (ruling R33).
        /// </summary>
        private Block Create(SnapshotTextBlock text)
        {
            List<BlockProperty>? properties = null;
            if (text.SplitFrom is { } from)
            {
                if (_blocks.TryGetValue(from, out var original)
                    && original.GetProperty("source") is { } source
                    && SourceValue.Parse(source) is { Count: > 0 } sources)
                {
                    properties = Link(source, [.. sources.Distinct()]);
                }
            }
            else if (_vault is not null
                     && text.Sources.Select(s => s.NoteId).Distinct().Where(id => _vault.FindBlockById(id) is not null).ToList()
                         is { Count: > 0 } found)
            {
                properties = Link(SourceValue.Format(found), found);
            }

            var block = Block.CreateDetached(_contents[text.Key], properties, text.Key);
            _blocks[text.Key] = block;
            return block;
        }

        /// <summary>The properties of a new linked block (a new id and <paramref name="source"/>); the notes gain its usage.</summary>
        private List<BlockProperty> Link(string source, IReadOnlyList<Guid> sources)
        {
            var id = Guid.CreateVersion7();
            Effects.Add(new BlockSplit(id, sources));
            return [new BlockProperty("id", id.ToString("D")), new BlockProperty("source", source)];
        }

        /// <summary>
        /// Rule 4: the block stays if it is shown in the view and comes after its snapshot predecessor there, with no
        /// heading of the snapshot in between. The view is the scope's subtree in file order (the manuscript) or, for
        /// a snapshot of one section's text blocks, the section's items, so text the user did not move stays even after
        /// a sub-section.
        /// </summary>
        private bool StaysInView(Block block, OutlineNode section, Block? predecessor)
        {
            if (block.Page != _page)
            {
                return false;
            }

            var view = Manuscript ? Linearize() : [.. ChildrenOf(section.Block)];
            var at = view.IndexOf(block);
            var after = predecessor is null ? -1 : view.IndexOf(predecessor);
            if (at < 0 || (predecessor is not null && after < 0) || at < after)
            {
                return false;
            }

            for (var i = after + 1; i < at; i++)
            {
                if (_listedHeadings.Contains(view[i].Key))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>The headings and text blocks of the scope's subtree in file order.</summary>
        private List<Block> Linearize()
        {
            var view = new List<Block>();
            if (_scope.Block is { } heading)
            {
                view.Add(heading);
            }

            AddSectionItems(view, _scope.Block);
            return view;
        }

        private void AddSectionItems(List<Block> view, Block? section)
        {
            foreach (var child in ChildrenOf(section))
            {
                view.Add(child);
                if (_headings.Contains(child.Key))
                {
                    AddSectionItems(view, child);
                }
            }
        }

        /// <summary>
        /// Where the first block of a segment goes: in the manuscript directly after the heading; in a snapshot of one
        /// section's text blocks before the first text block, else before the first sub-heading, else at the end.
        /// </summary>
        private int FirstIndex(Block? section, Block block)
        {
            if (Manuscript)
            {
                return 0;
            }

            var siblings = ChildrenOf(section).Where(b => b != block).ToList();
            return Math.Max(siblings.FindIndex(b => !_headings.Contains(b.Key)), 0);
        }

        /// <summary>Rule 6: depths build the child tree; a jump deeper than one level is limited to one.</summary>
        private void ReconcileParagraphs(Block textBlock, IReadOnlyList<SnapshotParagraph> paragraphs)
        {
            var path = new List<Block> { textBlock };
            var lastChild = new Dictionary<Block, Block>();
            foreach (var paragraph in paragraphs)
            {
                var depth = Math.Clamp(paragraph.Depth, 1, path.Count);
                var parent = path[depth - 1];
                path.RemoveRange(depth, path.Count - depth);

                if (_blocks.TryGetValue(paragraph.Key, out var block))
                {
                    WritePlanned(block);
                }
                else
                {
                    block = Block.CreateDetached(_contents[paragraph.Key], null, paragraph.Key);
                    _blocks[paragraph.Key] = block;
                }

                var predecessor = lastChild.GetValueOrDefault(parent);
                var stays = block.Page == _page && block.Parent == parent
                            && (predecessor is null || IndexAmongSiblings(block) > IndexAmongSiblings(predecessor));
                if (!stays)
                {
                    Put(block, parent, predecessor is null ? 0 : IndexAmongSiblings(predecessor, without: block) + 1);
                }

                lastChild[parent] = block;
                path.Add(block);
            }
        }

        private static string ParagraphContent(SnapshotParagraph paragraph)
        {
            var text = BlockTextEscape.Escape(paragraph.Text);
            return !paragraph.IsNote ? text
                : text.Length == 0 ? NoteTag.Tag
                : NoteTag.Add(text);
        }

        /// <summary>
        /// Rule 5: text blocks of the area (the section, or the scope's subtree) and descendants of the reconciled
        /// text blocks that are not in the snapshot are removed. Siblings go front to back, so that each recorded
        /// index is right for restoring the deletions in reverse order.
        /// </summary>
        private void RemoveMissing()
        {
            var sections = new List<OutlineNode> { _scope };
            for (var i = 0; Manuscript && i < sections.Count; i++)
            {
                sections.AddRange(sections[i].Children);
            }

            foreach (var section in sections)
            {
                foreach (var block in ChildrenOf(section.Block).ToList())
                {
                    if (!_headings.Contains(block.Key) && !_listed.Contains(block.Key))
                    {
                        Remove(block);
                    }
                }
            }

            foreach (var textBlock in _textBlocks)
            {
                RemoveUnlisted(textBlock);
            }
        }

        private void RemoveUnlisted(Block parent)
        {
            foreach (var child in parent.Children.ToList())
            {
                if (_listed.Contains(child.Key))
                {
                    RemoveUnlisted(child);
                }
                else
                {
                    Remove(child);
                }
            }
        }

        /// <summary>
        /// Removes the block with its subtree. Every linked block in the subtree (ruling R18) gets a
        /// <see cref="BlockDeleted"/>; all of them carry the removed top block, so that one undo restores everything.
        /// </summary>
        private void Remove(Block block)
        {
            var parent = block.Parent;
            var index = IndexAmongSiblings(block);
            _page.RemoveBlock(block);
            foreach (var (id, sources) in SourceValue.LinkedBlocksIn(block))
            {
                Effects.Add(new BlockDeleted(id, sources) { Removed = block, ParentKey = parent?.Key, Index = index });
            }
        }

        /// <summary>Inserts a new block or moves a present one; <paramref name="index"/> is counted without the block.</summary>
        private void Put(Block block, Block? parent, int index)
        {
            if (block.Page is null)
            {
                _page.InsertBlock(parent, index, block);
            }
            else
            {
                _page.MoveBlock(block, parent, index);
            }
        }

        private IReadOnlyList<Block> ChildrenOf(Block? parent) => parent?.Children ?? _page.Roots;

        private int IndexAmongSiblings(Block block, Block? without = null)
        {
            var index = 0;
            foreach (var sibling in ChildrenOf(block.Parent))
            {
                if (sibling == block)
                {
                    return index;
                }

                if (sibling != without)
                {
                    index++;
                }
            }

            throw new InvalidOperationException("The block is not among its parent's children.");
        }
    }
}
