# Verwendungen je Block — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every adopted block (text block and each detail) carries its own `source::` to the note block its text came from, every note block its own `used-in::`; links stay with their block through restructuring, move only on merge, can be loosened from the bar with undo, and an assistant adds them to existing books.

**Architecture:** `LinkService` maps each copied block to its note block while copying and writes all note updates of one adoption in one save of the note page. The "used" state keeps its existing rules (`NoteBlock.IsUsedBlock`); new adoptions mark note blocks with sub-points `used-without-children` so nothing inherits. A new `SubPointLinker` in `Core/Assistants` finds and applies the missing links for old adoptions. UI: the bar's source list becomes a list of links (block + note) with ×; a new assistant dialog.

**Tech Stack:** .NET 10, Blazor Hybrid (Photino), xUnit + bUnit, Logseq Markdown pages.

**Spec:** `docs/superpowers/specs/2026-11-10-verwendung-je-block-design.md`

## Global Constraints

- Build/test: PowerShell `$env:DOTNET_ROOT="$env:LOCALAPPDATA\Microsoft\dotnet"; $env:PATH="$env:DOTNET_ROOT;$env:PATH"; dotnet test NoteEvolution.slnx` (a `.slnx`, not `.sln`).
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; write message files without BOM (`[IO.File]::WriteAllText(path, text, (New-Object System.Text.UTF8Encoding $false))`).
- All UI text German, in `src/NoteEvolution.UI/Resources/Strings.resx`; code comments and identifiers English, in the style of the surrounding code (XML doc on public members).
- Only property lines change on note pages (`id::`, `used-in::`, `used-without-children::`); note text is never changed.
- One adoption writes the book page once and each note page once.
- `Link` (confirmed placement), `NoteBlock.IsUsedBlock`, `IsHiddenWhenUsedHidden`, `ShownChildren` keep their behaviour.
- Status `status:: relevant` only on the new top blocks of an adoption, as today.
- Text compare for "the text an adoption would make of S": sub-point content as is, for the adopted note block itself without `#notiz` (`NoteTag.Remove`); both trimmed.

## Review Focus

1. A note whose sub-points were adopted into two books, one of them by an old adoption (no own entries): undo or delete of one usage must leave the other book's sub-points marked used — test in Task 1 (mixed case).
2. A note page that cannot be written during an adoption with several sub-points: every sub-point's update must be queued in `pending.json` and completed by `RetryPending` — test in Task 1.
3. Two sub-points with the same text under one note: the assistant links neither and reports them as not matched — test in Task 5.
4. Loosening the only source of a detail that `MergeDetails` later merges: no stale usage pointing to a removed detail — test in Task 3.
5. Editor delete of a parent text block whose details the user lifted first: the lifted blocks keep their links and produce no `BlockDeleted` — test in Task 4.

---

### Task 1: Adopt links every copied block

**Files:**
- Modify: `src/NoteEvolution.Core/Links/LinkService.cs` (`Adopt`, `CopyOf`, `UpdateNote` callers, `UndoAdopt`, new private helpers)
- Modify: `src/NoteEvolution.Core/Links/ILinkService.cs` (doc of `Adopt` and the class summary)
- Test: `tests/NoteEvolution.Core.Tests/Links/AdoptPerBlockTests.cs` (new, uses `LinkSetup`)

**Interfaces:**
- Produces (private, used by Tasks 2 and 5):
  - `private static (Block Copy, List<(Block Copied, Block Note)> Pairs) CopyWithPairs(Block note, bool includeChildren, bool hideUsed)` — same copy as `CopyOf`, plus the pair list (copy root first, depth-first) of each copied block with its note block.
  - `private List<NoteChange> UpdateNotes(Page page, string linkName, IReadOnlyList<(Block Note, Guid BookBlockId, bool OwnOnly)> updates)` — applies every entry like `UpdateNote(..., remove: false, withoutChildren: OwnOnly)`, saves the page once; on a read-only or failing page queues one `PendingNoteUpdate` per entry. Returns one `NoteChange` per entry.
  - `private void LinkPairs(IEnumerable<(Block Copied, Block Note)> pairs, DateTimeOffset? now)` — `EnsureId` on both, `source:: ((note id))` on the copied block (added to an existing source without duplicate), dates on each copied block as `BlockDates.StampNew` does for the top.
