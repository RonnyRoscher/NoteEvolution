# Zuordnungen gezielt neu prüfen — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Gespeicherte Zuordnungen bleiben über einen Neustart lesbar, und nach einer Buchänderung wird nur noch eine Notiz neu geprüft, für die sich die Wahl des Modells wahrscheinlich ändert.

**Architecture:** Ein Ergebnis speichert neben den Schlüsseln einen dauerhaften Abschnittspfad, den Anfang der Textstelle und die Grundlage der Prüfung. `StoreUnderstanding.Placement` löst Schlüssel über Pfad und Text neu auf. `PlacementWork.Build` vergleicht die neue Anfrage mit der Grundlage des letzten Ergebnisses und gibt dem Worker bei unwesentlichen Änderungen den alten Fingerabdruck, sodass nichts neu gerechnet wird.

**Tech Stack:** .NET 10, xUnit + bUnit, SQLite über `UnderstandingStore`, `FakeChatModel` in `tests/NoteEvolution.UI.Tests/LanguageModelTests.cs`.

**Spec:** `docs/superpowers/specs/2026-11-10-zuordnung-gezielt-design.md`

## Global Constraints

- Neu geprüft wird nur nach den Punkten 1 bis 5 aus Spec Abschnitt 4. Allein nicht ausschlaggebend sind: andere Zusammenfassungssätze, andere oder verschwundene Textstellen, umbenannte, über ihren Schlüssel gefundene Abschnitte und eine geänderte Reihenfolge.
- „Beste 3 Kapitel“ = die ersten 3 verschiedenen Kapitel der Topics, die schon heute die Anfrage bestimmen (`PlacementCheck.TopicLimit`, `MaxChapters` = 3).
- Großer Umbau: weniger als die Hälfte der Abschnitte der Grundlage wird noch angeboten.
- Anfang der Textstelle: erste Zeile, höchstens 100 Zeichen.
- Verglichen wird immer mit der Grundlage der letzten echten Prüfung, nicht mit der vorigen Anfrage.
- Vorhandene Ergebnisse ohne Pfad werden nicht in großer Zahl neu gerechnet: nur die, deren Abschnitt sich nicht eindeutig über die gespeicherte Zusammenfassung erkennen lässt.
- `PromptVersion` und die Fingerabdrücke heutiger Anfragen bleiben unverändert; sonst würde alles neu gerechnet.
- Commits enden mit `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; neue Nutzertexte deutsch in `Strings.resx`.

## Review Focus

1. Neustart: Ein vor dem Neustart berechnetes Ergebnis ist danach in „Wohin damit?“, „Hierher eingeordnet“ und der Abdeckung sichtbar (Task 1).
2. Zwei Notizen mit gleicher erster Zeile auf einer Seite zeigen nie das Ergebnis der jeweils anderen (Task 2).
3. Nach einer neuen Überschrift in einem angebotenen Kapitel ohne bessere Rangfolge rechnet der Worker keine Zuordnung (keine Anfrage mit Schema an `FakeChatModel`) (Task 3).
4. Eine Notiz, deren Text sich ändert, wird trotz behaltener Grundlage neu geprüft (Task 3).
5. Ein altes Ergebnis ohne Pfad mit eindeutiger Zusammenfassung wird ohne Anfrage an das Modell nachgetragen; mit mehrdeutiger genau einmal neu gerechnet (Task 3).

---

### Task 1: Dauerhafte Bezüge im Ergebnis

**Files:**
- Create: `src/NoteEvolution.AI/Understanding/SectionPath.cs`
- Modify: `src/NoteEvolution.AI/Understanding/PlacementCheck.cs` (CheckTopic, Prompt, Serialize, Deserialize), `src/NoteEvolution.AI/Understanding/StoreUnderstanding.cs:98-114`, `src/NoteEvolution.AI/Understanding/UnderstandingWorker.cs:408`, `src/NoteEvolution.UI/State/VaultSession.cs:1143-1144`
- Test: `tests/NoteEvolution.AI.Tests/Understanding/` (vorhandene `PlacementCheck`- und `StoreUnderstanding`-Tests), `tests/NoteEvolution.UI.Tests/LanguageModelTests.cs`

**Interfaces:**
- Produces:
  - `static class SectionPath`: `string Of(OutlineNode node)`, also die Titel von oben nach unten, je mit `#<Index unter gleichnamigen Geschwistern>`, verbunden mit `" › "`, z. B. `"Schlaf#0 › Träume#0"`. Dazu `OutlineNode? Find(Book book, string path)`.
  - `CheckTopic` bekommt `string Path` (am Ende, mit Standardwert `""`).
  - `PlacementVerdict` bekommt `string? SectionPath = null, string? AfterText = null, PlacementBasis? Basis = null`.
  - `sealed record PlacementBasis(string NoteHash, IReadOnlyList<string> Offered, IReadOnlyList<string> TopChapters, IReadOnlyList<string> Ranking)`: `Offered` sind die Pfade der angebotenen Abschnitte in Anfragereihenfolge, `TopChapters` die Pfade der besten 3 Kapitel, `Ranking` die Pfade der Topics in Embedding-Reihenfolge.
  - `PlacementCheckPrompt` bekommt `PlacementBasis? Basis = null`; `PlacementCheck.Prompt` füllt ihn. `NoteHash` = `UnderstandingItems.NoteFingerprint(noteText, "")`.
  - `PlacementCheck.Serialize(PlacementVerdict verdict, PlacementCheckPrompt prompt)`: schreibt zusätzlich `path` (Pfad des gewählten Topics), `afterText` (erste Zeile der Textstelle, höchstens 100 Zeichen, aus dem Buch über den Prompt) und `basis`. `Deserialize` liest alles; fehlende Felder bleiben `null`. Die alte Überladung mit nur `verdict` entfällt.
  - `StoreUnderstanding.Placement(book, noteKey)`: Abschnitt über Schlüssel, sonst über `SectionPath.Find`; Textstelle über Schlüssel, sonst der erste Textblock im Abschnitt, dessen Text mit `AfterText` beginnt, sonst `null` (neu am Ende). Das zurückgegebene Ergebnis trägt die heutigen Schlüssel. Ohne gefundenen Abschnitt: `null`.

