# Splitansicht Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Gliederung und Buchansicht lassen sich waagrecht in zwei Hälften teilen, die dasselbe Buch an zwei Stellen zeigen; der Notizbereich folgt der aktiven Hälfte.

**Architecture:**
- Der Zustand einer Stelle im Buch wandert aus `AppState` in eine neue Klasse `BookView`. Er umfasst Cursor, Abschnitt, Bereich, Sprungmarke und die Rückrufe des Editors.
- `AppState` hält eine oder zwei Views und eine aktive View; seine bisherigen Mitglieder leiten an die aktive weiter. Notizbereich, Befehle und Dialoge bleiben dadurch unverändert.
- `OutlinePane` und `EditorPane` bekommen ihre View als Parameter.
- `Shell` legt bei eingeschalteter Teilung zwei Hälften mit einem Schieber an.

**Tech Stack:** .NET 10, Blazor Hybrid (Photino/WebView2), xUnit + bUnit, `FakeEditorInterop` für den Editor.

**Spec:** `docs/superpowers/specs/2026-10-23-splitansicht-design.md`

## Global Constraints

- **Umgebung:** `$env:DOTNET_ROOT="$env:LOCALAPPDATA\Microsoft\dotnet"; $env:PATH="$env:DOTNET_ROOT;$env:PATH"`. Tests laufen mit `dotnet test` im Worktree-Root.
- **Ohne Teilung bleibt alles wie heute:** Die bestehende Suite (Core 837, Pdf 40, AI 305 + 1 übersprungen, UI 742) bleibt grün. Bestehende Tests werden nur angepasst, wenn sich Markup bewusst ändert; jede solche Anpassung wird im Bericht begründet.
- **Texte:** Alle sichtbaren Texte auf Deutsch in `src/NoteEvolution.UI/Resources/Strings.resx`, keine Texte im Code.
- **Kopfzeile:** Knopf „⇅“ links neben ◀ (Klasse `ne-split-toggle`, Tooltip-Ressource `SplitToggleTitle` = „Buch geteilt anzeigen“, `aria-pressed`).
- **Kürzel:** Strg+Alt+S schaltet die Teilung um, auch im Editor. Bei offenem modalem Dialog (`[aria-modal="true"]`) tut es nichts.
- **Legende:** In `ShortcutsDialog` unter „Navigation“ der Eintrag „Strg+Alt+S“ – „Buch geteilt anzeigen ein / aus“.
- **`ui.json`:** `SplitView` (bool, Standard false) und `SplitRatio` (double, Anteil der oberen Hälfte, 0,2–0,8, Standard 0,5). Ungültige Werte werden beim Laden auf den Standard gesetzt.
- **Schieber:** Ziehen sowie Pfeil hoch/runter in Schritten von 0,05, begrenzt auf 0,2–0,8.
- **Aktive Hälfte:** schmaler Akzentstrich oben (`.ne-book-half.active`). Die Abschnittsleiste erscheint nur in der aktiven Hälfte.
- **Commits:** enden mit `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Ungespeicherter Text beim Hälftenwechsel:** Man tippt oben und klickt sofort unten hinein. Der Text oben wird gespeichert, bevor unten etwas geschieht, und unten erscheint er sofort. → Test in Task 4.
2. **Sprünge landen in der aktiven Hälfte:** Ein Sprung aus „Zuletzt geändert“, ◀ ▶ oder einem Quellen-Chip landet dort, und die andere Hälfte bleibt stehen. → Test in Task 2.
3. **Ausschalten, während die untere Hälfte aktiv ist:** Die obere wird aktiv, mit ihrer Stelle. Relevant und die Leiste beziehen sich danach auf sie, und es gibt keinen Verweis auf die entfernte View. → Test in Task 3.
4. **Buchwechsel bei geteilter Ansicht:** Die nicht aktive Hälfte springt an den Anfang. Ein Cursor aus dem alten Buch bleibt nirgends stehen. → Test in Task 1.
5. **Fenster schließen mit ungespeichertem Text in der nicht aktiven Hälfte:** Kann das eigentlich passieren? Dieser Text wurde beim Wechsel schon gespeichert. Schlug das Speichern damals fehl, muss die Warnung beim Schließen trotzdem kommen: `WarnUnsavedText` fragt beide Views. → Test in Task 1.

---

### Task 1: `BookView` und die Weiterleitung in `AppState`

**Files:**
- Create: `src/NoteEvolution.UI/State/BookView.cs`
- Modify: `src/NoteEvolution.UI/State/AppState.cs`
- Test: `tests/NoteEvolution.UI.Tests/BookViewTests.cs`

**Interfaces:**
- Produces: `public sealed class BookView` (in `NoteEvolution.UI.State`).
  - Eigenschaften:
    - `Guid CurrentSectionKey { get; set; }`
    - `CursorInfo? Cursor { get; set; }`
    - `int RangeLevel { get; set; }`
    - `Guid? PendingReveal { get; }`, `int PendingRevealOffset { get; }`
    - `Func<Task>? FlushEditor`, `Func<bool>? WarnUnsavedText`, `Func<bool>? HasUnsavedEditorText`
  - `event Action<Guid>? RevealRequested`
  - Methoden:
    - `Guid? TakePendingReveal()`
    - `BookElement? ElementIn(Book? book)`, mit der Logik des heutigen `CurrentElement`
    - `internal void RequestReveal(Guid elementKey, int offset)`, setzt die Sprungmarke und löst das Ereignis aus
- Produces in `AppState`:
  - `IReadOnlyList<BookView> Views` (eine View ohne Teilung, zwei mit) und `BookView ActiveView`.
  - `bool IsSplit`, `void SetSplit(bool split)`:
    - Beim Einschalten bekommt die zweite View den Abschnitt der aktiven View, ohne Cursor.
    - Beim Ausschalten wird die erste View aktiv, falls die zweite aktiv war.
    - Danach wird `Notify` aufgerufen.
  - `Task ActivateAsync(BookView view)`:
    - Ist die View schon aktiv, passiert nichts.
    - Sonst wird zuerst `FlushEditor` der bisherigen View abgewartet, dann wird `view` aktiv und `Notify` aufgerufen.
- **Weiterleitungen:** Diese bisherigen Mitglieder leiten an `ActiveView` weiter: `CurrentSectionKey`, `Cursor` (mit Bereich auf 0 und Verweildauer wie heute), `RangeLevel`, `CurrentElement`, `CurrentRange`, `CanRangeUp`/`CanRangeDown`, `CursorTextBlockKey`, `PendingReveal`/`PendingRevealOffset`, `TakePendingReveal()` und `RevealElement(...)`. `ChangeRange` wirkt auf die aktive View.
- **`event RevealRequested` bleibt bestehen:** Es wird zusätzlich zum Ereignis der aktiven View ausgelöst.
- **Editor-Rückrufe:**
  - `FlushEditor` liefert beim Lesen `null`, wenn keine View einen Rückruf hat. Sonst liefert es einen Rückruf, der die Rückrufe aller Views nacheinander abwartet.
  - Beim Setzen von `FlushEditor` wird `Views[0].FlushEditor` gesetzt; das erhält die bestehenden Tests.
  - `WarnUnsavedText` und `HasUnsavedEditorText` funktionieren genauso. Sie ergeben `true`, sobald eine View `true` meldet; jede View wird gefragt.
- **Buchwechsel:** Ändert sich `CurrentBook` auf ein anderes Buch (anderer `LinkName`), gilt für jede nicht aktive View: `CurrentSectionKey` = Wurzel des neuen Buchs, `Cursor` = null, `RangeLevel` = 0. `RefreshBook` prüft den Abschnitt jeder View, nicht nur den der aktiven.
- **`SetFocus` der Session:** Er bekommt weiter den Abschnitt der aktiven View, auch nach `ActivateAsync`.

- [ ] **Step 1: Write the failing tests** in `BookViewTests.cs`. Setup: `new AppState(new FakeTimeProvider())` mit einem Buch aus `TestVault` mit zwei Überschriften „Eins“ und „Zwei“ und je einem Textblock.
  - `Unsplit_OneView_ForwardsAsBefore`: `Views.Count == 1`. Nach `State.Cursor = c` ist `Views[0].Cursor == c`, und `CurrentSectionKey` liest dasselbe wie `ActiveView.CurrentSectionKey`.
  - `Split_SecondViewStartsAtTheActiveSection_WithoutCursor`
  - `Views_KeepTheirOwnCursorSectionAndRange`:
    - Cursor in View 0 auf „Eins“ und Bereich 1 setzen.
    - `await ActivateAsync(Views[1])` und dort den Cursor auf „Zwei“ setzen.
    - Erwartet: `Views[0].Cursor` zeigt weiter auf „Eins“ mit `RangeLevel == 1`, und `State.CurrentElement` ist „Zwei“.
  - `Activate_FlushesThePreviousViewFirst`:
    - Die Rückrufe protokollieren in eine Liste. `Views[0].FlushEditor` schreibt „flush0“, das `Changed`-Ereignis schreibt „notify“.
    - Erwartet: `["flush0", "notify"]`.
  - `RevealElement_GoesToTheActiveViewOnly`: Nach dem Aktivieren von View 1 bekommt nur `Views[1].RevealRequested` den Schlüssel; `Views[0].PendingReveal` bleibt null.
  - `OtherBook_ResetsTheInactiveView`
  - `Unsplit_WhileSecondActive_FirstBecomesActive_KeepsItsPlace`
  - `Flush_And_Warn_AskEveryView`: Beide Views haben Rückrufe, und View 1 meldet ungespeicherten Text.
    - `State.WarnUnsavedText!()` ist `true`, und `State.FlushEditor!()` ruft beide Rückrufe auf.
    - Ohne jeden Rückruf ist `State.FlushEditor == null`.
- [ ] **Step 2:** Run `dotnet test tests/NoteEvolution.UI.Tests --filter FullyQualifiedName~BookViewTests`. Expected: Build-Fehler, weil `BookView` fehlt.
- [ ] **Step 3:** `BookView` anlegen und `AppState` auf Weiterleitungen umbauen. Die Felder `_cursor`, `_currentSectionKey`, `RangeLevel`, `PendingReveal*` und die drei Rückrufe ziehen in die View um. Kommentare auf Englisch im Stil der Datei.
- [ ] **Step 4:** Run `dotnet test`. Expected: die neuen Tests und die ganze Suite grün.
- [ ] **Step 5:** Commit `feat: book views behind AppState (split view groundwork)`.

### Task 2: `OutlinePane` und `EditorPane` arbeiten mit ihrer View

**Files:**
- Modify:
  - `src/NoteEvolution.UI/Components/EditorPane.razor`
  - `src/NoteEvolution.UI/Components/OutlinePane.razor`
  - `src/NoteEvolution.UI/Components/OutlineItem.razor`, falls nötig
  - `src/NoteEvolution.UI/Components/RecentChangesList.razor`, falls nötig
- Test: `tests/NoteEvolution.UI.Tests/BookViewPaneTests.cs`

**Interfaces:**
- Consumes: aus Task 1 `BookView`, `AppState.Views`, `ActiveView` und `ActivateAsync`.
- Produces: `[Parameter] public BookView? View { get; set; }` auf beiden Komponenten. Ohne Wert gilt `State.Views[0]`, deshalb ändern sich bestehende Aufrufe nicht. Intern heißt der Wert `MyView`.
- **`EditorPane`:**
  - Die Rückrufe (`FlushEditor` …) registriert sie an `MyView` statt an `State`.
  - Sie abonniert `MyView.RevealRequested`.
  - Ein gemeldeter Cursor ruft zuerst `await State.ActivateAsync(MyView)` auf und setzt ihn dann auf `MyView`. Abschnitt, Markierung, `TakePendingReveal` und die Bereichsbefehle laufen über `MyView`.
  - Die `SectionBar` rendert nur, wenn `MyView == State.ActiveView`.
  - Das äußere Element trägt die Klasse `active`, wenn die View aktiv ist und `State.IsSplit` gilt.
- **`OutlinePane`:**
  - `IsSelected` liest `MyView.CurrentSectionKey`.
  - Ein Klick auf eine Zeile (`Select`, die Buchzeile, die Abdeckung, „Zuletzt geändert“) ruft zuerst `await State.ActivateAsync(MyView)` auf und springt dann wie heute über `State.JumpTo`.
  - Das Scrollen zur markierten Zeile folgt `MyView.CurrentSectionKey`.

- [ ] **Step 1: Write the failing tests** in `BookViewPaneTests.cs`. Zwei `EditorPane`-Instanzen mit `View = State.Views[0]` und `State.Views[1]`, nachdem `State.SetSplit(true)` gelaufen ist. Jede bekommt ihren eigenen `FakeEditorInterop`; sie werden über eine Fabrik in DI registriert, und die bestehende Klasse wird erweitert, wenn nötig.
  - `CursorInPane2_ActivatesIt_BarOnlyThere`
  - `Reveal_FromRecentChanges_LandsInTheActivePaneOnly`. Review Focus 2: die andere Fake-Instanz bekommt keinen `RevealAsync`-Aufruf.
  - `Outline2_Click_ActivatesView2_AndMarksItsRow`. Die Zeile in Gliederung 1 bleibt beim Abschnitt von View 1 markiert.
- [ ] **Step 2:** Run the filter. Expected: FAIL, weil der Parameter `View` fehlt.
- [ ] **Step 3:** Umbau wie oben beschrieben.
- [ ] **Step 4:** Run `dotnet test`. Expected: alles grün, die bestehenden Tests unverändert.
- [ ] **Step 5:** Commit `feat: outline and editor work on their own book view`.

### Task 3: Teilung in der Shell: Layout, Schieber, Knopf, Kürzel, Legende

**Files:**
- Create: `src/NoteEvolution.UI/Components/SplitDivider.razor`
- Modify:
  - `src/NoteEvolution.UI/Components/Shell.razor`
  - `src/NoteEvolution.UI/Components/HeaderBar.razor`
  - `src/NoteEvolution.UI/State/UiSettings.cs`
  - `src/NoteEvolution.UI/Components/Dialogs/ShortcutsDialog.razor`
  - `src/NoteEvolution.UI/Resources/Strings.resx`
  - `src/NoteEvolution.UI/wwwroot/css/app.css`
  - `src/NoteEvolution.Desktop/wwwroot/index.html`
  - `README.md`
- Test:
  - `tests/NoteEvolution.UI.Tests/SplitViewTests.cs`
  - `NavigationTests.HeaderOrder…`: dort kommt `ne-split-toggle` vor `ne-nav-back` hinzu.
  - `DialogTests`: Legende.

**Interfaces:**
- Consumes: `AppState.SetSplit`, `IsSplit`, `Views`, `ActiveView`; der Parameter `View` aus Task 2.
- Produces:
  - **`UiSettings`:** `bool SplitView`, `double SplitRatio`, `const double MinSplitRatio = 0.2, MaxSplitRatio = 0.8, DefaultSplitRatio = 0.5`. Beim Laden wird auf den Bereich geprüft wie bei `RecentListHeight`.
  - **`SplitDivider`:** Parameter `double Ratio`, `EventCallback<double> RatioChanged`, `EventCallback DragEnded`.
    - Das Element hat `role="separator"` und die Klassen `ne-row-splitter ne-split-divider`, damit die bestehende Pfeiltasten-Sperre in `index.html` greift.
    - Beim Ziehen ergibt sich der Anteil aus der Zeigerposition relativ zum Container. Die Container-Höhe kommt von `neLayout.paneHeight(element, true)` und dem `getBoundingClientRect().top`-Versatz. Dafür gibt es eine neue JS-Funktion `neLayout.paneTop(element)`; sie liefert die Oberkante des Containers.
  - **`HeaderBar`:**
    - `[Parameter] EventCallback OnToggleSplit` und `[JSInvokable] Task ToggleSplit()`.
    - Der Knopf `ne-split-toggle` steht vor `ne-nav-back`, mit `aria-pressed` = `State.IsSplit`.
  - **`index.html`:** Strg+Alt+S (`e.ctrlKey && e.altKey && !e.shiftKey && e.key.toLowerCase() === 's'`) ruft `neNav.header.invokeMethodAsync('ToggleSplit')` auf. Bei offenem Modal passiert nichts.
  - **`Shell`:**
    - Gliederung, ihr `Splitter` und der Editor kommen in `<div class="ne-book-area">`. Ohne Teilung enthält er eine `<div class="ne-book-half">`, mit Teilung zwei, getrennt durch `SplitDivider`. Die Hälften bekommen `style="flex: {ratio} …"`.
    - Jede Hälfte rendert `OutlinePane View=…` und `EditorPane View=…`.
    - `OnToggleSplit` setzt `Settings.SplitView`, ruft `State.SetSplit(...)` auf und speichert.
    - Beim Start wird `State.SetSplit(Settings.SplitView)` aufgerufen.
    - Die Gliederung einklappen gilt für beide Hälften.
  - **Ressourcen:** `SplitToggleTitle` = „Buch geteilt anzeigen“, `ResizeSplit` = „Höhe der Hälften ändern“, `KeysNavigation2Keys` = „Strg+Alt+S“, `KeysNavigation2` = „Buch geteilt anzeigen ein / aus“.

- [ ] **Step 1: Write the failing tests** in `SplitViewTests.cs`, rendert `Shell` mit Vault:
  - `Toggle_Button_SplitsIntoTwoHalves_AndSavesTheSetting`: zwei `.ne-book-half`, je ein `.ne-outline-tree` und eine `EditorPane`; `Settings.SplitView` ist true.
  - `Toggle_ViaInterop_ToggleSplit`
  - `Ratio_FromSettings_AppliedToTheHalves_ClampedOnLoad`: `SplitRatio = 0.95` in `ui.json` wird beim Laden zu 0,5.
  - `Divider_ArrowKeys_StepByFiveHundredths_WithinBounds`
  - `Unsplit_WhileLowerActive_UpperStays`. Review Focus 3: nach dem Ausschalten ist `State.ActiveView == State.Views[0]` und `Views.Count == 1`; die Leiste ist sichtbar, und `Relevant` fragt nach dem Abschnitt von View 0.
  - In `DialogTests` kommt `"Strg+Alt+S"` zu den Legenden-Asserts.
- [ ] **Step 2:** Run the filter. Expected: FAIL.
- [ ] **Step 3:** Umsetzung wie oben. Ins README kommen ein Absatz „Geteilte Ansicht“ beim Abschnitt Gliederung und Abnahme-Punkte: zwei Stellen, Schieber, Strg+Alt+S, aktive Hälfte steuert Relevant, Neustart behält Teilung und Verhältnis.
- [ ] **Step 4:** Run `dotnet test`. Expected: alles grün.
- [ ] **Step 5:** Commit `feat: split view of the book with divider, header button and Ctrl+Alt+S`.

### Task 4: Abgleich zwischen den Hälften

**Files:**
- Modify: `src/NoteEvolution.UI/Components/EditorPane.razor`, nur falls die Tests eine Lücke zeigen
- Test: `tests/NoteEvolution.UI.Tests/SplitSyncTests.cs`

**Interfaces:**
- Consumes: Tasks 1–3.
- **Vorgehen:**
  - Heute gibt `SyncCoreAsync` dem Editor ein neues Dokument mit `keepCursor`, sobald sich der Inhalt des Buchs von seinem Stand unterscheidet. Ausgelöst wird es über `State.Changed` und `PagesChanged`.
  - Dieser Weg soll für die andere Hälfte genügen. Nur wenn ein Test zeigt, dass er nicht ausgelöst wird, kommt ein gezielter Anstoß hinzu: `State.Notify()` nach dem Speichern in `FlushAsync`, wenn `State.IsSplit` gilt.

- [ ] **Step 1: Write the failing tests** (zwei Editoren wie in Task 2):
  - `SaveInUpperHalf_ShowsInLowerHalf_KeepingItsCursor`:
    - Oben über `OnDocumentChanged` einen Text ändern und mit `Time.Advance` über die Verzögerung hinaus speichern.
    - Erwartet: Der untere Fake bekommt ein neues Dokument mit `keepCursor: true`, das den Text enthält.
  - `TypeInUpper_ClickIntoLower_SavesFirst` (Review Focus 1):
    - Oben den Text ändern, ohne die Zeit vorzustellen, und sofort unten einen Cursor melden.
    - Erwartet: In der Buchdatei steht der Text, bevor `ActiveView == Views[1]` gilt, und der untere Editor zeigt ihn.
  - `RelevantTab_FollowsTheActiveHalf`: Cursor unten in „Zwei“. Die `TopicRequest` der Relevanz, wie in `SourceFinderTests` mit einem Stub, nennt den Abschnitt „Zwei“; danach Cursor oben in „Eins“, dann nennt sie „Eins“.
- [ ] **Step 2:** Run the filter. Expected: entweder grün, dann ohne Codeänderung committen, oder FAIL mit einer konkreten Lücke.
- [ ] **Step 3:** Nur die gezeigte Lücke schließen.
- [ ] **Step 4:** Run `dotnet test` dreimal hintereinander. Expected: jedes Mal alles grün.
- [ ] **Step 5:** Commit `test: both halves stay in step`, gegebenenfalls als `fix:` mit der Lücke.
