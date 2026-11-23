# Wiederholungen: Satzbestätigung, Unterstreichung, Tab — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Inhaltliche Wiederholungen nur noch mit bestätigtem Satzpaar; wörtliche immer, inhaltliche bei offenem Tab im Editor unterstrichen; ein Tab „Wiederholungen“, der einen Treffer neben der Buchansicht zeigt, mit Miniansicht und ‹ ›.

**Architecture:** Der Kern bekommt Satzzerlegung, eine Abbildung von Vergleichstext auf Editor-Offsets und eine zweistufige semantische Suche (Blockkandidaten → Satzbestätigung). Der `RepetitionTracker` bettet nach den Blöcken die Sätze der Kandidatenblöcke ein. Die UI ersetzt den Blockmarker durch Inline-Dekorationen (Muster der Glossarmarken), den Dialog durch `RepetitionsTab` mit `BookReader` (Leseansicht des ganzen Buchs) und `RepetitionMinimap`; der aktuelle Treffer liegt als `RepetitionFocus` in `AppState`.

**Tech Stack:** .NET 10, Blazor Hybrid, bUnit/xUnit, TipTap/ProseMirror (`editor.js`, Bundle per `npm run build`, Tests per `npm test`), e5-Embeddings über `RepetitionTracker`.

**Spec:** `docs/superpowers/specs/2026-11-23-wiederholungen-tab-design.md` (baut auf `docs/superpowers/specs/2026-11-16-wiederholungen-design.md` auf).

## Global Constraints

- Satzschwelle Start `0.92f`, Vorauswahl `0.85f`, 5 Kandidaten je Block, höchstens 3 Satzpaare je Blockpaar, höchstens 3 inhaltliche Gegenstücke je Block, Sätze unter 5 Wörtern zählen nicht.
- Satzende: `.`, `!`, `?`, `…`, gefolgt von Leerraum und dann Großbuchstabe, Ziffer oder Anführungszeichen (`"`, `„`, `»`, `“`), sowie Absatzende.
- Satzvektoren: Rolle `query:`, Vektorspeicher `book-vectors.db`, Schlüssel `EmbeddingText.Hash(modelId, text)`.
- Alle sichtbaren Texte in `Resources/Strings.resx` (mit `<comment>`), keine deutschen Literale in Komponenten (bestehende `NoGerman…`-Tests).
- Markierungen sind reine Anzeige: kein `docChanged` als Bearbeitung, nichts in der Undo-Historie (Muster `shownOnlyKey` / Glossar-Plugin).
- Nach JS-Änderungen `npm run build` im Editor-Ordner; das Bundle `wwwroot/js/editor.bundle.js` wird mit committet.
- Tests: `dotnet test NoteEvolution.slnx` (mit `DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet`), `npm test` im Ordner `src/NoteEvolution.UI/Editor/js`.
- Commit-Trailer: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. Ein Absatz mit Blockverweis, Quellenzitat, Link oder Fettdruck vor der markierten Stelle: die Unterstreichung muss genau auf den wiederholten Wörtern liegen (Task 2 testet die Offset-Abbildung mit allen vier).
2. Tippen im Buch, während der Tab einen Treffer zeigt: der Tab bleibt beim Paar oder geht zum nächsten nach derselben Stelle, ohne zu springen oder zu werfen, wenn das Paar wegfällt (Task 7).
3. Split-Ansicht an: beide Editoren zeigen dieselben Unterstreichungen, der Sprung zum Ausgangstext erfolgt in der aktiven Ansicht (Task 5, Task 7).
4. Großes Buch (700+ Blöcke): Die Leseansicht rendert nicht bei jedem Tastendruck neu, nur bei neuem Analysestand oder neuem Treffer (Task 6).
5. Ein Treffer, dessen Block inzwischen gelöscht ist: Meldung `RepetitionGone`, ‹ › funktionieren weiter (Task 7).

---

### Task 1: Satzzerlegung

