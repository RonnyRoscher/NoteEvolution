using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Links;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Storage;
using NoteEvolution.UI.Editor;

namespace NoteEvolution.UI.State;

/// <summary>
/// The structure commands of the manuscript (spec 3) on the element at the editor cursor
/// (<see cref="AppState.CurrentElement"/>), and Merge and Wrap on the marked range (<see cref="AppState.CurrentRange"/>,
/// package B, spec 4, 5), shared by the editor's shortcuts and the buttons of the marking. Every command is one entry
/// of the session's undo stack.
/// </summary>
public static class ManuscriptCommands
{
    /// <summary>
    /// Whether <paramref name="command"/> is possible now: the book is writable, no conflict is open for it (R26), and
    /// the command fits the cursor element (e.g. no indent without a previous heading of the same level, no outdent
    /// on level 1, indent, outdent and remove only on headings, ToggleRemark and AddRemark only on a text block or
    /// detail). Delete and ToggleRemark only take the element the cursor itself is in (<see cref="TargetOf"/>). An
    /// irregular section (a heading in it that is not deeper) is no exception: the editor marks what the book tree holds
    /// (package B), which is what the commands act on. At range level −1 Delete, Indent and Outdent are not possible on an element with sub-elements (<see cref="HasSubElements"/>): the box marks the
    /// element alone, but they would take its sub-elements along. Merge needs a group of at least two text blocks in the
    /// marked range (<see cref="RangeInfo.MergeGroups"/>) or a text block or detail with its details as the range that
    /// has a detail to merge into its text (<see cref="RangeInfo.MergesDetails"/>), Wrap a range head that
    /// <see cref="ManuscriptEditor.CanWrap"/> takes (not the whole book, no detail).
    /// </summary>
    public static bool CanRun(AppState state, SectionCommand command) =>
        state is { Session: { } session, CurrentBook: { Page.IsReadOnly: false } book }
        && !session.HasOpenConflict(book.Page.FilePath)
        && Applies(state, book, command);

    /// <summary>
    /// Runs <paramref name="command"/> on the cursor element. The editor saves its text first; text it could not save
    /// refuses the command before anything changes. A read-only book or an open conflict refuses it too; a command
    /// that does not fit the element (<see cref="CanRun"/>) does nothing. The structure commands save the page through
    /// <see cref="VaultSession.TrySave"/> and push an undo action (<paramref name="undoDescription"/>) that is refused
    /// once the page changed after the command; removing a heading gets a targeted undo instead
    /// (<see cref="RemovedHeading"/>), which later edits elsewhere do not block. Deleting a text block, a heading or a
    /// detail with linked blocks goes through the link service, which also removes the usages from the notes; it is
    /// refused while a conflict is open for one of those notes' pages (R26). Merge goes through the link service as well
    /// (with its own undo action), refused the same way for the note pages of the merged blocks' sources, and refused
    /// with <c>SectionMergeRefused</c> when the link service refuses it (<see cref="MergeRefusedException"/>); it reveals
    /// the first remaining block. On a text block or detail range Merge merges its details into its text instead
    /// (<see cref="ILinkService.MergeDetails"/>, refused the same way for the note pages of the blocks in its subtree)
    /// and reveals the head. Wrap gets a targeted undo (<see cref="WrapUndo"/>) and reveals the new heading. Merge
    /// and Wrap do nothing when saving the editor's text changed the marked range (another head or level, e.g. the
    /// cursor's detail the book did not know before). ToggleRemark makes the text block or detail at the cursor a
    /// remark, or a draft again if it is one (<see cref="ManuscriptEditor.SetStatus"/>), and reveals nothing (the
    /// cursor stays); AddRemark adds an empty remark detail to the cursor's text block
    /// (<see cref="ManuscriptEditor.AddRemark"/>) and reveals it. Both save the page with a <see cref="StructureUndo"/>
    /// and never go through the structure commands' switch, so neither can delete a detail. Whatever
    /// happens, the book view is taken from the vault again, the new or moved element is revealed (the cursor goes
    /// there, the range back to level 0) and <see cref="AppState.Notify"/> is raised.
    /// </summary>
    /// <returns>The message to show, or <c>null</c> if the command ran (or did nothing) without remark.</returns>
    public static async Task<AdoptMessage?> RunAsync(AppState state, SectionCommand command, string undoDescription, ILogger logger)
    {
        Guid? reveal = null;
        try
        {
            (var message, reveal) = await TryRunAsync(state, command, undoDescription, logger);
            return message;
        }
        finally
        {
            // On success the book changed; after a failure the vault holds the page as it is on disk again.
            state.RefreshBook();
            if (reveal is { } key)
            {
                state.RevealElement(key);
            }

            state.Notify();
        }
    }

