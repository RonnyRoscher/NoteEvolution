using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Text;

namespace NoteEvolution.Core.Books;

/// <summary>
/// Structure commands of the manuscript (new section after / below, indent, outdent, remove a heading, delete a
/// detail, put a range under a new heading) and the status commands (set a status, add a remark). Like
/// <see cref="OutlineEditor"/>, they change <c>book.Page</c> in memory and find their blocks through the
/// <see cref="Book"/> view; the caller saves the page and reloads the
/// <see cref="Book"/>. Every command validates first; if it throws (<see cref="ArgumentException"/>, also for an unknown key), the page is unchanged.
/// New headings are empty (<c>"#" * level + " "</c>), new text blocks and details are empty blocks.
/// </summary>
public static class ManuscriptEditor
{
    /// <summary>The properties a block converted to a heading hands on to the first text block under it.</summary>
    private static readonly string[] CarriedKeys = ["id", "source", BlockStatuses.Key];

    /// <summary>
    /// Adds a new empty element after <paramref name="current"/>: for a heading of level n a level-n heading after its
    /// whole section, for a text block a text block directly after it, for a detail a detail of the same depth after
    /// it and its deeper details.
    /// </summary>
    /// <param name="now">
    /// With a time, a new text block gets both dates and a new detail stamps <c>updated::</c> on its text block; a new
    /// heading gets none. Without, nothing is stamped.
    /// </param>
    /// <returns>The <see cref="Block.Key"/> of the new element.</returns>
    /// <exception cref="ArgumentException">The element is not in the book.</exception>
    public static Guid InsertAfter(Book book, BookElement current, DateTimeOffset? now = null)
    {
        var block = BlockOf(book, current);
        var content = current.Kind == ElementKind.Heading ? EmptyHeading(book.FindNode(current.Key)!.Level) : "";
        return Insert(book, block.Parent, IndexOf(book.Page, block) + 1, content, current.Kind, current, now);
    }

    /// <summary>
    /// Adds a new empty element as the first child of <paramref name="current"/>: for a heading of level n a level-(n+1)
    /// heading after its own text blocks and before its existing sub-sections, for a text block a detail of depth 1,
    /// for a detail a detail one level deeper.
    /// </summary>
    /// <param name="now">As for <see cref="InsertAfter"/>; a new detail stamps <c>updated::</c> on its text block.</param>
    /// <returns>The <see cref="Block.Key"/> of the new element.</returns>
    /// <exception cref="ArgumentException">The element is not in the book, or it is a level-6 heading.</exception>
    public static Guid InsertChild(Book book, BookElement current, DateTimeOffset? now = null)
    {
        var block = BlockOf(book, current);
        if (current.Kind != ElementKind.Heading)
        {
            return Insert(book, block, 0, "", ElementKind.Detail, current, now);
        }

        var node = book.FindNode(current.Key)!;
        if (node.Level >= OutlineEditor.MaxLevel)
        {
            throw new ArgumentException($"A heading below level {node.Level} would exceed level {OutlineEditor.MaxLevel}.", nameof(current));
        }

        var firstSubsection = node.Children.FirstOrDefault()?.Block;
        var index = firstSubsection is null ? block.Children.Count : IndexOf(book.Page, firstSubsection);
        return Insert(book, block, index, EmptyHeading(node.Level + 1), ElementKind.Heading, current, now);
    }

    /// <summary>Whether <see cref="InsertChild"/> is possible: <c>false</c> for a level-6 heading or an unknown element.</summary>
    public static bool CanInsertChild(Book book, BookElement current) =>
        BookElements.Find(book, current.Key) == current
        && (current.Kind != ElementKind.Heading || book.FindNode(current.Key)!.Level < OutlineEditor.MaxLevel);

    /// <summary>
    /// Moves the heading one level deeper, with its sub-sections: it becomes the last sub-section of the previous
    /// heading of the same level (<see cref="OutlineEditor.MoveSection"/>).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Unknown heading, no previous heading of the same level in the same section, or the subtree would get a heading
    /// deeper than level 6.
    /// </exception>
    public static void Indent(Book book, Guid headingKey)
    {
        var node = OutlineEditor.FindHeading(book, headingKey, nameof(headingKey));
        var previous = PreviousSameLevel(node)
                       ?? throw new ArgumentException("There is no previous heading of the same level.", nameof(headingKey));
        OutlineEditor.MoveSection(book, headingKey, previous.Key, previous.Children.Count());
    }

