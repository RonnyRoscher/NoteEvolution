# Datum je Textblock, „Zuletzt geändert“ und „Springen zum Tag“ – Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Book text blocks carry `created::`/`updated::` dates that are set when they are created or changed, shown in their editor header, and listed as "Zuletzt geändert" above the outline with a persisted resizable height; "Springen zum Tag" moves into the notes filter row.

**Architecture:**
- **Core:** a small `BlockDates` helper reads, formats and stamps the two properties. Every place that creates or changes a book text block stamps through it with the app clock: `BookSync.Apply` (editor saves), `ManuscriptEditor` (status, remark, insert), and `LinkService` (adopt a note).
- **UI:** the editor mapper passes display strings to the TipTap node view. A pure `RecentChanges` computes the list from the book snapshot, and a new vertical splitter persists the list height in `UiSettings`.

**Tech Stack:** .NET 10, Blazor Hybrid (Photino), TipTap 2 (bundled with esbuild), xUnit + bUnit.

**Spec:** `docs/superpowers/specs/2026-10-19-blockdaten-design.md` (binding; section numbers below refer to it).

## Global Constraints

- Property names and format: `created::` / `updated::`; written as local time `yyyy-MM-ddTHH:mm` (invariant culture); read also with seconds and with a zone; unreadable → missing, left in the file untouched unless the block changes (spec 2, 6).
- Only book text blocks get dates (not headings, not journal notes, not paragraphs inside a text block).
- A save without a real change keeps the page byte-identical (`BookSync.Apply` → `Changed == false`).
- Time comes from `IClock` (Core) — never `DateTime.Now` in code under test.
- UI text German in `Strings.resx`; comments English matching surrounding density; colors from existing CSS variables (`--fg-muted` for the grey dates).
- `UiSettings.RecentListHeight`: double, default 180; list min 60 px, outline min 120 px; keyboard step 20 px (spec 4.3).
- "Zuletzt geändert": 20 entries, newest `updated` first; empty hint „Noch keine Änderungen mit Datum“; hidden without a book (spec 4.1).
- Commits: `git commit -F <file>`; each message ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. SDK env (PowerShell): `$env:DOTNET_ROOT="$env:LOCALAPPDATA\Microsoft\dotnet"; $env:PATH="$env:DOTNET_ROOT;$env:PATH"`. Editor bundle: `npm run build` in `src/NoteEvolution.UI/Editor/js` (node is installed) — commit the rebuilt `wwwroot/js/editor.bundle.js`.

## Review Focus

1. **Opening and saving an unchanged book:** no line of the file changes; no block gains dates (Task 2 test `Apply_NoChange_ByteIdentical_NoDates`).
2. **A block whose `updated::` value is garbage (hand-edited):** reading does not throw; it is not rewritten on an unrelated save; it is replaced on the block's next change (Task 1 and 2 tests).
3. **Undo of an edit:** the editor's undo produces a save whose text differs from the last saved one, so `updated` changes again — expected; but undo of a status change via `StructureUndo` restores the old file lines, including the old `updated` (Task 3 test `SetStatus_Undo_RestoresDates`).
4. **Midnight and "gestern":** "heute"/"gestern" are computed from the clock's local date, not from elapsed hours (Task 5 test with 23:59 → 00:01).
5. **Stored height larger than the pane (small window):** the list is clamped for display, the stored value is unchanged (Task 6 test).

---

### Task 1: `BlockDates` helper and `TextBlock` dates

**Files:**
- Create: `src/NoteEvolution.Core/Books/BlockDates.cs`
- Modify: `src/NoteEvolution.Core/Books/TextBlock.cs`
- Test: `tests/NoteEvolution.Core.Tests/Books/BlockDatesTests.cs` (new)

**Interfaces:**
- Produces:
  - `public static class BlockDates`:
    - `CreatedKey = "created"`, `UpdatedKey = "updated"`
    - `public static DateTime? Created(Block block)` and `public static DateTime? Updated(Block block)`: local wall-clock time; null if the property is missing or unreadable.
    - `public static string Format(DateTimeOffset time)` → `time.ToLocalTime().ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture)`
    - `public static void StampNew(Block block, DateTimeOffset now)`: sets both.
    - `public static void StampChanged(Block block, DateTimeOffset now)`: sets `updated`, keeps `created` (missing stays missing).
  - `TextBlock.Created` / `TextBlock.Updated` (`DateTime?`), read from `Block`.