    /// <summary>
    /// The status of the text block or detail with the cursor's own key in the current book (as for Delete, never the
    /// fallback element), which the status selection shows and <see cref="SetStatusAsync"/> changes; <c>null</c> on a
    /// heading or without such an element.
    /// </summary>
    public static BlockStatus? StatusAt(AppState state) =>
        state.CurrentBook is { } book && StatusTargetOf(state, book) is { } element
            ? BlockStatuses.Of(BlockOf(book, element))
            : null;

    /// <summary>
    /// Whether <see cref="SetStatusAsync"/> is possible now: the book is writable, no conflict is open for it (R26), and
    /// the cursor is in a text block or detail (<see cref="StatusAt"/>).
    /// </summary>
    public static bool CanSetStatus(AppState state) =>
        state is { Session: { } session, CurrentBook: { Page.IsReadOnly: false } book }
        && !session.HasOpenConflict(book.Page.FilePath)
        && StatusTargetOf(state, book) is not null;

    /// <summary>
    /// Sets the status of the text block or detail at the cursor (<see cref="StatusAt"/>) to <paramref name="status"/>
    /// (<see cref="ManuscriptEditor.SetStatus"/>), in the same order as <see cref="RunAsync"/>: the editor saves its
    /// text first, and text it could not save refuses the change; a read-only book or an open conflict refuses it too.
    /// On a heading, without an element at the cursor or when the element already has the status nothing changes.
    /// The page is saved with an undo action (<paramref name="undoDescription"/>) that is refused once the page changed
    /// after the change. The cursor stays where it is; the book view is taken from the vault again and
    /// <see cref="AppState.Notify"/> is raised.
    /// </summary>
    /// <returns>The message to show, or <c>null</c> if the status was set (or nothing changed) without remark.</returns>
    public static async Task<AdoptMessage?> SetStatusAsync(AppState state, BlockStatus status, string undoDescription, ILogger logger)
    {
        try
        {
            var (message, session, book) = await PrepareAsync(state, logger);
            if (session is null || book is null || StatusTargetOf(state, book) is not { } element
                || BlockStatuses.Of(BlockOf(book, element)) == status)
            {
                return message;
            }

            return RunStatus(session, book, () =>
            {
                ManuscriptEditor.SetStatus(book, element, status, session.Clock.Now);
                return null;
            }, undoDescription, logger).Message;
        }
        finally
        {
            // On success the book changed; after a failure the vault holds the page as it is on disk again.
            state.RefreshBook();
            state.Notify();
        }
    }

    /// <summary>
    /// Whether <see cref="InsertReferenceAsync"/> is possible now: as for <see cref="CanSetStatus"/>, the book is
    /// writable, no conflict is open for it (R26), and the cursor is in a text block or detail (headings hold no
    /// references).
    /// </summary>
    public static bool CanInsertReference(AppState state) => CanSetStatus(state);

    /// <summary>
    /// Inserts a cross reference to the heading or text block <paramref name="targetKey"/> at the cursor of the text
    /// block or detail at the cursor (<see cref="ManuscriptEditor.InsertReference"/>, spec 2.2), in the same order as
    /// <see cref="RunAsync"/>: the editor saves its text first, and text it could not save refuses the command; a
    /// read-only book or an open conflict refuses it too. The editor reports the cursor's offset in the text it shows;
    /// it is converted to the offset in the block's Markdown (<see cref="ManuscriptEditor.ContentOffset"/>). On a
    /// heading or without an element at the cursor nothing changes; an invalid target shows <c>SectionCommandFailed</c>.
    /// The reference and an <c>id::</c> the target gets are saved together with one undo action
    /// (<paramref name="undoDescription"/>) that is refused once the page changed after the command. The element is
    /// revealed with the cursor right after the reference and the range level kept; the book view is taken from the
    /// vault again and <see cref="AppState.Notify"/> is raised. Should no place in the Markdown show the cursor's offset
    /// (not expected), a warning is logged and the reference goes at the end of the text.
    /// </summary>
    /// <returns>The message to show, or <c>null</c> if the reference was inserted (or nothing changed) without remark.</returns>
    public static async Task<AdoptMessage?> InsertReferenceAsync(AppState state, Guid targetKey, string undoDescription, ILogger logger)
    {
        Guid? reveal = null;
        var after = 0;
        try
        {
            var (message, session, book) = await PrepareAsync(state, logger);
            if (session is null || book is null || state.Cursor is not { } cursor || StatusTargetOf(state, book) is not { } element)
            {
                return message;
            }

            (message, reveal) = RunStatus(session, book, () =>
            {
                var offset = ManuscriptEditor.ContentOffset(book, element.Key, cursor.Offset);
                if (offset is null)
                {
                    // The end of the text at least keeps the text around the reference as it is.
                    logger.LogWarning(
                        "No place in the Markdown of {Element} shows offset {Offset}; the reference goes at the end", element.Key, cursor.Offset);
                }

                // The editor shows the reference as one node: one position of its text.
                after = offset is null ? int.MaxValue : cursor.Offset + 1;
                ManuscriptEditor.InsertReference(book, element.Key, offset ?? int.MaxValue, targetKey, session.Clock.Now);
                return element.Key;
            }, undoDescription, logger);
            return message;
        }
        finally
        {
            // On success the book changed; after a failure the vault holds the page as it is on disk again.
            state.RefreshBook();
            if (reveal is { } key)
            {
                // The cursor goes right after the reference; the range stays.
                state.RevealElement(key, after, keepRange: true);
            }

            state.Notify();
        }
    }

