# Einordnen nach Ebene Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Überschriften werden nach ihrer Ebene in der Gliederung eingeordnet. Dazu kommen ein Befehl „Gliederung nach Ebenen ordnen“ und die Knöpfe „⇤ Ausrücken“ und „Einrücken ⇥“ für Textblöcke, Unterpunkte und Überschriften.

**Architecture:**
- **Core:** `ManuscriptEditor` bekommt eine Einordnungs-Routine `Place(book, node)` mit den Schritten Heraus- und Hineinrücken, die jeweils das Folgende mitnehmen. `SetHeadingLevel` und `Outdent` nutzen sie, der Buch-Befehl `NormalizeOutline` ebenso.
- **Textblöcke und Unterpunkte:** Zwei Core-Befehle übernehmen die Übergänge zwischen Textblock und Unterpunkt (`IndentTextBlock`, `OutdentDetail`).
- **Tiefere Unterpunkte** verschiebt der Editor selbst, wie Tab heute (`shiftDepth`). Die Knöpfe rufen ihn über die Interop auf.

**Tech Stack:** .NET 10, Blazor Hybrid, xUnit + bUnit, TipTap (esbuild, jsdom).

**Spec:** `docs/superpowers/specs/2026-10-26-einordnen-design.md`

## Global Constraints

- **Umgebung:**
  - .NET: `$env:DOTNET_ROOT="$env:LOCALAPPDATA\Microsoft\dotnet"; $env:PATH="$env:DOTNET_ROOT;$env:PATH"`, dann `dotnet test` im Worktree-Root.
  - Editor: `npm run build` und `npm test` in `src/NoteEvolution.UI/Editor/js`; das Bundle wird mit committet.