- [ ] **Step 1: Write failing tests** (`BlockDatesTests`):
  - `Read_MinuteFormat`
  - `Read_WithSeconds`
  - `Read_WithZone_ConvertedToLocal`
  - `Read_Garbage_Null_NoThrow`
  - `StampNew_SetsBoth_Format` (value `"2026-10-19T14:32"` for a fixed local time)
  - `StampChanged_KeepsCreated`
  - `StampChanged_WithoutCreated_OnlyUpdated`
  - `StampChanged_OverwritesGarbageUpdated`
  - `TextBlock_ExposesDates` (via `Book.Load` of a page with the properties)
- [ ] **Step 2: Run** `dotnet test tests/NoteEvolution.Core.Tests --filter FullyQualifiedName~BlockDates`. Expected: FAIL.
- [ ] **Step 3: Implement** with `Block.GetProperty`/`SetProperty`. Parse with `DateTimeOffset.TryParse` (invariant culture) for values that carry a zone, and `DateTime.TryParseExact` with formats `yyyy-MM-ddTHH:mm` and `yyyy-MM-ddTHH:mm:ss` for local values.
- [ ] **Step 4: Run** the Core tests. Expected: PASS.
- [ ] **Step 5: Commit** "feat(core): created and updated dates of book text blocks".

### Task 2: Editor saves stamp the dates (`BookSync`)