    /// <summary>
    /// Moves the heading one level higher, with its sub-sections: it is placed directly after the section of its
    /// former parent heading, and every heading of its subtree gets one <c>#</c> less.
    /// </summary>
    /// <exception cref="ArgumentException">Unknown heading, or a level-1 heading.</exception>
    public static void Outdent(Book book, Guid headingKey)
    {
        var node = OutlineEditor.FindHeading(book, headingKey, nameof(headingKey));
        if (!CanOutdent(node))
        {
            throw new ArgumentException("A level-1 heading cannot be outdented.", nameof(headingKey));
        }

        var parent = node.Parent!;
        var grandparent = parent.Parent!;
        book.Page.MoveBlock(node.Block!, grandparent.Block, IndexOf(book.Page, parent.Block!) + 1);
        OutlineEditor.Relevel(node, grandparent.Level + 1);
    }

    /// <summary>Whether <see cref="Indent"/> is possible; <c>false</c> for an unknown key.</summary>
    public static bool CanIndent(Book book, Guid headingKey) =>
        book.FindNode(headingKey) is { Block: not null } node
        && PreviousSameLevel(node) is { } previous
        && previous.Level + OutlineEditor.Height(node) <= OutlineEditor.MaxLevel;

    /// <summary>Whether <see cref="Outdent"/> is possible; <c>false</c> for an unknown key.</summary>
    public static bool CanOutdent(Book book, Guid headingKey) =>
        book.FindNode(headingKey) is { Block: not null } node && CanOutdent(node);

    /// <summary>
    /// Removes the heading but keeps its content: its title (with any further lines of the heading's text) becomes a
    /// text block in its place, which takes over the heading's <c>id::</c> so that cross references to the heading keep
    /// resolving; its child blocks (text blocks and sub-sections) follow in the surrounding section, in order, and every
    /// moved sub-heading gets the level of the surrounding section + 1 (its own sub-headings the levels below). A heading
    /// with an empty title and no id leaves no text block. The heading's other properties go with it.
    /// </summary>
    /// <param name="now">With a time, the new text block gets both dates; without, none.</param>
    /// <returns>
    /// What was done, for a targeted undo (<see cref="RemovedHeading.RestoreInto"/>), with the key to reveal afterwards
    /// (<see cref="RemovedHeading.Reveal"/>): the new text block, else the first moved item, else the element before the
    /// heading in the file, else <see cref="Guid.Empty"/>.
    /// </returns>
    /// <exception cref="ArgumentException">Unknown heading.</exception>
    public static RemovedHeading RemoveHeading(Book book, Guid headingKey, DateTimeOffset? now = null)
    {
        var node = OutlineEditor.FindHeading(book, headingKey, nameof(headingKey));
        var heading = node.Block!;
        var page = book.Page;
        var parent = heading.Parent;
        var children = heading.Children.ToList();
        var titleBlock = TitleBlock(heading, node.Level, now);
        var reveal = titleBlock?.Key
                     ?? children.FirstOrDefault()?.Key
                     ?? page.AllBlocks().TakeWhile(block => block != heading).LastOrDefault()?.Key
                     ?? Guid.Empty;

        // Every moved block with its lines before, and each moved heading with its level before and after (as Relevel sets it).
        var moved = children.SelectMany(Subtree).Select(block => (Block: block, Before: block.Lines.ToList())).ToList();
        var levels = new Dictionary<Guid, (int Before, int After)>();
        foreach (var child in node.Children)
        {
            NewLevels(child, node.Parent!.Level + 1, levels);
        }

        var index = IndexOf(page, heading);
        for (var i = 0; i < children.Count; i++)
        {
            page.MoveBlock(children[i], parent, index + i);
        }

        page.RemoveBlock(heading);
        if (titleBlock is not null)
        {
            page.InsertBlock(parent, index, titleBlock);
        }

        foreach (var child in node.Children)
        {
            OutlineEditor.Relevel(child, node.Parent!.Level + 1);
        }

        return new RemovedHeading(
            reveal,
            heading,
            parent?.Key,
            index,
            titleBlock?.Key,
            titleBlock is null ? [] : [.. titleBlock.Lines],
            [.. children.Select(child => child.Key)],
            MovedBlocks(moved, levels));
    }

