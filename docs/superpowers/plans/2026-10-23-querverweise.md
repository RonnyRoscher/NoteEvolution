# Querverweise, Verlauf, Ebenen-Knöpfe, „[handled]“-Ablauf, Verbrauchsanzeige – Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** In-book cross references `((id))` usable in both directions (editor, PDF), back/forward navigation between active blocks, Text/H1–H6 level buttons, a select-then-convert [handled] wizard, and a meter with Ollama in the line.

**Architecture:**
- **Core:** a pure `BlockRefs` parser plus a `CrossRefIndex` built from a `Book` snapshot. It maps target ids to headings and text blocks, gives the incoming references per target, and lists missing targets. Writers use it in the existing places: merges, heading removal, the new insert and level commands.
- **Editor:** `((id))` becomes an inline atom node. The editor gets display labels from C# and never writes them back.
- **Navigation:** a pure `NavigationHistory` owned by `AppState`.

**Tech Stack:** .NET 10, Blazor Hybrid (Photino), TipTap 2 (esbuild bundle, jsdom tests via `npm test`), QuestPDF, xUnit + bUnit.

**Spec:** `docs/superpowers/specs/2026-10-23-querverweise-design.md` (binding; section numbers below refer to it).

## Global Constraints

- **Reference format:** `((<uuid>))`, uuid in `D` format, case-insensitive on read. Targets are only headings or text blocks of the same book, identified by their `id::` block property (`Block.Id`). Anything else in `((…))` stays plain text (spec 2.1, 7).
- **Unchanged content stays byte-identical:** opening and saving a page whose text did not change writes nothing (`BookSync.Apply` → `Changed == false`).
- **Dates:** adding a reference stamps `updated` on the referring text block. Adding `id::` to a target does not stamp it. Level conversions stamp as in spec 4.1.
- **Undo:** every new command is one undo step. Use the existing `StructureUndo` / `PageStructureSnapshot` pattern of `ManuscriptCommands`.
- **Text and comments:** UI text German in `Strings.resx`; comments English matching surrounding density; colors from existing CSS variables.
- **Tests:** bUnit `WaitForAssertion` after state changes. Poll session state that is not tied to a render with a real-time loop (see `CoverageTabTests.Eventually`). Time through `TimeProvider`/`FakeTimeProvider`.
- **Editor bundle:** `npm run build` in `src/NoteEvolution.UI/Editor/js`; commit the rebuilt `wwwroot/js/editor.bundle.js`. JS tests: `npm test`.
- **Commits:** `git commit -F <file>`, each ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. SDK env (PowerShell): `$env:DOTNET_ROOT="$env:LOCALAPPDATA\Microsoft\dotnet"; $env:PATH="$env:DOTNET_ROOT;$env:PATH"`.
- **Process hygiene:** never kill processes you did not start; never touch master or other worktrees. For an app check, back up the user's `ui.json` and restore it byte-for-byte.

## Review Focus

1. **A reference inside bold or italic text** (`**siehe ((id))**`): mapping to the editor and back keeps the file byte-identical (Task 4).
2. **Merging three blocks where one is a reference target, and the referring block is one of the merged ones:** the references are retargeted and the text stays intact (Task 2).
3. **History after the referenced block was deleted:** going back skips the entry without an error (Task 7).
4. **H-level change with sub-headings at H6:** the button is disabled, never producing H7 (Task 8).
5. **[handled] convert where one note fails (read-only journal):** the others are written, the failed one stays in the list (Task 9).

---

### Task 1: `BlockRefs` and `CrossRefIndex` (Core)

**Files:**
- Create: `src/NoteEvolution.Core/Books/BlockRefs.cs`, `src/NoteEvolution.Core/Books/CrossRefIndex.cs`
- Test: `tests/NoteEvolution.Core.Tests/Books/CrossRefTests.cs`

**Interfaces:**
- **`BlockRefs`:**
  - `public readonly record struct BlockRef(Guid Id, int Start, int Length)`
  - `public static IReadOnlyList<BlockRef> BlockRefs.Find(string text)`
  - `public static string BlockRefs.Format(Guid id)` → `"((" + id.ToString("D") + "))"`
  - `public static string BlockRefs.Retarget(string text, IReadOnlyDictionary<Guid, Guid> map)`: replaces refs whose id is in the map.
  - `public static string BlockRefs.Remove(string text, IReadOnlySet<Guid> ids)`: removes those refs plus one adjacent space so no double space remains.
- **`CrossRefIndex`:**
  - `public sealed record CrossRefTarget(Guid Id, OutlineNode? Heading, TextBlock? TextBlock)`
  - `public sealed record CrossRefSource(Guid ElementKey, TextBlock TextBlock, Guid TargetId)`: the referring text block, or the paragraph key inside it.
  - `public static CrossRefIndex CrossRefIndex.Of(Book book)`
  - `public CrossRefTarget? Target(Guid id)`
  - `public IReadOnlyList<CrossRefSource> To(Guid targetId)`: in book order.
  - `public IReadOnlyList<CrossRefSource> Missing`: sources whose target is not in the book.