**Files:**
- Create: `src/NoteEvolution.Core/Repetitions/Sentences.cs`
- Test: `tests/NoteEvolution.Core.Tests/Repetitions/SentencesTests.cs`

**Interfaces:**
- Consumes: `ComparedBlock`, `ComparedParagraph.Plain`, `ComparedWord` (bestehend).
- Produces:
  - `public readonly record struct ComparedSentence(int Paragraph, int Start, int Length, int Words)`: Offsets in `ComparedParagraph.Plain`.
  - `public static class Sentences { public const int MinWords = 5; public static IReadOnlyList<ComparedSentence> Of(ComparedBlock block); public static string Text(ComparedBlock block, ComparedSentence sentence); }`.

- [ ] **Step 1: Tests schreiben**

```csharp
[Fact] public void Of_SplitsAtSentenceEnds_WithinEachParagraph()
// Absatz 0: "Schlaf festigt das Gedächtnis sehr. Das Gehirn sortiert nachts Erlebtes neu!"
// Unterpunkt: "Am Morgen ist vieles klarer als am Abend"
// => 3 Sätze; Text(...) liefert "Schlaf festigt das Gedächtnis sehr." usw.; der Unterpunkt hat Paragraph == 1.
[Fact] public void Of_NoSplit_WhenNoCapitalFollows()      // "z. B. ein Beispiel mit vielen Worten hier" bleibt ein Satz
[Fact] public void Of_SplitsBeforeDigitAndQuote()          // "… Ende. 2024 war … " und "… Ende. „Zitat …" teilen
[Fact] public void Of_ShortSentences_AreLeftOut()          // "Ja. Das ist ein Satz mit sechs Worten." => nur der zweite
[Fact] public void Of_SpanExcludesTrailingWhitespace()     // Start/Length decken den Satz samt Endzeichen, ohne Leerraum
```

- [ ] **Step 2:** `dotnet test tests/NoteEvolution.Core.Tests --filter SentencesTests` → FAIL (Typ fehlt).
- [ ] **Step 3:** `Sentences.Of` implementieren; Wörter zählt sie über `block.Words` im Bereich des Satzes.
- [ ] **Step 4:** Tests → PASS.
- [ ] **Step 5:** Commit `Repetitions: split compared blocks into sentences`.

### Task 2: Editor-Offsets und Absatzschlüssel im Vergleichstext

**Files:**
- Modify: `src/NoteEvolution.Core/Repetitions/ComparisonText.cs`
- Test: `tests/NoteEvolution.Core.Tests/Repetitions/ComparisonTextTests.cs`

**Interfaces:**
- Produces: `ComparedParagraph(Guid Key, int Depth, IReadOnlyList<InlineRun> Runs, string Plain, IReadOnlyList<int> Shown)`.
  - `Key`: Schlüssel des Textblocks (Absatz 0) bzw. des Unterpunkts (`Paragraph.Block.Key`).
  - `Shown[i]`: der Offset von `Plain[i]` im Text, wie der Editor ihn zeigt (Einheiten wie `GlossaryHit.Start`, siehe `AppendixIndex.ShownText` / `InlineMarkdown.ShownChars`: Blockverweis und Zitat je eine Einheit, Markdown-Zeichen keine). `Shown.Count == Plain.Length + 1`; der letzte Eintrag ist das Ende.
- Produces: `public static (Guid Key, int Start, int Length) ComparisonText.ShownSpan(ComparedBlock block, int paragraph, int start, int length)`, gebildet aus `Shown[start]` und `Shown[start + length]`.

- [ ] **Step 1: Tests**

```csharp
[Fact] public void ShownSpan_AfterBlockReference_CountsItAsOneUnit()
[Fact] public void ShownSpan_AfterSourceCitation_CountsItAsOneUnit()
[Fact] public void ShownSpan_InsideBold_SkipsTheStars()           // "**fett** Wort": "Wort" liegt bei Editor-Offset 5
[Fact] public void ShownSpan_AfterLink_UsesTheShownLinkText()
[Fact] public void Paragraph_Keys_AreTheBlockAndItsDetails()
```
  Jeder Test vergleicht mit dem Offset, den `AppendixIndex` für denselben Text als `GlossaryHit.Start` liefern würde (gleiches Einheitenmodell).