- `OwnOnly` for a pair = the note block has at least one child (shown per `hideUsed`) — every new usage takes a block without its sub-points (spec 3.3).

- [ ] **Step 1: Write the failing tests** in `AdoptPerBlockTests`, journal:
  `"- Kern\n\t- Unter A\n\t\t- Tief A1\n\t- Unter B\n- Zweite Notiz\n"`
  - `Adopt_LinksEveryCopiedBlock`: after `Adopt(book, kern, SectionEnd(Abschnitt))` the book's text block has `source::` = Kern id, the details "Unter A", "Tief A1", "Unter B" each have `id::` and `source::` = id of their note block; each of the four note blocks has `used-in:: [[Buch - Test]] ((its book block id))`; "Kern" and "Unter A" have `used-without-children:: true`, "Tief A1" and "Unter B" not; only the text block has `status:: relevant`.
  - `Adopt_WritesTheNotePageOnce`: `Writer` counts one write of `JournalFile`.
  - `Adopt_WithoutChildren_LinksOnlyTheHead`: `includeChildren: false` → only "Kern" gets an entry, sub-points unused (`NoteBlock.IsUsedBlock` false).
  - `Adopt_HideUsed_LeavesOutUsedSubPoint`: "Unter B" already has `used-in:: [[Andere]]`; with `hideUsed: true` it is not copied, keeps its single entry.
  - `DeleteParentTextBlock_SubPointsStayUsed`: after adopt, `OutdentDetail` on "Unter A" and "Unter B" (they become text blocks), then `DeleteTextBlock` on the parent → "Kern" unused, "Unter A", "Tief A1", "Unter B" used.
  - `Undo_RemovesEveryEntryAndMark`: undo → journal bytes as before, book as before.
  - `NotePageReadOnly_QueuesEverySubPoint`: `Writer` fails the journal write → `Pending` holds 4 updates; after writer recovers, `Links.RetryPending()` returns 4 and all entries are on the page.
  - `MixedOldAndNewUsage`: journal "Kern" already `used-in:: [[Andere]]` (old style, no mark); new adopt → "Kern" has no `used-without-children` (old usage inherits); removing the old entry with `RemoveUsage(kernId, "Andere", null)` → mark set, each sub-point used only through its own entry.
- [ ] **Step 2: Run** `dotnet test tests/NoteEvolution.Core.Tests --filter AdoptPerBlockTests` — expected FAIL (details have no `source::`).
- [ ] **Step 3: Implement** the three helpers and switch `Adopt` to them; `UndoAdopt` restores via the existing `RestoreNotes(linkName, changes)` with the list. Update the `ILinkService` docs (class summary: usages per block; `Adopt`: each copied block linked). Existing tests in `AdoptTests`/`AdoptWithoutChildrenTests` that assert the old single-entry behaviour are updated to the new expectation, each change named in the commit message.
- [ ] **Step 4: Run** the Core test project — expected PASS.
- [ ] **Step 5: Commit** `Adopt links every copied block to its own note block`.

### Task 2: AdoptInto and AdoptAsHeading per block

**Files:**
- Modify: `src/NoteEvolution.Core/Links/LinkService.cs` (`AdoptInto`, `UndoAdoptInto`, `AdoptAsHeading`, `UndoAdoptAsHeading`, `AddSource`)
- Modify: `src/NoteEvolution.Core/Links/ILinkService.cs` (docs)
- Test: `tests/NoteEvolution.Core.Tests/Links/AdoptPerBlockTests.cs`

**Interfaces:**
- Consumes: `CopyWithPairs`, `UpdateNotes`, `LinkPairs` (Task 1).
- `AdoptResult.TextBlockId` stays the enclosing text block's id (callers use it for the view).

- [ ] **Step 1: Tests**
  - `AdoptInto_AtCursorInDetail_DetailCarriesTheSource`: book text block with detail "D"; `AdoptInto(AtCursor(D, 1))` with "Kern" → D has `id::` and `source::` Kern; the text block has no `source::`; inserted details for "Unter A"/"Tief A1"/"Unter B" linked to theirs; Kern's entry points to D's id.
  - `AdoptInto_AtCursorInTextBlock_TextBlockCarriesTheSource`.
  - `AdoptInto_AtCursor_NoteAlreadySource_NotTwice`: D already has `source::` Kern → stays one ref.
  - `AdoptInto_AfterDetail_NewDetailAndChildrenLinked` and `AdoptInto_FirstChild_...` likewise.
  - `AdoptAsHeading_DeeperDetailsLinked`: "Tief A1" (detail under text block "Unter A") has its own `source::`.
  - `AdoptInto_Undo_RemovesAll`, `AdoptAsHeading_Undo_RemovesAll`.