- [ ] **Tests first:**
  - Find refs in plain, bold and italic text and in sub-points; ignore malformed ones (`((abc))`, missing parens).
  - Upper-case uuids are found.
  - `Retarget` and `Remove` keep the surrounding text, and `Remove` leaves no double space.
  - `CrossRefIndex.Of`: resolves heading and text block targets by `Block.Id`; `To` is in book order; `Missing` lists refs to unknown ids; a ref to a sub-point id counts as missing (not a valid target).
- [ ] Implement, run the Core tests, commit "feat(core): block references and the cross reference index".

### Task 2: Keep references valid (merge, heading removal) and check them

**Files:**
- Modify: `src/NoteEvolution.Core/Links/LinkService.cs` (`MergeTextBlocks`, `MergeDetails`)
- Modify: `src/NoteEvolution.Core/Books/ManuscriptEditor.cs` (`RemoveHeading`)
- Modify: `src/NoteEvolution.Core/Links/LinkChecker.cs` (+ `LinkCheckReport`)
- Modify: the link-check dialog in `src/NoteEvolution.UI/Components/Dialogs/`, `Strings.resx`
- Test: Core merge, remove-heading and link-checker tests; the UI dialog test

**Interfaces:**
- Consumes: Task 1.
- Produces: `LinkCheckReport.MissingCrossRefs: IReadOnlyList<CrossRefSource>` and `LinkChecker.RemoveMissingCrossRefs(LinkCheckReport report) : int`.

- [ ] **Tests first:**
  - **Merge:** merging text blocks retargets every `((id))` in the book that points to a removed block, to the remaining block's id. If the remaining block has no `id::` and the removed ones were targets, it gets one (Review Focus 2, three blocks).
  - **MergeDetails:** retargets refs to merged sub-point ids. Sub-points are not valid targets, so this is mostly a no-op; test that nothing breaks.
  - **RemoveHeading:** the new text block made from the title takes over the heading's `id::`, so refs keep resolving.
  - **Undo:** undo of both restores the original bytes.
  - **Link checker:** reports missing cross refs with their place. `RemoveMissingCrossRefs` removes them, keeps the text and saves through the writer. The dialog shows the group "Querverweise ohne Ziel" with a remove button.
- [ ] Implement, run all tests, commit "feat: cross references survive merges and heading removal; link check finds missing ones".

### Task 3: Insert a reference (command and picker)

**Files:**
- Modify: `ManuscriptEditor.cs` (new method)
- Modify: `src/NoteEvolution.UI/State/ManuscriptCommands.cs`
- Create: `src/NoteEvolution.UI/Components/ReferencePicker.razor`
- Modify: the active-block footer (`SectionBar.razor`), `Strings.resx`, `app.css`
- Test: Core (`ManuscriptEditorRefTests`), UI (`ReferencePickerTests`)

**Interfaces:**
- Consumes: Task 1; the editor's cursor (`AppState.Cursor` with element key and offset in the element's Markdown text).
- Produces:
  - `public static void ManuscriptEditor.InsertReference(Book book, Guid elementKey, int offset, Guid targetKey, DateTimeOffset now)`:
    - `targetKey` is the target element's `Block.Key`.
    - The target gets an `id::` if it has none (no date stamp).
    - The ref is inserted at `offset`, clamped to the text.
    - The referring text block is stamped with `updated`.
    - Throws `ArgumentException` for an invalid target (sub-point, other book, the element itself).
  - `ManuscriptCommands.InsertReferenceAsync(...)`: editor flush first, one `StructureUndo` step, reveal the element.

- [ ] **Tests first:**
  - **Core:**
    - The ref is inserted at the offset, inside bold text too.
    - `id::` is added to the target only when it is missing.
    - `updated` is stamped on the referring block only.
    - Invalid targets throw; the element itself as target throws.
  - **UI:**
    - The "Verweis einfügen" button is enabled only for text blocks and sub-points.
    - The picker lists headings (path) and text blocks (section › excerpt) and filters case-insensitively.
    - Arrows and Enter insert; Escape cancels.
    - Read-only book and open conflict show the usual message and change nothing.
    - Undo restores the file.
- [ ] Implement, run all tests, commit "feat: insert a cross reference from the active block's footer".

### Task 4: References in the editor (TipTap node, mapper, click)

**Files:**
- Modify: `src/NoteEvolution.UI/Editor/EditorDocMapper.cs`
- Modify: `src/NoteEvolution.UI/Editor/js/editor.js` (new inline atom node `blockRef`), plus the rebuilt bundle
- Modify: `IEditorInterop.cs`, `TipTapInterop.cs`, `FakeEditorInterop`
- Modify: `EditorPane.razor`, `app.css`
- Test: `EditorDocMapperTests`, `EditorPaneTests`, `Editor/js/test/*.test.mjs`