- [ ] **Step 2:** FAIL. **Step 3:** In `Visible`/`Paragraph` beim Entfernen und Parsen die Editor-Position jedes behaltenen Zeichens mitführen. Bestehende Konstruktoraufrufe (auch in Tests) anpassen. **Step 4:** Core-Tests → PASS.
- [ ] **Step 5:** Commit `Repetitions: map compared text to editor offsets`.

### Task 3: Zweistufige semantische Suche

**Files:**
- Modify: `src/NoteEvolution.Core/Repetitions/SemanticRepetitions.cs`, `RepetitionSet.cs`
- Test: `tests/NoteEvolution.Core.Tests/Repetitions/SemanticRepetitionsTests.cs`, `RepetitionSetTests.cs`

**Interfaces:**
- Consumes: `ComparedSentence`, `Sentences.Of` (Task 1).
- Produces:
  - `public sealed record SentencePair(ComparedSentence A, ComparedSentence B, float Score)`.
  - `public sealed record SemanticPair(Guid A, Guid B, float Score, IReadOnlyList<SentencePair> Sentences)`: `Score` ist der Wert des besten Satzpaars.
  - `SemanticRepetitions`: `CandidateThreshold = 0.85f`, `CandidatesPerBlock = 5`, `SentenceThreshold = 0.92f`, `SentencePairs = 3`, `PerBlock = 3`. `Threshold` und `LogThreshold` entfallen.
  - `public static IReadOnlyList<(Guid A, Guid B, float Score)> Candidates(IReadOnlyList<Guid> blocks, Func<Guid, float[]?> vectorOf, IReadOnlySet<(Guid A, Guid B)> literal)`: A vor B in Buchreihenfolge, ab `CandidateThreshold`, unter den `CandidatesPerBlock` ähnlichsten eines der beiden.
  - `public static IReadOnlyList<SemanticPair> Confirm(IReadOnlyList<(Guid A, Guid B, float Score)> candidates, Func<Guid, IReadOnlyList<ComparedSentence>> sentencesOf, Func<Guid, ComparedSentence, float[]?> vectorOf, Action<Guid, Guid, float, bool>? near = null)`: `near` erhält für jeden Kandidaten den besten Satzwert (oder 0) und ob er gelistet ist.
  - `Repetition` bekommt als letzten Parameter `IReadOnlyList<SentencePair> Sentences` (leer bei wörtlichen).

- [ ] **Step 1: Tests**

```csharp
[Fact] public void Confirm_TopicPairWithoutCloseSentence_IsDropped()         // Blockwert 0.95, bester Satz 0.88 → leer
[Fact] public void Confirm_PairWithOneCloseSentence_Counts_ScoreIsTheBestSentence()
[Fact] public void Confirm_AtMostThreeSentencePairs_BestFirst()
[Fact] public void Confirm_ASentenceIsUsedOnce_GreedyByScore()
[Fact] public void Confirm_PerBlock_KeepsTheThreeWithTheBestSentence()
[Fact] public void Confirm_MissingSentenceVector_LeavesThatSentenceOut()
[Fact] public void Candidates_LiteralPairs_AreExcluded()
[Fact] public void Candidates_KeepsTopFiveOfEitherBlock_From085()
[Fact] public void Near_ReportsTheBestSentenceOfEveryCandidate()
// RepetitionSetTests:
[Fact] public void Create_SemanticRepetitions_CarryTheirSentencePairs()
```

- [ ] **Step 2:** FAIL. **Step 3:** `Find` durch `Candidates` + `Confirm` ersetzen; `RepetitionSet.Create` reicht `SemanticPair.Sentences` durch. Alte Tests für `Find` an die neue Form anpassen oder entfernen, wenn ihr Fall in den neuen aufgeht. **Step 4:** PASS.
- [ ] **Step 5:** Commit `Repetitions: semantic pairs need a confirmed sentence pair`.