    /// <summary>
    /// The level of the element at the cursor (as for Delete, never the fallback element) the level buttons show
    /// (spec 4.1): 0 (Text) for a text block or detail, 1–6 for a heading; <c>null</c> without such an element.
    /// </summary>
    public static int? LevelAt(AppState state) =>
        state.CurrentBook is { } book && LevelTargetOf(state, book) is { } element
            ? element.Kind == ElementKind.Heading ? book.FindNode(element.Key)!.Level : 0
            : null;

    /// <summary>
    /// Why the level button <paramref name="level"/> (0 = Text, 1–6 = H1–H6) cannot convert the element at the cursor
    /// (<see cref="ManuscriptEditor.LevelBlocked"/>), for its tooltip: the resource key of the reason; <c>null</c> if it
    /// can, or without an element.
    /// </summary>
    public static string? LevelBlocked(AppState state, int level) =>
        state.CurrentBook is { } book && LevelTargetOf(state, book) is { } element
            ? ManuscriptEditor.LevelBlocked(book, element, level)
            : null;

    /// <summary>
    /// Whether <see cref="SetLevelAsync"/> converts the element at the cursor to <paramref name="level"/> now: the book is
    /// writable, no conflict is open for it (R26), the element has another level and the conversion is possible
    /// (<see cref="LevelBlocked"/>).
    /// </summary>
    public static bool CanSetLevel(AppState state, int level) =>
        state is { Session: { } session, CurrentBook: { Page.IsReadOnly: false } book }
        && !session.HasOpenConflict(book.Page.FilePath)
        && LevelTargetOf(state, book) is { } element
        && LevelAt(state) != level
        && ManuscriptEditor.LevelBlocked(book, element, level) is null;

    /// <summary>
    /// Converts the element at the cursor (<see cref="LevelAt"/>) to <paramref name="level"/> (spec 4), in the same order
    /// as <see cref="RunAsync"/>: the editor saves its text first, and text it could not save refuses the conversion; a
    /// read-only book or an open conflict refuses it too. A text block or detail becomes a heading
    /// (<see cref="ManuscriptEditor.ToHeading"/>), a heading another level (<see cref="ManuscriptEditor.SetHeadingLevel"/>),
    /// each saved with a <see cref="StructureUndo"/>; Text on a heading removes it (<see cref="ManuscriptEditor.RemoveHeading"/>,
    /// with its targeted undo). The same level, a level <see cref="LevelBlocked"/> refuses (shows <c>SectionCommandFailed</c>)
    /// or no element changes nothing. A conversion that fails after it changed the page reads the page from the file
    /// again. The converted element is revealed and stays the active one (no jump in the history); the book view is
    /// taken from the vault again and <see cref="AppState.Notify"/> is raised.
    /// </summary>
    /// <returns>The message to show, or <c>null</c> if the element was converted (or nothing changed) without remark.</returns>
    public static async Task<AdoptMessage?> SetLevelAsync(AppState state, int level, string undoDescription, ILogger logger)
    {
        Guid? reveal = null;
        try
        {
            var (message, session, book) = await PrepareAsync(state, logger);
            if (session is null || book is null || LevelTargetOf(state, book) is not { } element || LevelAt(state) == level)
            {
                return message;
            }

            if (element.Kind == ElementKind.Heading && level == 0)
            {
                (message, reveal) = RunStructure(session, book, element, SectionCommand.RemoveHeading, undoDescription, logger);
                return message;
            }

            var page = book.Page;
            (message, reveal) = RunStatus(session, book, () =>
            {
                try
                {
                    if (element.Kind != ElementKind.Heading)
                    {
                        return ManuscriptEditor.ToHeading(book, element.Key, level, session.Clock.Now);
                    }

                    ManuscriptEditor.SetHeadingLevel(book, element.Key, level);
                    return element.Key;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException && page.IsDirty)
                {
                    // Not expected (the conversion validates first): the page is not left half converted.
                    PageReload.FromDisk(session, page);
                    throw;
                }
            }, undoDescription, logger);
            return message;
        }
        finally
        {
            // On success the book changed; after a failure the vault holds the page as it is on disk again.
            state.RefreshBook();
            if (reveal is { } key)
            {
                state.RevealElement(key);
            }

            state.Notify();
        }
    }