**Files:**
- Modify: `src/NoteEvolution.Core/Books/BookSync.cs`
- Modify: `src/NoteEvolution.UI/State/BookTextSaver.cs` (pass the session's clock)
- Test: `tests/NoteEvolution.Core.Tests/Books/BookSyncDatesTests.cs` (new)

**Interfaces:**
- Consumes: `BlockDates` (Task 1).
- Produces: `public static SyncResult Apply(Book book, SectionSnapshot snapshot, IVault? vault = null, DateTimeOffset? now = null)`. Without `now`, nothing is stamped, so existing callers and tests are unchanged. `BookTextSaver` passes `session.Clock.Now`, or whatever the session's `IClock` member is called.

- [ ] **Step 1: Write failing tests:**
  - `Apply_NewTextBlock_GetsBothDates`
  - `Apply_TextChanged_UpdatesOnly`: `created` is kept.
  - `Apply_ParagraphChanged_UpdatesItsTextBlock`
  - `Apply_StatusChanged_Updates`
  - `Apply_Split_NewPartGetsBothDates`
  - `Apply_Merge_RemainingUpdated`
  - `Apply_MovedOnly_NoDates`
  - `Apply_HeadingRenamed_NoDates`
  - `Apply_NoChange_ByteIdentical_NoDates` (Review Focus 1)
  - `Apply_ExistingBlockWithoutDates_ChangedGetsUpdatedOnly`
  - `Apply_GarbageUpdated_UnrelatedSaveKeepsIt` (Review Focus 2)
  - `Apply_WithoutNow_StampsNothing`
- [ ] **Step 2: Run** the filter `BookSyncDates`. Expected: FAIL.
- [ ] **Step 3: Implement** in `Run`:
  - A block gets `StampNew` when it is created (`CreateDetached` paths, including split and relinked).
  - It gets `StampChanged` when its content (own text or a paragraph) or its status is written with a value different from before. Compare before writing, and use the same equality the code already uses to keep unchanged blocks untouched.
  - Moving alone stamps nothing.
- [ ] **Step 4: Run** Core and UI tests. Expected: PASS.
- [ ] **Step 5: Commit** "feat(core): editor saves stamp created and updated".

### Task 3: Other paths that create or change text blocks

**Files:**
- Modify: `src/NoteEvolution.Core/Books/ManuscriptEditor.cs` (`SetStatus`, `AddRemark`, `InsertAfter`/`InsertChild` when they create a text block, any other creator of book text blocks found by searching `CreateDetached` in `Books/`)
- Modify: `src/NoteEvolution.Core/Links/LinkService.cs` (adopting a note into the book, `CopyOf`, around line 33)
- Modify: callers in `src/NoteEvolution.UI/State/ManuscriptCommands.cs` and the session/LinkService construction, which pass the clock
- Test: `tests/NoteEvolution.Core.Tests/Books/ManuscriptEditorDatesTests.cs` (new), the existing LinkService test file

**Interfaces:**
- Consumes: `BlockDates` (Task 1), `IClock`.
- Produces: an extra `DateTimeOffset now` parameter on the changed `ManuscriptEditor` methods; the `LinkService` gets the `IClock` through its constructor. Keep both optional where existing callers would otherwise all change, with the same rule as Task 2 (no `now` → nothing stamped).

- [ ] **Step 1: Write failing tests:**
  - `AddRemark_NewBlockHasBothDates`
  - `SetStatus_Updates`
  - `SetStatus_Undo_RestoresDates` (Review Focus 3, through `StructureUndo` as `ManuscriptCommands` uses it)
  - `InsertAfter_TextBlock_HasBothDates`
  - `InsertAfter_Heading_NoDates`
  - `Adopt_NewTextBlockHasBothDates` (LinkService)
- [ ] **Step 2: Run** them. Expected: FAIL.
- [ ] **Step 3: Implement**, stamping right where the block is created or its status is set.
- [ ] **Step 4: Run** all tests. Expected: PASS.
- [ ] **Step 5: Commit** "feat(core): new and re-statused text blocks get their dates".

### Task 4: Dates in the text block header (editor)

**Files:**
- Modify: `src/NoteEvolution.UI/Editor/EditorDocMapper.cs`
- Modify: `src/NoteEvolution.UI/Editor/js/editor.js`, then rebuild `src/NoteEvolution.UI/wwwroot/js/editor.bundle.js`
- Modify: `src/NoteEvolution.UI/wwwroot/css/app.css`, `Strings.resx`
- Test: the existing EditorDocMapper tests in `tests/NoteEvolution.UI.Tests/`

**Interfaces:**
- Consumes: `TextBlock.Created`/`Updated` (Task 1).
- Produces:
  - **Text block node attributes** `dates` (display text) and `datesTitle` (tooltip), both null when there are no dates.
  - **`public static string? BlockDateText.Of(DateTime? created, DateTime? updated, IStringLocalizer<Strings> l)`** (put it in `src/NoteEvolution.UI/Editor/`):
    - `"erstellt 12.10.2026 · geändert 19.10.2026 14:32"`
    - the creation time is shown only when it falls on the same day as `updated`
    - a missing part is dropped
    - null if both are missing
  - **Title:** `"Erstellt: 12.10.2026 09:15\nGeändert: 19.10.2026 14:32"`.

- [ ] **Step 1: Write failing tests:**
  - `BlockDateText` cases (both / only updated / only created / none / same day).
  - The mapper sets `dates`/`datesTitle` on the text block node.
  - Mapping a document back ignores these attributes. A doc with changed `dates` maps to the same snapshot.
- [ ] **Step 2: Run** them. Expected: FAIL.
- [ ] **Step 3: Implement.**
  - **Mapper:** fills the attributes. The localizer reaches the mapper the way existing localized strings do; if it has none, pass the formatter in.
  - **editor.js:** adds `dates`/`datesTitle` to the text block's attributes, with `parseHTML`/`renderHTML` like `statusAttribute`, so they survive transactions.
  - **Node view:** renders `<span class="ne-block-dates" contenteditable="false" title=…>` at the top right of the text block, and updates it in its `update()`.
  - **Not saved:** the attributes never reach the snapshot. Check `EditorDocMapper`'s reverse mapping and the JS that builds it.
  - **CSS:** `.ne-block-dates { float: right; font-size: 11px; color: var(--fg-muted); user-select: none; }`, or positioned so it never overlaps the status marker.
  - **Rebuild** the bundle.
- [ ] **Step 4: Run** the UI tests. Expected: PASS. Then build the app and look at one book page via a PrintWindow screenshot. Name the screenshot path in the report.
- [ ] **Step 5: Commit** "feat(ui): text blocks show their dates".

### Task 5: "Zuletzt geändert" list

**Files:**
- Create: `src/NoteEvolution.UI/State/RecentChanges.cs`, `src/NoteEvolution.UI/Components/RecentChangesList.razor`
- Modify: `src/NoteEvolution.UI/Components/OutlinePane.razor` (host the list above the tree), `Strings.resx`, `app.css`
- Test: `tests/NoteEvolution.UI.Tests/RecentChangesTests.cs` (new)

**Interfaces:**
- Consumes:
  - `TextBlock.Updated` (Task 1)
  - `VaultSession` access to the understanding: the existing `IUnderstanding.Heading(book, node)?.Text` path that "Wohin damit?" uses for summaries; null without the model
  - `PagesChanged`, `UnderstandingChanged`/`PlacementsChanged` (whichever fires when summaries arrive)
  - `RevealElement` (as CoverageTab uses it)
- Produces:
  - `public sealed record RecentChange(Guid TextBlockKey, DateTime Updated, string SectionPath, string Summary, string Excerpt)`
  - `public static IReadOnlyList<RecentChange> RecentChanges.Of(Book book, Func<OutlineNode, string?> sectionSummary, int limit = 20)`: Summary is the first sentence of the section summary, else the excerpt (80 chars).
  - `public static string RecentChanges.When(DateTime updated, DateTime now, IStringLocalizer<Strings> l)`: "heute 14:32" / "gestern 09:10" / "17.10.2026".

- [ ] **Step 1: Write failing tests:**
  - `Of_NewestFirst_AtMost20`
  - `Of_SkipsBlocksWithoutUpdated`
  - `Of_SummaryFirstSentence_ElseExcerpt`
  - `Of_SectionPath`
  - `When_Today_Yesterday_Date`
  - `When_Midnight` (23:59 updated, now 00:01 next day → "gestern 23:59"; Review Focus 4)
  - bUnit:
    - `List_ShowsEntries_ClickReveals`
    - `List_Empty_Hint`
    - `List_NoBook_Hidden`
    - `List_UpdatesAfterSave`
- [ ] **Step 2: Run** them. Expected: FAIL.
- [ ] **Step 3: Implement.**
  - **Where and when:** the list is computed on the UI thread from the current book, on `PagesChanged`, a book switch and the summaries' event.
  - **First sentence:** reuse the first-sentence logic of `PlacementCheck` if it is accessible; otherwise make a small shared helper rather than a second copy.
  - **Click:** stops propagation and reveals the block.
- [ ] **Step 4: Run** the UI tests. Expected: PASS.
- [ ] **Step 5: Commit** "feat(ui): recently changed text blocks above the outline".

### Task 6: Splitter between the list and the outline, persisted height

**Files:**
- Modify: `src/NoteEvolution.UI/Components/OutlinePane.razor` (layout); the existing `Splitter.razor` as a vertical variant (`Orientation` parameter) or a new `RowSplitter.razor`, whichever keeps both readable
- Modify: `src/NoteEvolution.UI/State/UiSettings.cs` (`RecentListHeight`, default 180, clamped on load like `LineWidthCh`), `app.css`, `index.html`/JS only if the existing splitter's JS needs a vertical case
- Test: `tests/NoteEvolution.UI.Tests/RecentChangesTests.cs` or a new `RowSplitterTests.cs`

**Interfaces:**
- Consumes: `UiSettings` save pattern (as `OutlineWidth`).
- Produces: `UiSettings.RecentListHeight`.

- [ ] **Step 1: Write failing tests:**
  - Dragging changes the height within [60, pane − 120].
  - Arrow up/down changes it by 20.
  - The value is saved on drag end and on keyboard change.
  - A new render restores it.
  - A stored height larger than the pane is clamped for display while `Settings.RecentListHeight` stays unchanged (Review Focus 5).
  - `UiSettings` load clamps a negative or absurd value to the default.
- [ ] **Step 2: Run** them. Expected: FAIL.
- [ ] **Step 3: Implement** with the existing pane-size JS (`neLayout.paneWidth` pattern) extended to a height if needed.
- [ ] **Step 4: Run** the UI tests. Expected: PASS.
- [ ] **Step 5: Commit** "feat(ui): resizable height of the recent changes list".

### Task 7: "Springen zum Tag" in the filter row

**Files:**
- Modify: `src/NoteEvolution.UI/Components/JournalTab.razor`, `NotesPane.razor` (filter row), `app.css`
- Test: the existing Journal tests in `tests/NoteEvolution.UI.Tests/`

**Interfaces:**
- Consumes: the existing jump state of `JournalTab` (`_jump`, `OnJump`). Move the input to `NotesPane`'s `.ne-notes-filters` row, shown only for the Journal tab, with the value passed to `JournalTab` as a parameter plus a callback.

- [ ] **Step 1: Write failing tests:**
  - On the Journal tab, `.ne-notes-filters` contains `.ne-journal-jump` and there is no `.ne-journal-jump-label` outside it.
  - On the other tabs, `.ne-journal-jump` is absent.
  - Jumping still filters the days, as in the existing test.
- [ ] **Step 2: Run** them. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the UI tests. Expected: PASS.
- [ ] **Step 5: Commit** "feat(ui): jump-to-day in the notes filter row".

### Task 8 (controller): README, suite, app check

- [ ] **README:**
  - book section: dates per text block and "Zuletzt geändert" with its splitter;
  - notes section: "Springen zum Tag" placement;
  - manual acceptance items.
- [ ] **Suite:** the full `dotnet test` is green twice.
- [ ] **App:** start it and take a PrintWindow screenshot of a book page with dates and the list. If the current vault has no book, say so.