### Task 4: Tracker bettet die Sätze der Kandidaten ein

**Files:**
- Modify: `src/NoteEvolution.UI/State/RepetitionTracker.cs`, `src/NoteEvolution.AI/Embeddings/EmbeddingText.cs`
- Test: `tests/NoteEvolution.UI.Tests/RepetitionTrackerTests.cs`, `tests/NoteEvolution.AI.Tests/…/EmbeddingTextTests.cs`

**Interfaces:**
- Consumes: `Sentences`, `SemanticRepetitions.Candidates/Confirm` (Tasks 1, 3).
- Produces: `public static string EmbeddingText.ForSentence(ComparedBlock block, ComparedSentence sentence)` = `QueryPrefix + Sentences.Text(block, sentence)`.
- `RepetitionSet.Embedded/Total` zählen ab jetzt Blöcke **und** die Sätze der Kandidatenblöcke (Total wächst, sobald Kandidaten bekannt sind); `Complete` heißt: beides fertig.

Ablauf in `AnalyseAsync`: Phase 1 wie bisher (Blöcke). Danach Phase 2: `Candidates` aus allen bekannten Blockvektoren, die Sätze aller Blöcke in Kandidaten sammeln, fehlende Satzvektoren einzeln einbetten (gleiche Regeln: `_mayEmbed`, `IsFailed/MarkFailed`, sofort `cache.Put`, alle `PublishEvery` veröffentlichen). `Compute` ruft `Candidates` und `Confirm`; `LogNear` protokolliert den besten Satzwert je Kandidat (Grenze `MaxNearLines` bleibt).

- [ ] **Step 1: Tests**

```csharp
[Fact] public void ForSentence_IsAQueryWithTheSentenceText()                       // AI.Tests
[Fact] public async Task Sentences_AreEmbeddedOnlyForCandidateBlocks()
[Fact] public async Task SentenceVectors_ComeFromTheCache_OnTheNextRun()
[Fact] public async Task TopicPair_WithoutCloseSentence_IsNotListed()
[Fact] public async Task CloseSentence_ListsThePair_WithItsSentences()
[Fact] public async Task WithoutModel_OnlyLiteral()                                // besteht schon: an neue API anpassen
[Fact] public async Task Pending_UntilTheSentencesAreEmbedded()
```
  Die Fake-Einbettung liefert Vektoren je Text aus einem Wörterbuch, so dass Block- und Satzwerte gezielt gesetzt werden.

- [ ] **Step 2:** FAIL. **Step 3:** umsetzen. **Step 4:** UI- und AI-Tests → PASS.
- [ ] **Step 5:** Commit `Repetition tracker: embed the sentences of candidate blocks`.

### Task 5: Unterstreichungen im Editor

**Files:**
- Create: `src/NoteEvolution.UI/Editor/RepetitionTextMark.cs`, `src/NoteEvolution.UI/State/RepetitionMarks.cs`, `src/NoteEvolution.UI/Editor/js/test/repetitionmarks.test.mjs`
- Delete: `src/NoteEvolution.UI/Editor/RepetitionMark.cs`
- Modify: `editor.js` (Attribut `repetitions` und `setBlockRepetitions` raus, neues Plugin), `IEditorInterop.cs`, `TipTapInterop.cs`, `EditorPane.razor` (`ShowRepetitionsAsync`), `AppState.cs`, `wwwroot/css/app.css`, `tests/NoteEvolution.UI.Tests/FakeEditorInterop.cs`, `EditorPaneRepetitionTests.cs`, `Strings.resx`
- Test: `tests/NoteEvolution.UI.Tests/RepetitionMarksTests.cs`, `EditorPaneRepetitionTests.cs`, `repetitionmarks.test.mjs`