- [ ] **Step 2: Run** — FAIL.
- [ ] **Step 3: Implement.** At the cursor the pair for the note block itself is (element at cursor, note); the pairs of its children are the inserted details. `AddSource` takes any block.
- [ ] **Step 4: Run** Core tests — PASS (update old `AdoptInto` assertions "source on the text block" where the element is a detail).
- [ ] **Step 5: Commit** `AdoptInto and AdoptAsHeading link each block to its note block`.

### Task 3: Loosen a link on any block, with undo; MergeDetails to the head

**Files:**
- Modify: `src/NoteEvolution.Core/Links/LinkService.cs` (`RemoveSource`, `MergeDetails`)
- Modify: `src/NoteEvolution.Core/Links/ILinkService.cs`
- Test: `tests/NoteEvolution.Core.Tests/Links/RemoveAndDeleteTests.cs`, `MergeDetailsTests.cs`

**Interfaces:**
- Produces: `void RemoveSource(Book book, Guid blockKey, Guid noteBlockId)` — `blockKey` is a text block or a detail (`BookElements.Find` kind TextBlock or Detail, else `ArgumentException`). Removes the ref from `source::` (property gone when empty), removes the note's entry for this block and sets the mark as the other usages require; a missing note only changes the book. Pushes undo `"Verknüpfung lösen"`, which restores block lines and note lines while the block is still as it left it (same pattern as `UndoAdoptInto` with `CaptureSubtree`/`SubtreeIs`).

- [ ] **Step 1: Tests**
  - `RemoveSource_OnDetail_LoosensOnlyThatLink`: after adopt (Task 1 journal) remove "Unter A"'s source on its detail → detail keeps `id::` and text, no `source::`; note "Unter A" unused, "Tief A1" still used.
  - `RemoveSource_MissingNote_RemovesOnlySource`: detail with `source::` to an unknown id → removed, no note write.
  - `RemoveSource_Undo_RestoresSourceAndEntry`.
  - `RemoveSource_Undo_RefusedAfterBlockChanged`: block content changed after loosening → undo changes nothing.
  - `MergeDetails_IntoDetail_SourcesGoToTheHead`: text block T with detail H (source Kern-sub "Unter A") and H's child X (source "Tief A1"); `MergeDetails(H)` → H's `source::` has both, T unchanged, note "Tief A1"'s entry points to H's id; undo restores.
  - `MergeDetails_LoosenedDetail_NoStaleUsage` (Review Focus 4).
- [ ] **Step 2: Run** — FAIL.
- [ ] **Step 3: Implement**; in `MergeDetails` replace `textBlock` by `head` as receiver of sources, moves and `EnsureId`.
- [ ] **Step 4: Run** Core tests — PASS.
- [ ] **Step 5: Commit** `Loosen a source on any linked block with undo; MergeDetails moves links to the head`.

### Task 4: Reading the sources of a text block and its details; sync and link check

**Files:**
- Modify: `src/NoteEvolution.Core/Books/TextBlock.cs` (new `Links`, `AllSources`)
- Modify: `src/NoteEvolution.Core/Books/OutlineNode.cs:57` (`SourceCount` uses `AllSources`)
- Modify: `src/NoteEvolution.Core/Books/BookElements.cs:71` (`SourcesOf` uses `AllSources`)
- Modify (only if a test shows it is needed): `src/NoteEvolution.Core/Books/BookSync.cs`, `src/NoteEvolution.Core/Links/LinkChecker.cs`
- Test: `tests/NoteEvolution.Core.Tests/Books/BookModelTests.cs`, `BookSyncTests.cs`, `tests/NoteEvolution.Core.Tests/Links/LinkCheckerTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record SourceLink(Guid BlockKey, Guid? BlockId, Guid NoteId);` in `src/NoteEvolution.Core/Books/SourceLink.cs`.
  - `TextBlock.Links : IReadOnlyList<SourceLink>` — the text block's own refs, then its paragraphs' in file order (each block × each distinct ref).
  - `TextBlock.AllSources : IReadOnlyList<Guid>` — distinct `NoteId` of `Links`, first-seen order. `TextBlock.Sources` stays the own refs.

