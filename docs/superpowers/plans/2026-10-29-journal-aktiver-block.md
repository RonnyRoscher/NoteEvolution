# Journal: verwendete Blöcke und aktiver Block Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:**
- Verwendete Blöcke werden auf jeder Ebene ausgeblendet bzw. grau gezeigt und mit ✓ und Sprung zum Ziel markiert.
- Die Knöpfe an den Notizkarten werden durch einen aktiven Block mit Rahmen, Bereich und Leiste ersetzt, wie in der Buchansicht.

**Architecture:**
- Teil A ändert nur die Darstellung in `NoteCard`/`JournalTab`; die Filterregel `IsUsed` gibt es schon.
- Teil B bekommt einen Zustand `NoteSelection` in `AppState` (Block, Bereichsstufe) und eine reine Bereichslogik `NoteRanges` über den Blockbaum der Notiz.
- Die Übernahme lernt „ohne Unterpunkte“ (`includeChildren: false`) im Link-Service.
- Eine neue Komponente `NoteBar` übernimmt die Knöpfe, die heute in `NoteCard` stehen.

**Tech Stack:** .NET 10, Blazor Hybrid, xUnit + bUnit.

**Spec:** `docs/superpowers/specs/2026-10-29-journal-aktiver-block-design.md`

## Global Constraints