**Interfaces:**
- Produces:
  - `public enum RepetitionMarkKind { Literal, Semantic, Current }`
  - `public sealed record RepetitionTextMark(Guid ParagraphKey, int Start, int Length, RepetitionMarkKind Kind, string Title)`: Offsets wie `GlossaryMark`.
  - `IEditorInterop.SetRepetitionMarksAsync(IReadOnlyList<RepetitionTextMark> marks)` (ersetzt `SetBlockRepetitionsAsync`); JS `setRepetitionMarks(marks)` mit `{ key, start, length, kind: 'literal'|'semantic'|'current', title }`.
  - JS-Klassen: `ne-rep-literal` (durchgezogen), `ne-rep-semantic` (gepunktet, zweite Farbe), `ne-rep-current` (hinterlegt); ein Textblock mit einer `current`-Marke bekommt per Node-Dekoration `ne-rep-current-block` (Randlinie).
  - `AppState`: `public sealed record RepetitionFocus(RepetitionKind Kind, Guid A, Guid B, bool Swapped) { public Guid Origin => Swapped ? B : A; public Guid Other => Swapped ? A : B; }`, `RepetitionFocus? RepetitionFocus`, `bool RepetitionsTabOpen`, `void SetRepetitionFocus(RepetitionFocus?)`, `void SetRepetitionsTabOpen(bool)`, Event `Action? RepetitionViewChanged` (beide Setter lösen es aus, wenn sich etwas ändert).
  - `public static class RepetitionMarks`:
    - `IReadOnlyList<(int Paragraph, int Start, int Length)> Spans(Repetition repetition, Guid side, RepetitionSet set)`: in `Plain`-Offsets; wörtlich aus den `SharedRun`-Wortbereichen (je Lauf und Absatz ein Stück), inhaltlich die Sätze dieser Seite.
    - `IReadOnlyList<RepetitionTextMark> For(RepetitionSet set, Book book, bool tabOpen, RepetitionFocus? focus, bool showAccepted, Func<Guid, string> placeOf, IStringLocalizer l)`: wörtlich immer (offene Paare), inhaltlich nur mit `tabOpen`, `Current` nur mit `tabOpen` für die Ursprungsseite des Fokus (auch gewollt, wenn `showAccepted`). Titel: `RepetitionAlsoIn` („Auch in: {0}“, mehrere Orte mit „; “ verbunden).

- [ ] **Step 1: Tests**

```csharp
// RepetitionMarksTests
[Fact] public void Literal_IsMarkedOnBothSides_WithTheOtherPlaceAsTitle()
[Fact] public void Semantic_OnlyWhileTheTabIsOpen()
[Fact] public void Current_OnlyOnTheOriginSide_AndOnlyWithTheTab()
[Fact] public void Accepted_IsNotMarked()
[Fact] public void Spans_LiteralRunOverTwoParagraphs_GivesOnePiecePerParagraph()
[Fact] public void Marks_UseTheEditorOffsets_AfterAReference()           // Review Focus 1
// EditorPaneRepetitionTests (bestehende an neue API anpassen, dazu)
[Fact] public async Task Pane_SwitchOff_SendsNoMarks()
[Fact] public async Task Pane_TabOpened_SendsTheSemanticMarksToo()
[Fact] public async Task SplitView_BothPanesGetTheSameMarks()             // Review Focus 3
```
```js
// repetitionmarks.test.mjs
test('marks decorate the given ranges with their class and title, the document is unchanged')
test('a current mark gives its text block the ne-rep-current-block class')
test('typing maps the marks; new marks replace the old ones')
test('setting marks adds nothing to the undo history and reports no change')
```

- [ ] **Step 2:** FAIL. **Step 3:** umsetzen; Plugin nach dem Muster `GlossaryMarks` (eigener `PluginKey`, Neuberechnung bei Meta, sonst `map`); `npm run build`. **Step 4:** `npm test`, UI-Tests → PASS.
- [ ] **Step 5:** Commit `Editor: underline repetitions instead of the block mark`.

### Task 6: Leseansicht des Buchs

