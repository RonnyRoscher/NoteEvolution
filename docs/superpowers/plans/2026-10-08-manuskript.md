# Manuscript as the Only View (Package A) – Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The editor always shows the whole book. The element at the cursor is marked. Sections are created, indented, outdented, removed and deleted by command. Notes are adopted at the cursor, after the current element, or below it. The marking shows its sources, which jump into the journal.

**Architecture:**
- **Core:** New pure book operations change `book.Page` in memory, like `OutlineEditor` does: `ManuscriptEditor` for structure, `BookElements` for locating elements, and `PageStructureSnapshot` for undo. Link-aware operations (`DeleteSection`, `AdoptInto`) live in `LinkService`.
- **UI state:** `ManuscriptCommands` runs a command: it flushes the editor, checks R26 and read-only, saves, pushes an undo action and asks the editor to reveal the resulting element.
- **Editor (JS):** It reports the element at the cursor and the geometry of the marking box. It turns shortcuts into commands. The Blazor `SectionBar` renders the buttons and the sources.

**Tech Stack:** .NET 10, Blazor Hybrid (Photino), TipTap 2.27 (esbuild bundle), xUnit + bUnit.

**Spec:** `docs/superpowers/specs/2026-10-08-manuskript-design.md`. Read it first; this plan argues from it.

## Global Constraints

- **Working tree:** `C:\Users\Ronny Roscher\claude\NoteEvolution\.claude\worktrees\stufe-1`, branch `manuskript`.
  - Use absolute paths.
  - Use plain, separate git commands; no compound git lines and no bash scripts.
- **SDK 10:** In PowerShell, run `$env:DOTNET_ROOT="$env:LOCALAPPDATA\Microsoft\dotnet"; $env:PATH="$env:DOTNET_ROOT;$env:PATH"` first.
  - Build: `dotnet build NoteEvolution.slnx`
  - Tests: `dotnet test NoteEvolution.slnx`
  - Baseline: 944 tests, 1 real-model skip.
  - Warnings are errors.
- **Editor bundle:** Build it with `npm run build` in `src/NoteEvolution.UI/Editor/js` (Node is installed). Commit the rebuilt `src/NoteEvolution.UI/wwwroot/js/editor.bundle.js` with every change to `editor.js`.
- **R6:** User-visible strings only in `src/NoteEvolution.UI/Resources/Strings.resx`, in German. The JS gets no German literals; C# passes any labels it needs.
- **Threads (S4/S5):** The vault and the notes are touched only on the UI thread.
- **R26:** No write path while a conflict is open for the book file. A command is refused before anything changes.
- **R4:** Use `TimeProvider` for any delay.
- **Lossless writing:** Every Core operation is tested with `LineDiff`. Only the lines of deliberately changed blocks differ, byte for byte.
- **Undo:** Every command is one entry of the session's `UndoManager`.
  - Undo is refused, with no change, when the page no longer is what the command left. The header then shows the existing `UndoFailed` text.
- **Commits:**
  - Conventional style, e.g. `feat(core): …` or `feat(ui): …`.
  - Every commit message ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **A command while the editor holds text it could not save** (file locked): the command is refused with a message, and the text and the file stay as they were. Test: Task 6 `Command_UnsavedEditorText_Refused_NothingChanged`.
2. **"Rückgängig" after the user typed into the newly created section:** undo is refused with `UndoFailed`, and the file keeps the typed text. Test: Task 6 `Undo_AfterTypingIntoNewSection_Refused`.
3. **The cursor element is unknown after a reload** (a new paragraph the book stored under another key): the current element falls back to the enclosing text block, else to the nearest heading, else to the start of the book. Test: Task 5 `Cursor_UnknownDetailKey_FallsBackToTextBlock`.
4. **Adopting at a cursor inside bold, inside escaped text, or after a line break:** the result is valid Markdown, and the text before and after the cursor is intact. Tests: Task 3 `SplitAt_*` cases.
5. **Deleting a heading section that contains linked text blocks:** the `used-in::` entries are removed from every note, and undo restores book and notes. Test: Task 2 `DeleteSection_LinkedBlocks_RemovesUsages_UndoRestores`.

---

### Task 1: Core – locate elements, structure commands, structure undo

**Files:**
- Create:
  - `src/NoteEvolution.Core/Books/BookElements.cs`
  - `src/NoteEvolution.Core/Books/ManuscriptEditor.cs`
  - `src/NoteEvolution.Core/Books/PageStructureSnapshot.cs`
- Modify: `src/NoteEvolution.Core/Books/HeadingText.cs` and/or `src/NoteEvolution.Core/Format/LogseqParser.cs`, only if an empty heading (`- #`) is not yet parsed as a heading with an empty title.
- Test: `tests/NoteEvolution.Core.Tests/Books/ManuscriptEditorTests.cs`, `tests/NoteEvolution.Core.Tests/Books/BookElementsTests.cs`