- [ ] **Step 1: Tests**
  - `TextBlock_Links_IncludeDetails` and `AllSources_Distinct`.
  - `OutlineNode_SourceCount_CountsDetailSources`.
  - `Sync_DeleteParentAfterLiftingChildren_OnlyParentDeleted` (Review Focus 5): book with linked text block T and two linked details; the editor's new document has T removed and its two details as sibling text blocks with the same keys and properties → effects: exactly one `BlockDeleted` (T's id); both lifted blocks keep `id::`/`source::`.
  - `LinkChecker_LinkedDetail_IsValid`: a detail with `source::` and the note's `used-in::` pointing to the detail's id → no finding.
- [ ] **Step 2: Run** — FAIL for the new members; sync/check tests show whether a fix is needed.
- [ ] **Step 3: Implement** the members and switch the two call sites; fix `BookSync`/`LinkChecker` only where their test fails.
- [ ] **Step 4: Run** Core tests — PASS.
- [ ] **Step 5: Commit** `Count the sources of details; detail links survive sync and pass the link check`.

### Task 5: SubPointLinker (assistant core)

**Files:**
- Create: `src/NoteEvolution.Core/Assistants/SubPointLinker.cs`
- Modify: `src/NoteEvolution.Core/Links/ILinkService.cs` + `LinkService.cs` (new method)
- Test: `tests/NoteEvolution.Core.Tests/Assistants/SubPointLinkerTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record SubPointMatch(Book Book, Guid DetailKey, Guid NoteBlockKey);`
  - `public sealed record SubPointPlan(IReadOnlyList<SubPointMatch> Matches, IReadOnlyList<(string BookLinkName, int Linked, int Unmatched)> PerBook) { public int Unmatched { get; } }`
  - `public sealed class SubPointLinker(IVault vault, ILinkService links)` with `SubPointPlan Find()` (spec 7.1: all books; per linked block B and each source note N, each detail of B without own `source::`; unique text match among all descendants of N; with several source notes exactly one may match; an S matched twice in B matches neither) and `void Apply(SubPointPlan plan)` → `links.LinkSubPoints(plan.Matches)`.
  - `ILinkService.LinkSubPoints(IReadOnlyList<SubPointMatch> matches)`: per book page one save (`id::`+`source::` on details), per note page one save (`id::`, entry, mark only if afterwards every descendant of the note block that the old usage covered has its own `used-in::`); read-only note page → `pending.json`; `FileChangedExternallyException` propagates with nothing written to that page; one undo `"Verknüpfungen nachtragen"` restoring every page still as it left it, and returning the paths it did not restore.

- [ ] **Step 1: Tests** (old-style book: text block with `source::` Kern, details "Unter A", "Tief A1", "Unter B" without; journal: only "Kern" has `used-in::`):
  - `Find_UniqueText_Matches` (3 matches), `Find_DuplicateSubPointText_NoMatch` (Review Focus 3), `Find_RewordedDetail_Unmatched`, `Find_TwoSourceNotes_OnlyUniqueAcrossBoth`, `Find_TagInDetailText_StillMatches` (`#notiz` removed only for the note block itself — detail equal to sub-point content).
  - `Apply_LinksAndMarksCompleteNote`: all three matched → Kern gets `used-without-children`; `Apply_PartialMatch_NoMark`.
  - `Apply_WritesEachPageOnce`, `Apply_SecondFindEmpty`, `Apply_Undo_RestoresBytes`, `Apply_ReadOnlyNotePage_Queued`.
- [ ] **Step 2: Run** — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Core tests — PASS.
- [ ] **Step 5: Commit** `SubPointLinker finds and adds the links of sub-points adopted before`.

### Task 6: UI — the bar's link list with ×, counts on all sources