**Files:**
- Create: `src/NoteEvolution.UI/Components/BookReader.razor`
- Modify: `src/NoteEvolution.UI/Components/RepetitionText.razor` (Markierung über Spans mit Art statt Wortläufen), `wwwroot/index.html` (`neLayout.scrollReaderTo(container, id)`), `app.css`
- Test: `tests/NoteEvolution.UI.Tests/BookReaderTests.cs`, `RepetitionTextTests.cs`

**Interfaces:**
- Consumes: `RepetitionMarks.Spans` (Task 5).
- Produces: `BookReader` mit `[Parameter] Book Book`, `RepetitionSet Set`, `Repetition? Current`, `Guid? Side` (die hervorgehobene und angesteuerte Seite), `int Revision` (neuer Stand). Rendert alle Überschriften (`.ne-reader-heading`, Ebene als Klasse) und Textblöcke (`.ne-reader-block`, `id="ne-reader-{key}"`) in Buchreihenfolge, Anhang ausgenommen. Verglichene Blöcke über `RepetitionText` mit wörtlichen Unterstreichungen (`ne-rep-literal`) und, für `Side`, der Hervorhebung (`ne-rep-current` und `ne-rep-current-block`); übrige Blöcke als formatierter Text (`EmphasizedText`). Scrollt nach dem Rendern zu `Side`, wenn sich `Current`/`Side` geändert hat.
- `RepetitionText`: `[Parameter] IReadOnlyList<(int Paragraph, int Start, int Length, RepetitionMarkKind Kind)> Spans` ersetzt den bisherigen Wortlauf-Parameter.

- [ ] **Step 1: Tests**

```csharp
[Fact] public void Reader_ShowsHeadingsAndBlocksInBookOrder_WithoutTheAppendix()
[Fact] public void Reader_HighlightsTheSideOfTheCurrentHit()
[Fact] public void Reader_UnderlinesLiteralRepetitions()
[Fact] public void Reader_ScrollsToTheSide_OnlyWhenTheHitChanges()
[Fact] public void Reader_SameRevision_DoesNotRenderAgain()               // Review Focus 4 (ShouldRender)
```

- [ ] **Step 2:** FAIL. **Step 3:** umsetzen; `ShouldRender` nur bei geändertem `Revision`, `Current` oder `Side`. **Step 4:** PASS.
- [ ] **Step 5:** Commit `Book reader: the whole book read-only, with the repetition marks`.

### Task 7: Der Tab „Wiederholungen“

**Files:**
- Create: `src/NoteEvolution.UI/Components/RepetitionsTab.razor`, `src/NoteEvolution.UI/State/RepetitionHits.cs`
- Delete: `src/NoteEvolution.UI/Components/Dialogs/RepetitionsDialog.razor` (und seine Tests; übertragbare Fälle wandern in `RepetitionsTabTests`)
- Modify: `NotesPane.razor` (Tab `Repetitions`, meldet `SetRepetitionsTabOpen`), `AppState.cs` (`NotesTab.Repetitions`; `RequestRepetitions((Guid A, Guid B)? pair, Guid? origin = null)` wählt den Tab und setzt den Fokus), `Shell.razor` (Dialog raus), `SectionBar.razor` (Zähler übergibt den Block als `origin`), `HeaderBar.razor`, `ManuscriptCommands.cs` (`OpenRepetitionAsync` → `RevealRepetitionAsync`), `Strings.resx`, `app.css`, README
- Test: `tests/NoteEvolution.UI.Tests/RepetitionsTabTests.cs`, `RepetitionHitsTests.cs`, `SectionBarRepetitionTests.cs`