**Interfaces (produces):**

```csharp
namespace NoteEvolution.Core.Books;
public enum ElementKind { Heading, TextBlock, Detail }
/// <param name="Key">Block.Key of the heading, the text block or the detail block.</param>
public sealed record BookElement(ElementKind Kind, Guid Key);

public static class BookElements
{
    public static BookElement? Find(Book book, Guid key);            // null if no heading/text block/detail has the key
    public static Guid SectionOf(Book book, BookElement element);   // heading key; Guid.Empty for the prologue
    public static TextBlock? TextBlockOf(Book book, BookElement element); // the text block itself or the detail's; null for a heading
    /// distinct note ids: heading → all text blocks of its whole section; text block → own; detail → its text block's
    public static IReadOnlyList<Guid> SourcesOf(Book book, BookElement element);
    public static Guid? LastHeadingKey(Book book);                  // last heading in document order; null if none
}

public static class ManuscriptEditor   // changes book.Page in memory; the caller saves and reloads the Book
{
    public static Guid InsertAfter(Book book, BookElement current);  // returns the new element's key
    public static Guid InsertChild(Book book, BookElement current);
    public static bool CanInsertChild(Book book, BookElement current); // false for a level-6 heading
    public static void Indent(Book book, Guid headingKey);
    public static void Outdent(Book book, Guid headingKey);
    public static bool CanIndent(Book book, Guid headingKey);
    public static bool CanOutdent(Book book, Guid headingKey);
    public static Guid RemoveHeading(Book book, Guid headingKey);   // returns the key to reveal: first moved item, else the previous element, else Guid.Empty
    public static void DeleteDetail(Book book, Guid detailKey);
}

public sealed class PageStructureSnapshot
{
    public static PageStructureSnapshot Capture(Page page);
    public bool Matches(Page page);      // the page serializes to exactly the captured bytes (PageSerializer.Serialize)
    public void RestoreInto(Page page);  // puts every captured block back (parent, index, lines); removes blocks added since
}
```

Semantics are spec section 3. The new elements are:
- new heading: `HeadingText.WithTitle("", level, "")`, i.e. an empty title;
- new text block: an empty block;
- new detail: an empty child block.

Invalid commands throw `ArgumentException`, and the page stays unchanged:
- indenting without a previous heading of the same level;
- outdenting a level-1 heading;
- `InsertChild` on a level-6 heading;
- an unknown key.

Indenting and outdenting reuse `OutlineEditor.MoveSection` and its releveling. `RemoveHeading` inserts the heading's child blocks into the parent at the heading's index, in order, and relevels the moved sub-headings to parent level + 1.

- [ ] **Step 1: Write the failing tests.** Build on a shared book fixture in the style of `OutlineEditorTests`. Each structure test asserts the exact resulting file lines and, with `LineDiff`, that every other line is unchanged.
  - `InsertAfter_Heading_NewEmptyHeadingSameLevelAfterWholeSection`
  - `InsertAfter_TextBlock_EmptyBlockDirectlyAfter`
  - `InsertAfter_Detail_EmptyDetailSameDepthAfterItsDeeperDetails`
  - `InsertChild_Heading_FirstSubsectionAfterOwnTextBlocks`
  - `InsertChild_TextBlock_FirstDetailDepth1`
  - `InsertChild_Detail_FirstDetailOneDeeper`
  - `InsertChild_Level6Heading_Throws_CanInsertChildFalse`
  - `Indent_BecomesLastChildOfPreviousSameLevelHeading_SubtreeReleveled`
  - `Indent_WithoutPreviousSameLevelHeading_Throws_CanIndentFalse`
  - `Outdent_PlacedDirectlyAfterFormerParentSection`
  - `Outdent_Level1_Throws_CanOutdentFalse`
  - `RemoveHeading_TextAndSubsectionsMoveUpInPlace_SubheadingsOneLevelHigher`
  - `RemoveHeading_FirstHeading_ContentJoinsPrologue`
  - `DeleteDetail_RemovesDetailWithDeeperDetails`
  - `EmptyHeading_ParsesAsHeadingWithEmptyTitle_RoundTrips`
  - `Snapshot_RestoreInto_GivesOriginalBytes_ForEveryCommand`: a theory over all commands. Capture, apply, `RestoreInto`, then `Serialize` equals the original.
  - `Snapshot_Matches_FalseAfterAnyFurtherChange`
  - BookElementsTests:
    - `Find_EachKind`
    - `SectionOf_PrologueIsEmpty`
    - `SourcesOf_HeadingCountsWholeSectionDistinct`
    - `SourcesOf_DetailUsesItsTextBlock`
    - `LastHeadingKey`
- [ ] **Step 2: Run the tests.** They fail to compile.
  - Command: `dotnet test tests/NoteEvolution.Core.Tests --filter "ManuscriptEditorTests|BookElementsTests"`