- [ ] **Step 1: Tests schreiben**

`SectionPath_Of_AndFind_RoundTrip_WithSameTitles`, `Serialize_WritesPathAfterTextAndBasis_DeserializeReadsThem`, `Deserialize_OldJsonWithoutPath_StillReads`, `Prompt_Basis_HoldsOfferedTopChaptersRankingAndNoteHash`, `Placement_KeyGone_FoundByPath`, `Placement_AfterKeyGone_FoundByText`, `Placement_AfterTextGone_EndOfSection`, `Placement_SectionGone_Null`; in `LanguageModelTests`: `Session_Reopened_StoredPlacementStillShown` (Review Focus 1: Ergebnis berechnen, Session schließen, neu öffnen, `session.Placement(key)` nicht `null`, Abschnitt mit dem heutigen Schlüssel, und keine neue Anfrage mit Schema).

- [ ] **Step 2: Fehlschlag prüfen.** `Session_Reopened_StoredPlacementStillShown` muss vor der Änderung an `Placement` mit `null` scheitern und belegt so den Fehler aus Spec Abschnitt 1. Scheitert er nicht, NEEDS_CONTEXT melden.
- [ ] **Step 3: Implementieren.**
- [ ] **Step 4: Tests grün, dann AI- und UI-Projekt komplett.**
- [ ] **Step 5: Commit** — „Keep placements readable after a restart: store section path, place text and the check's basis“.

### Task 2: Eindeutige Identität bei gleicher erster Zeile