**Interfaces:**
- Consumes: `RepetitionFocus`, `RepetitionViewChanged` (Task 5), `BookReader` (Task 6).
- Produces:
  - `public static class RepetitionHits`:
    - `IReadOnlyList<Repetition> Ordered(RepetitionSet set, Book book, bool showAccepted, OutlineNode? chapter)`: erst wörtlich, dann inhaltlich, je nach Position der früheren Seite. `set.All` hat diese Ordnung schon, die Methode filtert nur.
    - `int IndexAfterChange(IReadOnlyList<Repetition> hits, RepetitionFocus focus, Book book)`: dasselbe Paar (Art, A, B), sonst der erste Treffer ab der Position des bisherigen Ausgangstexts, sonst der letzte; −1 bei keinem.
    - `RepetitionFocus FocusOn(Repetition hit, Guid? origin)`: `Swapped`, wenn `origin == hit.B`.
  - `ManuscriptCommands.RevealRepetitionAsync(AppState state, Guid origin) -> Task<AdoptMessage?>`: zeigt den Block in der aktiven Ansicht (`RevealElement`), ohne Split; `RepetitionGone`, wenn er fehlt.
  - Tab-Markup:
    - Kopfzeile: `.ne-reps-prev`, `.ne-reps-next` (ausgegraut an den Enden), `.ne-reps-count` („3 / 8“), `.ne-reps-kind`, `.ne-reps-swap` (⇄).
    - Aktionen: `.ne-reps-accept` (`RepAccept` / `RepUnaccept`), `.ne-reps-ref` mit `.ne-reps-ref-forward`, `.ne-reps-ref-back`, `.ne-reps-ref-both`.
    - Filter: `.ne-reps-filter` öffnet `.ne-reps-chapter` und `.ne-reps-show-accepted`.
    - Zustände: `.ne-reps-progress`, `.ne-reps-nomodel`, `.ne-reps-empty`, `.ne-reps-message`.
    - Darunter `.ne-reps-body` mit Minimap (Task 8) und `BookReader`.
  - Fortschritt: `RepProgress` = „inhaltlich: {0} von {1} Texten“ (Blöcke und Sätze zusammen, Task 4; die Spec nennt
    „Blöcken“, das stimmt mit den Sätzen nicht mehr).
  - Verhalten: Beim Öffnen und bei jedem Wechsel `SetRepetitionFocus` und `RevealRepetitionAsync(origin)`. Nach „Gewollt“ geht der Tab zum nächsten Treffer. Bei `RepetitionsChanged` bleibt der Index über `IndexAfterChange` erhalten.

- [ ] **Step 1: Tests**

```csharp
// RepetitionHitsTests
[Fact] public void Ordered_LiteralFirst_ThenSemantic_ByPosition()
[Fact] public void Ordered_ChapterFilter_And_ShowAccepted()
[Fact] public void IndexAfterChange_SamePair_StaysOnIt()
[Fact] public void IndexAfterChange_PairGone_TakesTheNextAfterTheSamePlace()    // Review Focus 2
[Fact] public void FocusOn_OriginIsB_Swaps()
// RepetitionsTabTests
[Fact] public async Task Tab_ShowsCountAndKind_PrevGreyedAtTheStart_NextAtTheEnd()
[Fact] public async Task Next_RevealsTheOriginInTheBook_AndTheReaderShowsTheOther()
[Fact] public async Task Swap_ExchangesOriginAndOther()
[Fact] public async Task SectionBarCounter_OpensTheTab_WithThatBlockAsOrigin()
[Fact] public async Task Accept_GoesToTheNextHit()
[Fact] public async Task References_AsBefore_GreyedOnConflict()
[Fact] public async Task Typing_KeepsThePair_OrMovesToTheNext()                  // Review Focus 2
[Fact] public async Task DeletedBlock_ShowsRepetitionGone_NavigationStillWorks() // Review Focus 5
[Fact] public async Task SplitView_RevealsInTheActiveView()                      // Review Focus 3
[Fact] public async Task Progress_And_NoModelHint_And_Empty()
[Fact] public async Task OpeningAndClosingTheTab_TogglesTheSemanticMarks()
[Fact] public void NoGermanLiterals_InRepetitionsTab()
```

- [ ] **Step 2:** FAIL. **Step 3:** umsetzen; README-Abschnitt „Wiederholungen“ auf Tab, Unterstreichung und Satzbestätigung umschreiben. **Step 4:** ganze UI-Suite → PASS.
- [ ] **Step 5:** Commit `Repetitions tab: one hit beside the book view, replaces the dialog`.

