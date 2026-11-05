# Änderungshistorie je Textblock — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Jeder Textblock eines Buchs bekommt einen Verlauf in `.noteevolution/history.db`, mit Ansicht, Wiederherstellen und einer Liste gelöschter Blöcke; der Löschknopf heißt an Blöcken „Entfernen“.

**Architecture:** `PageWriter` meldet vor jedem Schreiben ein Ereignis `Saving`, `IVault.PageReplaced` meldet Neueinlesen. Ein `BlockHistoryTracker` vergleicht dort alle Textblöcke eines Buchs mit dem bekannten Stand, vergibt beim Speichern die `id::` und schreibt Versionen in einen `HistoryStore` (SQLite). Dialoge lesen aus dem Store; Wiederherstellen läuft über `ManuscriptCommands`.

**Tech Stack:** .NET 10, Blazor, Microsoft.Data.Sqlite (kommt über `NoteEvolution.AI`), xUnit + bUnit, FakeTimeProvider (`Time` im `UiTestContext`).

**Spec:** `docs/superpowers/specs/2026-11-05-block-historie-design.md`

## Global Constraints

- Datenbank: `<vault>/.noteevolution/history.db`; eine unlesbare Datei wird zu `history.db.defekt-<yyyyMMddHHmmss>` umbenannt, nie gelöscht.
- Pause `TimeSpan.FromMinutes(10)` je Block, gemessen mit dem `TimeProvider` der Session.
- Größeres Löschen: längster Stand seit der letzten Version mindestens 40 Zeichen, und kürzer um mindestens 200 Zeichen oder mindestens ein Viertel.
- Gründe und Texte: „Erste Fassung“, „Pause“, „Vor dem Löschen“, „Beim Schließen“, „Beim Öffnen“, „Vor dem Wiederherstellen“.
- Ein Fehler der Historie verhindert nie das Speichern eines Buchs: Jeder Aufruf aus `Saving` und `PageReplaced` fängt alles ab und loggt es.
- Nur Textblöcke im Text eines Buchs: keine Überschriften, kein Anhang (`Book.IsInAppendix`), keine Notizseiten (`Book.IsBook`), keine schreibgeschützten Seiten.
- Neue Texte stehen in `Strings.resx` (deutsch); Reports an den Nutzer auf Deutsch; Commits enden mit `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Tests: `dotnet test tests\NoteEvolution.UI.Tests --filter "FullyQualifiedName~<Name>"` nach `$env:DOTNET_ROOT="$env:LOCALAPPDATA\Microsoft\dotnet"; $env:PATH="$env:DOTNET_ROOT;$env:PATH"`.

## Review Focus

1. Tippen mit gespeicherter Datei alle paar Sekunden: keine zusätzliche Version vor Ablauf der 10 Minuten; der Zähler startet je Speichern neu (Task 2).
2. „Rückgängig“ der ersten Änderung nimmt die `id::` weg: Der Block behält danach seinen Verlauf und steht nicht in „Gelöschte Blöcke“ (Task 2).
3. Eine kaputte `history.db` oder ein Fehler im Store: Das Buch wird trotzdem gespeichert, die alte Datei bleibt erhalten (Task 1, Task 2).
4. Absturz mit offenem Stand: Beim nächsten Öffnen entsteht „Beim Öffnen“ aus dem Text der Datei (Task 2).
5. Wiederherstellen eines Blocks, dessen Unterpunkt Quelle einer Notiz ist: Die Verwendung folgt, keine doppelten Keys, „Rückgängig“ stellt den Text wieder her (Task 3).

---

### Task 1: Stand und Store

**Files:**
- Create: `src/NoteEvolution.UI/History/BlockStand.cs`, `src/NoteEvolution.UI/History/HistoryStore.cs`
- Test: `tests/NoteEvolution.UI.Tests/HistoryStoreTests.cs`

**Interfaces:**
- Produces:
  - `enum HistoryReason { First, Pause, BeforeDelete, OnClose, OnOpen, BeforeRestore }`
  - `sealed record StandParagraph(string Text, int Depth, BlockStatus Status)`
  - `sealed record BlockStand(string Text, IReadOnlyList<StandParagraph> Paragraphs, BlockStatus Status, IReadOnlyList<Guid> Sources)` mit `static BlockStand Of(TextBlock block)`, `int Length` (Zeichen von Text und Unterpunkten) und `bool SameText(BlockStand other)` (Text, Tiefe und Text der Unterpunkte; Status und Quellen zählen nicht).
  - `sealed record HistoryVersion(long Id, Guid BlockId, DateTimeOffset At, HistoryReason Reason, BlockStand Stand)`
  - `sealed record DeletedBlock(Guid BlockId, string Book, DateTimeOffset At, string SectionPath, Guid? AfterBlockId, BlockStand Stand)`
  - `sealed class HistoryStore : IDisposable`: `static HistoryStore Open(string vaultRoot, ILogger logger)`, `void Add(Guid blockId, string book, DateTimeOffset at, HistoryReason reason, BlockStand stand)`, `IReadOnlyList<HistoryVersion> Versions(Guid blockId)` (neueste zuerst), `HistoryVersion? Latest(Guid blockId)`, `IReadOnlySet<Guid> BlocksWithHistory()`, `void MarkDeleted(DeletedBlock deleted)`, `void ClearDeleted(Guid blockId)`, `IReadOnlyList<DeletedBlock> Deleted(string book)` (neueste zuerst).
- Tabellen: `versions(id INTEGER PRIMARY KEY, block_id, book, at, reason, stand)`, `deleted(block_id PRIMARY KEY, book, at, section_path, after_block_id, stand)`; `stand` als JSON (`System.Text.Json`). Thread-sicher mit einem Lock und `journal_mode=WAL`, wie `UnderstandingStore`.

- [ ] **Step 1: Tests schreiben**

Tests: `Add_ThenVersions_NewestFirst_WithAllFields`, `Stand_RoundTrip_KeepsParagraphsStatusAndSources`, `SameText_IgnoresStatusAndSources`, `Length_CountsParagraphs`, `Latest_Unknown_IsNull`, `Deleted_ListedPerBook_NewestFirst`, `ClearDeleted_RemovesFromList_KeepsVersions`, `Open_UnusableFile_MovedAside_NewStoreWorks` (schreibt Müll in `history.db`, erwartet genau eine Datei `history.db.defekt-*` mit dem Müll als Inhalt und ein funktionierendes `Add`), `Reopen_KeepsEverything`.

- [ ] **Step 2: Fehlschlag prüfen** — Filter `HistoryStoreTests`; erwartet: Kompilierfehler, Typen fehlen.

- [ ] **Step 3: Implementieren** — `Open` wie `UnderstandingStore.Open`, aber umbenennen statt `DeleteFiles`; falls auch das Umbenennen scheitert, wirft `Open` (der Tracker fängt das, Task 2).

- [ ] **Step 4: Tests grün** — Filter `HistoryStoreTests`; erwartet: alle bestanden.

- [ ] **Step 5: Commit** — `git add src/NoteEvolution.UI/History tests/NoteEvolution.UI.Tests/HistoryStoreTests.cs`, Nachricht „Store the versions of text blocks in history.db“.

### Task 2: Tracker und Anbindung

**Files:**
- Create: `src/NoteEvolution.UI/History/BlockHistoryTracker.cs`
- Modify: `src/NoteEvolution.Core/Storage/PageWriter.cs:15-22`, `src/NoteEvolution.UI/State/VaultSession.cs:124-167` und `Dispose`, `src/NoteEvolution.UI/Components/HeaderBar.razor`, `Strings.resx`
- Test: `tests/NoteEvolution.UI.Tests/BlockHistoryTrackerTests.cs`, `tests/NoteEvolution.Core.Tests/` (Ereignis des Writers, in der vorhandenen `PageWriter`-Testdatei)

**Interfaces:**
- Consumes: Task 1.
- Produces:
  - `PageWriter.Saving`: `public event Action<Page>? Saving;`, ausgelöst nach der Prüfung auf `IsReadOnly`, vor `PageSerializer.Serialize`; nicht Teil von `IPageWriter`.
  - `BlockHistoryTracker(IVault vault, Func<HistoryStore?> store, IClock clock, TimeProvider time, ILogger logger)` mit `void Start()` (Ausgangsstände aller Bücher, „Beim Öffnen“), `void OnSaving(Page page)`, `void OnReplaced(Page page)`, `void Flush()` („Beim Schließen“), `void Dispose()`, `bool HasProblem`, `event Action? ProblemChanged`, `void SaveBeforeRestore(Guid blockId, string book)`.
  - `VaultSession.History`: `HistoryStore?`; `VaultSession.HistoryProblem`: `bool`; `VaultSession.HistoryTracker` intern.

**Regeln von `Observe(Page page, bool mayWrite)`** (kein Code vorgegeben; Reihenfolge verbindlich):
1. Nur `Book.IsBook(page)`, nicht schreibgeschützt. `Book.Load(page)`; Textblöcke außerhalb des Anhangs. Bekannt sind Stände je `id::` und, für Blöcke ohne, je `Block.Key`.
2. Beim ersten Blick auf eine Seite (`Start`, neue Seite) und beim Neueinlesen für Blöcke ohne `id::`: nur Ausgangsstand merken, keine Version, keine `id::`.
3. Unbekannter Block mit Text bei `mayWrite` (neu in dieser Sitzung): `EnsureId`, offener Stand, Zähler starten.
4. Bekannter Block mit anderem Text (`SameText`): Bei `mayWrite` `EnsureId`. Hat die `id::` noch keine Version, zuerst `Add(First, alter Stand)`; ohne `id::` (Neueinlesen) nur den Ausgangsstand ersetzen. Größeres Löschen prüfen (Global Constraints) gegen den längsten Stand seit der letzten Version, ggf. diesen als `BeforeDelete` sichern. Neuen Stand als offen merken, Zähler auf `Now + 10 min`.
5. `Add` nur, wenn der Stand nicht `SameText` mit `Latest` ist.
6. Verschwundener bekannter Block: Steht sein Text (nicht leer) in `Text` oder Unterpunkten eines anderen Blocks dieser Seite, nur vergessen; sonst `id` vergeben (`id::` oder neue Guid), letzten Stand sichern, `MarkDeleted` mit Abschnittspfad (`" › "`) und `AfterBlockId` (`id::` des Vorgängers in Lesereihenfolge, falls vorhanden). Blöcke, deren Text leer ist, zählen nie als gelöscht.
7. Rückkehr: Ein Block mit `id::` aus `deleted` → `ClearDeleted`. Ein Block ohne `id::`, dessen Stand `SameText` mit einem in dieser Sitzung gelöschten Block ist, bekommt bei `mayWrite` dessen `id` zurück und `ClearDeleted`.
8. Doppelte `id::`: Nur der erste Block in Lesereihenfolge zählt; einmal je Sitzung `LogWarning`.

Der Zähler ist ein einziger `ITimer` auf den frühesten fälligen Block; `Flush` schreibt alle offenen Stände mit `OnClose`. Alle Zustände unter einem Lock, Store-Zugriffe in `try/catch`, ein Fehler setzt `HasProblem` und feuert `ProblemChanged` einmal.

- [ ] **Step 1: Tests schreiben** (mit einem echten `VaultSession` über `OpenSessionAsync`, `Time` für die Zeit)

```csharp
[Fact] Typing_NoVersionBeforePause_OneAfterTenQuietMinutes
// Text ändern + TrySave: genau eine Version First mit dem alten Text. Time.Advance(9 min), noch eine Änderung + TrySave,
// Time.Advance(9 min): weiter 1 Version. Time.Advance(1 min): 2 Versionen, die neueste Reason == Pause mit dem letzten Text.
[Fact] FirstChange_AssignsIdInTheSameSave  // Datei enthält "id::" direkt nach dem ersten TrySave; andere Blöcke bleiben ohne.
[Fact] UntouchedBlocks_FileUnchanged
[Fact] PropertiesOnly_NoVersion  // SetStatus ändert nichts am Verlauf
[Fact] LargeDeletion_SavesLongestStandAtOnce  // 300 Zeichen → 50: sofort BeforeDelete mit den 300 Zeichen
[Fact] QuarterDeletion_Counts_ShortBlockDoesNot  // 100→70 ja; 39→1 nein
[Fact] SlowDeletion_OverSeveralSaves_Counts  // 300→280→…→80 in Schritten: BeforeDelete mit 300
[Fact] SameTextAgain_NoNewVersion
[Fact] NewBlock_VersionAfterPause_NoFirst
[Fact] DeletedBlock_Listed_WithSectionPathAndPredecessor
[Fact] MergedBlock_NotListed  // session.Links.MergeTextBlocks
[Fact] MovedBlock_KeepsHistory
[Fact] Undo_OfFirstChange_KeepsHistory_NotListed  // Review Focus 2: session.TryUndo nach RunStatus-Änderung; Id wieder da, Deleted leer
[Fact] Undo_OfDelete_ClearsDeleted
[Fact] DisposeSession_WritesOpenStands_OnClose
[Fact] Reopen_FileDiffersFromLatest_OnOpenVersion  // Review Focus 4: Datei von Hand ändern, Session neu öffnen
[Fact] ExternalChange_WithId_Compared_WithoutId_BaselineOnly  // session.HandleExternalChange
[Fact] Appendix_AndNotePages_Ignored
[Fact] DuplicateId_OnlyFirstCounts
[Fact] BrokenStore_BookStillSaved_ProblemShown  // Review Focus 3: Store disposen, TrySave == true, HistoryProblem == true
```

In `PageWriterTests`: `Save_RaisesSaving_BeforeWriting_ChangesGoIntoTheFile` (Handler setzt `EnsureId`, die Datei enthält `id::`), `Save_ReadOnly_DoesNotRaiseSaving`.

- [ ] **Step 2: Fehlschlag prüfen** — Filter `BlockHistoryTrackerTests`; erwartet: Kompilierfehler.

- [ ] **Step 3: `PageWriter.Saving` einbauen** und Core-Tests grün.

- [ ] **Step 4: Tracker implementieren und in `VaultSession` anbinden.** Im Konstruktor `Writer` als lokale `PageWriter`-Variable halten, `writer.Saving += tracker.OnSaving`, `vault.PageReplaced += tracker.OnReplaced`. Store und `Start()` im Konstruktor in `try/catch`; `Dispose`: `Flush`, Abmelden, Tracker und Store schließen, vor `Writer`-Ende. Handler in `HeaderBar`: Ist `session.HistoryProblem`, ein Hinweis `.ne-history-problem` mit `L["HistoryProblem"]` = „Die Änderungshistorie kann gerade nicht gespeichert werden; das Buch wird weiter normal gespeichert.“

- [ ] **Step 5: Tests grün, dann alle UI-Tests** — erwartet: `Bestanden!`. Bestehende Tests, die den exakten Dateiinhalt nach einer Bearbeitung prüfen und jetzt ein `id::` sehen, werden angepasst; die Änderung im Ledger begründen.

- [ ] **Step 6: Commit** — „Track the versions of text blocks when a book is saved or read again“.

### Task 3: Wiederherstellen

**Files:**
- Modify: `src/NoteEvolution.Core/Books/ManuscriptEditor.cs`, `src/NoteEvolution.UI/State/ManuscriptCommands.cs`, `Strings.resx`
- Test: `tests/NoteEvolution.Core.Tests/` (vorhandene `ManuscriptEditor`-Testdatei), `tests/NoteEvolution.UI.Tests/HistoryRestoreTests.cs`

**Interfaces:**
- Consumes: Task 1, `tracker.SaveBeforeRestore`.
- Produces:
  - `ManuscriptEditor.RestoreText(Book book, Guid textBlockKey, string text, IReadOnlyList<(string Text, int Depth, BlockStatus Status)> paragraphs, IVault vault, DateTimeOffset now) : SyncResult`: über `BookSnapshot.Create(book, vault, section, includeSubsections: false)` den Knoten ersetzen und `BookSync.Apply`. Unterpunkte behalten ihren Key, wenn Text und Tiefe übereinstimmen, sonst in Reihenfolge den nächsten freien alten Key derselben Tiefe, sonst eine neue Guid.
  - `ManuscriptEditor.RestoreTextBlock(Book book, Guid blockId, Guid? afterBlockId, string sectionPath, string text, paragraphs, BlockStatus status, IReadOnlyList<Guid> sources, DateTimeOffset now) : Guid` (Key des neuen Blocks): Einfügen hinter dem Block mit `afterBlockId`, sonst ans Ende des Abschnitts mit dem Pfad, sonst ans Ende des Buchs, immer vor dem Anhang. Setzt `id::`, Status und `source::`.
  - `ManuscriptCommands.RestoreVersionAsync(AppState state, Guid blockId, HistoryVersion version, string undoDescription, ILogger logger) : Task<AdoptMessage?>` und `RestoreDeletedAsync(AppState state, DeletedBlock deleted, string undoDescription, ILogger logger) : Task<AdoptMessage?>`, beide wie `NormalizeOutlineAsync`: `PrepareAsync`, `RunStatus`, `RefreshBook`, `RevealElement`, `Notify`.
  - `ManuscriptCommands.CanRestore(AppState state) : bool` und `RestoreBlocked(AppState state) : string?` (Ressourcenschlüssel des Grunds).
- Wirkung auf Notizen: Die `SyncEffect`s aus `RestoreText` gehen nach erfolgreichem Speichern an `session.Links.ApplySyncEffects`, danach `RetryPending`, wie in `BookTextSaver`. `RestoreTextBlock`: je vorhandener Quelle `BlockSplit(blockId, sources)` auf denselben Weg.

- [ ] **Step 1: Tests schreiben**

`RestoreText_ReplacesTextAndParagraphs_KeepsSourcesAndStatus`, `RestoreText_UnchangedParagraph_KeepsKey`, `RestoreTextBlock_AfterPredecessor`, `RestoreTextBlock_PredecessorGone_EndOfSection`, `RestoreTextBlock_SectionGone_EndOfBook_BeforeAppendix`; in `HistoryRestoreTests`: `RestoreVersion_SavesCurrentFirst_ThenText_UndoBringsBack`, `RestoreVersion_LinkedParagraphRemoved_UsageFollows` (Review Focus 5), `RestoreDeleted_KeepsIdAndHistory_LeavesList_SourcesLinkedAgain`, `RestoreDeleted_SourceNoteGone_Skipped`, `Restore_ReadOnlyOrConflict_Refused`.

- [ ] **Step 2: Fehlschlag prüfen.**
- [ ] **Step 3: Implementieren.** Undo-Text `L["UndoRestore"]` = „Wiederherstellen“.
- [ ] **Step 4: Tests grün.**
- [ ] **Step 5: Commit** — „Restore a version of a text block or a deleted block“.

### Task 4: Fenster „Historie“, Knöpfe, Liste gelöschter Blöcke

**Files:**
- Create: `src/NoteEvolution.UI/History/WordDiff.cs`, `src/NoteEvolution.UI/Components/Dialogs/HistoryDialog.razor`, `src/NoteEvolution.UI/Components/Dialogs/DeletedBlocksDialog.razor`
- Modify: `src/NoteEvolution.UI/Components/SectionBar.razor`, `HeaderBar.razor`, `Shell.razor`, `src/NoteEvolution.UI/State/AppState.cs`, CSS der Dialoge, `Strings.resx`, `README.md`
- Test: `tests/NoteEvolution.UI.Tests/HistoryDialogTests.cs`, `tests/NoteEvolution.UI.Tests/WordDiffTests.cs`, bestehende `SectionBar`-Tests

**Interfaces:**
- Consumes: Task 1, Task 3.
- Produces:
  - `WordDiff.Compare(string before, string after) : IReadOnlyList<DiffPart>`, `record DiffPart(DiffKind Kind, string Text)`, `enum DiffKind { Same, Removed, Added }`: LCS über Wörter samt Leerraum; bei mehr als 4000 Wörtern ganzer Text als Removed und Added.
  - `AppState.RequestBlockHistory(Guid textBlockKey)` und Ereignis `BlockHistoryRequested`; die Shell zeigt `HistoryDialog`.
- Vergleichstext eines Stands: Text und Unterpunkte, je Zeile mit zwei Leerzeichen je Tiefe.

**Texte:** `SectionHistory` „Historie“, `SectionHistoryTitle` „Frühere Fassungen dieses Blocks ansehen“, `SectionHistoryNone` „Für diesen Block gibt es noch keine Versionen“, `SectionRemove` „Entfernen“, `SectionRemoveTitle` „Diesen Block samt Unterpunkten entfernen“, `AssistantDeletedBlocks` „Gelöschte Blöcke“, `HistoryRestore` „Wiederherstellen“, `HistoryClose` „Schließen“, `HistoryReasonFirst` … je Grund aus den Global Constraints, `DeletedBlocksEmpty` „Keine gelöschten Blöcke in diesem Buch“.

- [ ] **Step 1: Tests schreiben**

`Compare_ChangedWord_RemovedAndAdded`, `Compare_Equal_OnlySame`, `Compare_Empty_Sides`, `Compare_HugeText_FallsBackToWhole`; in `HistoryDialogTests`: `Dialog_ListsVersions_NewestFirst_WithReason`, `Dialog_SelectVersion_ShowsDiffMarks` (`.ne-history-removed`, `.ne-history-added`), `Dialog_Restore_ReplacesText_Closes`, `Dialog_ReadOnlyBook_RestoreDisabled_WithReason_ViewStillWorks`, `Bar_HistoryButton_OnTextBlockAndDetail_OpensBlocksHistory`, `Bar_HistoryButton_NotOnHeading`, `Bar_HistoryButton_NoVersions_DisabledWithReason`, `Bar_Delete_LabelByElement` („Entfernen“ an Textblock und Unterpunkt, „Abschnitt löschen“ an Überschrift; Tooltip an Blöcken), `Deleted_ListedInMenu_Restore_LeavesList`, `Deleted_Empty_Hint`, `Deleted_ShowHistoryOfABlock`.

- [ ] **Step 2: Fehlschlag prüfen.**
- [ ] **Step 3: `WordDiff` implementieren.**
- [ ] **Step 4: Dialoge und Knöpfe.** `HistoryDialog` nach dem Muster von `NormalizeOutlineDialog`; in `SectionBar` die Beschriftung von `ne-sec-delete` je `element.Kind`; `ne-sec-history` nach `ne-sec-remark`. „Gelöschte Blöcke“ als `.ne-assistant-deleted` im Menü „Assistenten“, ausgegraut ohne Buch.
- [ ] **Step 5: README** um einen Abschnitt „Änderungshistorie“ ergänzen und die Testliste um die manuellen Schritte (Tippen, 10 Minuten warten, Wiederherstellen, Entfernen, Gelöschte Blöcke).
- [ ] **Step 6: Alle Tests** — `dotnet test NoteEvolution.slnx`; erwartet: `Bestanden!` in allen Projekten.
- [ ] **Step 7: Commit** — „Show the history of a text block and the deleted blocks; name the delete button Entfernen on blocks“.