**Files:**
- Modify: `src/NoteEvolution.UI/State/UnderstandingParts.cs:156` (NoteSnapshot), `src/NoteEvolution.UI/State/VaultSession.cs` (Snapshot-Aufbau um Zeile 1325, `CheckPlacementAsync` um Zeile 1063), `src/NoteEvolution.UI/State/PlacementWork.cs:178`, `src/NoteEvolution.AI/Understanding/UnderstandingItems.cs` (`ForNote`)
- Test: `tests/NoteEvolution.UI.Tests/LanguageModelTests.cs`

**Interfaces:**
- Produces: `NoteSnapshot` bekommt `string Identity`; `UnderstandingItems.NoteIdentities(IEnumerable<(Guid Key, string Content, string FilePath)> notes) : IReadOnlyDictionary<Guid, string>` hängt an die zweite und jede weitere gleiche Identität einer Seite in Dateireihenfolge `#2`, `#3` … an; die erste bleibt unverändert. Alle Stellen, die heute `NoteIdentity` aufrufen, nehmen die Identität aus dem Snapshot; `CheckPlacementAsync` aus der Zuordnung, die die Session für den letzten Snapshot hält.

- [ ] **Step 1: Tests** — `NoteIdentities_SameFirstLineOnOnePage_Numbered_FirstUnchanged`, `NoteIdentities_SameFirstLineOnTwoPages_Unchanged`, `Session_TwoNotesSameFirstLine_EachShowsOwnPlacement` (Review Focus 2).
- [ ] **Step 2: Fehlschlag prüfen.**
- [ ] **Step 3: Implementieren.**
- [ ] **Step 4: Tests grün, UI-Projekt komplett.**
- [ ] **Step 5: Commit** — „Tell notes with the same first line on one page apart“.

### Task 3: Nur betroffene Notizen neu prüfen

**Files:**
- Create: `src/NoteEvolution.UI/State/PlacementKeep.cs`
- Modify: `src/NoteEvolution.UI/State/PlacementWork.cs` (`Build`), `src/NoteEvolution.AI/Understanding/UnderstandingStore.cs`
- Test: `tests/NoteEvolution.UI.Tests/PlacementKeepTests.cs`, `tests/NoteEvolution.UI.Tests/LanguageModelTests.cs`

**Interfaces:**
- Consumes: Task 1 (`PlacementBasis`, `PlacementVerdict.Basis`, `SectionPath`), Task 2 (`NoteSnapshot.Identity`).
- Produces:
  - `UnderstandingStore.LatestPlacements() : IReadOnlyDictionary<string, UnderstandingEntry>`: das neueste Placement-Ergebnis je Identität, in einer Abfrage.
  - `enum RecheckReason { None, NoteText, SectionGone, NewCandidate, Rebuilt, NoBasis }`.
  - `static class PlacementKeep`: `RecheckReason Decide(PlacementVerdict latest, PlacementCheckPrompt prompt, Book book)`, die Regeln aus Spec Abschnitt 4 in dieser Reihenfolge:
    1. `NoBasis`, wenn `latest.Basis` fehlt.
    2. `NoteText`, wenn sich `NoteHash` unterscheidet.
    3. `SectionGone`, wenn der Abschnitt weder über Schlüssel noch über Pfad zu finden ist.
    4. `NewCandidate`, wenn ein Kapitel aus `prompt.Basis.TopChapters` nicht in `latest.Basis.TopChapters` steht, oder wenn ein Pfad aus `prompt.Basis.Offered`, der in `latest.Basis.Offered` fehlt, in `prompt.Basis.Ranking` vor dem gewählten Pfad steht. Ein gewählter Pfad, der nicht im Ranking steht, zählt als letzter.
    5. `Rebuilt`, wenn weniger als die Hälfte von `latest.Basis.Offered` in `prompt.Basis.Offered` vorkommt.
    6. Sonst `None`.
  - In `PlacementWork.Build`, je Notiz mit fertigem Prompt: Ist der Fingerabdruck schon gespeichert, bleibt alles wie heute. Sonst mit `LatestPlacements()` (einmal je Build gelesen) und dem Eintrag zur Identität:
    - Hat der Eintrag ein Ergebnis mit Basis und liefert `Decide` `None`, bekommt das Item den Fingerabdruck dieses Eintrags. Der Worker sieht es dann als erledigt, die Anzeige als aktuell.
    - **Altes Ergebnis ohne Pfad und Basis,** dessen Fingerabdruck dem heutigen gleicht: Stimmt seine `Summary` genau mit der `Summary` genau eines Topics der heutigen Anfrage überein, wird es mit Pfad und Basis aus der heutigen Anfrage per `Store.Put` unter demselben Fingerabdruck neu geschrieben (`AfterText` bleibt `null`). Sonst bekommt das Item den Fingerabdruck mit dem Zusatz `"|repath"`, damit es genau einmal neu gerechnet wird.
    - Dass ein solcher Eintrag heute als „aktuell“ gilt, bleibt unverändert.
  - `PlacementBuild` bekommt `IReadOnlyDictionary<RecheckReason, int> Reasons` und `int Kept` für die Messung.

