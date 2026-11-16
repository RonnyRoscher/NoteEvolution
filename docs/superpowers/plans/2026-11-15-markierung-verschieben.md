# Markierung verschieben Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The marked range of the book (a heading with its section, a text block with its sub-points, a sub-point with its deeper ones) can be dragged by a grip to another place in the same editor, the other half's editor or an outline.

**Architecture:** Core gets the missing moves (a section move that takes along what reads as its content, a section move "after a section", a detail move). One UI command (`ManuscriptCommands.MoveRangeAsync`) maps the dragged head and a drop description to the core move, with save, undo and reveal like the existing structure commands. The section bar gets a grip that sets `AppState.DraggedRange`; editors and outlines accept that drag.

**Tech Stack:** .NET 10, Blazor, TipTap/ProseMirror (`src/NoteEvolution.UI/Editor/js/editor.js`), xUnit, bUnit, the editor's JS tests.

**Spec:** `docs/superpowers/specs/2026-11-15-markierung-verschieben-design.md`

## Global Constraints

- Work in the worktree `C:\Users\Ronny Roscher\claude\NoteEvolution\.claude\worktrees\stufe-1`, branch `verschieben-mit-unterbloecken`. Never touch the main checkout.
- Tests (PowerShell): `$env:DOTNET_ROOT="$env:LOCALAPPDATA\Microsoft\dotnet"; $env:PATH="$env:DOTNET_ROOT;$env:PATH"; dotnet test tests/NoteEvolution.Core.Tests` (or `tests/NoteEvolution.UI.Tests`).
- Moved blocks keep `id::`, `source::`, status and dates; a move stamps no `updated::` — except where a sub-point becomes a text block (as `OutdentDetail` does). Notes are not touched.
- A refused move changes nothing in the file (byte for byte). Undo restores the file byte for byte.
- Maximum heading level 6. The appendix is never a source or a target.
- UI text is German; code, comments and commit messages are English. Commit messages end with `Co-Authored-By: Claude Fable 5.2 <noreply@anthropic.com>`.
- No bare `git stash`. TDD: failing test first. Follow the existing patterns of the file you change.

## Rulings made while planning

- The grip moves the **head** of the marked range (`RangeInfo.Head`) with everything below it, also when the marking shows the element without its sub-elements (level −1). No head (whole book): the grip is disabled.
- Headings taken along from the same indentation are nested by their level (as "Gliederung einordnen" would place them); after the move the existing `Relevel` sets levels by depth, as it does today.
- A section dropped inside its own section is refused with the existing message `OutlineMoveInvalid`.

## Review Focus

- A section whose tail in the file contains: a text block, a deeper heading with its own content, then a heading of the same level — only the first two come along. → Task 1.
- A section that is the last one before the appendix: the tail stops at the appendix. → Task 1.
- A detail moved onto its own text block's text, onto itself, onto one of its own deeper details, and the last detail of a block (the block keeps no empty sub-list). → Task 2.
- A drop while an editor has unsaved text, while the book has an open conflict, and after an external change between drag start and drop. → Task 3.
- A drag from one half dropped in the other half's editor and outline; the moved range is marked in the half where it was dropped. → Tasks 4, 5.

---

### Task 1: Section move takes its read content along; move after a section

**Files:**
- Modify: `src/NoteEvolution.Core/Books/OutlineEditor.cs`
- Test: `tests/NoteEvolution.Core.Tests/Books/` (the existing outline editor test file; add a file `SectionTailTests.cs` if that one is large)

Study first: `OutlineEditor.MoveSection`, `Relevel`, `ChildBlocks`, `ManuscriptEditor.MisplacedHeadings` and the command that places them (used by `ManuscriptCommands.NormalizeOutlineAsync`) — reuse its placing rule for headings taken along.

**Interfaces — Produces:**

```csharp
/// <summary>
/// The blocks after the heading's block under the same parent that are read as part of its section: text blocks and
/// headings of a deeper level, up to (not including) the next heading of the same or a higher level, an appendix
/// section or the end of the parent. Empty for a heading without such blocks.
/// </summary>
public static IReadOnlyList<Block> ReadTail(Book book, Guid headingKey);

/// <summary>Moves the section to the same level as <paramref name="anchorSectionKey"/>, directly after it
/// (<see cref="Guid.Empty"/>: first section of the top level).</summary>
/// <exception cref="ArgumentException">As MoveSection; also if the anchor is the node or inside its subtree.</exception>
public static void MoveSectionAfter(Book book, Guid nodeKey, Guid anchorSectionKey);
```