- **Bestehende Suite bleibt grün:** Core 879, Pdf 58, AI 306 + 1 übersprungen, UI 825, npm 17. Tests, die die alte Sperre „LevelBlockedOutOfParent“ prüfen, werden an die neue Regel angepasst; jede solche Anpassung wird im Bericht begründet.
- **Lesereihenfolge:** Jede Einordnung lässt die Reihenfolge aller Überschriften, Textblöcke und Unterpunkte unverändert.
- **Anhang:** Er bleibt unberührt. Es gibt keine Einordnung in den Anhang und keine aus ihm heraus.
- **Grenzen:** H1–H6. Ein Teilbaum darf nicht tiefer als H6 werden (`LevelBlockedBelowH6`) und nicht höher als H1 (`LevelBlockedAboveH1`).
- **Rückgängig:** Jeder Befehl ist ein Undo-Schritt (`StructureUndo`).
- **Texte** auf Deutsch in `Strings.resx`, Kommentare auf Englisch im Stil der Dateien.
- **Commits** enden mit `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Mitnehmen beim Herausrücken über zwei Stufen:** H3, tief unter H1 › H2 geschachtelt, wird zu H1. Alles, was danach in H2 und in H1 stand, gehört ihm an, und die Lesereihenfolge bleibt gleich. → Test in Task 1.
2. **Warnung „in einem Textblock“, verschachtelt:** Die Überschrift steht unter einem Unterpunkt eines Textblocks. Der Buch-Befehl macht sie zur Überschrift; ihre Unterpunkte werden Textblöcke unter ihr, wie bei `ToHeading`. → Test in Task 1.
3. **Unterpunkt Stufe 1 mit Quellen am Block ausrücken:** `source::` bleibt am alten Block, und die `used-in`-Einträge der Notizen bleiben gültig. Der neue Block hat kein `id::`, solange ihn nichts verlinkt. → Test in Task 2.
4. **Einrücken eines verlinkten Textblocks unter einen anderen:**
   - Seine `id::` und `source::` wandern auf den Unterpunkt, der dadurch entsteht.
   - Die Notizen zeigen weiter auf diese id: Ein Detail mit id ist ein gültiges Verweisziel, wie nach dem Zusammenfügen (`MergeDetails`).
   - Querverweise auf ihn bleiben gültig.

   → Test in Task 2.
5. **Knopf-Sperren im Grenzfall:** Ein Textblock als erstes Element eines Abschnitts (Einrücken gesperrt), ein Unterpunkt ohne Unterpunkt davor, eine H6-Unterüberschrift im Teilbaum. Grund im Tooltip. → Test in Task 3.

---

### Task 1: Einordnung nach Ebene (Core)

**Files:**
- Modify: `src/NoteEvolution.Core/Books/ManuscriptEditor.cs` (`SetHeadingLevel`, `Outdent`, `LevelBlocked`, neu `Place`, `NormalizeOutline`, `OutlineWarningsToFix`)
- Modify: `src/NoteEvolution.Core/Books/OutlineEditor.cs`, falls `Relevel`/`Shift` dorthin gehören
- Test: `tests/NoteEvolution.Core.Tests/Books/PlaceByLevelTests.cs`

**Interfaces:**
- Produces:
  - `public static void SetHeadingLevel(Book book, Guid headingKey, int level)`: verschiebt die `#` im Teilbaum wie heute, danach `Place`. Bei unveränderter Ebene nur `Place`.
  - `public static void Outdent(Book book, Guid headingKey)`: wird zu `SetHeadingLevel(book, key, level - 1)`.
  - `public static string? LevelBlocked(...)`: `LevelBlockedOutOfParent` entfällt für Überschriften; die übrigen Gründe bleiben.
  - `internal static void Place(Book book, OutlineNode node)`: Heraus- und Hineinrücken nach Spec 2.
  - `public static IReadOnlyList<Guid> MisplacedHeadings(Book book)`: alle Überschriften mit einer Einrückungs-Warnung, in Lesereihenfolge. Das sind die Fälle „nicht tiefer“ und „in einem Textblock“, ohne den Anhang.
  - `public static int NormalizeOutline(Book book, DateTimeOffset now)`: ordnet alle diese Überschriften ein und gibt ihre Zahl zurück.
    - Eine Überschrift „in einem Textblock“ geht über `ToHeading`, mit ihrer Ebene aus den `#`. Ist diese Ebene über der des Abschnitts gesperrt, gilt die Ebene des Abschnitts + 1, höchstens 6.
    - Jede andere geht über `SetHeadingLevel` mit ihrer eigenen Ebene.
    - Da Blöcke sich bewegen, wird `Book.Load(book.Page)` zwischen den Schritten neu gelesen.

- [ ] **Step 1: Write the failing tests:**
  - `SameLevel_UnderSameLevelParent_MovesBesideIt_TakesFollowing`: `## A` mit [Text a, `## B` mit Text b, Text danach] → A [Text a], B [Text b, Text danach], und B ist A's nächstes Geschwister.
  - `Outdent_TwoSteps_KeepsReadingOrder`: Review Focus 1.
  - `Indent_PreviousSiblingLowerLevel_BecomesItsLastChild`
  - `Outdent_CommandTakesFollowing`: Umschalt+Tab-Semantik.
  - `LevelBlocked_NoOutOfParent_ButH1H6Bounds`
  - `Appendix_Untouched`
  - `Normalize_FixesAllWarnings_OneCallReturnsCount`: Ein Buch mit beiden Warnarten hat danach `Warnings` leer; dazu Review Focus 2.
  - `ReadingOrder_Unchanged`: eine Hilfsfunktion, die Titel und Texte in Lesereihenfolge vor und nach jedem Test vergleicht.
- [ ] **Step 2:** `dotnet test tests/NoteEvolution.Core.Tests --filter FullyQualifiedName~PlaceByLevelTests`. Expected: FAIL.
- [ ] **Step 3:** Umsetzen.
- [ ] **Step 4:** `dotnet test`. Expected: grün; angepasste Alt-Tests sind im Bericht begründet.
- [ ] **Step 5:** Commit `feat: a heading's level decides its place in the outline`.