    private static async Task<(AdoptMessage? Message, Guid? Reveal)> TryRunAsync(
        AppState state, SectionCommand command, string undoDescription, ILogger logger)
    {
        // Merge and Wrap act on the range the user sees now; the save may change it (e.g. make the cursor's detail known).
        var onRange = command is SectionCommand.Merge or SectionCommand.Wrap;
        var range = onRange ? HeadOf(state.CurrentRange) : null;
        var (message, session, book) = await PrepareAsync(state, logger);
        if (session is null || book is null)
        {
            return (message, null);
        }

        if (!Applies(state, book, command))
        {
            return (null, null);
        }

        if (onRange && !Nullable.Equals(range, HeadOf(RangeOf(state, book))))
        {
            // The same level now marks another range than the one the command was given for: nothing changes.
            return (null, null);
        }

        if (command == SectionCommand.Merge)
        {
            var merged = RangeOf(state, book)!;
            return merged.MergeGroups.Count > 0
                ? Merge(session, book, merged.MergeGroups, logger)
                : MergeDetails(session, book, merged.Head!, logger);
        }

        // Wrap acts on the head of the range, every other command on its target element.
        var element = command == SectionCommand.Wrap ? RangeOf(state, book)!.Head! : TargetOf(state, book, command)!;
        if (command == SectionCommand.ToggleRemark)
        {
            var toggled = BlockStatuses.Of(BlockOf(book, element)) == BlockStatus.Remark ? BlockStatus.Draft : BlockStatus.Remark;
            return RunStatus(session, book, () =>
            {
                ManuscriptEditor.SetStatus(book, element, toggled, session.Clock.Now);
                return null;
            }, undoDescription, logger);
        }

        if (command == SectionCommand.AddRemark)
        {
            return RunStatus(session, book, () => ManuscriptEditor.AddRemark(book, element, session.Clock.Now), undoDescription, logger);
        }

        if (command != SectionCommand.Delete)
        {
            return RunStructure(session, book, element, command, undoDescription, logger);
        }

        // The notes whose usages a delete removes are written directly by the link service (R26).
        var linked = SourceValue.LinkedBlocksIn(BlockOf(book, element)).ToList();
        var notePages = NotePagesOf(session, linked.SelectMany(block => block.Sources));
        if (notePages.Any(session.HasOpenConflict))
        {
            return (new AdoptMessage("EditorConflict", true), null);
        }

        return element.Kind != ElementKind.Detail || linked.Count > 0
            ? (Delete(session, book, element, notePages, logger), null)
            : RunStructure(session, book, element, command, undoDescription, logger);
    }

    /// <summary>
    /// What every command does before it changes anything: the editor saves its text (text it could not save refuses
    /// the command), then the book is taken from the vault and checked for being writable and free of an open conflict
    /// (R26).
    /// </summary>
    /// <returns>
    /// The session and the book to change; or, when the command may not run, no book and the message to show
    /// (<c>null</c> when there is simply no book).
    /// </returns>
    private static async Task<(AdoptMessage? Message, VaultSession? Session, Book? Book)> PrepareAsync(AppState state, ILogger logger)
    {
        try
        {
            if (state.FlushEditor is { } flush)
            {
                await flush();
            }
        }
        catch (Exception ex)
        {
            // The editor's text may not be saved, so the page is not changed behind it.
            logger.LogError(ex, "Saving the editor before a structure command failed");
            return (new AdoptMessage("SectionCommandUnsaved", true), null, null);
        }

        if (state.HasUnsavedEditorText?.Invoke() == true)
        {
            return (new AdoptMessage("SectionCommandUnsaved", true), null, null);
        }

        // The flush may have saved the page or changed the state, so everything is read after it, the book from the vault.
        if (state.Session is not { } session || state.CurrentBook is not { } current
            || session.Vault.FindBook(current.LinkName) is not { } book)
        {
            return (null, null, null);
        }

        if (book.Page.IsReadOnly)
        {
            return (new AdoptMessage("EditorReadOnly", true), null, null);
        }

        return session.HasOpenConflict(book.Page.FilePath)
            ? (new AdoptMessage("EditorConflict", true), null, null)
            : (null, session, book);
    }