    /// <summary>
    /// The detached text block made from a heading's title for <see cref="RemoveHeading"/>: the heading's text without
    /// its hashes, with the heading's raw <c>id::</c> and, with <paramref name="now"/>, both dates; <c>null</c> for an
    /// empty title without id.
    /// </summary>
    private static Block? TitleBlock(Block heading, int level, DateTimeOffset? now)
    {
        var content = HeadingText.WithoutMarks(heading.Content, level);
        var id = heading.GetProperty("id");
        if (string.IsNullOrWhiteSpace(content) && id is null)
        {
            return null;
        }

        var block = Block.CreateDetached(content, id is null ? null : [new BlockProperty("id", id)]);
        if (now is { } time)
        {
            BlockDates.StampNew(block, time);
        }

        return block;
    }

    /// <summary>
    /// Why the level button <paramref name="level"/> (0 = Text, 1–6 = H1–H6) cannot convert <paramref name="element"/>
    /// (spec 4.2), for its tooltip: the resource key of the German reason (<c>LevelBlocked…</c> in the UI's strings,
    /// where all UI text lives); <c>null</c> if it can (or the element already has that level). There is no level
    /// outside 0–6. A text block or detail cannot become a heading above the level of its section (it would take over
    /// sections not its own), except in the prologue. A heading cannot go above the level of its parent heading + 1 (that
    /// is outdenting), nor so deep that a sub-heading would fall below H6. Text is always possible.
    /// </summary>
    /// <exception cref="ArgumentException">The element is not in the book.</exception>
    public static string? LevelBlocked(Book book, BookElement element, int level)
    {
        if (BookElements.Find(book, element.Key) != element)
        {
            throw new ArgumentException($"There is no such {element.Kind} in the book.", nameof(element));
        }

        if (level is < 0 or > OutlineEditor.MaxLevel)
        {
            return "LevelBlockedNoSuchLevel";
        }

        if (element.Kind != ElementKind.Heading)
        {
            var section = BookElements.TextBlockOf(book, element)!.Section;
            return level > 0 && level < section.Level
                ? "LevelBlockedAboveSection"
                : null;
        }

        var node = book.FindNode(element.Key)!;
        if (level == 0 || level == node.Level)
        {
            return null;
        }

        if (level < node.Parent!.Level + 1)
        {
            return "LevelBlockedOutOfParent";
        }

        return DeepestLevel(node) + level - node.Level > OutlineEditor.MaxLevel
            ? "LevelBlockedBelowH6"
            : null;
    }