### Task 2: Textblock ↔ Unterpunkt (Core)

**Files:**
- Modify: `src/NoteEvolution.Core/Books/ManuscriptEditor.cs`
- Test: `tests/NoteEvolution.Core.Tests/Books/IndentTextTests.cs`

**Interfaces:**
- Produces:
  - `public static string? IndentBlocked(Book book, BookElement element)` und `string? OutdentBlocked(Book book, BookElement element)`. Rückgabe sind Ressourcen-Schlüssel:
    - `IndentBlockedNoPreviousText`: Textblock ohne Textblock davor im selben Abschnitt.
    - `IndentBlockedNoPreviousDetail`: Unterpunkt ohne Unterpunkt davor auf seiner Stufe.
    - `OutdentBlockedTextBlock`: Textblock.
    - Für Überschriften gelten die Gründe von Task 1 (`CanIndent`, H1/H6).
  - `public static Guid IndentTextBlock(Book book, Guid textBlockKey, DateTimeOffset now)`:
    - Der Block wird letzter Kind-Block (Unterpunkt Stufe 1) des vorherigen Textblocks; seine Kinder bleiben seine Kinder.
    - Der aufnehmende Block bekommt `updated::`.
    - Rückgabe ist der Schlüssel des Unterpunkts.
    - `id::`, `source::`, Status und Daten bleiben am verschobenen Block. Ein Unterpunkt mit id ist ein gültiges Ziel für `used-in` und Querverweise; die Prüfung des Link-Service für Details deckt das schon ab.
  - `public static Guid OutdentDetail(Book book, Guid detailKey, DateTimeOffset now)`:
    - Nur für Unterpunkte der Stufe 1; andere Stufen verschiebt der Editor.
    - Der Unterpunkt wird ein Textblock direkt nach seinem Textblock, mit beiden Daten.
    - Seine folgenden Geschwister (Stufe 1) werden seine Kinder, also Stufe 1 des neuen Blocks.
    - Der alte Block bekommt `updated::`.
    - `source::` am alten Block bleibt. Hat der Unterpunkt selbst `source::` oder `id::`, behält er sie.

- [ ] **Step 1: Write the failing tests:**
  - `IndentTextBlock_UnderPrevious_KeepsItsDetails_AndIdAndSource`: Review Focus 4. Eine Notiz mit `used-in:: [[Buch]] ((id))` und ein Querverweis bleiben gültig, geprüft über `CrossRefIndex` bzw. das Link-Service-`Resolve` der vorhandenen Tests.
  - `IndentTextBlock_FirstInSection_Blocked`
  - `OutdentDetail_BecomesTextBlockAfter_FollowingSiblingsComeAlong`
  - `OutdentDetail_SourceStaysOnOldBlock`: Review Focus 3.
  - `OutdentDetail_DeeperLevel_NotCore`: `ArgumentException`.
  - `Dates_StampedAsSpecified`
  - `ReadingOrder_Unchanged`
- [ ] **Step 2:** Filter. Expected: FAIL.
- [ ] **Step 3:** Umsetzen.
- [ ] **Step 4:** `dotnet test`. Expected: grün.
- [ ] **Step 5:** Commit `feat: indent a text block into the previous one, outdent a detail into its own text block`.

### Task 3: UI: Knöpfe, Ebenen-Knöpfe, Tab, Buch-Befehl, Legende, README

**Files:**
- Modify:
  - `src/NoteEvolution.UI/Components/SectionBar.razor`
  - `src/NoteEvolution.UI/State/ManuscriptCommands.cs`
  - `src/NoteEvolution.UI/Components/EditorPane.razor`
  - `src/NoteEvolution.UI/Editor/js/editor.js` und Bundle
  - `IEditorInterop.cs`, `TipTapInterop.cs`, `FakeEditorInterop.cs`
  - `src/NoteEvolution.UI/Components/HeaderBar.razor`
  - `src/NoteEvolution.UI/Components/Dialogs/NormalizeOutlineDialog.razor` (neu)
  - `Shell.razor`, `ShortcutsDialog.razor`, `Strings.resx`, `app.css`, `README.md`