    /// <summary>
    /// Whether <paramref name="command"/> applies in <paramref name="book"/> now: Merge and Wrap to the marked range
    /// (<see cref="RangeOf"/>), every other command to its target element (<see cref="TargetOf"/>, <see cref="Fits"/>).
    /// </summary>
    private static bool Applies(AppState state, Book book, SectionCommand command) => command switch
    {
        SectionCommand.Merge => RangeOf(state, book) is { MergeGroups.Count: > 0 } or { MergesDetails: true },
        SectionCommand.Wrap => RangeOf(state, book) is { Head: { } head } && ManuscriptEditor.CanWrap(book, head),
        _ => TargetOf(state, book, command) is { } element && Fits(book, element, command)
             && !(command is SectionCommand.Delete or SectionCommand.Indent or SectionCommand.Outdent
                  && state.RangeLevel < 0 && HasSubElements(book, element)),
    };

    /// <summary>
    /// Whether <paramref name="element"/> (in <paramref name="book"/>) has sub-elements the range of level −1 does not
    /// mark, but Delete, Indent and Outdent would take along: sub-headings of a heading, details of a text block,
    /// deeper details of a detail.
    /// </summary>
    private static bool HasSubElements(Book book, BookElement element) => element.Kind == ElementKind.Heading
        ? book.FindNode(element.Key)!.Children.Any()
        : BlockOf(book, element).Children.Count > 0;

    /// <summary>The head and the level of <paramref name="range"/>, to tell whether it is still the same range; <c>null</c> without one.</summary>
    private static (Guid? Head, int Level)? HeadOf(RangeInfo? range) => range is null ? null : (range.Head?.Key, range.Level);

    /// <summary>
    /// The marked range in <paramref name="book"/>, as <see cref="AppState.CurrentRange"/> has it: the range of
    /// <see cref="AppState.RangeLevel"/> around <see cref="AppState.CurrentElement"/>; <c>null</c> if the book does not
    /// have that element.
    /// </summary>
    private static RangeInfo? RangeOf(AppState state, Book book) =>
        state.CurrentElement is { } element && BookElements.Find(book, element.Key) == element
            ? BookRanges.Of(book, element, state.RangeLevel)
            : null;

    /// <summary>The distinct files of the pages holding the notes with the block ids <paramref name="noteIds"/>.</summary>
    private static List<string> NotePagesOf(VaultSession session, IEnumerable<Guid> noteIds) =>
        [.. noteIds.Distinct().Select(id => session.Vault.FindBlockById(id)?.Page.FilePath).OfType<string>().Distinct()];

    /// <summary>
    /// The element <paramref name="command"/> acts on: <see cref="AppState.CurrentElement"/>, but for Delete and
    /// ToggleRemark only the element with the cursor's own key in <paramref name="book"/>, never the fallback (a text
    /// block or a whole section the user may not see as the cursor's place); <c>null</c> if there is none.
    /// </summary>
    private static BookElement? TargetOf(AppState state, Book book, SectionCommand command) =>
        command is not (SectionCommand.Delete or SectionCommand.ToggleRemark) ? state.CurrentElement
        : state.Cursor is { } cursor ? BookElements.Find(book, cursor.Key)
        : null;

    /// <summary>
    /// The text block or detail whose status the status selection shows and <see cref="SetStatusAsync"/> changes: the
    /// element with the cursor's own key in <paramref name="book"/> (as for Delete); <c>null</c> on a heading or
    /// without one.
    /// </summary>
    private static BookElement? StatusTargetOf(AppState state, Book book) =>
        TargetOf(state, book, SectionCommand.ToggleRemark) is { Kind: not ElementKind.Heading } element ? element : null;

    /// <summary>
    /// The element the level buttons convert: the element with the cursor's own key in <paramref name="book"/> (as for
    /// Delete); <c>null</c> without one.
    /// </summary>
    private static BookElement? LevelTargetOf(AppState state, Book book) => TargetOf(state, book, SectionCommand.Delete);

    /// <summary>Whether <paramref name="command"/> fits <paramref name="element"/>, which must be in <paramref name="book"/>.</summary>
    private static bool Fits(Book book, BookElement element, SectionCommand command) =>
        BookElements.Find(book, element.Key) == element
        && command switch
        {
            SectionCommand.InsertAfter or SectionCommand.Delete => true,
            SectionCommand.InsertChild => ManuscriptEditor.CanInsertChild(book, element),
            SectionCommand.Indent => element.Kind == ElementKind.Heading && ManuscriptEditor.CanIndent(book, element.Key),
            SectionCommand.Outdent => element.Kind == ElementKind.Heading && ManuscriptEditor.CanOutdent(book, element.Key),
            SectionCommand.RemoveHeading => element.Kind == ElementKind.Heading,
            SectionCommand.ToggleRemark or SectionCommand.AddRemark => element.Kind != ElementKind.Heading,
            _ => false,
        };