    /// <summary>
    /// Makes the text block or detail <paramref name="elementKey"/> a heading of <paramref name="level"/> (spec 4.2). The
    /// block's first line (as the editor shows it, without heading marks and a <c>#notiz</c> tag) is the title; the
    /// further text becomes the first text block under it, and each direct sub-point a text block with its deeper
    /// sub-points as details. The heading stands in the block's place, a detail's heading right after its text block;
    /// it takes over the section's items that follow there up to the next heading that is not deeper (above the
    /// section's level these are the section's own following text blocks, at its level also its following sub-sections,
    /// and the heading then follows the section). The block's <c>id::</c>, <c>source::</c> and status (also from a
    /// <c>#notiz</c> tag) go to the first text block: the block itself keeps them with the further text, else the first
    /// sub-point takes them if it has none of its own, else the block stays as an empty text block with them; a block
    /// with none of them and no further text goes. Each text block made gets both dates (<c>updated::</c> only if it has
    /// <c>created::</c>), a detail's text block <c>updated::</c>; the heading gets none.
    /// </summary>
    /// <returns>The key of the new heading.</returns>
    /// <exception cref="ArgumentException">
    /// The element is a heading or not in the book, <see cref="LevelBlocked"/> refuses the level, or the further text
    /// would not stay one block.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A block has no place for properties (an open code fence, which makes the page read-only); the page is then left
    /// partly converted and must be read again.
    /// </exception>
    public static Guid ToHeading(Book book, Guid elementKey, int level, DateTimeOffset now)
    {
        var element = BookElements.Find(book, elementKey) is { Kind: not ElementKind.Heading } found
            ? found
            : throw new ArgumentException("Only a text block or a detail of the book becomes a heading.", nameof(elementKey));
        if (level == 0)
        {
            throw new ArgumentException("Text is no heading level.", nameof(level));
        }

        if (LevelBlocked(book, element, level) is { } reason)
        {
            throw new ArgumentException(reason, nameof(level));
        }

        var page = book.Page;
        var block = BlockOf(book, element);
        var textBlock = BookElements.TextBlockOf(book, element)!;
        var section = textBlock.Section;

        var hasTag = NoteTag.Has(block.Content);
        var (title, further) = HeadingText.Split(BlockTextEscape.Unescape(hasTag ? NoteTag.Remove(block.Content) : block.Content));
        if (further is not null)
        {
            // Escaped anew as the editor saves it, so that no line of it reads as structure as the first one of a block.
            further = BlockTextEscape.Escape(further);
            Block.ValidateContent(further);
        }

        var carried = new List<BlockProperty>();
        foreach (var key in CarriedKeys)
        {
            if (block.GetProperty(key) is { } value)
            {
                carried.Add(new BlockProperty(key, value));
            }
        }

        if (hasTag && block.GetProperty(BlockStatuses.Key) is null)
        {
            carried.Add(new BlockProperty(BlockStatuses.Key, BlockStatuses.FileValue(BlockStatus.Remark)!));
        }

        var subPoints = block.Children.ToList();
        // The first sub-point takes the carried properties only if it has none of its own.
        var keepBlock = further is not null || (carried.Count > 0 && (subPoints.Count == 0 || Carries(subPoints[0])));
        var following = section.Items
            .SkipWhile(item => item != textBlock)
            .Skip(1)
            .TakeWhile(item => item is TextBlock || item is OutlineNode { Level: var l } && l > level)
            .Select(item => item is TextBlock followingText ? followingText.Block : ((OutlineNode)item).Block!)
            .ToList();

        // Above the section's level the heading goes into the section after the text block, at its level after the section.
        var (parent, index) = level > section.Level
            ? (section.Block, IndexOf(page, textBlock.Block) + 1)
            : (section.Block!.Parent, IndexOf(page, section.Block) + 1);
        var heading = Block.CreateDetached(HeadingText.WithTitle("", level, title));
        page.InsertBlock(parent, index, heading);

        var position = 0;
        if (keepBlock)
        {
            block.SetContent(further ?? "");
            foreach (var property in carried.Where(property => property.Key == BlockStatuses.Key && block.GetProperty(property.Key) is null))
            {
                block.SetProperty(property.Key, property.Value);
            }

            StampAsTextBlock(block, now);
            page.MoveBlock(block, heading, position++);
        }
        else if (carried.Count > 0)
        {
            foreach (var property in carried)
            {
                subPoints[0].SetProperty(property.Key, property.Value);
            }
        }

        foreach (var subPoint in subPoints)
        {
            StampAsTextBlock(subPoint, now);
            page.MoveBlock(subPoint, heading, position++);
        }

        if (!keepBlock)
        {
            page.RemoveBlock(block);
        }

        foreach (var item in following)
        {
            page.MoveBlock(item, heading, position++);
        }

        if (element.Kind == ElementKind.Detail)
        {
            BlockDates.StampChanged(textBlock.Block, now);
        }

        return heading.Key;
    }

    /// <summary>
    /// Gives the heading <paramref name="headingKey"/> the level <paramref name="level"/> (1–6, spec 4.2); every heading
    /// of its subtree shifts by the same difference, so the outline below it stays as it is. Only the <c>#</c> marks
    /// change.
    /// </summary>
    /// <exception cref="ArgumentException">Unknown heading, or <see cref="LevelBlocked"/> refuses the level (also Text).</exception>
    public static void SetHeadingLevel(Book book, Guid headingKey, int level)
    {
        var node = OutlineEditor.FindHeading(book, headingKey, nameof(headingKey));
        if (level == 0)
        {
            throw new ArgumentException("Text is no heading level; the heading is removed instead.", nameof(level));
        }

        if (LevelBlocked(book, new BookElement(ElementKind.Heading, headingKey), level) is { } reason)
        {
            throw new ArgumentException(reason, nameof(level));
        }

        Shift(node, level - node.Level);
    }