- [ ] **Step 3: Implement** the three files and any empty-heading fix.
- [ ] **Step 4: Run the Core tests and the full suite.** All green.
- [ ] **Step 5: Commit** with `feat(core): manuscript structure commands with undo snapshot`.

### Task 2: Core – delete a heading section with its links

**Files:**
- Modify: `src/NoteEvolution.Core/Links/ILinkService.cs`, `src/NoteEvolution.Core/Links/LinkService.cs`
- Test: `tests/NoteEvolution.Core.Tests/Links/RemoveAndDeleteTests.cs`

**Interfaces (produces):** `void ILinkService.DeleteSection(Book book, Guid headingKey)`.
- It deletes the heading block with its whole subtree.
- It removes the usages of every linked text block in it from the notes.
- It pushes one undo action, „Löschen“, that puts the subtree and the notes back. It follows the pattern of `DeleteTextBlock` (`RemoveUsagesOfDeleted`, `UndoDelete`).
- Exceptions are those of `DeleteTextBlock`; an unknown heading or the root throws `ArgumentException`.

- [ ] **Step 1: Write the failing tests.**
  - `DeleteSection_LinkedBlocks_RemovesUsages_UndoRestores`: a section with 2 linked text blocks and a sub-section with 1 linked block. After the delete, the 3 notes have no `used-in::` entry for these blocks and the book lines are gone (`LineDiff`). After undo, book and notes equal their original bytes.
  - `DeleteSection_Unknown_Throws`
  - `DeleteSection_ReadOnlyBook_Throws_NothingChanged`
- [ ] **Step 2: Run the tests.** They fail.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the tests.** They are green, and the Core suite stays green.
- [ ] **Step 5: Commit** with `feat(core): delete a section with its links`.

### Task 3: Core – adopt into an existing block

**Files:**
- Create: `src/NoteEvolution.Core/Links/IntoPosition.cs`
- Modify:
  - `src/NoteEvolution.Core/Text/InlineMarkdown.cs`, adding `SplitAt`
  - `src/NoteEvolution.Core/Links/ILinkService.cs`
  - `src/NoteEvolution.Core/Links/LinkService.cs`
- Test: `tests/NoteEvolution.Core.Tests/Text/InlineMarkdownTests.cs`, `tests/NoteEvolution.Core.Tests/Links/AdoptTests.cs`

**Interfaces (produces):**

```csharp
namespace NoteEvolution.Core.Links;
public abstract record IntoPosition
{
    /// Offset counts characters of the element's text as the editor shows it: unescaped (BlockTextEscape.Unescape),
    /// without Markdown markers, "\n" counting as 1.
    public sealed record AtCursor(Guid ElementKey, int Offset) : IntoPosition;   // a text block (its own text) or a detail
    public sealed record AfterDetail(Guid DetailKey) : IntoPosition;
    public sealed record FirstChild(Guid ElementKey) : IntoPosition;            // a text block or a detail
}
// ILinkService:
AdoptResult AdoptInto(Book book, Guid noteBlockKey, IntoPosition position);
// InlineMarkdown:
public static (string Before, string After) SplitAt(string content, int plainOffset);
```

`AdoptInto` follows spec section 4. It inserts the note text and/or its sub-bullets, without property lines:
- **AtCursor:** the element's content becomes `before + note content + after`, using `SplitAt` on the element's content. The note's sub-bullets become the first children of the element.
- **AfterDetail:** the note copy becomes the next sibling block after the detail, after the detail's deeper details.
- **FirstChild:** the note copy becomes the first child block.

Then it links:
- The enclosing text block gets `id::` if needed, and the note's id is added to its `source::` unless it is already there.
- The note gets `used-in:: [[book]] ((text block id))`.
- Book first, then note; a pending entry on failure, as in `Adopt`.

The undo action „Übernehmen“ does two things:
- It restores the text block's subtree lines as they were before (`RestoreLines` and removing the inserted blocks), if its subtree still serializes as `AdoptInto` left it. Otherwise it refuses with `InvalidOperationException`.
- It removes the usage again.

`AdoptResult.TextBlockKey` and `TextBlockId` are those of the enclosing text block.

