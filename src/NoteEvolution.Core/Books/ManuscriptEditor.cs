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
/// New headings are empty (<c>"#" * level + " "</c>), new text blocks and details are empty blocks. The appendix is
/// no element of the manuscript (<see cref="BookElements.Find"/>): every command refuses it.
/// </summary>
public static partial class ManuscriptEditor
{
    /// <summary>
    /// Adds a new empty element after <paramref name="current"/>: for a heading of level n a level-n heading after its
    /// whole section, for a text block a text block directly after it, for a detail a detail of the same depth after
    /// it and its deeper details. A new top-level heading goes before the appendix.
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
        var index = IndexOf(book.Page, block) + 1;
        if (current.Kind == ElementKind.Heading && block.Parent is null)
        {
            index = book.BeforeAppendix(index);
        }

        return Insert(book, block.Parent, index, content, current.Kind, current, now);
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
    /// heading of the same level (<see cref="OutlineEditor.MoveSection"/>), never of an appendix section.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Unknown heading or one in the appendix, no previous heading of the same level in the same section, or the
    /// subtree would get a heading deeper than level 6.
    /// </exception>
    public static void Indent(Book book, Guid headingKey)
    {
        var node = OutlineEditor.FindHeading(book, headingKey, nameof(headingKey));
        var previous = PreviousSameLevel(node)
                       ?? throw new ArgumentException("There is no previous heading of the same level.", nameof(headingKey));
        OutlineEditor.MoveSection(book, headingKey, previous.Key, previous.Children.Count());
    }

    /// <summary>
    /// Moves the heading one level higher: <see cref="SetHeadingLevel"/> with its level - 1. Every heading of its
    /// subtree gets one <c>#</c> less, and it is placed by its level (<see cref="Place"/>): out of a parent section of
    /// its new level, directly after it, with what followed it there.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Unknown heading, one in the appendix, a level-1 heading, or a sub-heading of an irregular outline would rise
    /// above H1.
    /// </exception>
    public static void Outdent(Book book, Guid headingKey)
    {
        var node = OutlineEditor.FindHeading(book, headingKey, nameof(headingKey));
        if (!CanOutdent(book, headingKey))
        {
            throw new ArgumentException("The heading cannot go one level higher.", nameof(headingKey));
        }

        SetHeadingLevel(book, headingKey, node.Level - 1);
    }

    /// <summary>Whether <see cref="Indent"/> is possible; <c>false</c> for an unknown key.</summary>
    public static bool CanIndent(Book book, Guid headingKey) =>
        book.FindNode(headingKey) is { Block: not null } node
        && !book.IsInAppendix(headingKey)
        && PreviousSameLevel(node) is { } previous
        && previous.Level + OutlineEditor.Height(node) <= OutlineEditor.MaxLevel;

    /// <summary>Whether <see cref="Outdent"/> is possible; <c>false</c> for an unknown key.</summary>
    public static bool CanOutdent(Book book, Guid headingKey) =>
        book.FindNode(headingKey) is { Block: not null, Level: > 1 } node
        && !book.IsInAppendix(headingKey)
        && LevelBlocked(book, new BookElement(ElementKind.Heading, headingKey), node.Level - 1) is null;

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
    /// <exception cref="ArgumentException">Unknown heading, or one in the appendix.</exception>
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
    /// its hashes (its first line escaped, see <see cref="HeadingText.AsTextBlockContent"/>), with the heading's raw
    /// <c>id::</c> and, with <paramref name="now"/>, both dates; <c>null</c> for an empty title without id.
    /// </summary>
    private static Block? TitleBlock(Block heading, int level, DateTimeOffset? now)
    {
        // A title "# …" as text would read as a heading again: escaped, it shows as it did.
        var content = HeadingText.AsTextBlockContent(HeadingText.WithoutMarks(heading.Content, level), shownUnescaped: false);
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
    /// sections not its own), except in the prologue. A heading may take any level, also above its parent heading's
    /// (<see cref="SetHeadingLevel"/> places it out of that section), but not so deep that a sub-heading would fall below
    /// H6, nor so high that a sub-heading of an irregular outline (one that is not deeper) would rise above H1. A
    /// reference in the first line (the title) is no obstacle: <see cref="ToHeading"/> moves it into the text below.
    /// Text is always possible.
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
            return level == 0 || level >= section.Level ? null : "LevelBlockedAboveSection";
        }

        var node = book.FindNode(element.Key)!;
        if (level == 0 || level == node.Level)
        {
            return null;
        }