    /// <summary>Whether <paramref name="block"/> has an <c>id::</c>, <c>source::</c> or status (also a <c>#notiz</c> tag) of its own.</summary>
    private static bool Carries(Block block) =>
        CarriedKeys.Any(key => block.GetProperty(key) is not null) || NoteTag.Has(block.Content);

    /// <summary>Stamps a block that is a text block now: both dates for a new one, <c>updated::</c> if it has <c>created::</c>.</summary>
    private static void StampAsTextBlock(Block block, DateTimeOffset now)
    {
        if (block.GetProperty(BlockDates.CreatedKey) is null)
        {
            BlockDates.StampNew(block, now);
        }
        else
        {
            BlockDates.StampChanged(block, now);
        }
    }

    /// <summary>Changes the level of <paramref name="node"/> and every heading below it by <paramref name="difference"/>.</summary>
    private static void Shift(OutlineNode node, int difference)
    {
        if (difference != 0)
        {
            node.Block!.SetContent(HeadingText.WithLevel(node.Block.Content, node.Level, node.Level + difference));
        }

        foreach (var child in node.Children)
        {
            Shift(child, difference);
        }
    }

    /// <summary>The deepest level of a heading in the subtree of <paramref name="node"/>, the node included.</summary>
    private static int DeepestLevel(OutlineNode node) => node.Children.Select(DeepestLevel).Append(node.Level).Max();

    /// <summary>
    /// Whether <see cref="Wrap"/> is possible: <c>false</c> for a detail, for no head (the whole book), for an unknown
    /// element and when a heading would get deeper than level 6.
    /// </summary>
    public static bool CanWrap(Book book, BookElement? head) =>
        head is not null && BookElements.Find(book, head.Key) == head && head.Kind switch
        {
            ElementKind.Heading => book.FindNode(head.Key) is { } node
                                   && node.Parent!.Level + 1 + OutlineEditor.Height(node) <= OutlineEditor.MaxLevel,
            ElementKind.TextBlock => book.FindTextBlock(head.Key)!.Section.Level + 1 <= OutlineEditor.MaxLevel,
            _ => false,
        };

    /// <summary>
    /// Puts the range of <paramref name="head"/> under a new empty heading N of the level of the head's section S + 1,
    /// placed at the head's index in S. A heading head becomes N's only child with its whole section, and every heading
    /// of its subtree gets the level below N (<see cref="OutlineEditor.Relevel"/>). A text block head and the own text
    /// blocks of S directly following it, up to S's next sub-section (or S's end), become N's children, in order; S's
    /// sub-sections and own text blocks after one of them stay where they are.
    /// </summary>
    /// <returns>What was done, for a targeted undo (<see cref="WrappedSection.RestoreInto"/>), with N's key.</returns>
    /// <exception cref="ArgumentException"><see cref="CanWrap"/> is <c>false</c>.</exception>
    public static WrappedSection Wrap(Book book, BookElement head)
    {
        if (!CanWrap(book, head))
        {
            throw new ArgumentException("The element cannot be put under a new heading.", nameof(head));
        }

        var page = book.Page;
        var node = head.Kind == ElementKind.Heading ? book.FindNode(head.Key)! : null;
        var section = node?.Parent ?? book.FindTextBlock(head.Key)!.Section;
        List<Block> tops = node is not null
            ? [node.Block!]
            : [.. section.Items
                .SkipWhile(item => !(item is TextBlock textBlock && textBlock.Key == head.Key))
                .TakeWhile(item => item is TextBlock)
                .Select(item => ((TextBlock)item).Block)];
        var indices = tops.Select(top => IndexOf(page, top)).ToList();

        // Every moved block with its lines before, and each moved heading with its level before and after (as Relevel sets it).
        var moved = tops.SelectMany(Subtree).Select(block => (Block: block, Before: block.Lines.ToList())).ToList();
        var levels = new Dictionary<Guid, (int Before, int After)>();
        if (node is not null)
        {
            NewLevels(node, section.Level + 2, levels);
        }

        var heading = Block.CreateDetached(EmptyHeading(section.Level + 1));
        page.InsertBlock(section.Block, indices[0], heading);
        for (var i = 0; i < tops.Count; i++)
        {
            page.MoveBlock(tops[i], heading, i);
        }

        if (node is not null)
        {
            OutlineEditor.Relevel(node, section.Level + 2);
        }

        return new WrappedSection(heading.Key, section.Block?.Key, indices, [.. tops.Select(top => top.Key)], MovedBlocks(moved, levels));
    }