    /// <summary>The block of <paramref name="element"/>, which must be in <paramref name="book"/>.</summary>
    private static Block BlockOf(Book book, BookElement element) => element.Kind switch
    {
        ElementKind.Heading => book.FindNode(element.Key)!.Block!,
        ElementKind.TextBlock => book.FindTextBlock(element.Key)!.Block,
        _ => BookElements.TextBlockOf(book, element)!.Paragraphs.First(p => p.Block.Key == element.Key).Block,
    };

    /// <summary>
    /// Runs a <see cref="ManuscriptEditor"/> command on the page, saves it and pushes its undo action (for removing a
    /// heading the targeted <see cref="RemoveHeadingUndo"/>, for Wrap on the range head <paramref name="element"/> the
    /// targeted <see cref="WrapUndo"/>, otherwise <see cref="StructureUndo"/>). If saving fails
    /// for another reason than a change by another program or a conflict (both handled by the session), the page is
    /// read from the file again.
    /// </summary>
    /// <returns>The message, and the element to reveal (none after deleting a detail).</returns>
    private static (AdoptMessage? Message, Guid? Reveal) RunStructure(
        VaultSession session, Book book, BookElement element, SectionCommand command, string undoDescription, ILogger logger)
    {
        var page = book.Page;
        var before = PageStructureSnapshot.Capture(page);
        RemovedHeading? removed = null;
        WrappedSection? wrapped = null;
        Guid? reveal = null;
        try
        {
            switch (command)
            {
                case SectionCommand.InsertAfter:
                    reveal = ManuscriptEditor.InsertAfter(book, element, session.Clock.Now);
                    break;
                case SectionCommand.InsertChild:
                    reveal = ManuscriptEditor.InsertChild(book, element, session.Clock.Now);
                    break;
                case SectionCommand.Indent:
                    ManuscriptEditor.Indent(book, element.Key);
                    reveal = element.Key;
                    break;
                case SectionCommand.Outdent:
                    ManuscriptEditor.Outdent(book, element.Key);
                    reveal = element.Key;
                    break;
                case SectionCommand.RemoveHeading:
                    removed = ManuscriptEditor.RemoveHeading(book, element.Key, session.Clock.Now);
                    reveal = removed.Reveal;
                    break;
                case SectionCommand.Wrap:
                    wrapped = ManuscriptEditor.Wrap(book, element);
                    reveal = wrapped.NewHeadingKey;
                    break;
                case SectionCommand.Delete:
                    ManuscriptEditor.DeleteDetail(book, element.Key, session.Clock.Now);
                    break;
                default:
                    // Every other command has its own path; none may fall through to a change of the page.
                    throw new UnreachableException($"{command} is not a structure command.");
            }
        }
        catch (ArgumentException ex)
        {
            // Refused before anything changed.
            logger.LogWarning(ex, "The structure command {Command} was refused", command);
            return (new AdoptMessage("SectionCommandFailed", true), null);
        }

        if (!session.TrySave(page, out var error))
        {
            return (SaveFailed(session, page, error), null);
        }

        session.Undo.Push(
            removed is not null ? new RemoveHeadingUndo(undoDescription, session, page.FilePath, removed)
            : wrapped is not null ? new WrapUndo(undoDescription, session, book.LinkName, wrapped)
            : new StructureUndo(undoDescription, session, page.FilePath, before, PageStructureSnapshot.Capture(page)));
        return (null, reveal);
    }

    /// <summary>
    /// Runs a change of block text or properties on the page (<paramref name="change"/>:
    /// <see cref="ManuscriptEditor.SetStatus"/>, <see cref="ManuscriptEditor.AddRemark"/>,
    /// <see cref="ManuscriptEditor.InsertReference"/> or a level conversion), saves it and pushes a <see cref="StructureUndo"/>
    /// (<paramref name="undoDescription"/>). A change refused before anything changed (a heading, a block without place
    /// for properties, an invalid reference target) shows <c>SectionCommandFailed</c>; a failed save is handled like
    /// that of a structure command.
    /// </summary>
    /// <returns>
    /// The message, and the element to reveal (the new remark, the referring element, the converted element; none after a
    /// status change).
    /// </returns>
    private static (AdoptMessage? Message, Guid? Reveal) RunStatus(
        VaultSession session, Book book, Func<Guid?> change, string undoDescription, ILogger logger)
    {
        var page = book.Page;
        var before = PageStructureSnapshot.Capture(page);
        Guid? reveal;
        try
        {
            reveal = change();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Refused before anything changed.
            logger.LogWarning(ex, "The block change was refused");
            return (new AdoptMessage("SectionCommandFailed", true), null);
        }

        if (!session.TrySave(page, out var error))
        {
            return (SaveFailed(session, page, error), null);
        }

        session.Undo.Push(new StructureUndo(undoDescription, session, page.FilePath, before, PageStructureSnapshot.Capture(page)));
        return (null, reveal);
    }

