# Splitansicht für das Buch — Entwurf

Stand 2026-10-23. Ziel: an zwei Stellen desselben Buchs gleichzeitig arbeiten.

## 1. Verhalten

- **Ein- und ausschalten:** Knopf „⇅“ in der Kopfzeile links neben ◀ (Tooltip „Buch geteilt anzeigen“) und
  Strg+Alt+S. Der Zustand und das Teilungsverhältnis bleiben nach einem Neustart erhalten (`ui.json`:
  `SplitView`, `SplitRatio`).
- **Legende:** Strg+Alt+S steht in der Legende „Tastenkürzel“ unter „Navigation“: „Buch geteilt anzeigen ein / aus“.
- **Aufteilung:** Gliederung und Buchansicht werden zusammen waagrecht geteilt: oben eine Hälfte mit eigener Gliederung
  und eigenem Editor, unten eine zweite. Der Notizbereich rechts bleibt über die ganze Höhe. Ein Schieber zwischen den
  Hälften stellt das Verhältnis ein (Ziehen, Pfeil hoch/runter; 20–80 %, Standard 50 %). Die Breite der Gliederung
  gilt für beide Hälften.
- **Dasselbe Buch:** Beide Hälften zeigen das gewählte Buch, jede mit eigener Stelle: Cursor, Abschnitt, Bereich,
  Scrollposition. Ein Buchwechsel gilt für beide; die zweite Hälfte springt dann an den Anfang.
- **Aktive Hälfte:** Die Hälfte, in die zuletzt geklickt oder getippt wurde, ist aktiv. Sie hat oben einen schmalen
  Akzentstrich. Alles, was heute „das Element am Cursor“ meint, bezieht sich auf sie:
  - der Reiter „Relevant“ und „Wohin damit?“;
  - Übernehmen am Cursor / danach / darunter und das Ablegen per Ziehen;
  - „Rückgängig“, die Leiste mit Abschnittsknöpfen, Status und Ebenen;
  - Verweise einfügen und „Quellen suchen“;
  - ◀ ▶ und die Sprünge aus „Zuletzt geändert“, den Quellen und der Verknüpfungsprüfung.
  Die Leiste erscheint nur in der aktiven Hälfte.
- **Änderungen:** Was in einer Hälfte gespeichert wird, erscheint sofort in der anderen. Die andere behält dabei
  Cursor und Scrollposition, wie bei einer Änderung von außen. Beim Wechsel der aktiven Hälfte wird ungespeicherter
  Text der bisherigen zuerst gespeichert. Getippt wird immer nur in einer Hälfte.
- **Ausschalten:** Die untere Hälfte verschwindet. Die obere bleibt und wird aktiv, mit ihrer Stelle.

## 2. Umsetzung

- **Zustand je Hälfte:** Eine neue Klasse `BookView` (UI/State) hält, was heute in `AppState` an einer Stelle hängt:
  - `CurrentSectionKey`, `Cursor`, `RangeLevel`;
  - die ausstehende Sprungmarke (`PendingReveal`, `PendingRevealOffset`, `RevealRequested`);
  - die Rückrufe des Editors (`FlushEditor`, `WarnUnsavedText`, `HasUnsavedEditorText`).

  `AppState` hat zwei Views und `ActiveView`. Seine bisherigen Eigenschaften und Methoden leiten an `ActiveView`
  weiter, damit Notizbereich, `ManuscriptCommands`, `NoteAdoption` und Kopfzeile unverändert bleiben.
  `SetActiveView(view)` speichert zuerst den Text der bisherigen View (`FlushEditor`) und ruft dann `Notify`.
- **Komponenten:** `OutlinePane` und `EditorPane` bekommen die View als Parameter (`BookView View`) und lesen bzw.
  schreiben Cursor, Abschnitt und Sprünge über sie statt über `State`.
  - Klicks, Fokus und Cursor-Meldungen machen ihre View aktiv.
  - Jede `EditorPane` hat ihr eigenes `IEditorInterop`; es ist bereits transient registriert.
  - Die Leiste rendert nur, wenn `View == State.ActiveView`.
- **Abgleich:** Speichert eine Hälfte, lädt die andere das Buch mit `keepCursor` neu. Das ist derselbe Weg wie bei
  einer Änderung der Datei von außen. Er wird ausgelöst über das bestehende `State.Changed` mit einem neuen Zähler
  `BookRevision`, den `RefreshBook` erhöht.
- **Layout:** In `Shell.razor` umschließt ein Container die Spalten Gliederung und Editor; mit Split enthält er zwei
  Zeilen.
  - Der neue Schieber verwendet `RowSplitter` wieder: die Höhe der oberen Hälfte in Prozent des Containers.
  - Der Notizbereich und sein Splitter bleiben, wie sie sind.
- **Ohne Split** gibt es nur die erste View. Verhalten und Tests bleiben wie heute, das ist der Maßstab für den Umbau.

## 3. Tests

- **AppState:**
  - Zwei Views haben getrennte Cursor und Abschnitte.
  - `ActiveView` wechselt beim Cursor-Melden.
  - Der Wechsel speichert vorher die alte View.
  - Weiterleitungen lesen die aktive View.
- **Shell:**
  - Der Knopf und Strg+Alt+S schalten den Split um.
  - Das Verhältnis wird gespeichert.
  - Beim Ausschalten bleibt die obere Hälfte mit ihrer Stelle.
- **Zwei Editoren** (zwei `FakeEditorInterop`):
  - Eine Änderung in der oberen Hälfte lädt die untere mit `keepCursor` neu.
  - Die Leiste erscheint nur in der aktiven Hälfte.
  - „Relevant“ folgt der aktiven Hälfte.
- **Die bestehende Suite bleibt grün**, ohne dass Tests angepasst werden müssen, außer bei bewusst geändertem Markup.

## 4. Nicht enthalten

- Verschiedene Bücher in den Hälften.
- Mehr als zwei Hälften.
- Eine senkrechte Teilung nebeneinander.
- Eine eigene Rückgängig-Liste je Hälfte. „Rückgängig“ bleibt eine Liste für den Vault.