        var difference = level - node.Level;
        return DeepestLevel(node) + difference > OutlineEditor.MaxLevel ? "LevelBlockedBelowH6"
            : ShallowestLevel(node) + difference < 1 ? "LevelBlockedAboveH1"
            : null;
    }

    /// <summary>
    /// Why the button "Einrücken" (indent) cannot move <paramref name="element"/> (Einordnen spec 4), for its tooltip: the
    /// resource key of the German reason, <c>null</c> if it can. A text block needs a text block directly before it in
    /// its section (<c>IndentBlockedNoPreviousText</c>; a heading between them would change the reading order), a
    /// detail a sibling detail before it (<c>IndentBlockedNoPreviousDetail</c>). A heading goes under the previous
    /// heading of its level (<see cref="CanIndent"/>; <c>IndentBlockedNoPreviousHeading</c>), but not so deep that its
    /// subtree falls below H6 (<c>LevelBlockedBelowH6</c>); an H6 itself cannot go deeper (<c>IndentBlockedH6</c>).
    /// </summary>
    /// <exception cref="ArgumentException">The element is not in the book.</exception>
    public static string? IndentBlocked(Book book, BookElement element)
    {
        if (BookElements.Find(book, element.Key) != element)
        {
            throw new ArgumentException($"There is no such {element.Kind} in the book.", nameof(element));
        }

        switch (element.Kind)
        {
            case ElementKind.TextBlock:
                return PreviousTextBlock(book.FindTextBlock(element.Key)!) is null ? "IndentBlockedNoPreviousText" : null;
            case ElementKind.Detail:
                var detail = BlockOf(book, element);
                return detail.Parent!.Children[0] == detail ? "IndentBlockedNoPreviousDetail" : null;
            default:
                var node = book.FindNode(element.Key)!;
                return CanIndent(book, element.Key) ? null
                    : node.Level == OutlineEditor.MaxLevel ? "IndentBlockedH6"
                    : PreviousSameLevel(node) is null ? "IndentBlockedNoPreviousHeading"
                    : "LevelBlockedBelowH6";
        }
    }

    /// <summary>
    /// Why the button "Ausrücken" (outdent) cannot move <paramref name="element"/> (Einordnen spec 4), for its tooltip:
    /// the resource key of the German reason, <c>null</c> if it can. A text block cannot (<c>OutdentBlockedTextBlock</c>),
    /// a detail always can, a heading by the reasons of <see cref="LevelBlocked"/> for its level - 1 (a level-1 heading
    /// itself cannot: <c>OutdentBlockedH1</c>).
    /// </summary>
    /// <exception cref="ArgumentException">The element is not in the book.</exception>
    public static string? OutdentBlocked(Book book, BookElement element)
    {
        if (BookElements.Find(book, element.Key) != element)
        {
            throw new ArgumentException($"There is no such {element.Kind} in the book.", nameof(element));
        }

        if (element.Kind != ElementKind.Heading)
        {
            return element.Kind == ElementKind.TextBlock ? "OutdentBlockedTextBlock" : null;
        }

        var level = book.FindNode(element.Key)!.Level;
        return level == 1 ? "OutdentBlockedH1" : LevelBlocked(book, element, level - 1);
    }

    /// <summary>The text block directly before <paramref name="textBlock"/> in its section; <c>null</c> if an outline node or nothing is.</summary>
    private static TextBlock? PreviousTextBlock(TextBlock textBlock)
    {
        var items = textBlock.Section.Items;
        var index = 0;
        while (items[index] != textBlock)
        {
            index++;
        }

        return index > 0 ? items[index - 1] as TextBlock : null;
    }

    /// <summary>
    /// Makes the text block <paramref name="textBlockKey"/> the last sub-point (detail, level 1) of the text block
    /// directly before it in its section (Einordnen spec 4); its own details stay its details, so the reading order
    /// stays. The block keeps everything it has (<c>id::</c>, <c>source::</c>, status, dates); the receiving text block
    /// gets <c>updated::</c>. References <c>((id))</c> to the block in the page's texts are retargeted to the receiving
    /// text block, which gets an <c>id::</c> if it has none (a detail is no cross-reference target).
    /// </summary>
    /// <returns>The key of the new detail, which is that of the text block.</returns>
    /// <exception cref="ArgumentException">
    /// Not a text block of the book, or <see cref="IndentBlocked"/> refuses it (no text block before it).
    /// </exception>
    /// <exception cref="InvalidOperationException">The page is read-only; nothing is changed.</exception>
    public static Guid IndentTextBlock(Book book, Guid textBlockKey, DateTimeOffset now)
    {
        var element = BookElements.Find(book, textBlockKey) is { Kind: ElementKind.TextBlock } found
            ? found
            : throw new ArgumentException("Only a text block of the book is indented into another.", nameof(textBlockKey));
        if (IndentBlocked(book, element) is { } reason)
        {
            throw new ArgumentException(reason, nameof(textBlockKey));
        }

        var page = book.Page;
        if (page.IsReadOnly)
        {
            throw new InvalidOperationException("The page is read-only.");
        }

        var textBlock = book.FindTextBlock(textBlockKey)!;
        var previous = PreviousTextBlock(textBlock)!;
        page.MoveBlock(textBlock.Block, previous.Block, previous.Block.Children.Count);
        BlockDates.StampChanged(previous.Block, now);

        // A detail is no reference target: cross references to the block now point to the text block it went into
        // (which gets an id for that), as after "Zusammenfügen". Its used-in entries keep naming its own id.
        if (textBlock.Block.Id is { } id && page.AllBlocks().Any(b => BlockRefs.Find(b.Content).Any(r => r.Id == id)))
        {
            var map = new Dictionary<Guid, Guid> { [id] = previous.Block.EnsureId() };
            foreach (var block in page.AllBlocks().ToList())
            {
                if (BlockRefs.Find(block.Content).Any(r => map.ContainsKey(r.Id)))
                {
                    block.SetContent(BlockRefs.Retarget(block.Content, map));
                }
            }
        }

        return textBlockKey;
    }

    /// <summary>
    /// Makes the level-1 detail <paramref name="detailKey"/> a text block of its own right after its text block (Einordnen
    /// spec 4). Its later sibling details (level 1) go along as the last children of the new text block, so the reading
    /// order stays. The new text block gets both dates (<c>updated::</c> only if it has <c>created::</c>), the old one
    /// <c>updated::</c>. <c>source::</c> stays with the old text block; the detail keeps its own <c>id::</c> and
    /// <c>source::</c>. A first line that would read as a heading is escaped, so it stays text.
    /// </summary>
    /// <returns>The key of the new text block, which is that of the detail.</returns>
    /// <exception cref="ArgumentException">Not a detail of the book, or one deeper than level 1 (the editor moves those).</exception>
    /// <exception cref="InvalidOperationException">The page is read-only; nothing is changed.</exception>
    public static Guid OutdentDetail(Book book, Guid detailKey, DateTimeOffset now)
    {
        var element = BookElements.Find(book, detailKey) is { Kind: ElementKind.Detail } found
            ? found
            : throw new ArgumentException("Only a detail of the book becomes a text block.", nameof(detailKey));
        var detail = BlockOf(book, element);
        var textBlock = BookElements.TextBlockOf(book, element)!.Block;
        if (detail.Parent != textBlock)
        {
            throw new ArgumentException("Only a detail of level 1 becomes a text block.", nameof(detailKey));
        }

        var page = book.Page;
        if (page.IsReadOnly)
        {
            throw new InvalidOperationException("The page is read-only.");
        }

        var following = textBlock.Children.SkipWhile(child => child != detail).Skip(1).ToList();

        // As a text block a first line "# …" would read as a heading: escaped, it shows as it did.
        if (HeadingText.AsTextBlockContent(detail.Content, shownUnescaped: true) is var escaped && escaped != detail.Content)
        {
            detail.SetContent(escaped);
        }

        page.MoveBlock(detail, textBlock.Parent, IndexOf(page, textBlock) + 1);
        foreach (var sibling in following)
        {
            page.MoveBlock(sibling, detail, detail.Children.Count);
        }

        StampAsTextBlock(detail, now);
        BlockDates.StampChanged(textBlock, now);
        return detailKey;
    }

    /// <summary>
    /// Makes the text block or detail <paramref name="elementKey"/> a heading of <paramref name="level"/> (spec 4.2). The
    /// block's first line (as the editor shows it, without heading marks and a <c>#notiz</c> tag) is the title; the
    /// further text becomes the first text block under it, and each direct sub-point a text block with its deeper
    /// sub-points as details (a sub-point "# …" escaped, so it stays text). For a detail, so that the reading order stays,
    /// what follows it in its text block (its later sibling sub-points, then the later siblings of each sub-point above
    /// it) goes under the heading after its own, as text blocks in the same way; but one that reads as a heading (a
    /// heading inside the text block) stays a heading, unescaped with its sub-points, and is placed by its level
    /// (<see cref="Place"/>). The heading stands in the block's place,
    /// a detail's heading right after its text block; above the section's level inside the section, at its level after
    /// the section. The section's items after the text block go along as <see cref="TakeAlong"/> puts them: up to the
    /// next heading that is not deeper they become the heading's last children, that one and the rest follow the heading
    /// (at the section's level they leave the section with it). The block's properties (<c>id::</c>, <c>source::</c>,
    /// the status, also from a <c>#notiz</c> tag, and any other, <c>created::</c> included) go to the first text block:
    /// the block itself keeps them with the further text, else the first sub-point takes them if it has none of the same
    /// keys, else the block stays as an empty text block with them. A block with no further text and no properties but its dates goes
    /// (a heading has no dates). Each text block made gets both dates (<c>updated::</c> only if it has
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
    public static Guid ToHeading(Book book, Guid elementKey, int level, DateTimeOffset now) =>
        ToHeadingPlacing(book, elementKey, level, now, []);

    /// <summary>
    /// <see cref="ToHeading"/>; the keys of the later sub-points that were headings
    /// inside the text block and are placed as headings now are added to <paramref name="placedHeadings"/>.
    /// </summary>
    private static Guid ToHeadingPlacing(Book book, Guid elementKey, int level, DateTimeOffset now, List<Guid> placedHeadings)
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
        var (title, further) = HeadingSplit(block);

        // A heading holds no reference: the title's references and citations open the text below it, on a line of their own.
        if (TakeReferences(title) is ({ } plain, { } references))
        {
            title = plain;
            further = further is null ? references : references + "\n" + further;
        }

        if (further is not null)
        {
            // Escaped anew as the editor saves it, so that no line of it reads as structure as the first one of a block.
            further = BlockTextEscape.Escape(further);
            Block.ValidateContent(further);
        }

        // Every property line goes to the first text block; the tag as a status.
        var carried = block.Properties.ToList();
        if (hasTag && block.GetProperty(BlockStatuses.Key) is null)
        {
            carried.Add(new BlockProperty(BlockStatuses.Key, BlockStatuses.FileValue(BlockStatus.Remark)!));
        }

        // Dates alone need no text block: they belong to the text, which is the heading's title now.
        var needsCarrier = carried.Any(property => !IsDate(property.Key));
        var subPoints = block.Children.ToList();
        var keepBlock = further is not null
                        || (needsCarrier && (subPoints.Count == 0 || Collides(subPoints[0], carried)));
        var following = section.Items
            .SkipWhile(item => item != textBlock)
            .Skip(1)
            .Select(item => item is TextBlock followingText ? followingText.Block : ((OutlineNode)item).Block!)
            .ToList();

        // What follows a detail in its text block, its own siblings first, then those of each sub-point above it.
        var laterSubPoints = new List<Block>();
        for (var below = block; below != textBlock.Block; below = below.Parent!)
        {
            laterSubPoints.AddRange(below.Parent!.Children.SkipWhile(child => child != below).Skip(1));
        }

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
        else if (needsCarrier)
        {
            // updated:: is stamped anew below.
            foreach (var property in carried.Where(property => !IsKey(property, BlockDates.UpdatedKey)))
            {
                subPoints[0].SetProperty(property.Key, property.Value);
            }
        }

        var laterHeadings = new List<Block>();
        foreach (var subPoint in subPoints.Concat(laterSubPoints))
        {
            if (laterSubPoints.Contains(subPoint) && HeadingText.StartsAsHeading(subPoint.Content))
            {
                // A later sub-point that reads as a heading is one (it was warned inside the text block): it goes as it
                // is, with its sub-points, and is placed by its level below.
                laterHeadings.Add(subPoint);
                page.MoveBlock(subPoint, heading, position++);
                continue;
            }

            // As a text block a sub-point "# …" would read as a heading: escaped, it shows as it did.
            if (HeadingText.AsTextBlockContent(subPoint.Content, shownUnescaped: true) is var escaped && escaped != subPoint.Content)
            {
                subPoint.SetContent(escaped);
            }

            StampAsTextBlock(subPoint, now);
            page.MoveBlock(subPoint, heading, position++);
        }

        if (!keepBlock)
        {
            page.RemoveBlock(block);
        }

        TakeAlong(book, heading, level, following);

        if (element.Kind == ElementKind.Detail)
        {
            BlockDates.StampChanged(textBlock.Block, now);
        }

        foreach (var later in laterHeadings)
        {
            var view = Book.Load(page);
            Place(view, view.FindNode(later.Key)!);
            placedHeadings.Add(later.Key);
        }

        return heading.Key;
    }

    /// <summary>
    /// Gives the heading <paramref name="headingKey"/> the level <paramref name="level"/> (1–6, spec 4.2); every heading
    /// of its subtree shifts by the same difference, so the outline below it stays as it is. Then the heading is placed
    /// by its level (<see cref="Place"/>), also when the level stays the same; the reading order stays as it is.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Unknown heading or one in the appendix, or <see cref="LevelBlocked"/> refuses the level (also Text).
    /// </exception>
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
        Place(book, node);
    }

    /// <summary>
    /// Places the heading <paramref name="node"/> where its level (as its <c>#</c> marks stand now) puts it (Einordnen
    /// spec 2), keeping the reading order of every block. Out: while its level is not deeper than its parent heading's,
    /// it goes directly after that parent, into the parent's section, and the blocks that followed it in the parent go
    /// along as their Markdown meaning puts them (<see cref="TakeAlong"/>). In: while the block directly before it in its
    /// section is a heading of a higher level (smaller number), not an appendix section, it becomes that heading's last
    /// child. The levels do not change; the
    /// appendix is never a parent.
    /// </summary>
    /// <param name="book">The book; its view may be stale (e.g. after the marks changed), the page is read anew.</param>
    internal static void Place(Book book, OutlineNode node)
    {
        var page = book.Page;
        var key = node.Key;
        while (Book.Load(page) is var view
               && view.FindNode(key) is { Parent: { Block: { } parent } parentNode } current
               && current.Level <= parentNode.Level)
        {
            var block = current.Block!;
            var following = parent.Children.SkipWhile(child => child != block).Skip(1).ToList();
            page.MoveBlock(block, parent.Parent, IndexOf(page, parent) + 1);
            TakeAlong(view, block, current.Level, following);
        }

        while (true)
        {
            var current = Book.Load(page);
            var heading = current.FindNode(key)!;
            var block = heading.Block!;
            var index = IndexOf(page, block);
            var siblings = block.Parent?.Children ?? page.Roots;
            if (index == 0
                || current.FindNode(siblings[index - 1].Key) is not { Block: { } previous } previousNode
                || previousNode.Level >= heading.Level
                || current.IsInAppendix(previous.Key))
            {
                return;
            }

            page.MoveBlock(block, previous, previous.Children.Count);
        }
    }

    /// <summary>
    /// Moves <paramref name="following"/>, the blocks that came directly after the heading <paramref name="heading"/> of
    /// <paramref name="level"/> in reading order, where their Markdown meaning puts them: the text blocks and headings
    /// deeper than <paramref name="level"/> up to the first heading that is not deeper (or an appendix section, which
    /// never goes into the text) become the heading's last children; that heading and every block after it follow the
    /// heading as its siblings, in order.
    /// </summary>
    /// <param name="view">A view that knows the following blocks (it may be stale: their levels have not changed).</param>
    private static void TakeAlong(Book view, Block heading, int level, IReadOnlyList<Block> following)
    {
        var page = view.Page;
        var taken = following
            .TakeWhile(block => view.FindNode(block.Key) is not { } node
                                || (node.Level > level && node.AppendixKind == AppendixKind.None))
            .Count();
        foreach (var item in following.Take(taken))
        {
            page.MoveBlock(item, heading, heading.Children.Count);
        }

        var index = IndexOf(page, heading) + 1;
        foreach (var item in following.Skip(taken))
        {
            page.MoveBlock(item, heading.Parent, index++);
        }
    }

    /// <summary>
    /// The headings <see cref="NormalizeOutline"/> places, in reading order: of the headings with an indentation warning
    /// (<see cref="Book.Warnings"/>: not deeper than their parent heading, or inside a text block) those it moves or
    /// makes a heading. Left out are the appendix (it is not changed) and those that a conversion before them turns into
    /// text (a sub-point of a converted heading). The command is tried on a copy of the page; the book is not changed.
    /// </summary>
    public static IReadOnlyList<Guid> MisplacedHeadings(Book book) => TryNormalize(book);

    /// <summary>
    /// Places every heading with an indentation warning outside the appendix, in reading order (Einordnen spec 3): a
    /// heading inside a text block becomes a heading of the level of its marks (<see cref="ToHeading"/>), or of its
    /// section's level where that level is above its section, and then placed by its own level (<see cref="SetHeadingLevel"/>); every other heading keeps its level and
    /// is placed by it (<see cref="SetHeadingLevel"/>). A heading that is no longer warned when its turn comes (a
    /// sub-point of a converted one, which became a text block, or a later heading in the same text block, which that
    /// conversion placed and which counts with it) is skipped. A reference in such a heading's title moves into the text below it (<see cref="ToHeading"/>). Afterwards the book's text has no indentation warnings. All or
    /// nothing: if a step throws, the page gets its state before the command back.
    /// </summary>
    /// <param name="now">The dates for the text blocks <see cref="ToHeading"/> makes.</param>
    /// <returns>The number of headings placed: as many as <see cref="MisplacedHeadings"/> lists.</returns>
    /// <exception cref="ArgumentException">A conversion is refused (not expected: every step validates first).</exception>
    /// <exception cref="InvalidOperationException">
    /// The page is read-only (an open code fence): it is refused before anything changes. A step that throws it later
    /// (as for <see cref="ToHeading"/>) is not expected.
    /// </exception>
    public static int NormalizeOutline(Book book, DateTimeOffset now)
    {
        var page = book.Page;
        var before = PageStructureSnapshot.Capture(page);
        var placed = new List<Guid>();
        try
        {
            PlaceWarned(book, now, placed);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            before.RestoreInto(page);
            throw;
        }

        return placed.Count;
    }

    /// <summary>The steps of <see cref="NormalizeOutline"/>; the key of each heading placed is added to <paramref name="placed"/>.</summary>
    private static void PlaceWarned(Book book, DateTimeOffset now, List<Guid> placed)
    {
        foreach (var key in Warned(book))
        {
            // Blocks moved in the steps before: the view is read anew for each.
            var current = Book.Load(book.Page);
            if (placed.Contains(key) || !Warned(current).Contains(key))
            {
                continue;
            }

            if (current.FindNode(key) is { } node)
            {
                SetHeadingLevel(current, key, node.Level);
                placed.Add(key);
                continue;
            }

            var element = BookElements.Find(current, key)!;
            // A warned block inside a text block starts with 1–6 marks and a space (Book reads it so).
            var level = BlockOf(current, element).Content.TakeWhile(c => c == '#').Count();
            // Above its section's level the heading is made at that level (its section's, which it may take) and then
            // set to its own, which places it out of the section where its marks put it (the reading order stays).
            var ownLevel = level;
            switch (LevelBlocked(current, element, level))
            {
                case "LevelBlockedAboveSection":
                    level = BookElements.TextBlockOf(current, element)!.Section.Level;
                    break;
                case not null:
                    continue;
            }

            var alongWith = new List<Guid>();
            var made = ToHeadingPlacing(current, key, level, now, alongWith);
            if (ownLevel != level)
            {
                SetHeadingLevel(Book.Load(book.Page), made, ownLevel);
            }

            placed.Add(key);
            placed.AddRange(alongWith);
        }
    }

    /// <summary>The headings with an indentation warning outside the appendix, in reading order.</summary>
    private static List<Guid> Warned(Book book) =>
        [.. book.Warnings.Select(warning => warning.BlockKey).Where(key => !book.IsInAppendix(key)).Distinct()];

    /// <summary>
    /// Runs the steps of <see cref="NormalizeOutline"/> on a copy of the page (read anew from its bytes, so every block
    /// stands at the same place) and maps the result back to the book's keys: the headings placed, and the warned ones
    /// left. If a step fails, the steps before it count (the command itself would then change nothing). A read-only page
    /// gets neither: the command refuses it.
    /// </summary>
    private static List<Guid> TryNormalize(Book book)
    {
        if (book.Page.IsReadOnly)
        {
            return [];
        }

        var copy = Book.Load(LogseqParser.Parse(book.Page.FilePath, PageSerializer.Serialize(book.Page)));
        var keys = copy.Page.AllBlocks().Zip(book.Page.AllBlocks()).ToDictionary(pair => pair.First.Key, pair => pair.Second.Key);
        var placed = new List<Guid>();
        try
        {
            PlaceWarned(copy, DateTimeOffset.UnixEpoch, placed);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // NormalizeOutline throws there and changes nothing; the preview shows what came before.
        }

        return [.. placed.Select(key => keys[key])];
    }

    /// <summary>
    /// The title and further text of a heading made from <paramref name="block"/> (<see cref="HeadingText.Split"/>), from
    /// its text as the editor shows it: unescaped, without a <c>#notiz</c> tag.
    /// </summary>
    private static (string Title, string? Further) HeadingSplit(Block block) =>
        HeadingText.Split(BlockTextEscape.Unescape(NoteTag.Has(block.Content) ? NoteTag.Remove(block.Content) : block.Content));

    [System.Text.RegularExpressions.GeneratedRegex(@" ([,.;:!?)\]])")]
    private static partial System.Text.RegularExpressions.Regex SpaceBeforePunctuation();

    /// <summary>
    /// The title without its block references and source citations, and those (as written, in their order, joined by a
    /// space); <c>(null, null)</c> if the title has none. Spaces left around a removed one are collapsed, a space left
    /// before a punctuation mark is dropped, and so is a comma, semicolon or colon left at the start.
    /// </summary>
    private static (string? Plain, string? References) TakeReferences(string title)
    {
        var spans = BlockRefs.Find(title).Select(r => (r.Start, r.Length))
            .Concat(SourceCitations.Find(title).Select(c => (c.Start, c.Length)))
            .OrderBy(span => span.Start)
            .ToList();
        if (spans.Count == 0)
        {
            return (null, null);
        }

        var plain = new System.Text.StringBuilder();
        var position = 0;
        foreach (var (start, length) in spans)
        {
            plain.Append(title, position, start - position);
            position = start + length;
        }

        plain.Append(title, position, title.Length - position);
        var collapsed = string.Join(' ', plain.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        // A removed reference leaves no space before a punctuation mark that followed it, nor one at the start.
        collapsed = SpaceBeforePunctuation().Replace(collapsed, "$1").TrimStart(',', ';', ':', ' ');
        return (collapsed, string.Join(' ', spans.Select(span => title.Substring(span.Start, span.Length))));
    }

    private static bool IsKey(BlockProperty property, string key) => property.Key.Equals(key, StringComparison.OrdinalIgnoreCase);

    private static bool IsDate(string key) =>
        key.Equals(BlockDates.CreatedKey, StringComparison.OrdinalIgnoreCase)
        || key.Equals(BlockDates.UpdatedKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the sub-point <paramref name="block"/> has a property of <paramref name="carried"/> of its own (but
    /// <c>updated::</c>, which is stamped anew), or a <c>#notiz</c> tag where a status is carried: it cannot take them.
    /// </summary>
    private static bool Collides(Block block, List<BlockProperty> carried) =>
        carried.Any(property => !IsKey(property, BlockDates.UpdatedKey)
                                && (block.GetProperty(property.Key) is not null
                                    || (IsKey(property, BlockStatuses.Key) && NoteTag.Has(block.Content))));

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

    /// <summary>The highest level (smallest number) of a heading in the subtree of <paramref name="node"/>, the node included.</summary>
    private static int ShallowestLevel(OutlineNode node) => node.Children.Select(ShallowestLevel).Append(node.Level).Min();

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

        Insert(book, element, offset, now, target, () => BlockRefs.Format(target.EnsureId()));
    }

    /// <summary>
    /// Inserts <paramref name="markdown"/> (e.g. a reference to a person <c>((id))</c> or a source citation
    /// <c>{{quelle ((id))}}</c>) into the text of the text block or detail <paramref name="elementKey"/>, at
    /// <paramref name="offset"/> in its content, as <see cref="InsertReference"/> inserts a cross reference: clamped to
    /// the content, the line escaped anew at the start of a line, the referring text block stamped <c>updated::</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The element is a heading or not in the book.</exception>
    /// <exception cref="InvalidOperationException">A block has no place for properties (an open code fence); nothing is changed.</exception>
    public static void InsertText(Book book, Guid elementKey, int offset, string markdown, DateTimeOffset now) =>
        Insert(book, ReferringElement(book, elementKey), offset, now, null, () => markdown);

    /// <summary>
    /// Inserts a reference to the appendix entry <paramref name="entryKey"/> (its <see cref="Block.Key"/>) into the text
    /// of the text block or detail <paramref name="elementKey"/>, as <see cref="InsertReference"/> inserts a cross
    /// reference: for a person (<see cref="AppendixKind.People"/>) <c>((id))</c>, for a source the citation
    /// <c>{{quelle ((id)) Fundstelle}}</c> with <paramref name="locator"/> (<see cref="SourceCitations.Format"/>). An entry
    /// written in Logseq without an <c>id::</c> gets one now, without a date, in the same change.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is neither people nor sources.</exception>
    /// <exception cref="ArgumentException">
    /// The element is a heading or not in the book, or the section of <paramref name="kind"/> has no such entry.
    /// </exception>
    /// <exception cref="InvalidOperationException">A block has no place for properties (an open code fence); nothing is changed.</exception>
    public static void InsertAppendixRef(
        Book book, Guid elementKey, int offset, AppendixKind kind, Guid entryKey, string? locator, DateTimeOffset now)
    {
        if (kind is not (AppendixKind.People or AppendixKind.Sources))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only people and sources are referred to in the text.");
        }

        var element = ReferringElement(book, elementKey);
        var entry = book.AppendixSection(kind)?.TextBlocks.FirstOrDefault(text => text.Key == entryKey)?.Block
                    ?? throw new ArgumentException("The appendix has no such entry.", nameof(entryKey));
        Insert(book, element, offset, now, entry, () => kind == AppendixKind.People
            ? BlockRefs.Format(entry.EnsureId())
            : SourceCitations.Format(entry.EnsureId(), locator));
    }

    /// <summary>
    /// The source citation the editor shows at <paramref name="shownOffset"/> of the text block or detail
    /// <paramref name="elementKey"/> (see <see cref="ContentOffset"/>), if it cites <paramref name="sourceId"/>; its span
    /// is in the block's content. <c>null</c> if there is none.
    /// </summary>
    /// <exception cref="ArgumentException">The element is a heading or not in the book.</exception>
    public static SourceCitation? CitationAt(Book book, Guid elementKey, int shownOffset, Guid sourceId)
    {
        var element = ReferringElement(book, elementKey);
        var content = BlockOf(book, element).Content;
        // The first place that shows something at the offset is at or before the citation (before emphasis around it).
        var from = ContentOffset(book, elementKey, shownOffset) ?? 0;
        foreach (var citation in SourceCitations.Find(content))
        {
            if (citation.Start >= from && citation.Id == sourceId)
            {
                return citation;
            }
        }

        return null;
    }

    /// <summary>
    /// Gives the source citation at <paramref name="shownOffset"/> (see <see cref="CitationAt"/>) the locator
    /// <paramref name="locator"/> (none if empty, see <see cref="SourceCitations.Format"/>); the referring text block
    /// gets <c>updated::</c> if the text changed.
    /// </summary>
    /// <exception cref="ArgumentException">The element is a heading or not in the book, or there is no such citation.</exception>
    /// <exception cref="InvalidOperationException">A block has no place for properties (an open code fence); nothing is changed.</exception>
    public static void SetCitationLocator(Book book, Guid elementKey, int shownOffset, Guid sourceId, string? locator, DateTimeOffset now)
    {
        var citation = CitationAt(book, elementKey, shownOffset, sourceId)
                       ?? throw new ArgumentException("The element shows no citation of the source there.", nameof(shownOffset));
        var element = ReferringElement(book, elementKey);
        var block = BlockOf(book, element);
        var content = block.Content;
        var changed = content[..citation.Start] + SourceCitations.Format(sourceId, locator) + content[(citation.Start + citation.Length)..];
        if (changed == content)
        {
            return;
        }

        var owner = BookElements.TextBlockOf(book, element)!.Block;
        Changing([block, owner], () =>
        {
            block.SetContent(changed);
            BlockDates.StampChanged(owner, now);
        });
    }

    /// <summary>
    /// Puts <paramref name="markdown"/> (computed once nothing failed before) into the element's content at
    /// <paramref name="offset"/> and stamps its text block; <paramref name="target"/> is a further block the text may
    /// change (the target of a cross reference getting an <c>id::</c>).
    /// </summary>
    private static void Insert(Book book, BookElement element, int offset, DateTimeOffset now, Block? target, Func<string> markdown)
    {
        var block = BlockOf(book, element);
        var owner = BookElements.TextBlockOf(book, element)!.Block;
        Changing(target is null ? [block, owner] : [block, owner, target], () =>
        {
            var text = markdown();
            block.SetContent(WithReference(block.Content, Math.Clamp(offset, 0, block.Content.Length), text));
            BlockDates.StampChanged(owner, now);
        });
    }

    /// <summary>Runs <paramref name="change"/>; if it throws, the blocks get their lines back.</summary>
    private static void Changing(Block[] blocks, Action change)
    {
        var saved = blocks.Distinct().Select(b => (Block: b, Lines: b.Lines.ToList(), b.IsDirty)).ToList();
        try
        {
            change();
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

    /// <summary>
    /// The last heading of the same level before <paramref name="node"/> in its section, or <c>null</c>; an appendix
    /// section does not count.
    /// </summary>
    private static OutlineNode? PreviousSameLevel(OutlineNode node) =>
        node.Parent!.Children.TakeWhile(sibling => sibling != node)
            .LastOrDefault(sibling => sibling.Level == node.Level && sibling.AppendixKind == AppendixKind.None);

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