    /// <summary>Deletes a detail with its deeper details; with <paramref name="now"/> its text block gets <c>updated::</c>.</summary>
    /// <exception cref="ArgumentException">The key is not that of a detail.</exception>
    public static void DeleteDetail(Book book, Guid detailKey, DateTimeOffset? now = null)
    {
        var element = new BookElement(ElementKind.Detail, detailKey);
        var block = BlockOf(book, element);
        StampOwner(book, element, now);
        book.Page.RemoveBlock(block);
    }

    /// <summary>
    /// Sets the status of a text block or detail: sets the <c>status::</c> line, or removes it for
    /// <see cref="BlockStatus.Draft"/>. Any status but <see cref="BlockStatus.Remark"/> also removes a <c>#notiz</c> tag
    /// (<see cref="NoteTag.Remove"/>); a remark on a block that has <c>#notiz</c> and no <c>status::</c> line changes
    /// nothing. Everything else of the block stays as it is. With <paramref name="now"/> a change of the block gives its
    /// text block (the block itself, or the one a detail belongs to) <c>updated::</c>; an unchanged block gets nothing.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The element is a heading or not in the book, or the text without the tag would not stay one block.
    /// </exception>
    /// <exception cref="InvalidOperationException">The block's code fence is not closed; it has no place for properties.</exception>
    public static void SetStatus(Book book, BookElement element, BlockStatus status, DateTimeOffset? now = null)
    {
        var block = StatusBlockOf(book, element);
        var hasTag = NoteTag.Has(block.Content);
        if (status == BlockStatus.Remark && hasTag && block.GetProperty(BlockStatuses.Key) is null)
        {
            return;
        }

        var content = hasTag && status != BlockStatus.Remark ? NoteTag.Remove(block.Content) : null;
        if (content is not null)
        {
            Block.ValidateContent(content);
        }

        var linesBefore = block.Lines.ToList();
        var wasDirty = block.IsDirty;

        if (BlockStatuses.FileValue(status) is { } value)
        {
            block.SetProperty(BlockStatuses.Key, value);
        }
        else
        {
            block.RemoveProperty(BlockStatuses.Key);
        }

        if (content is not null)
        {
            block.SetContent(content);
        }

        if (!block.Lines.SequenceEqual(linesBefore))
        {
            try
            {
                StampOwner(book, element, now);
            }
            catch (InvalidOperationException)
            {
                // The text block has no place for the date: the status change is refused as a whole.
                block.RestoreLines(linesBefore, wasDirty);
                throw;
            }
        }
    }

    /// <summary>
    /// Adds a remark to the text block of <paramref name="element"/> (the text block itself, or the one a detail belongs
    /// to): a new empty detail of depth 1 with <c>status:: bemerkung</c>, after its last detail. With
    /// <paramref name="now"/> the text block gets <c>updated::</c>.
    /// </summary>
    /// <returns>The <see cref="Block.Key"/> of the new detail.</returns>
    /// <exception cref="ArgumentException">The element is a heading or not in the book.</exception>
    public static Guid AddRemark(Book book, BookElement element, DateTimeOffset? now = null)
    {
        StatusBlockOf(book, element);
        StampOwner(book, element, now);
        var textBlock = BookElements.TextBlockOf(book, element)!.Block;
        var remark = Block.CreateDetached("", [new BlockProperty(BlockStatuses.Key, BlockStatuses.FileValue(BlockStatus.Remark)!)]);
        book.Page.InsertBlock(textBlock, textBlock.Children.Count, remark);
        return remark.Key;
    }