- [ ] **Step 1: Write the failing tests.**
  - SplitAt:
    - `SplitAt_PlainText`, with `("Hallo Welt", 5)` giving `("Hallo", " Welt")`
    - `SplitAt_InsideBold_ClosesAndReopens`, with `("**abcd**", 2)` giving `("**ab**", "**cd**")`
    - `SplitAt_Start_And_End`
    - `SplitAt_AfterLineBreak`, with `("eins\nzwei", 5)` giving `("eins\n", "zwei")`
    - `SplitAt_EscapedLeadingDash_StaysEscaped`
    - `SplitAt_OffsetOutOfRange_Throws`
  - AdoptInto:
    - `AdoptInto_AtCursor_TextBlockMidParagraph_RestMovesBehind_SourceOnBlock`
    - `AdoptInto_AtCursor_Detail_SubBulletsBecomeFirstChildren`
    - `AdoptInto_AfterDetail_AfterDeeperDetails_SameDepth`
    - `AdoptInto_FirstChild_TextBlock_FirstDetail`
    - `AdoptInto_FirstChild_Detail_OneDeeper`
    - `AdoptInto_NoteAlreadySource_NotDuplicated`
    - `AdoptInto_Undo_RestoresBlockAndNote`
    - `AdoptInto_Undo_AfterFurtherEdit_Refused`
    - `AdoptInto_SubBulletOfNote_LinksThatSubBullet`
  - Each AdoptInto test checks the exact lines, the `source::`/`id::` on the text block and the `used-in::` on the note, with `LineDiff`.
- [ ] **Step 2: Run the tests.** They fail.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the tests and the Core suite.** All green.
- [ ] **Step 5: Commit** with `feat(core): adopt a note into an existing block`.

### Task 4: Editor – whole book, cursor element, marking box, shortcuts

**Files:**
- Modify:
  - `src/NoteEvolution.UI/Editor/js/editor.js`
  - `src/NoteEvolution.UI/Editor/IEditorInterop.cs`
  - `src/NoteEvolution.UI/Editor/TipTapInterop.cs`
  - `src/NoteEvolution.UI/wwwroot/css/app.css`
  - `src/NoteEvolution.UI/wwwroot/js/editor.bundle.js` (rebuilt)
  - `tests/NoteEvolution.UI.Tests/FakeEditorInterop.cs`
  - all callers of the changed interface members, so it compiles: `EditorPane.razor`. Its logic changes in Task 5; here keep it compiling with the new signatures.
- Test: `tests/NoteEvolution.UI.Tests/EditorInteropTests.cs`, new, for the C#-side parsing in `TipTapInterop`

**Interfaces (produces):**

```csharp
namespace NoteEvolution.UI.Editor;
public sealed record CursorInfo(ElementKind Kind, Guid Key, Guid? TextBlockKey, int Offset); // TextBlockKey null for a heading
public enum SectionCommand { InsertAfter, InsertChild, Indent, Outdent, RemoveHeading, Delete }
// IEditorCallbacks: OnCursorBlockChanged is replaced by
Task OnCursorChanged(CursorInfo? cursor);
Task OnSectionCommand(SectionCommand command);
Task OnSectionBoxMoved(double barTop, bool visible);   // px relative to the editor pane; see JS below
// IEditorInterop: SetDocumentAsync(string docJson, bool manuscript, bool showChips) becomes
Task SetDocumentAsync(string docJson, bool showChips);
Task RevealAsync(Guid elementKey);                      // cursor at the start of the element's text, scrolled into view; Guid.Empty = start of the book
```