**Files:**
- Modify: `src/NoteEvolution.UI/Components/SectionBar.razor` (source list from `TextBlock.Links` of the range; × per row; stays open)
- Modify: `src/NoteEvolution.UI/Components/EditorPane.razor` (`OnChipRemoved` → shared path; new handler for the bar's ×), `src/NoteEvolution.UI/State/BookTextSaver.cs` (`RemoveSource(Guid blockKey, Guid noteId)` doc: any linked block; undo pushed by the link service is labelled)
- Modify: `src/NoteEvolution.UI/Components/OutlineItem.razor:10`, `NoteBar.razor:814`, `SourceFinder.razor:79-96` (use `AllSources`)
- Modify: `src/NoteEvolution.UI/Resources/Strings.resx` (`SectionSourceLoosen` „Verknüpfung lösen“, `SectionSourceLoosenTitle` „Verknüpfung mit „{0}“ lösen“, `UndoLoosenSource` „Verknüpfung lösen“)
- Test: `tests/NoteEvolution.UI.Tests/SectionBarTests.cs`, `EditorPaneTests.cs`, `OutlinePaneTests.cs`, `NoteRevealTests.cs`

**Interfaces:**
- Consumes: `TextBlock.Links`, `AllSources` (Task 4); `ILinkService.RemoveSource(book, blockKey, noteId)` (Task 3).
- SectionBar gets `[Parameter] public EventCallback<SourceLink> OnLoosen`; EditorPane wires it to the same guarded path as `OnChipRemoved` (save first, refuse on `EditorChipConflict`).

- [ ] **Step 1: Tests**
  - `Sources_ListsDetailLinks_OneRowPerBlock` (same note on text block and detail → two rows, × tooltip names the block's first line ≤ 60 chars).
  - `Sources_Loosen_RemovesThere_ListStaysOpen`; `Sources_LoosenLast_ShowsNoSources`; `Sources_BrokenSource_HasLoosen`.
  - `Loosen_Undo_Restores` (via `State` undo); `Loosen_OpenConflict_ChangesNothing`; `ChipRemove_CanBeUndone`.
  - `Outline_SourceCount_IncludesDetails`.
  - `RevealUsage_OfDetail_JumpsToDetail`: a note's usage `((detail id))` jumps to the detail (fix the lookup if it only finds text blocks).
- [ ] **Step 2: Run** `dotnet test tests/NoteEvolution.UI.Tests --filter "SectionBarTests|EditorPaneTests|OutlinePaneTests|NoteRevealTests"` — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** UI tests — PASS.
- [ ] **Step 5: Commit** `Loosen a wrong source from the bar, with undo; counts include detail sources`.

### Task 7: UI — assistant dialog, README

**Files:**
- Create: `src/NoteEvolution.UI/Components/Dialogs/SubPointLinkDialog.razor` (modelled on `HandledWizard.razor`: preview, „Nachtragen“, messages)
- Modify: `src/NoteEvolution.UI/Components/HeaderBar.razor` (button `ne-assistant-subpoints` in `ne-assistants-popup`, `[Parameter] EventCallback OnSubPointLinks`), `Shell.razor` (`Dialog.SubPointLinks`)
- Modify: `Strings.resx` (`AssistantSubPoints` „Verknüpfungen der Unterpunkte nachtragen …“, dialog title, `SubPointsSummary` „{0} Unterpunkte in {1} Büchern werden verknüpft, {2} Details bleiben ohne eindeutigen Treffer.“, `SubPointsNone` „Alle Unterpunkte sind schon verknüpft.“, `SubPointsApply` „Nachtragen“, `UndoSubPoints` „Verknüpfungen nachtragen“, `SubPointsUndoKept` „Nicht zurückgenommen, weil inzwischen geändert: {0}“); conflict / unsaved / changed-vault messages reuse the `[handled]` wizard's keys
- Modify: `README.md` (sections on adopting and on assistants: usages per block, loosening, the new assistant)
- Test: `tests/NoteEvolution.UI.Tests/SubPointLinkDialogTests.cs` (new)

**Interfaces:**
- Consumes: `SubPointLinker` (Task 5).

- [ ] **Step 1: Tests** `Dialog_ShowsCountsPerBook`, `Dialog_NothingToDo`, `Apply_LinksAndCloses`, `Apply_OpenConflict_NothingWritten`, `Apply_UnsavedEditorText_Refused`, `Undo_FromHeader`.
- [ ] **Step 2: Run** — FAIL.
- [ ] **Step 3: Implement**, README.
- [ ] **Step 4: Run** the whole solution `dotnet test NoteEvolution.slnx` — PASS.
- [ ] **Step 5: Commit** `Assistant: add the links of sub-points adopted before`.