- [ ] **Step 1: Tests**

`PlacementKeepTests` (mit Prompts aus `PlacementCheck.Prompt` über ein kleines Buch): `NewHeadingInOfferedChapter_WorseRank_Kept`, `NewHeadingRankedBeforeChosen_NewCandidate`, `NewChapterInTopThree_NewCandidate`, `RenamedSectionFoundByKey_Kept`, `OtherSummary_Kept`, `PlaceGone_Kept`, `ChosenSectionDeleted_SectionGone`, `HalfOfferedGone_Rebuilt`, `NoteTextChanged_NoteText`, `NoBasis_NoBasis`, `SmallChangesAddUp_ComparedWithBasisNotPrevious`.

`LanguageModelTests`: `Session_NewHeadingWithoutBetterRank_NoNewPlacementRequest` (Review Focus 3), `Session_NoteEdited_RecheckedDespiteBasis` (Review Focus 4), `Session_OldResultWithoutPath_SummaryUnique_UpgradedWithoutRequest`, `Session_OldResultWithoutPath_SummaryAmbiguous_RecomputedOnce` (Review Focus 5), `Session_KeptPlacement_StatusCurrent_NotStale`.

- [ ] **Step 2: Fehlschlag prüfen.**
- [ ] **Step 3: `LatestPlacements`, `PlacementKeep` und die Änderung in `Build` implementieren.**
- [ ] **Step 4: Tests grün, alle Projekte.**
- [ ] **Step 5: Commit** — „Recheck a placement only when the book change can change its choice“.

### Task 4: Messung und Dokumentation

**Files:**
- Modify: `src/NoteEvolution.UI/State/PlacementChurn.cs`, `src/NoteEvolution.UI/State/PlacementWork.cs`, `README.md`
- Test: `tests/NoteEvolution.UI.Tests/PlacementChurnTests.cs`

**Interfaces:**
- Consumes: Task 3 (`PlacementBuild.Reasons`, `Kept`), Task 1 (`PlacementVerdict.SectionPath`).
- Produces:
  - Eine Logzeile je Build, sobald etwas behalten oder neu geprüft wird: `"Placement keep in {Book}: {Kept} kept, {Rechecked} rechecked; why: {Reasons}"`.
  - `PlacementChurn.Stored` vergleicht über `SectionPath` und `AfterText` des Ergebnisses, wenn vorhanden. Sonst bleibt es beim heutigen Weg.

- [ ] **Step 1: Tests** — `Stored_ComparesByPath_AfterRestart_SamePlace`, `KeepLine_CountsKeptAndReasons`.
- [ ] **Step 2: Fehlschlag prüfen.**
- [ ] **Step 3: Implementieren.** README: Im Abschnitt „Vorberechnete Zuordnung“ beschreiben, wann neu geprüft wird (Punkte aus Spec 4), und „Placement keep“ zu den Messzeilen ergänzen.
- [ ] **Step 4: Alle Tests** — `dotnet test NoteEvolution.slnx`; erwartet: `Bestanden!` in allen Projekten.
- [ ] **Step 5: Commit** — „Log kept and rechecked placements; compare rechecks by section path“.