JS behavior (editor.js):
- **Look:** always the manuscript look. Remove the `mode-section` and `mode-manuscript` toggling; the root has class `mode-manuscript` permanently.
- **Cursor:** `onSelectionUpdate` calls `CursorChanged(kind, key, textBlockKey, offset)` whenever any of them changes.
  - `kind` is `heading`, `textBlock` (para at depth 0) or `detail` (para at depth ≥ 1, `key` = the para's key).
  - `offset` is `$from.parentOffset`.
- **Marking:** a decoration with class `ne-current` on the nodes of the current element's range.
  - For a heading: the heading plus the following top-level nodes up to the next heading of level ≤ its own.
  - For a text block: the node.
  - For a detail: the para plus the following paras of greater depth in the same block.
  - The decoration updates with the selection.
- **Bar position:** after each selection change, scroll and resize (once per animation frame, only on a change of at least 1 px), call `SectionBoxMoved(barTop, visible)`.
  - `barTop` is the range's bottom relative to the editor pane, clamped to the pane's visible bottom minus `barHeight`. `barHeight` is passed in `createEditor(host, dotnet, { barHeight })` as 40.
  - `visible` is whether the range intersects the visible area.
- **Shortcuts** (they call `SectionCommand(name)` and return true):
  - `Alt-Enter` → `InsertAfter`
  - `Alt-Shift-Enter` → `InsertChild`
  - `Tab` / `Shift-Tab` while the cursor is in a heading → `Indent` / `Outdent`. In paras the existing depth change stays.
  - `Backspace` at offset 0 of a heading's title with an empty selection → `RemoveHeading`
- **Empty headings:** `FixedHeadings` stays. Typing still cannot add or remove headings, but an empty heading title is allowed.
- **`reveal(key)`:** sets the selection at the start of the element's text and scrolls it into view. An unknown key does nothing; `Guid.Empty` means the start of the document.

- [ ] **Step 1: Write failing C# tests** for the interop parsing in `EditorInteropTests`. Drive the `TipTapInterop` `[JSInvokable]` methods directly with a fake callbacks object.
  - `CursorChanged_ParsesKinds`
  - `CursorChanged_BadKey_Ignored`
  - `SectionCommand_ParsesNames_UnknownIgnored`
  - `SectionBoxMoved_Forwarded`
- [ ] **Step 2: Run the tests.** They fail.
- [ ] **Step 3: Implement** the JS and C# changes, then run `npm run build`.
- [ ] **Step 4: Run the full suite.** It is green, and the build has 0 warnings.
- [ ] **Step 5: Commit** with `feat(editor): whole-book manuscript, cursor element, marking and section shortcuts`, including the bundle.

### Task 5: UI state – one view, current element, reveal, relevance topic

**Files:**
- Modify:
  - `src/NoteEvolution.UI/State/AppState.cs`, removing `ViewMode` and `Mode` and adding the members below
  - `src/NoteEvolution.UI/Components/EditorPane.razor`
  - `src/NoteEvolution.UI/Components/HeaderBar.razor`, removing the mode group
  - `src/NoteEvolution.UI/Components/OutlinePane.razor`
  - `src/NoteEvolution.UI/Components/RelevantTab.razor`
  - `src/NoteEvolution.UI/Components/NoteCard.razor` (✓-jump and WhereTo jump)
  - `src/NoteEvolution.UI/Components/Dialogs/LinkCheckDialog.razor`
  - `src/NoteEvolution.UI/Components/Shell.razor`
  - `src/NoteEvolution.UI/Resources/Strings.resx`: remove `ModeSection`, `ModeManuscript` and `ViewMode`; add `OutlineUntitled` = `(ohne Titel)` for empty headings in the outline
  - `src/NoteEvolution.UI/wwwroot/css/app.css`
  - existing tests that use `ViewMode`, `Mode` or `ne-mode-` (EditorPaneTests, RelevantTabTests, ShellTests)
- Test: `tests/NoteEvolution.UI.Tests/ManuscriptViewTests.cs`, new

**Interfaces:**
- Consumes: `CursorInfo` and `IEditorInterop.RevealAsync` (Task 4); `BookElements` (Task 1).
- Produces on `AppState`:
  - `CursorInfo? Cursor { get; set; }` replaces the settable `CursorTextBlockKey`.
  - `Guid? CursorTextBlockKey => Cursor is { Kind: not ElementKind.Heading } c ? c.TextBlockKey ?? c.Key : null;`
  - `BookElement? CurrentElement { get; }`: the element resolved from `Cursor` against `CurrentBook`, with the fallback below.
  - `void RevealElement(Guid elementKey)` raises `event Action<Guid>? RevealRequested`. It sets `CurrentSectionKey` to the element's section and keeps a pending reveal that `EditorPane` performs after its next document load.

Behavior:
- **EditorPane:**
  - It always loads `BookSnapshot.Create(book, vault, book.Root.Key, true)`.
  - The "Quellen anzeigen" checkbox always shows.
  - On `OnCursorChanged` it sets `State.Cursor` and `State.CurrentSectionKey = BookElements.SectionOf(...)`.
  - **Fallback when resolving:** an unknown detail key uses `TextBlockKey`, then the nearest heading before it, then `Guid.Empty`.
- **Outline:** `Select(node)` calls `State.RevealElement(node.Key)`. An empty title shows `OutlineUntitled`.
- **Jumps:** the NoteCard ✓ jump, the "Wohin damit?" jump and the LinkCheck jump set the book if needed and call `RevealElement` with the target block's key (a text block or a heading).
- **RelevantTab:** the topic is `new TopicRequest(book, State.CurrentSectionKey, State.CursorTextBlockKey, Manuscript: State.CursorTextBlockKey is not null)`. The `Refresh` tuple uses the same values.
- **PdfExportDialog:** unchanged; it already uses `CurrentSectionKey`, and the root means the whole book.

- [ ] **Step 1: Write the failing tests** in `ManuscriptViewTests` (bUnit with `FakeEditorInterop`):
  - `Header_HasNoViewToggle`
  - `Editor_LoadsWholeBook_WithAllHeadings`
  - `CursorInTextBlock_SetsSectionAndTextBlock`
  - `CursorOnHeading_TopicIsSection_NoCursorTextBlock`
  - `Cursor_UnknownDetailKey_FallsBackToTextBlock`
  - `OutlineClick_RevealsHeading_AndHighlightsNode`
  - `NoteCardUsedJump_RevealsBlock`
  - `LinkCheckJump_RevealsBlock`
  - `RelevantTab_UsesCursorTopic_And_HeadingTopic`
  - `PdfCurrentSection_IsSectionOfCursor`
  - `EmptyHeading_OutlineShowsPlaceholder`
  - Adapt the existing tests that referenced the view modes.
- [ ] **Step 2: Run the tests.** They fail.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the full suite.** It is green.
- [ ] **Step 5: Commit** with `feat(ui): manuscript is the only view; the cursor element drives the current section`.

### Task 6: UI – running structure commands with undo

**Files:**
- Create: `src/NoteEvolution.UI/State/ManuscriptCommands.cs`
- Modify:
  - `src/NoteEvolution.UI/Components/EditorPane.razor`, so `OnSectionCommand` runs the command and shows its message in the existing message line
  - `src/NoteEvolution.UI/Resources/Strings.resx`
- Test: `tests/NoteEvolution.UI.Tests/ManuscriptCommandTests.cs`

**Interfaces:**
- Consumes:
  - `ManuscriptEditor`, `PageStructureSnapshot` and `BookElements` (Task 1)
  - `ILinkService.DeleteSection` (Task 2) and the existing `DeleteTextBlock`
  - `AppState.CurrentElement` and `RevealElement` (Task 5)
- Produces:
  - `public static Task<AdoptMessage?> ManuscriptCommands.RunAsync(AppState state, SectionCommand command, string undoDescription, ILogger logger)`. It returns null on success, else the message (`AdoptMessage` reused: resource key plus whether it is an error).
  - `public static bool ManuscriptCommands.CanRun(AppState state, SectionCommand command)`, for the button states in Task 7.

`RunAsync` order, matching `NoteAdoption` and the model switch:
1. Flush the editor. A throw, or `state.HasUnsavedEditorText?.Invoke() == true` afterwards, returns `SectionCommandUnsaved` and changes nothing.
2. Take the book from the vault. A read-only page returns `EditorReadOnly`; an open conflict for the book returns `EditorConflict`.
3. Resolve `state.CurrentElement`. If there is none, or the command does not fit (`CanRun` is false), return null without a change.
4. Dispatch:
   - **Pure structure commands** (InsertAfter, InsertChild, Indent, Outdent, RemoveHeading, and Delete of a detail): capture `before`, apply the `ManuscriptEditor` operation, then `session.TrySave(page, out _)`. On success, capture `after` and push an undo action with `undoDescription`. Its `Undo()` takes the current page of the path from the vault. If `!after.Matches(page)` it throws `InvalidOperationException`; otherwise it calls `before.RestoreInto(page)` and saves through `TrySave`, throwing on failure. On a save failure other than an external change or a conflict, reload the page from disk as `OutlinePane.ReloadFromDisk` does, and return `SectionCommandFailed`.
   - **Delete of a text block:** `session.Links.DeleteTextBlock`.
   - **Delete of a heading:** `session.Links.DeleteSection`.
   - Exceptions are mapped as in `NoteAdoption.TryAdoptAsync`.
5. Finally: `state.RefreshBook()`, then `state.RevealElement(target)` (the new element, the moved heading, or the key `RemoveHeading` returned), then `state.Notify()`.

**Strings (exact):**

| Key | Value |
|---|---|
| `SectionCommandUnsaved` | `Der Text im Editor konnte nicht gespeichert werden; die Änderung wurde nicht ausgeführt.` |
| `SectionCommandFailed` | `Die Änderung konnte nicht gespeichert werden.` |
| `UndoStructure` | `Gliederung ändern` |

EditorPane passes `L["UndoStructure"]` as `undoDescription` (R6).

`EditorReadOnly` and `EditorConflict` exist already.

- [ ] **Step 1: Write the failing tests.** Use a real session with a test vault and assert the file's lines and the reveal target.
  - `InsertAfter_Heading_SavesAndRevealsNewHeading`
  - `InsertChild_TextBlock_RevealsNewDetail`
  - `Indent_Outdent_Heading`
  - `RemoveHeading_RevealsFirstMovedItem`
  - `Delete_Detail_Heading_TextBlock`: the heading variant removes usages
  - `Command_UnsavedEditorText_Refused_NothingChanged`
  - `Command_OpenConflict_Refused`
  - `Undo_RestoresFile_ForEachStructureCommand`
  - `Undo_AfterTypingIntoNewSection_Refused`: after `InsertAfter`, change the new heading's title through `Writer`; `TryUndo` is false and the file keeps the title
  - `Shortcut_RaisesCommand_ViaFakeEditor`: `FakeEditorInterop.Callbacks.OnSectionCommand(InsertAfter)` makes the file change
- [ ] **Step 2: Run the tests.** They fail.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the full suite.** It is green.
- [ ] **Step 5: Commit** with `feat(ui): run structure commands with undo`.

### Task 7: SectionBar – buttons and sources at the marking

**Files:**
- Create: `src/NoteEvolution.UI/Components/SectionBar.razor`
- Modify:
  - `src/NoteEvolution.UI/Components/EditorPane.razor`: it renders `<SectionBar>` inside `.ne-editor-pane`, which is `position: relative`, at `top: {barTop}px`, hidden while `!visible`; the position comes from `OnSectionBoxMoved`
  - `src/NoteEvolution.Core/Books/BookSnapshot.cs`: make the label builder public as `public static SourceInfo SourceOf(Guid noteId, IVault vault)`
  - `src/NoteEvolution.UI/Resources/Strings.resx`
  - `src/NoteEvolution.UI/wwwroot/css/app.css`
- Test: `tests/NoteEvolution.UI.Tests/SectionBarTests.cs`

**Interfaces:**
- Consumes: `ManuscriptCommands.RunAsync/CanRun` (Task 6), `BookElements.SourcesOf` (Task 1), `AppState.CurrentElement` (Task 5), `BookSnapshot.SourceOf`.
- Produces: `AppState.RevealNote(Guid noteId)`, raising `event Action<Guid>? NoteRevealRequested`. Task 8 handles the event.

**Content:**
- **Buttons** (classes / resource keys):
  - `.ne-sec-after` / `SectionInsertAfter` = `Neuer Abschnitt danach`
  - `.ne-sec-child` / `SectionInsertChild` = `Neuer Unterabschnitt`
  - `.ne-sec-indent` / `SectionIndent` = `Einrücken`, headings only
  - `.ne-sec-outdent` / `SectionOutdent` = `Ausrücken`, headings only
  - `.ne-sec-remove-heading` / `SectionRemoveHeading` = `Überschrift entfernen`, headings only
  - `.ne-sec-delete` / `SectionDelete` = `Abschnitt löschen`
- **Disabled states:** each button is disabled when `!CanRun`. With an open conflict or a read-only book, all are disabled.
- **Sources toggle** `.ne-sec-sources`:
  - `SectionSources` = `{0} Quellen ▸` / `SectionSourcesOpen` = `{0} Quellen ▾`
  - `SectionSourcesOne` = `1 Quelle ▸` / `SectionSourcesOneOpen` = `1 Quelle ▾`
  - `SectionNoSources` = `Keine Quellen` (not a button)
- **List** `.ne-sec-source-list`:
  - Entries are sorted by note date, then label. Notes without a date come last, by label.
  - Each entry is a button `.ne-sec-source` with `SourceInfo.Label`, which calls `State.RevealNote(noteId)`.
  - A broken entry is a `span.ne-sec-source-broken` with `SectionSourceMissing` = `Quelle fehlt`.
- **Expanded state:** whether the list is open survives element changes (a component field).

- [ ] **Step 1: Write the failing tests.**
  - `Buttons_FollowElementKind`: a heading shows all six; a text block and a detail show after/child/delete
  - `Buttons_DisabledOnConflict`
  - `InsertChild_DisabledOnLevel6`
  - `Click_RunsCommand`
  - `Sources_CountDistinct_PerKind`
  - `Sources_Expand_SortedByDate_MissingNotClickable`
  - `Sources_ExpandedStateSurvivesCursorMove`
  - `SourceClick_RaisesRevealNote`
  - `NoSources_ShowsKeineQuellen`
  - Extend `AiTests.NoGermanLiterals_InNewComponents`, or add an equivalent, for `SectionBar`: labels are data, not resources.
- [ ] **Step 2: Run the tests.** They fail.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the full suite.** It is green.
- [ ] **Step 5: Commit** with `feat(ui): section bar with commands and sources`.

### Task 8: Jump to a note in the journal

**Files:**
- Modify:
  - `src/NoteEvolution.UI/Components/NotesPane.razor`: it handles `NoteRevealRequested`
  - `src/NoteEvolution.UI/Components/JournalTab.razor`: new parameter `Guid? RevealKey` plus a sequence number
  - `src/NoteEvolution.UI/Components/NoteCard.razor`: new parameter `bool Highlighted`, adding class `ne-note-highlight`; the root element is focusable (`tabindex="-1"`)
  - `src/NoteEvolution.UI/Components/EditorPane.razor`: `OnChipClicked` calls `State.RevealNote(noteId)`
  - `src/NoteEvolution.UI/wwwroot/css/app.css`
- Test: `tests/NoteEvolution.UI.Tests/NoteRevealTests.cs`

**Behavior:**
1. `NotesPane` resolves the id with `session.Vault.FindBlockById` and takes the root note `NoteBlock` that contains the block.
2. If the root note has a date and is among `session.Notes.Journal(filter)`:
   - switch to the Journal tab;
   - `JournalTab` sets its date jump to that date and makes sure the note is within the shown entries;
   - it renders the card with `Highlighted`. The class is removed after 2 s, measured with the injected `TimeProvider` (R4);
   - it focuses the card's element after render (`FocusAsync`), which scrolls it into view.
3. Otherwise set `State.FocusedNoteKey` to the root note, which shows the existing card at the top. This covers a note without a date and a note hidden by the filter.

- [ ] **Step 1: Write the failing tests.**
  - `RevealNote_JournalNote_SwitchesTab_JumpsToDate_Highlights`
  - `RevealNote_SubBulletSource_HighlightsRootNote`
  - `RevealNote_HiddenByFilter_ShowsFocusedCard`
  - `RevealNote_NoteWithoutDate_ShowsFocusedCard`
  - `Highlight_RemovedAfter2s` (with FakeTimeProvider)
  - `ChipClick_RevealsNote`
- [ ] **Step 2: Run the tests.** They fail.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the full suite.** It is green.
- [ ] **Step 5: Commit** with `feat(ui): sources jump to the note in the journal`.

### Task 9: Adopt in three ways

**Files:**
- Modify:
  - `src/NoteEvolution.UI/State/NoteAdoption.cs`: new overload
  - `src/NoteEvolution.UI/Components/NoteCard.razor`
  - `src/NoteEvolution.UI/Resources/Strings.resx`
  - `src/NoteEvolution.UI/wwwroot/css/app.css`
- Test: `tests/NoteEvolution.UI.Tests/AdoptVariantTests.cs`

**Interfaces:**
- Consumes: `ILinkService.AdoptInto` and `IntoPosition` (Task 3), `AppState.CurrentElement` and `Cursor` (Task 5), `BookElements.LastHeadingKey` (Task 1).
- Produces:
  - `public enum AdoptVariant { AtCursor, After, Below }`
  - `NoteAdoption.AdoptAsync(AppState state, Guid noteBlockKey, AdoptVariant variant, ILogger logger)`. It keeps all checks of the existing overload. The target is resolved after the flush, from `state.CurrentElement` and `state.Cursor.Offset`.

**Mapping** (spec section 4):

| Element | AtCursor | After | Below |
|---|---|---|---|
| Heading | `Adopt(SectionStart(h))` | `Adopt(SectionStart(h))` | `Adopt(SectionStart(h))` |
| Text block t | `AdoptInto(AtCursor(t, offset))` | `Adopt(After(t))` | `AdoptInto(FirstChild(t))` |
| Detail d | `AdoptInto(AtCursor(d, offset))` | `AdoptInto(AfterDetail(d))` | `AdoptInto(FirstChild(d))` |

Without an element, every variant gives `Adopt(SectionEnd(LastHeadingKey ?? Guid.Empty))`.

**UI:** at the note and at every sub-bullet, a split button.
- The main button is `.ne-note-adopt` or `.ne-note-child-adopt`. It keeps its label `NoteAdopt` and adopts with AtCursor.
- The arrow is `.ne-note-adopt-more` or `.ne-note-child-adopt-more`, titled `NoteAdoptMore` = `Weitere Arten zu übernehmen`. It opens a menu with three items:
  - `.ne-adopt-at-cursor` / `AdoptAtCursor` = `Am Cursor`
  - `.ne-adopt-after` / `AdoptAfter` = `Danach`
  - `.ne-adopt-below` / `AdoptBelow` = `Darunter`
- The disabled state is as today (`CanAdopt`). Dropping into the editor stays on the old overload.

- [ ] **Step 1: Write the failing tests** with a real session and the resulting file lines:
  - `Adopt_AtCursor_TextBlockMidText`
  - `Adopt_After_TextBlock_NewBlock`
  - `Adopt_Below_TextBlock_FirstDetail`
  - `Adopt_After_Detail`
  - `Adopt_Below_Detail`
  - `Adopt_AnyVariant_OnHeading_FirstTextBlockOfSection`
  - `Adopt_NoElement_EndOfBook`
  - `Adopt_SubBullet_AtCursor`
  - `SplitButton_MenuShowsThreeVariants`
  - `SplitButton_MainClick_IsAtCursor`
  - `Adopt_ConflictOpen_Refused`
- [ ] **Step 2: Run the tests.** They fail.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the full suite.** It is green.
- [ ] **Step 5: Commit** with `feat(ui): adopt at the cursor, after or below the current element`.

### Task 10: README and verification (controller)

**Files:** Modify `README.md`.

- [ ] **Step 1: Update the README.**
  - The views section: one manuscript view, the marking, the section bar, the shortcuts and the three ways to adopt.
  - "Manuelle Abnahme", replacing the section-view checks:
    - the shortcuts in the real window;
    - the marking and the bar while scrolling;
    - adopting in each of the three ways;
    - a source jump into the journal;
    - a large book with several hundred blocks, typing and scrolling smoothly.
- [ ] **Step 2: Run the full suite.** Record the test count.
- [ ] **Step 3: Smoke run (R3).** Start the app, open a copy of a test vault and check the log. Run one structure command and one adoption against a copy of a test vault through a throwaway harness, and check the resulting file with `git diff --no-index`.
- [ ] **Step 4: Commit** with `docs: manuscript view in readme`.