**Interfaces:**
- Consumes: Task 1 (`BlockRefs.Find`, `CrossRefIndex`).
- Produces:
  - `EditorDocMapper.ToJson(..., Func<Guid, RefLabel>? refOf = null)` with `public sealed record RefLabel(string Text, string Title, bool Missing)`:
    - `Text` is "→ Titel" or "→ Abschnitt: Textanfang …" (40 chars).
    - Missing refs give "→ Verweis fehlt" with `Missing = true`.
  - The node `blockRef{id, label, title, missing}` is inline and atomic. It is written back as `((id))`; label, title and missing are never written.
  - The interop event `ReferenceClicked(Guid id)` reaches `EditorPane`. It reveals the target and records a jump in the history (Task 7; until Task 7 exists, call the reveal only and leave a `// history:` hook at that one place).

- [ ] **Tests first:**
  - **Mapper round-trip** for refs in plain, bold and italic text, at the start or end, and two in a row: an unchanged paragraph keeps its `md`, so the file stays byte-identical (Review Focus 1).
  - **Mapping back** a doc with changed labels gives the same snapshot.
  - **jsdom:** the node renders the label, the `missing` class and the tooltip; deleting the node with Backspace removes it whole; a click raises the event.
  - **bUnit:** a click event reveals the target.
- [ ] Implement, rebuild the bundle, run `npm test` and the UI tests, commit "feat(ui): cross references in the editor".

### Task 5: "Verweise hierher" in the footer

**Files:**
- Modify: `SectionBar.razor` (next to the sources toggle), `Strings.resx`, `app.css`
- Test: `SectionBarTests` (or the existing sources tests)

**Interfaces:**
- Consumes: `CrossRefIndex.To(targetId)`, built from the current book; recompute when the book instance changes.

- [ ] **Tests first:**
  - "↩ N Verweise hierher" is shown only for a target with references.
  - Expanding lists "Abschnitt › Textanfang" in book order; a click reveals the referring element (and records a jump, Task 7 hook).
  - The count updates after a save that adds a reference.
- [ ] Implement, run the UI tests, commit "feat(ui): references to the active block in its footer".

### Task 6: References in the PDF

**Files:**
- Modify: `src/NoteEvolution.Pdf/QuestPdfExporter.cs` and its input model if needed
- Test: `tests/NoteEvolution.Pdf.Tests/`

**Interfaces:**
- Consumes: Task 1.
- Produces: a ref renders as "→ Titel (S. n)", one internal link (`SectionLink`) to the target.
  - Heading targets reuse the heading's section id; text block targets get their own section id.
  - The page number comes from `BeginPageNumberOfSection`.
  - A target excluded from export (Relevant, Remark) renders "→ Titel" without page or link; a missing target renders "→ (Verweis fehlt)".

- [ ] **Tests first**, using the existing Pdf test approach (extracted text or the document model):
  - The rendered text contains "→ Abendroutine (S. " for a heading target.
  - A text block target shows the section title.
  - An excluded target has no page; a missing target shows "(Verweis fehlt)".
  - Export of a book without refs is unchanged.
- [ ] Implement, run the Pdf tests, commit "feat(pdf): cross references link to their target with its page".

### Task 7: Back/forward navigation

**Files:**
- Create: `src/NoteEvolution.UI/State/NavigationHistory.cs`
- Modify: `AppState.cs` (own the history; `RecordJump`, cursor dwell), `HeaderBar.razor` (◀ ▶ left of Undo)
- Modify: the reveal call sites (outline click, recent list, references, references-to-here, sources, search, coverage, book switch; replace the Task 4/5 hooks)
- Modify: `index.html` (global Alt+←/→ → interop), `Strings.resx`, `app.css`
- Test: `NavigationHistoryTests`, `HeaderBar`/`AppState` tests

**Interfaces:**
- `public sealed record NavEntry(string BookLink, Guid ElementKey)`
- `NavigationHistory(int capacity = 50)`:
  - `void Record(NavEntry entry)`: drops forward entries and skips a duplicate of the current entry.
  - `NavEntry? Back(Func<NavEntry, bool> exists)` and `NavEntry? Forward(Func<NavEntry, bool> exists)`: skip and remove entries that no longer exist.
  - `bool CanGoBack`, `bool CanGoForward`, `void Clear()`.
- `AppState`:
  - `void RecordJump(NavEntry entry)` records at once.
  - Cursor changes to another element record after `NavigationHistory.DwellTime = 2 s` of staying (via `TimeProvider`).
  - `Task GoBackAsync()` / `Task GoForwardAsync()` flush the editor, switch the book if needed and reveal.
  - The history is cleared on vault switch.