`MoveSection` (and so `MoveSectionAfter`) changes: if the target is the place the section already has, nothing changes (not even the tail). Otherwise the `ReadTail` blocks are first nested under the heading, in order: a text block under the deepest open heading at the section's end, a heading under the nearest open heading of a smaller level; then the move and `Relevel` as today. All checks (own subtree, level 6 counted with the tail, appendix) happen before anything changes.

- [ ] **Step 1: Failing tests** (book texts as in the existing outline tests; assert the whole file text):
  - `ReadTail_TextAndDeeperHeadings_UpToTheNextHeadingOfSameOrHigherLevel`
  - `ReadTail_StopsAtTheAppendix`, `ReadTail_NothingForAProperlyNestedSection`
  - `MoveSection_TakesTheTailAlong_NestedUnderTheHeading` (tail: text, `###` with text, text after it; expected nesting under the moved `##`)
  - `MoveSection_TailHeadingsAreNestedByLevel` (`##`, then `###`, `####`, `###` on the same indent)
  - `MoveSection_SamePlace_ChangesNothing_AlsoWithATail`
  - `MoveSection_TailWouldExceedLevel6_Refused_FileUnchanged`
  - `MoveSection_KeepsIdsSourcesAndDatesOfTheTail`
  - `MoveSectionAfter_SameLevelDirectlyAfterTheAnchorSection` (anchor with sub-sections: the moved one comes after all of them), `MoveSectionAfter_Root_FirstTopLevelSection`, `MoveSectionAfter_LevelsShift`, `MoveSectionAfter_AnchorInsideTheMovedSection_Refused`, `MoveSectionAfter_AnchorIsThePreviousSibling_ChangesNothing`
  - The existing `MoveSection` tests stay unchanged and green.
- [ ] **Step 2: Run** `dotnet test tests/NoteEvolution.Core.Tests --filter "ReadTail|MoveSection"` — fails.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the whole Core test project — green.
- [ ] **Step 5: Commit** "Section move takes along what reads as its content; move after a section".

### Task 2: Move a sub-point with its deeper sub-points

**Files:**
- Modify: `src/NoteEvolution.Core/Books/ManuscriptEditor.Move.cs`
- Test: `tests/NoteEvolution.Core.Tests/Books/` (next to the `MoveTextBlocks` tests)

Study first: `MoveTextBlocks`, `OutdentDetail` (how a detail becomes a text block: dates, sources, ids, references), `IndentTextBlock`, `Paragraph`/`TextBlock`.

**Interfaces — Produces:**

```csharp
public abstract record DetailTarget
{
    /// <summary>First sub-point (depth 1) of the text block.</summary>
    public sealed record FirstOf(Guid TextBlockKey) : DetailTarget;
    /// <summary>Directly after the detail and its deeper details, at the same depth.</summary>
    public sealed record After(Guid DetailKey) : DetailTarget;
    /// <summary>A text block of its own: first text block of the section (Guid.Empty = prologue).</summary>
    public sealed record SectionStart(Guid SectionKey) : DetailTarget;
    /// <summary>A text block of its own: last text block of the section.</summary>
    public sealed record SectionEnd(Guid SectionKey) : DetailTarget;
}

/// <summary>Moves the detail with all details below it. Returns whether the page changed.</summary>
/// <exception cref="ArgumentException">Unknown detail or target, appendix, target is the detail itself or one below it; nothing is changed.</exception>
public static bool MoveDetail(Book book, Guid detailKey, DetailTarget target, DateTimeOffset now);
```

`FirstOf` / `After`: only the place changes (lines unchanged apart from indentation, nothing stamped); a target that is the place it already has returns false. `SectionStart` / `SectionEnd`: the detail becomes a text block exactly as `OutdentDetail` makes one (same treatment of dates, sources, `#notiz`, references), its deeper details become its details.

- [ ] **Step 1: Failing tests:** `MoveDetail_OntoATextBlock_BecomesItsFirstSubPoint`, `MoveDetail_AfterADetail_SameDepth_AfterItsDeeperOnes`, `MoveDetail_DeeperDetailsKeepTheirDistance`, `MoveDetail_ToAnotherTextBlockInAnotherSection`, `MoveDetail_SectionStart_BecomesATextBlock_LikeOutdent` (compare with the result of `OutdentDetail` on the same detail regarding dates and sources), `MoveDetail_SectionEnd`, `MoveDetail_LastDetailOfABlock_LeavesTheBlockClean`, `MoveDetail_SamePlace_ReturnsFalse_FileUnchanged`, `MoveDetail_OntoItselfOrItsOwnDeeperDetail_Refused_FileUnchanged`, `MoveDetail_IntoTheAppendix_Refused`, `MoveDetail_KeepsIdAndSourcesOfDetails` (for FirstOf/After).
- [ ] **Step 2: Run** `--filter MoveDetail` — fails.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the whole Core test project — green.
- [ ] **Step 5: Commit** "Move a sub-point with its deeper sub-points".