- **Umgebung:** `$env:DOTNET_ROOT="$env:LOCALAPPDATA\Microsoft\dotnet"; $env:PATH="$env:DOTNET_ROOT;$env:PATH"`, dann `dotnet test` im Worktree-Root.
- **Ausgangsstand:** Core 949, Pdf 58, AI 306 + 1 übersprungen, UI 842, npm 19.
- **Teil-B-Tests:** UI-Tests, die heute Knöpfe an der Karte klicken (`.ne-note-adopt`, `.ne-adopt-*`, `.ne-note-whereto` …), werden auf „Block aktiv machen, dann Knopf in der Leiste“ umgestellt. Was sie prüfen (Dateiinhalt, Status, Quellen, Undo), bleibt gleich; jede Umstellung wird im Bericht kurz genannt.
- **Übernahme:** Die bestehenden Regeln (Positionen, Status Relevant, `source::`/`used-in::`, Undo, Konfliktprüfung) bleiben unverändert. Neu ist nur „ohne Unterpunkte“.
- **Texte:** auf Deutsch in `Strings.resx`. Kommentare auf Englisch im Stil der Dateien.
- **Commits** enden mit `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Verwendeter Unterpunkt bei „Verwendete ausblenden“:** Er verschwindet, sein unverwendeter Hauptpunkt bleibt. Ist der ganze Tag verwendet, verschwindet die Tageskarte. → Test in Task 1.
2. **Übernehmen mit Bereich −1:** Ein Block mit Unterpunkten wird ohne die Unterpunkte übernommen. `used-in` steht nur an ihm, die Unterpunkte bleiben unverwendet und sichtbar. → Test in Task 2.
3. **Aktiver Block nach dem Übernehmen bei „Verwendete ausblenden“:** Der Block verschwindet aus der Liste. Die Auswahl wird aufgehoben, ohne Fehler und ohne dass eine andere Karte aktiv wird. → Test in Task 3.
4. **Klick zum Markieren von Text:** Wer Text markiert, macht den Block nicht aktiv. Ein Klick ohne Markierung macht ihn aktiv. → Test in Task 3, über das Auswerten der Markierung per JS-Interop, mit Fake.
5. **„Wohin damit?“ aus dem Reiter Relevant:** Die Liste lädt nicht neu, wenn sich der aktive Block oder die Liste ändert. Dieses Verhalten gibt es heute schon und bleibt so. → Test in Task 3.

---

### Task 1: Teil A, verwendete Blöcke auf jeder Ebene

**Files:**
- Modify:
  - `src/NoteEvolution.UI/Components/NoteCard.razor`
  - `src/NoteEvolution.UI/Components/JournalTab.razor`
  - `src/NoteEvolution.UI/Components/SearchTab.razor` und `RelevantTab.razor`, falls Karten dort leer werden
  - `app.css`
  - `README.md`
- Test: `tests/NoteEvolution.UI.Tests/UsedBlocksTests.cs`

**Interfaces:**
- Produces:
  - **Unterpunkte bei „Verwendete ausblenden“:** `NoteCard` rendert Unterpunkte mit `IsUsed` (eigenes oder geerbtes `used-in`) nicht, wenn `Settings.HideUsed` gilt.
  - **Graue Darstellung:** Ohne den Haken erhalten verwendete Blöcke (Hauptpunkt und Unterpunkte) die Klasse `ne-note-used-text` mit grauer Schrift.
  - **✓ je Block:** `UsedMark(Block block)` steht an jedem Block mit eigenem `used-in::` und zeigt die Zahl seiner Einträge.
    - Ein Klick ruft `GoToUsageAsync(Block block)` auf, verallgemeinert aus dem heutigen Hauptpunkt-Sprung.
    - Der Sprung geht zum Buch des ersten Eintrags mit Blockverweis und zeigt das Element. Ohne Blockverweis öffnet er das Buch am Anfang.
  - **Tageskarte:** `JournalTab` blendet eine Tageskarte aus, wenn bei gesetztem Haken keiner ihrer Hauptpunkte sichtbar ist. Die Hauptpunkte filtert heute schon `NoteRepository.Journal(filter)`; eine Karte ohne verbleibende Hauptpunkte entfällt.

- [ ] **Step 1: Write the failing tests:**
  - `HideUsed_UsedSubPoint_Hidden_ParentShown`
  - `HideUsed_UsedMain_HiddenWithSubPoints`
  - `HideUsed_WholeDayUsed_DayCardGone`: Review Focus 1.
  - `ShowUsed_UsedBlocksGrey_IncludingInheritedSubPoints`
  - `UsedSubPoint_ShowsCheckWithCount_ClickJumpsToTarget`: `State.JumpTo` landet beim Zielblock im Buch.
  - `SearchAndRelevant_SameRule`
- [ ] **Step 2:** Filter. Expected: FAIL.
- [ ] **Step 3:** Umsetzen.
- [ ] **Step 4:** `dotnet test`. Expected: grün.
- [ ] **Step 5:** Commit `feat: used journal blocks hidden or greyed on every level, with a check mark that jumps to the target`.

### Task 2: Teil B, Zustand, Bereichslogik und Übernehmen ohne Unterpunkte

**Files:**
- Create:
  - `src/NoteEvolution.UI/State/NoteSelection.cs`
  - `src/NoteEvolution.Core/Vaults/NoteRanges.cs`
- Modify:
  - `src/NoteEvolution.UI/State/AppState.cs`
  - `src/NoteEvolution.Core/Links/ILinkService.cs` und `LinkService.cs` (`Adopt`, `AdoptInto`, `AdoptAsHeading` mit `bool includeChildren = true`)
  - `src/NoteEvolution.UI/State/NoteAdoption.cs`
- Test:
  - `tests/NoteEvolution.Core.Tests/Vaults/NoteRangesTests.cs`
  - `tests/NoteEvolution.Core.Tests/Links/AdoptWithoutChildrenTests.cs`
  - `tests/NoteEvolution.UI.Tests/NoteSelectionTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record NoteRange(Guid HeadKey, bool OwnOnly, IReadOnlyList<Guid> Keys)` in Core. `Keys` sind alle markierten Blöcke in Lesereihenfolge.
  - `public static NoteRange Of(NoteBlock note, Guid blockKey, int level)`:
    - Stufe −1: der Block allein.
    - Stufe 0: der Block mit seinem Teilbaum.
    - Stufe k > 0: der k-te übergeordnete Block mit seinem Teilbaum, höchstens bis zum Hauptpunkt.
  - `public static (int Min, int Max) LevelBounds(NoteBlock note, Guid blockKey)`: Min ist −1, Max die Tiefe des Blocks unter dem Hauptpunkt.
  - `AppState`:
    - `NoteSelection? ActiveNote { get; }` mit `NoteSelection(Guid NoteKey, Guid BlockKey, int Level)`, wobei `NoteKey` der Hauptpunkt ist.
    - `void SelectNoteBlock(Guid noteKey, Guid blockKey)`: Stufe 0.
    - `void ChangeNoteRange(int delta)`, begrenzt durch `LevelBounds`.
    - `void ClearNoteSelection()`.
    - `NoteRange? ActiveNoteRange`.
    - Ein Vault-Wechsel leert die Auswahl. Fehlt der Block nach einem Neuladen, wird sie ebenfalls geleert.
    - Jede Änderung ruft `Notify` auf.
  - **Link-Service:** `Adopt`, `AdoptInto` und `AdoptAsHeading` bekommen `bool includeChildren = true`.
    - Mit `false` wird nur der Text des Blocks übernommen; seine Unterpunkte bleiben in der Notiz und erscheinen nicht im Buch.
    - `used-in` und `id::` stehen am Block wie heute.
    - `AdoptAsHeading` mit `false` ergibt eine Überschrift ohne Textblöcke darunter.
  - **`NoteAdoption.AdoptAsync`:** bekommt `includeChildren` und reicht es durch.

- [ ] **Step 1: Write the failing tests:**
  - `NoteRanges`: `Level0_BlockAndSubtree`, `LevelMinus1_BlockOnly`, `LevelUp_ParentWithSubtree_CappedAtMain`, `Bounds`.
  - `AdoptWithoutChildren`, je für die drei Arten: Die Datei des Buchs enthält nur den Blocktext. `used-in` steht am Block. Die Unterpunkte bleiben unverwendet (Review Focus 2). Undo stellt den Ausgangszustand wieder her.
  - `NoteSelection`: Auswahl, Bereich mit Grenzen, Leeren beim Vault-Wechsel und bei einem verschwundenen Block.
- [ ] **Step 2:** Filter. Expected: FAIL.
- [ ] **Step 3:** Umsetzen.
- [ ] **Step 4:** `dotnet test`. Expected: grün.
- [ ] **Step 5:** Commit `feat: note selection with range, and adopting a note block without its sub-points`.

### Task 3: Teil B, aktiver Block in der Oberfläche

**Files:**
- Create:
  - `src/NoteEvolution.UI/Components/NoteBar.razor`: die Leiste.
- Modify:
  - `NoteCard.razor`: keine Knöpfe mehr; ein Klick macht den Block aktiv; Rahmen für Blöcke in `ActiveNoteRange`.
  - `NotesPane.razor`: Alt+Pfeil im Fokusbereich; die Auswahl wird beim Reiterwechsel geleert.
  - `JournalTab.razor`, `SearchTab.razor`, `RelevantTab.razor`
  - `index.html`: JS `neNotes.hasSelection()` für Review Focus 4.
  - `ShortcutsDialog.razor`, `Strings.resx`, `app.css`, `README.md`
  - bestehende UI-Tests (siehe Global Constraints)
- Test:
  - `tests/NoteEvolution.UI.Tests/NoteBarTests.cs`
  - umgestellte Adopt-, WhereTo- und Journal-Tests

**Interfaces:**
- Consumes: Task 2 (`AppState.ActiveNote`/`ActiveNoteRange`/`SelectNoteBlock`/`ChangeNoteRange`, `NoteAdoption.AdoptAsync(..., includeChildren)`) und Task 1 (`UsedMark`, `GoToUsageAsync`).
- Produces:
  - **Klick auf einen Block:** Auf `.ne-note-block` ruft der Klick zuerst `neNotes.hasSelection()` auf. Ist keine Textmarkierung vorhanden, folgt `State.SelectNoteBlock(note.Key, block.Key)`.
  - **Rahmen:** Alle Blöcke in `ActiveNoteRange` bekommen die Klasse `ne-note-in-range`. Die `NoteBar` steht unter dem letzten Block des Bereichs in dessen Karte.
  - **`NoteBar` (`.ne-note-bar`):**
    - Bereich: `.ne-note-range-up`, `.ne-note-range-down`.
    - Übernehmen: `.ne-note-adopt-cursor`, `.ne-note-adopt-after`, `.ne-note-adopt-under`, sowie `.ne-note-adopt-heading` und `.ne-note-adopt-subheading`, wenn der Bereichskopf Unterpunkte hat und `OwnOnly` falsch ist.
    - `.ne-note-whereto` (nur mit lokaler KI): die Liste wie heute, unter der Leiste.
    - ✓ (`UsedMark`) und der Fortschrittspunkt.
    - Bei Journal-Blöcken: `.ne-note-bar-not-relevant` und `.ne-note-bar-later`, dieselben Aktionen wie an der Tageskarte.
    - `.ne-note-logseq`.
    - Gesperrte Knöpfe nennen den Grund im Tooltip.
  - **Nach dem Übernehmen:** Ist der Block nicht mehr sichtbar (Haken „Verwendete ausblenden“), wird die Auswahl geleert. Review Focus 3.

- [ ] **Step 1: Write the failing tests:**
  - `Click_SelectsBlock_FrameAndBarShown_NoButtonsOnCards`
  - `TextSelection_DoesNotSelect`: Review Focus 4; Fake-JS liefert `true`.
  - `RangeButtons_And_AltArrows_ChangeTheFrame`
  - `Adopt_FromBar_TakesTheRange_OwnOnlyWithoutSubPoints`
  - `Adopt_HideUsed_SelectionClearedCleanly`: Review Focus 3.
  - `WhereTo_FromBar_InRelevant_DoesNotReloadTheList`: Review Focus 5.
  - `DayMarks_InBar_SameAsOnCard`
  - `Logseq_InBar_OpensThePage`
  - `Blocked_TooltipNamesReason`
  - Die bestehenden Adopt-, WhereTo-, Journal- und Relevant-Tests laufen über die Leiste.
- [ ] **Step 2:** Filter. Expected: FAIL.
- [ ] **Step 3:** Umsetzen; Legende und README ergänzen.
- [ ] **Step 4:** `dotnet test` dreimal. Expected: jedes Mal grün.
- [ ] **Step 5:** Commit `feat: notes pane actions on the active block with frame, range and bar`.