- Test:
  - `tests/NoteEvolution.UI.Tests/IndentButtonsTests.cs`
  - `tests/NoteEvolution.UI.Tests/NormalizeOutlineTests.cs`
  - bestehende `SectionBarTests` und `DialogTests`

**Interfaces:**
- Consumes: Tasks 1 und 2.
- Produces:
  - **Knöpfe** `.ne-sec-outdent-any` („⇤ Ausrücken“) und `.ne-sec-indent-any` („Einrücken ⇥“) in der Leiste, mit Tooltip aus den Sperrgründen.
    - Sie rufen `ManuscriptCommands.IndentAsync(state, …)` bzw. `OutdentAsync(state, …)` auf.
    - Überschrift: Indent bzw. die neue Outdent-Semantik.
    - Textblock: `IndentTextBlock`.
    - Unterpunkt Stufe 1, Ausrücken: `OutdentDetail`.
    - Unterpunkte Einrücken und tiefere Stufen: der Editor über die neue Interop `Task ShiftDepthAsync(int delta)`, die in `editor.js` `shiftDepth(delta)` am Cursor ausführt. Das Speichern geht den normalen Weg.
  - **Tab / Umschalt+Tab im Editor:**
    - In Überschriften wie bisher, mit der neuen Outdent-Semantik.
    - Im ersten Absatz eines Textblocks (heute ohne Wirkung) ruft Tab `IndentTextBlock` über einen neuen `SectionCommand.IndentText` auf.
    - In einem Unterpunkt der Stufe 1 ruft Umschalt+Tab `OutdentDetail` über `SectionCommand.OutdentDetail` auf.
    - Alles andere bleibt `shiftDepth`.
  - **Ebenen-Knöpfe:** Der Knopf der aktuellen Ebene ist bei einer Überschrift mit Einrückungs-Warnung aktiv, mit dem Tooltip `SectionLevelPlace` = „Nach Ebene neu einordnen“, und ruft `SetLevelAsync` mit derselben Ebene auf. Ohne Warnung bleibt er wie heute ohne Wirkung.
  - **Assistent „Gliederung nach Ebenen ordnen“** (`.ne-assistant-normalize`):
    - Er öffnet `NormalizeOutlineDialog` mit „{0} Überschriften werden neu eingeordnet“ und der Titelliste. Bei 0 Überschriften erscheint „Die Gliederung ist schon nach Ebenen geordnet.“
    - Ausführen bedeutet `NormalizeOutline`, Speichern und Undo.
  - **Legende:** Die Einträge zu Tab / Umschalt+Tab nennen auch die Knöpfe und die neuen Übergänge.
  - **README:** „Ebene“ und „Einrücken“ im Abschnitt Gliederung, dazu Abnahme-Punkte.

- [ ] **Step 1: Write the failing tests:**
  - `Buttons_HeadingTextDetail_CallTheRightCommand_FileChecked`
  - `Buttons_Blocked_TooltipNamesReason`: Review Focus 5.
  - `Tab_FirstParagraph_IndentsTextBlock`
  - `ShiftTab_DetailLevel1_OutdentsIntoOwnTextBlock`
  - `DeeperDetail_UsesEditorShiftDepth`: Fake zählt `ShiftDepthAsync`.
  - `CurrentLevelButton_OnMisplacedHeading_Places`
  - `Normalize_Preview_Run_Undo`
  - jsdom: `shiftDepth an der Cursor-Stelle über die Interop`
- [ ] **Step 2:** Filter und `npm test`. Expected: FAIL.
- [ ] **Step 3:** Umsetzen. Danach `npm run build`.
- [ ] **Step 4:** `npm test` und `dotnet test` dreimal. Expected: grün.
- [ ] **Step 5:** Commit `feat: indent/outdent buttons, place-by-level in the UI, normalize outline`.