    /// <summary>
    /// Inserts a cross reference <c>((id))</c> to the heading or text block <paramref name="targetKey"/> into the text of
    /// the text block or detail <paramref name="elementKey"/> (spec 2.2), at <paramref name="offset"/> in its content
    /// (the block's Markdown, clamped to it; see <see cref="ContentOffset"/> for an offset the editor reports). The
    /// target gets an <c>id::</c> if it has none, without a date; the referring text block (the element itself, or the
    /// one a detail belongs to) gets <c>updated::</c>. At the start of a line, the line escape (see
    /// <see cref="BlockTextEscape"/>) is written anew for the line with the reference in front.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The element is a heading or not in the book, or the target is not a heading or text block of the book, or it is
    /// the element itself.
    /// </exception>
    /// <exception cref="InvalidOperationException">A block has no place for properties (an open code fence); nothing is changed.</exception>
    public static void InsertReference(Book book, Guid elementKey, int offset, Guid targetKey, DateTimeOffset now)
    {
        var element = ReferringElement(book, elementKey);
        var target = (book.FindNode(targetKey)?.Block ?? book.FindTextBlock(targetKey)?.Block)
                     ?? throw new ArgumentException("The target is not a heading or text block of the book.", nameof(targetKey));
        if (targetKey == elementKey)
        {
            throw new ArgumentException("An element cannot refer to itself.", nameof(targetKey));
        }

        var block = BlockOf(book, element);
        var owner = BookElements.TextBlockOf(book, element)!.Block;
        var saved = new[] { block, owner, target }.Distinct().Select(b => (Block: b, Lines: b.Lines.ToList(), b.IsDirty)).ToList();
        try
        {
            var reference = BlockRefs.Format(target.EnsureId());
            block.SetContent(WithReference(block.Content, Math.Clamp(offset, 0, block.Content.Length), reference));
            BlockDates.StampChanged(owner, now);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            foreach (var (b, lines, isDirty) in saved)
            {
                b.RestoreLines(lines, isDirty);
            }

            throw;
        }
    }

    /// <summary>
    /// The offset in the content of the text block or detail <paramref name="elementKey"/> (as <see cref="InsertReference"/>
    /// takes it) for an offset in its text as the editor shows it: without emphasis markers, line escapes and a
    /// <c>#notiz</c> tag, <c>\*</c> and <c>\_</c> as one character, a line break and a block reference (one node in the
    /// editor) as one. It is the first place where a reference shows at that offset with the text around it unchanged
    /// (also in its emphasis); out of range is clamped.
    /// </summary>
    /// <returns>The offset; <c>null</c> if there is no such place (not expected), and the caller decides where to go.</returns>
    /// <exception cref="ArgumentException">The element is a heading or not in the book.</exception>
    public static int? ContentOffset(Book book, Guid elementKey, int shownOffset)
    {
        var element = ReferringElement(book, elementKey);
        Func<string, string> editorText = element.Kind == ElementKind.TextBlock ? BlockTextEscape.Unescape : BookSnapshot.ParagraphText;
        var content = BlockOf(book, element).Content;
        var shown = InlineMarkdown.ShownChars(editorText(content));
        var at = Math.Clamp(shownOffset, 0, shown.Count);
        var probe = BlockRefs.Format(Guid.Empty);
        for (var m = 0; m <= content.Length; m++)
        {
            // The reference shows as one unit (one node in the editor).
            var trial = InlineMarkdown.ShownChars(editorText(WithReference(content, m, probe)));
            if (trial.Count == shown.Count + 1
                && trial.Take(at).SequenceEqual(shown.Take(at))
                && trial[at].Char == probe
                && trial.Skip(at + 1).SequenceEqual(shown.Skip(at)))
            {
                return m;
            }
        }

        // Not expected: every shown offset has such a place.
        return null;
    }

    /// <summary>The text block or detail that may hold a reference.</summary>
    /// <exception cref="ArgumentException">The key is that of a heading or of no element of the book.</exception>
    private static BookElement ReferringElement(Book book, Guid elementKey) =>
        BookElements.Find(book, elementKey) is { Kind: not ElementKind.Heading } element
            ? element
            : throw new ArgumentException("A reference goes into a text block or a detail of the book.", nameof(elementKey));

    /// <summary>
    /// <paramref name="content"/> with <paramref name="reference"/> at <paramref name="offset"/>; at the start of a line
    /// the line is escaped anew, so an escape the line no longer needs goes (<c>\# x</c> becomes <c>((id))# x</c>).
    /// </summary>
    private static string WithReference(string content, int offset, string reference)
    {
        if (offset > 0 && content[offset - 1] != '\n')
        {
            return content.Insert(offset, reference);
        }

        var end = content.IndexOf('\n', offset);
        end = end < 0 ? content.Length : end;
        var line = BlockTextEscape.Escape(reference + BlockTextEscape.Unescape(content[offset..end]));
        return content[..offset] + line + content[end..];
    }