### Task 8: Miniansicht

**Files:**
- Create: `src/NoteEvolution.UI/Components/RepetitionMinimap.razor`
- Modify: `wwwroot/index.html` (`neRepetitions.trackViewports(minimap, reader)` / `untrack`), `RepetitionsTab.razor`, `app.css`
- Test: `tests/NoteEvolution.UI.Tests/RepetitionMinimapTests.cs`

**Interfaces:**
- Produces: `RepetitionMinimap` mit `[Parameter] Book Book`, `IReadOnlyList<Repetition> Hits`, `Repetition? Current`, `Guid? Origin`, `EventCallback<Guid> OnPick` (Block an der geklickten Stelle).
  - Position eines Blocks = Anteil der Zeichen (`Plain`-Längen aller Textblöcke) vor ihm am ganzen Buch, in Prozent als `top`.
  - Elemente:
    - `.ne-minimap-chapter`: Linie je Kapitel der Ebene 1.
    - `.ne-minimap-hit`: blass, je Seite jedes Treffers.
    - `.ne-minimap-a` und `.ne-minimap-b`: die Ursprungs- und die Gegenseite des aktuellen Treffers.
    - `.ne-minimap-link`: die Verbindung zwischen beiden.
    - `.ne-minimap-view-book` und `.ne-minimap-view-reader`: die Sichtrahmen. Die setzt JS aus `scrollTop / scrollHeight` des Editors der aktiven Ansicht und der Leseansicht.
  - Ein Klick ruft `OnPick` mit dem Block an diesem Anteil; der Tab scrollt die Leseansicht dorthin.

- [ ] **Step 1: Tests**

```csharp
[Fact] public void Minimap_PlacesAAndBAtTheirShareOfTheBook()
[Fact] public void Minimap_ChapterLines_And_AllHitsFaint()
[Fact] public void Minimap_Click_PicksTheBlockAtThatShare()
[Fact] public void Minimap_RegistersTheViewportTracking_AndRemovesItOnDispose()
```

- [ ] **Step 2:** FAIL. **Step 3:** umsetzen; die Sichtrahmen hören auf `scroll`- und `resize`-Ereignisse und aktualisieren sich höchstens einmal pro Animationsbild. **Step 4:** PASS.
- [ ] **Step 5:** Commit `Repetitions tab: minimap of the book between the two texts`.

### Task 9: Kalibrierung und Prüfung im Programm

**Files:**
- Modify: `src/NoteEvolution.Core/Repetitions/SemanticRepetitions.cs` (Wert und Begründung von `SentenceThreshold`)

- [ ] **Step 1:** Die App aus dem Worktree mit dem großen Testbuch und einem echten Buch des Autors starten. Das Buch nur lesend nutzen, ohne „Gewollt“ und ohne Verweise. Die protokollierten besten Satzwerte der Kandidaten sammeln.
- [ ] **Step 2:** Bei 10–20 Kandidaten nach Augenmaß beurteilen, ob es eine Wiederholung oder ein Themenpaar ist. Die Schwelle zwischen die beiden Gruppen legen. Den Wert und eine kurze Begründung in den Doc-Kommentar schreiben, Tests anpassen, falls sie den Wert verwenden.
- [ ] **Step 3:** Im Fenster prüfen:
  - wörtliche Unterstreichung ohne Tab,
  - Tab öffnen: gepunktete Unterstreichungen und Hervorhebung erscheinen,
  - ‹ › springen in beiden Texten,
  - ⇄ tauscht die Seiten,
  - die Miniansicht zeigt beide Stellen und die Sichtrahmen,
  - Tippen hält den Treffer,
  - der Zähler in der Leiste öffnet den Tab beim Block.
- [ ] **Step 4:** `dotnet test NoteEvolution.slnx` und `npm test` → alles grün.
- [ ] **Step 5:** Commit `Repetitions: sentence threshold calibrated on a real book`.