### Task 3: The move command and the drag state

**Files:**
- Modify: `src/NoteEvolution.UI/State/ManuscriptCommands.cs`, `src/NoteEvolution.UI/State/AppState.cs`, `src/NoteEvolution.UI/Resources/Strings.resx`
- Test: `tests/NoteEvolution.UI.Tests/` (new `MoveRangeTests.cs`, pattern of the tests for `MoveTextBlocksAsync`)

Study first: `ManuscriptCommands.MoveTextBlocksAsync` (prepare, snapshot, save, `StructureUndo`, reveal, notify), how `OutdentAsync` records its undo (a detail turned into a text block changes more than the structure), `AppState.DraggedSectionKey` / `DraggedTextBlockKeys`, `OutlinePane.MoveTextBlocks` (end of a section's text).

**Interfaces:**
- Consumes: `OutlineEditor.MoveSection`, `MoveSectionAfter` (Task 1); `ManuscriptEditor.MoveDetail`, `DetailTarget` (Task 2); `ManuscriptEditor.MoveTextBlocks`.
- Produces:

```csharp
/// <summary>Where a dragged range is dropped.</summary>
public abstract record RangeDrop
{
    /// <summary>In an editor, on the element with this key (heading, text block or detail); null: above the first element.</summary>
    public sealed record InEditor(Guid? ElementKey) : RangeDrop;
    /// <summary>On a heading of an outline (Guid.Empty = prologue).</summary>
    public sealed record OnSection(Guid SectionKey) : RangeDrop;
    /// <summary>In front of a heading of an outline.</summary>
    public sealed record BeforeSection(Guid SectionKey) : RangeDrop;
    /// <summary>At the end of an outline.</summary>
    public sealed record AtEnd : RangeDrop;
}

// AppState
public BookElement? DraggedRange { get; set; }          // the head being dragged by the grip; null otherwise
public BookElement? MovableRangeHead { get; }           // the head of the marked range if it can be moved, else null

// ManuscriptCommands
public static Task<AdoptMessage?> MoveRangeAsync(AppState state, BookElement head, RangeDrop drop, string undoDescription, ILogger logger);
```

Mapping (spec 5):

| Head | `InEditor(element)` | `OnSection(s)` | `BeforeSection(s)` | `AtEnd` |
|---|---|---|---|---|
| Heading | `MoveSectionAfter(head, section containing the element)`; a heading element is its own section; null → `Guid.Empty` | `MoveSection(head, s, last)` | sibling before `s` | last top-level section |
| TextBlock | heading → `SectionStart(heading)`; text block or detail → `After(its text block)`; null → `SectionStart(Guid.Empty)` | end of the text of `s` | nothing | nothing |
| Detail | heading → `SectionStart`; text block → `FirstOf`; detail → `After`; null → `SectionStart(Guid.Empty)` | `SectionEnd(s)` | nothing | nothing |

Refusals show: level 6 → new string `MoveRangeTooDeep` ("Der Abschnitt würde tiefer als Ebene 6."), own section / other argument errors → `OutlineMoveInvalid`, as the existing commands for conflict, read-only and failed save. After a move the moved head is revealed and is the current element with range level 0 in the active view. Undo description string `UndoMoveRange` = "Bereich verschieben". `MovableRangeHead` is null without a book, without a head, in the appendix, for the prologue's pseudo section, with an open conflict or a locked editor.

- [ ] **Step 1: Failing tests:** one per table cell that does something (the file text after the move), `Nothing` cells leave the file unchanged without a message; `MoveRange_Undo_RestoresTheFileByteForByte` for a section with a tail, for a detail made a text block and for a text block; `MoveRange_RevealsAndMarksTheMovedHead`; `MoveRange_Level6_ShowsTheMessage_FileUnchanged`; `MoveRange_IntoItsOwnSection_Refused`; `MoveRange_OpenConflict_Refused`; `MoveRange_UnsavedEditorTextIsSavedFirst`; `MovableRangeHead_NullInTheListedCases`.
- [ ] **Step 2: Run** `dotnet test tests/NoteEvolution.UI.Tests --filter MoveRange` — fails.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the whole UI test project — green.
- [ ] **Step 5: Commit** "Move command for the marked range".

### Task 4: Grip at the section bar; dropping in an editor

**Files:**
- Modify: `src/NoteEvolution.UI/Components/SectionBar.razor`, `src/NoteEvolution.UI/Components/EditorPane.razor`, `src/NoteEvolution.UI/Editor/js/editor.js` (and the built bundle, as the repo keeps it — see how `build.mjs` and the csproj produce `editor.bundle.js`), `Strings.resx`, `wwwroot/css/app.css`
- Test: `tests/NoteEvolution.UI.Tests/` (section bar and editor pane tests), `src/NoteEvolution.UI/Editor/js/test/`

Study first: the grip of the notes bar (`NoteBar.razor`, `.ne-note-drag`, how its drag is announced and how `EditorPane.NoteDropped` takes it), `EditorPane`'s `BlocksDropped`, `dropTarget` and the `drop` handler in `editor.js`.

**Interfaces:**
- Consumes: `AppState.DraggedRange`, `MovableRangeHead`, `ManuscriptCommands.MoveRangeAsync`, `RangeDrop.InEditor` (Task 3).
- Produces: in `editor.js` a function `dropElement(view, event)` returning the key of the innermost element under the pointer (a sub-point paragraph's key, else the text block's or heading's key, null above the first element); the editor's drop callback passes it in addition to what it passes today. In `SectionBar` an element `.ne-sec-drag` (text "⠿", `draggable`, title and aria-label from `SectionDragTitle` = "Markierten Bereich verschieben"), disabled look and not draggable while `MovableRangeHead` is null.

Behaviour: drag start on the grip sets `State.DraggedRange` to the head (drag end clears it). A drop in an editor while `DraggedRange` is set calls `MoveRangeAsync(head, new RangeDrop.InEditor(elementKey))` in the editor's own view (which becomes the active one first, as for dropped blocks) instead of treating the drop as a note. A note drop and the block drag at the coloured bar work as before.

- [ ] **Step 1: Failing tests:** JS: `dropElement` for a pointer over a heading, a text block's own text, a sub-point, above the first element. bUnit: grip present with a movable head, not draggable without; drag start/end set and clear `DraggedRange`; the editor's drop callback with `DraggedRange` set moves the range (file text) for head kinds heading, text block, detail, and does not adopt a note; without `DraggedRange` a note drop works as before; a drop in the other half's editor moves there and makes that view active.
- [ ] **Step 2: Run** the JS tests (see `package.json`) and `dotnet test tests/NoteEvolution.UI.Tests --filter "SectionBar|EditorPane|MoveRange"` — fails.
- [ ] **Step 3: Implement**, rebuild the bundle.
- [ ] **Step 4: Run** JS tests and the whole UI test project — green.
- [ ] **Step 5: Commit** "Grip at the section bar; the marked range can be dropped in an editor".

### Task 5: Dropping on an outline; README

**Files:**
- Modify: `src/NoteEvolution.UI/Components/OutlinePane.razor`, `OutlineItem.razor` (only if the hint classes need it), `README.md`
- Test: `tests/NoteEvolution.UI.Tests/` (outline pane tests)

**Interfaces:**
- Consumes: `AppState.DraggedRange`, `ManuscriptCommands.MoveRangeAsync`, `RangeDrop.OnSection`, `BeforeSection`, `AtEnd` (Task 3).

Behaviour: while `DraggedRange` is set, the outline shows drop hints like for a dragged section (head is a heading: on, before, end) or like for dragged text blocks (text block or detail: only on a heading or the prologue). A drop calls `MoveRangeAsync` with the matching `RangeDrop` in that outline's view. The existing section drag of the outline now takes the read tail along (Task 1) — add one outline test for it.

README: in the part on the book view, a paragraph "Markierung verschieben" (grip, what comes along, where it can be dropped, the depth rules of spec 5, undo), the sentence that a section takes along everything read under its heading even if the file does not nest it, and checklist items for the manual check (section, text block, sub-point; same editor, other half, outline).

- [ ] **Step 1: Failing tests:** `Outline_DraggedHeadingRange_OnBeforeEnd`, `Outline_DraggedTextBlockRange_OnAHeading_EndOfItsText`, `Outline_DraggedDetailRange_OnAHeading_BecomesItsLastTextBlock`, `Outline_DraggedTextBlockRange_BeforeAHeading_DoesNothing`, `Outline_DropFromTheOtherHalf`, `Outline_SectionDrag_TakesTheReadTailAlong`.
- [ ] **Step 2: Run** `--filter Outline` — fails.
- [ ] **Step 3: Implement**, write the README part.
- [ ] **Step 4: Run** `dotnet test NoteEvolution.slnx` — green.
- [ ] **Step 5: Commit** "The marked range can be dropped on an outline; docs".