- [ ] **Tests first:**
  - **`NavigationHistory`:**
    - A duplicate of the current entry is not recorded again.
    - A new entry after Back drops the forward entries.
    - The capacity of 50 drops the oldest entries.
    - `exists` = false skips and removes the entry (Review Focus 3).
  - **`AppState`:**
    - A jump records at once; a cursor dwell records after 2 s of fake time; a quick pass through blocks records nothing.
    - Back switches the book; the vault switch clears the history.
  - **`HeaderBar`:** the buttons are disabled when there is nothing in that direction; a click goes back or forward.
  - **Shortcut:** Alt+←/→ through the interop event.
- [ ] Implement, run the UI tests, commit "feat(ui): back and forward between active blocks".

### Task 8: Level buttons Text/H1–H6

**Files:**
- Modify: `ManuscriptEditor.cs` (new methods), `ManuscriptCommands.cs`, `SectionBar.razor`, `Strings.resx`, `app.css`
- Test: Core (`LevelConversionTests`), UI (`SectionBarLevelTests`)

**Interfaces:**
- `public static Guid ManuscriptEditor.ToHeading(Book book, Guid elementKey, int level, DateTimeOffset now)`:
  - Text block or sub-point to heading, per spec 4.2.
  - Returns the new heading's key.
  - `id::`, `source::` and `status::` move to the first text block under the heading.
  - Sub-point hierarchy is kept, one level up.
- `public static void ManuscriptEditor.SetHeadingLevel(Book book, Guid headingKey, int level)`: shifts the heading's sub-headings by the same difference.
- `public static string? ManuscriptEditor.LevelBlocked(Book book, BookElement element, int level)`: null if possible, else the German reason (for the tooltip); the rules of spec 4.2.
- "Text" on a heading reuses `RemoveHeading`, which carries the `id::` per Task 2.
- `ManuscriptCommands`: one command per level, with flush, `StructureUndo` and reveal of the converted element.

- [ ] **Tests first:**
  - `ToHeading` with extra main text, with nested sub-points (hierarchy kept), and with following text blocks moving under the heading.
  - A sub-point becomes a heading after its text block.
  - `id::`, `source::` and status carry over; dates are stamped.
  - `SetHeadingLevel` with sub-headings shifts them.
  - `LevelBlocked`: H7 is impossible (Review Focus 4); a level above the parent is impossible; the prologue allows any level.
  - **UI:** the current level is highlighted ("Text" for text blocks and sub-points); disabled buttons show the reason; a click converts; undo restores the file bytes.
- [ ] Implement, run all tests, commit "feat: level buttons convert the active block between text and headings".

### Task 9: "[handled] umwandeln" select-then-convert

**Files:**
- Modify: `src/NoteEvolution.UI/Components/Dialogs/HandledWizard.razor`, `Strings.resx`
- Test: `DialogTests.cs` (handled wizard; `WaitForPlaces` helper)

- [ ] **Tests first:**
  - The wizard opens with no box checked.
  - Choosing a place checks its box; "Stelle unbekannt" unchecks it; manual toggling stays free.
  - "Umwandeln" is disabled with no box checked.
  - "Umwandeln" writes only the checked notes and keeps the dialog open; converted notes leave the list; the message reads "N umgewandelt".
  - The other notes keep their choices.
  - An empty list shows "Keine [handled]-Notizen mehr".
  - One read-only note: the others are written, the failed one stays, and the message names both counts (Review Focus 5).
  - "Schließen" closes the dialog.
- [ ] Implement, run the UI tests, commit "feat(ui): handled wizard converts the checked notes and stays open".

### Task 10: Meter with Ollama; header button spacing

**Files:**
- Modify: `SystemMeter.razor`, `SystemReadings.cs` (if formatting lives there), `HeaderBar.razor`, `app.css`, `Strings.resx`
- Test: `SystemMeterTests`, `HeaderBar` tests

- [ ] **Tests first:**
  - The meter text is "CPU 63 % (App 12 % · Ollama 40 %) · RAM 9,8/15,8 GB (App 0,6 · Ollama 4,9 GB) · Frei 112 GB · Analyse 38 MB".
  - Without Ollama its parts are absent.
  - The meter has no `title`.
  - Header order: ◀ ▶ Rückgängig, then Assistenten, PDF-Export, Verknüpfungsprüfung, Einstellungen, then the spacer, then the indicators.
- [ ] Implement, run the UI tests, commit "feat(ui): meter shows Ollama in the line; header buttons next to undo".

### Task 11 (controller): README, suite, app check

- [ ] **README:** describe each feature and add manual acceptance items.
- [ ] **Suite:** full `dotnet test` and `npm test`, green twice.
- [ ] **App check:** screenshots with a scratch vault.