    /// <summary>
    /// The message after <see cref="VaultSession.TrySave"/> failed. A change by another program was merged or reported
    /// as a conflict by the session, and an open conflict keeps the local page for its resolution; after any other
    /// failure the page is read from the file again, so the editor shows what is on disk. A merged external change
    /// brings the command's change into the file together with it, but without an undo entry (none is pushed after a
    /// failed save).
    /// </summary>
    private static AdoptMessage SaveFailed(VaultSession session, Page page, Exception? error)
    {
        if (session.HasOpenConflict(page.FilePath))
        {
            return new AdoptMessage("EditorConflict", true);
        }

        if (error is FileChangedExternallyException)
        {
            return new AdoptMessage("EditorExternalChange", false);
        }

        PageReload.FromDisk(session, page);
        return new AdoptMessage("SectionCommandFailed", true);
    }

    /// <summary>
    /// Deletes a text block, a heading with its section or a detail through the link service, which removes the usages
    /// from the notes (on <paramref name="notePages"/>) and pushes its own undo action („Löschen“).
    /// </summary>
    private static AdoptMessage? Delete(
        VaultSession session, Book book, BookElement element, IReadOnlyList<string> notePages, ILogger logger) =>
        RunLinked(session, book, notePages, logger, $"Deleting a {element.Kind}", () =>
        {
            switch (element.Kind)
            {
                case ElementKind.Heading:
                    session.Links.DeleteSection(book, element.Key);
                    break;
                case ElementKind.TextBlock:
                    session.Links.DeleteTextBlock(book, element.Key);
                    break;
                default:
                    session.Links.DeleteDetail(book, element.Key);
                    break;
            }
        });

    /// <summary>
    /// Merges the text blocks of each of <paramref name="groups"/> (the range's <see cref="RangeInfo.MergeGroups"/>)
    /// through the link service, which moves the notes' usages of the removed blocks to the remaining one and pushes its
    /// own undo action („Zusammenfügen“). Refused while a conflict is open for the page of a note that is a source of a
    /// merged block (R26).
    /// </summary>
    /// <returns>The message, and the first remaining block to reveal (none if the merge failed).</returns>
    private static (AdoptMessage? Message, Guid? Reveal) Merge(
        VaultSession session, Book book, IReadOnlyList<IReadOnlyList<Guid>> groups, ILogger logger)
    {
        var notePages = NotePagesOf(session, groups.SelectMany(group => group)
            .Select(book.FindTextBlock).OfType<TextBlock>().SelectMany(textBlock => textBlock.Sources));
        if (notePages.Any(session.HasOpenConflict))
        {
            return (new AdoptMessage("EditorConflict", true), null);
        }

        Guid? first = null;
        var message = RunLinked(session, book, notePages, logger, "Merging text blocks",
            () => first = session.Links.MergeTextBlocks(book, groups));
        return (message, first);
    }

    /// <summary>
    /// Merges the details of the range head <paramref name="head"/> (a text block or a detail with its details) into its
    /// text through the link service, which moves the notes' usages of merged linked details to the text block and
    /// pushes its own undo action („Zusammenfügen“). Refused while a conflict is open for the page of a note that is a
    /// source of a block in the head's subtree (R26).
    /// </summary>
    /// <returns>The message, and the head to reveal (none if the merge failed).</returns>
    private static (AdoptMessage? Message, Guid? Reveal) MergeDetails(
        VaultSession session, Book book, BookElement head, ILogger logger)
    {
        var notePages = NotePagesOf(
            session, SourceValue.LinkedBlocksIn(BlockOf(book, head)).SelectMany(linked => linked.Sources));
        if (notePages.Any(session.HasOpenConflict))
        {
            return (new AdoptMessage("EditorConflict", true), null);
        }

        Guid? kept = null;
        var message = RunLinked(session, book, notePages, logger, "Merging details",
            () => kept = session.Links.MergeDetails(book, head.Key));
        return (message, kept);
    }