    /// <summary>The block of a text block or detail; headings have no status.</summary>
    /// <exception cref="ArgumentException">The element is a heading or not in the book.</exception>
    private static Block StatusBlockOf(Book book, BookElement element)
    {
        if (element.Kind == ElementKind.Heading)
        {
            throw new ArgumentException("A heading has no status.", nameof(element));
        }

        return BlockOf(book, element);
    }

    /// <summary>The element's block.</summary>
    /// <exception cref="ArgumentException">No element of this kind has the key.</exception>
    private static Block BlockOf(Book book, BookElement element)
    {
        if (BookElements.Find(book, element.Key) != element)
        {
            throw new ArgumentException($"There is no such {element.Kind} in the book.", nameof(element));
        }

        return element.Kind switch
        {
            ElementKind.Heading => book.FindNode(element.Key)!.Block!,
            ElementKind.TextBlock => book.FindTextBlock(element.Key)!.Block,
            _ => BookElements.TextBlockOf(book, element)!.Paragraphs.Single(p => p.Block.Key == element.Key).Block,
        };
    }

    /// <summary>
    /// Inserts an empty block of <paramref name="kind"/> and stamps it: a text block gets both dates, a detail changes
    /// its text block (<paramref name="reference"/> is the element it is inserted at, or below).
    /// </summary>
    private static Guid Insert(
        Book book, Block? parent, int index, string content, ElementKind kind, BookElement reference, DateTimeOffset? now)
    {
        var block = Block.CreateDetached(content);
        if (now is { } time && kind == ElementKind.TextBlock)
        {
            BlockDates.StampNew(block, time);
        }
        else if (kind == ElementKind.Detail)
        {
            StampOwner(book, reference, now);
        }

        book.Page.InsertBlock(parent, index, block);
        return block.Key;
    }

    /// <summary>Stamps <c>updated::</c> on the text block of a text block or detail element; nothing without a time.</summary>
    private static void StampOwner(Book book, BookElement element, DateTimeOffset? now)
    {
        if (now is { } time && BookElements.TextBlockOf(book, element) is { } owner)
        {
            BlockDates.StampChanged(owner.Block, time);
        }
    }

    private static string EmptyHeading(int level) => HeadingText.WithTitle("", level, "");

    /// <summary>The index of <paramref name="block"/> among its siblings (for a root block: among the page's roots).</summary>
    private static int IndexOf(Page page, Block block)
    {
        var siblings = block.Parent?.Children ?? page.Roots;
        var index = 0;
        while (siblings[index] != block)
        {
            index++;
        }

        return index;
    }

    /// <summary>The last heading of the same level before <paramref name="node"/> in its section, or <c>null</c>.</summary>
    private static OutlineNode? PreviousSameLevel(OutlineNode node) =>
        node.Parent!.Children.TakeWhile(sibling => sibling != node).LastOrDefault(sibling => sibling.Level == node.Level);

    private static bool CanOutdent(OutlineNode node) => node.Level > 1 && node.Parent is { Block: not null };

    /// <summary>The block and all blocks below it, depth-first in file order.</summary>
    private static IEnumerable<Block> Subtree(Block block) => block.Children.SelectMany(Subtree).Prepend(block);

    /// <summary>Records the level of <paramref name="node"/> and its sub-headings before and after <c>Relevel(node, level)</c>.</summary>
    private static void NewLevels(OutlineNode node, int level, Dictionary<Guid, (int Before, int After)> levels)
    {
        levels[node.Key] = (node.Level, level);
        foreach (var child in node.Children)
        {
            NewLevels(child, level + 1, levels);
        }
    }

    /// <summary>The moved blocks for an undo, each with its lines before and now, its levels and its parent now.</summary>
    private static List<RemovedHeading.MovedBlock> MovedBlocks(
        IEnumerable<(Block Block, List<RawLine> Before)> moved, Dictionary<Guid, (int Before, int After)> levels) =>
        [.. moved.Select(m => new RemovedHeading.MovedBlock(
            m.Block.Key,
            m.Before,
            [.. m.Block.Lines],
            levels.TryGetValue(m.Block.Key, out var level) ? level : null,
            m.Block.Parent?.Key))];
}