    /// <summary>
    /// Changes <paramref name="book"/> and the notes on <paramref name="notePages"/> through the link service
    /// (<paramref name="change"/>, which writes the files and pushes its own undo action), then updates the search index
    /// for those notes. A change of the book by another program is handled by the session.
    /// </summary>
    /// <param name="what">What <paramref name="change"/> does, for the log.</param>
    private static AdoptMessage? RunLinked(
        VaultSession session, Book book, IReadOnlyList<string> notePages, ILogger logger, string what, Action change)
    {
        var bookPath = book.Page.FilePath;
        try
        {
            change();
        }
        catch (FileChangedExternallyException)
        {
            session.HandleExternalChange(bookPath);
            return session.HasOpenConflict(bookPath)
                ? new AdoptMessage("EditorConflict", true)
                : new AdoptMessage("EditorExternalChange", false);
        }
        catch (ReadOnlyPageException)
        {
            return new AdoptMessage("EditorReadOnly", true);
        }
        catch (MergeRefusedException ex)
        {
            // Different properties or a reference elsewhere to a block that would go: nothing was changed.
            logger.LogInformation(ex, "{What} was refused", what);
            return new AdoptMessage("SectionMergeRefused", true);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Includes the page writer refusing a result that would not parse safely (nothing was written).
            logger.LogWarning(ex, "{What} failed", what);
            return new AdoptMessage("SectionCommandFailed", true);
        }

        // The search index knows which notes are used; it is updated for the notes whose used-in changed.
        foreach (var path in notePages)
        {
            try
            {
                session.Search.UpdatePage(session.Notes, path);
            }
            catch (Exception ex)
            {
                // The change is done; a stale search index (an aid, rebuilt on opening) must not hide that.
                logger.LogWarning(ex, "Updating the search index for {Path} failed", path);
            }
        }

        return null;
    }

    /// <summary>
    /// Undoes a structure command: puts the page of <paramref name="path"/> back to its state <paramref name="before"/>
    /// the command and saves it, but only while the page is exactly what the command left (<paramref name="after"/>);
    /// a later change (e.g. a title typed into a new heading) is never overwritten.
    /// </summary>
    private sealed class StructureUndo(
        string description, VaultSession session, string path, PageStructureSnapshot before, PageStructureSnapshot after) : IUndoAction
    {
        public string Description => description;

        /// <exception cref="InvalidOperationException">The page changed after the command, or saving it failed.</exception>
        public void Undo()
        {
            var page = session.Vault.FindPageByPath(path);
            if (page is null || !after.Matches(page))
            {
                throw new InvalidOperationException($"'{path}' changed after the structure command; it is not undone.");
            }

            before.RestoreInto(page);
            SaveUndone(session, page, path);
        }
    }

    /// <summary>
    /// Undoes removing a heading (<see cref="RemovedHeading.RestoreInto"/>) and saves the page: the heading comes back
    /// around the blocks it held, also after later edits elsewhere or in those blocks' text, and the text block made
    /// from its title goes. Refused (no change) when the heading's former parent is gone, a moved block is gone or was
    /// moved elsewhere, the heading is back, or the title's text block was edited, got details or was moved elsewhere
    /// (undo would lose that).
    /// </summary>
    private sealed class RemoveHeadingUndo(string description, VaultSession session, string path, RemovedHeading removed) : IUndoAction
    {
        public string Description => description;

        /// <exception cref="InvalidOperationException">The heading cannot be put back, or saving the page failed.</exception>
        public void Undo()
        {
            var page = session.Vault.FindPageByPath(path);
            if (page is null || !removed.CanRestore(page))
            {
                throw new InvalidOperationException($"The heading removed from '{path}' cannot be put back; it is not undone.");
            }

            removed.RestoreInto(page);
            SaveUndone(session, page, path);
        }
    }

    /// <summary>
    /// Undoes putting a range under a new heading (<see cref="WrappedSection.RestoreInto"/>) in the book
    /// <paramref name="linkName"/> and saves its page: the new heading goes and the moved blocks come back, also after
    /// later edits of the heading's title or of those blocks' text. Refused (no change) when the new heading is gone or
    /// holds other blocks, or a moved block was moved elsewhere.
    /// </summary>
    private sealed class WrapUndo(string description, VaultSession session, string linkName, WrappedSection wrapped) : IUndoAction
    {
        public string Description => description;

        /// <exception cref="InvalidOperationException">The structure cannot be put back, or saving the page failed.</exception>
        public void Undo()
        {
            var book = session.Vault.FindBook(linkName);
            if (book is null || !wrapped.CanRestore(book))
            {
                throw new InvalidOperationException($"The range put under a new heading in '{linkName}' cannot be put back; it is not undone.");
            }

            wrapped.RestoreInto(book);
            SaveUndone(session, book.Page, book.Page.FilePath);
        }
    }

    /// <summary>Saves the page after an undo; if that fails, the vault takes the file's version again where it should.</summary>
    /// <exception cref="InvalidOperationException">Saving failed.</exception>
    private static void SaveUndone(VaultSession session, Page page, string path)
    {
        if (!session.TrySave(page, out var error))
        {
            if (error is not FileChangedExternallyException && !session.HasOpenConflict(path))
            {
                // The restored page was not written: the vault takes the file's version again.
                PageReload.FromDisk(session, page);
            }

            throw new InvalidOperationException($"Saving '{path}' after undoing the structure command failed.", error);
        }
    }
}
