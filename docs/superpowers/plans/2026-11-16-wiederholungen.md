# Wiederholungen im Buch – Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Find text blocks of a book that repeat each other (literally or in meaning), show them in an assistant with both places side by side, and in the book view (section bar, block margin, outline), with cross references and "Gewollt" as actions.

**Architecture:** The analysis is pure code in `NoteEvolution.Core/Repetitions` (comparison text, literal runs, semantic pairs from given vectors, counting, the store of accepted pairs). `NoteEvolution.UI/State/RepetitionTracker` runs it in the background per book (as `CoverageTracker` does for the coverage), embeds changed blocks through the session's semantic index and keeps the vectors in a second `VectorCache` file. The UI reads one immutable `RepetitionSet` per book: a dialog (assistant), the section bar, a block label in the editor and the outline.

**Tech Stack:** .NET 10, Blazor Hybrid, xUnit + bUnit, TipTap (`src/NoteEvolution.UI/Editor/js/editor.js`, bundle committed), SQLite vector cache, e5 embeddings.

**Spec:** `docs/superpowers/specs/2026-11-16-wiederholungen-design.md`

## Global Constraints

- Literal: a shared run of at least **8** equal words in a row. Semantic: cosine **>= 0.90**, at most **3** counterparts per block, log line from **0.85** on.
- Compared: text blocks of the book body with their sub-points. Not compared: status `Relevant` or `Remark` (the block itself; sub-points with these statuses are left out of the block's text), the appendix, headings, image blocks, blocks with fewer than 8 words.
- The analysis never changes the book file. Only "Verweis einfügen" and "Gewollt" write.
- Accepted pairs live in `.noteevolution/repetitions.json`; a missing or damaged file counts as empty.
- UI texts are German and go through `Strings.resx` (`IStringLocalizer<Strings>`); code, comments and commit messages are English.
- Build and test: PowerShell, `$env:DOTNET_ROOT="$env:LOCALAPPDATA\Microsoft\dotnet"; $env:PATH="$env:DOTNET_ROOT;$env:PATH"; dotnet test NoteEvolution.slnx`. Editor script: `npm test` and `npm run build` in `src/NoteEvolution.UI/Editor/js`; commit `wwwroot/js/editor.bundle.js` with every change of `editor.js`.
- TDD: each task writes its failing tests first. Commit at the end of each task; messages end with `Co-Authored-By: Claude Fable 5.2 <noreply@anthropic.com>`.
- A `Book` is an immutable snapshot and may be read off the UI thread; the vault model (`IVault`, `INoteRepository`, `Page`) may not.
- The README gets a paragraph "Wiederholungen" and a checklist entry (Task 9).

## Review Focus

1. A book with one long boilerplate phrase in many blocks (e.g. the same closing sentence in 50 blocks): the analysis must stay fast and list each pair once. Test in Task 1.
2. A block edited while its embedding runs: the stale vector must not be attached to the new text. Test in Task 5.
3. `repetitions.json` naming a block id that no longer exists, or the same pair in both orders: counted once, cleaned on the next write. Test in Task 3.
4. "Verweis einfügen" on a block whose own text ends with a reference or is empty (only sub-points): the reference still lands at the end of the own text, separated by one space, no leading space in an empty text. Test in Task 4.
5. Split view with the same book in both halves: both halves show the same counts and a reference set from the assistant refreshes both. Test in Task 7.

---

### Task 1: Comparison text and literal repetitions

**Files:**
- Create: `src/NoteEvolution.Core/Repetitions/ComparisonText.cs`
- Create: `src/NoteEvolution.Core/Repetitions/LiteralRepetitions.cs`
- Test: `tests/NoteEvolution.Core.Tests/Repetitions/ComparisonTextTests.cs`, `LiteralRepetitionsTests.cs`

**Interfaces (produced):**

```csharp
namespace NoteEvolution.Core.Repetitions;

/// One paragraph of a block as shown side by side: the own text (Depth 0) or a sub-point.
public sealed record ComparedParagraph(int Depth, IReadOnlyList<InlineRun> Runs, string Plain);

/// A word of the block: paragraph index, offset and length in that paragraph's Plain, and its normalized form.
public readonly record struct ComparedWord(int Paragraph, int Start, int Length, string Normal);

public sealed record ComparedBlock(Guid Key, IReadOnlyList<ComparedParagraph> Paragraphs, IReadOnlyList<ComparedWord> Words);

public static class ComparisonText
{
    public const int MinWords = 8;
    /// The compared blocks of the book in book order (spec 2); blocks that are not compared are left out.
    public static IReadOnlyList<ComparedBlock> Of(Book book);
    /// Null when the block is not compared (status, image block, fewer than MinWords words).
    public static ComparedBlock? Of(TextBlock block);
}

/// A shared run: first word index and word count in A and in B (same count).
public readonly record struct SharedRun(int StartA, int StartB, int Words);

public sealed record LiteralPair(Guid A, Guid B, IReadOnlyList<SharedRun> Runs)
{
    public int Words { get; }   // sum of the runs
}

public static class LiteralRepetitions
{
    public const int MinRun = 8;
    /// All pairs with at least one run of MinRun words, A before B in the order of blocks, each pair once,
    /// ordered by A's then B's position. Runs are maximal and do not overlap within a pair.
    public static IReadOnlyList<LiteralPair> Find(IReadOnlyList<ComparedBlock> blocks);
}
```

**Decisions:**
- Visible text of a paragraph: `BlockTextEscape.Unescape`, then block references (`BlockRefs.Find`), source citations (`SourceCitations`), images (`BlockImages`) removed, then `InlineMarkdown.Parse`; `Plain` is the concatenation of the runs' texts. Sub-points with status `Relevant` or `Remark` are left out (`Paragraph.Status`).
- A word is a maximal sequence of letters and digits (`char.IsLetterOrDigit`); `Normal` is `ToLowerInvariant()`.
- Book body only: walk `book.ContentChildren` and the root's own text blocks; the appendix (`OutlineNode.AppendixKind != None`) is skipped.
- Algorithm: index every 8-gram of normalized words (hash → list of (block index, word index)); for each pair sharing a gram, extend along the diagonal to maximal runs. Within one block, repeated grams never pair the block with itself.

- [ ] **Step 1: Write the failing tests**

`ComparisonTextTests` (build books with the test helpers the other Core tests use, e.g. as `SectionTailTests` does):
- `Words_IgnoreCasePunctuationAndFormatting`: `"**Hallo**, Welt! Das *ist* ein Test."` gives normals `hallo, welt, das, ist, ein, test`; `Plain` is `"Hallo, Welt! Das ist ein Test."`; the first run is bold.
- `References_Citations_Images_AreNotWords`: a text with `((id))`, a `{{quelle ((id)) S. 3}}` and an image has only the words of the remaining text.
- `SubPoints_BelongToTheBlock_WithDepth`: own text plus two sub-points (depth 1, 2) gives three paragraphs with depths 0, 1, 2 and words in that order.
- `NotCompared`: a block with status `relevant`, one with `bemerkung`, one with 7 words, a block in the appendix and an image block are not in `Of(book)`; a block with exactly 8 words is.
- `RemarkSubPoints_AreLeftOut`.

`LiteralRepetitionsTests`:
- `SevenSharedWords_NoPair`, `EightSharedWords_Pair` (run of exactly 8, correct starts).
- `Run_IsMaximal`: 12 shared words give one run of 12, not several of 8.
- `TwoRuns_InOnePair`: two separate shared passages give two runs; `Words` is their sum.
- `RunAcrossSubPoints`: a run that continues from the own text into the first sub-point counts as one run.
- `PairOnce_AOrderedBeforeB`.
- `RepeatedPhraseInsideOneBlock_NoSelfPair`.
- `Boilerplate_In50Blocks_ListsEachPairOnce_Fast` (Review Focus 1): 50 blocks ending with the same 10-word sentence give 1225 pairs, each once; the call ends within 2 seconds.

- [ ] **Step 2: Run** `dotnet test tests/NoteEvolution.Core.Tests --filter FullyQualifiedName~Repetitions` — expected: compile errors (types missing).
- [ ] **Step 3: Implement** `ComparisonText` and `LiteralRepetitions` as specified above.
- [ ] **Step 4: Run the filter again** — expected: all pass.
- [ ] **Step 5: Commit** "Repetitions: comparison text and literal runs".

---

### Task 2: Semantic pairs, the combined set and counting

**Files:**
- Create: `src/NoteEvolution.Core/Repetitions/SemanticRepetitions.cs`
- Create: `src/NoteEvolution.Core/Repetitions/RepetitionSet.cs`
- Test: `tests/NoteEvolution.Core.Tests/Repetitions/SemanticRepetitionsTests.cs`, `RepetitionSetTests.cs`

**Interfaces:**
- Consumes: `ComparedBlock`, `LiteralPair` (Task 1).
- Produces:

```csharp
public sealed record SemanticPair(Guid A, Guid B, float Score);

public static class SemanticRepetitions
{
    public const float Threshold = 0.90f;
    public const float LogThreshold = 0.85f;
    public const int PerBlock = 3;
    /// Pairs of blocks (in the order of `blocks`) whose vectors reach Threshold and that are among the PerBlock
    /// most similar of at least one of the two; pairs in `literal` are left out. A block without a vector, or with
    /// one of another length, is left out. `near` receives every pair from LogThreshold on with whether it is listed.
    public static IReadOnlyList<SemanticPair> Find(
        IReadOnlyList<Guid> blocks, Func<Guid, float[]?> vectorOf, IReadOnlySet<(Guid A, Guid B)> literal,
        Action<Guid, Guid, float, bool>? near = null);
}

public enum RepetitionKind { Literal, Semantic }

public sealed record Repetition(RepetitionKind Kind, Guid A, Guid B, IReadOnlyList<SharedRun> Runs, int Words, float Score, bool Accepted);

public sealed class RepetitionSet
{
    public static RepetitionSet Empty { get; }
    /// Built for one book. `accepted` holds pairs of block ids (Block.Id), in either order.
    public static RepetitionSet Create(
        Book book, IReadOnlyList<ComparedBlock> blocks, IReadOnlyList<LiteralPair> literal,
        IReadOnlyList<SemanticPair> semantic, IReadOnlySet<(Guid, Guid)> acceptedIds, int embedded, int total);

    public bool IsFor(Book? book);                 // reference equality, as Coverage.IsFor
    public int Embedded { get; }                    // blocks with a vector
    public int Total { get; }                       // compared blocks
    public bool Complete => Embedded >= Total;
    public IReadOnlyList<Repetition> All { get; }   // literal first, then semantic; each in book order (A, then B)
    public ComparedBlock? Block(Guid key);
    /// The repetitions that are not accepted and have the block on one side.
    public IReadOnlyList<Repetition> Of(Guid textBlockKey);
    /// Not accepted pairs with at least one block in the set of keys, each pair once.
    public int Count(IReadOnlySet<Guid> textBlockKeys);
    /// As Count for the section with all its sub-sections.
    public int CountIn(OutlineNode section);
    public IReadOnlyList<Repetition> In(IReadOnlySet<Guid> textBlockKeys);
}
```

**Decisions:**
- Cosine through plain dot product of the given vectors divided by their norms (Core has no reference to the AI project; write a private helper).
- `Accepted` is true when both blocks have an id and the pair of ids is in `acceptedIds` (either order).
- `CountIn` is precomputed per node in `Create` (the outline asks for every row on every render).

- [ ] **Step 1: Write the failing tests**

`SemanticRepetitionsTests` (hand-made 3-dimensional vectors):
- `BelowThreshold_NoPair` (0.899), `AtThreshold_Pair` (0.90).
- `AtMostThreePerBlock_ButListedWhenAmongTheOthersThree`: block X has four counterparts above the threshold; the weakest is listed only if X is among that counterpart's own three.
- `LiteralPairs_AreLeftOut`.
- `MissingVector_OrOtherLength_LeavesBlockOut`.
- `Near_ReportsFrom085_WithListedFlag`.

`RepetitionSetTests`:
- `All_LiteralFirst_ThenSemantic_InBookOrder`.
- `Of_Block_GivesItsPairs_NotAccepted`.
- `Count_PairInsideRange_CountsOnce`, `Count_AcceptedPairs_DoNotCount`, `CountIn_IncludesSubSections`, `Count_WholeBook`.
- `Accepted_NeedsBothIds_EitherOrder`.
- `Complete_WhenAllEmbedded`.

- [ ] **Step 2: Run the filter** — expected: compile errors.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the filter** — expected: pass.
- [ ] **Step 5: Commit** "Repetitions: semantic pairs, set and counts".

---

### Task 3: Store of accepted pairs

**Files:**
- Create: `src/NoteEvolution.Core/Repetitions/AcceptedRepetitions.cs`
- Test: `tests/NoteEvolution.Core.Tests/Repetitions/AcceptedRepetitionsTests.cs`

**Interfaces (produced):**

```csharp
/// The accepted pairs in <vault>/.noteevolution/repetitions.json: { "books": { "<page name>": [["<id>","<id>"], …] } }.
public sealed class AcceptedRepetitions(string vaultRoot)
{
    public IReadOnlySet<(Guid, Guid)> Of(string bookName);          // normalized: smaller Guid first
    public bool Contains(string bookName, Guid idA, Guid idB);
    /// Adds or removes the pair and writes the file; pairs of `bookName` naming an id not in `liveIds` are dropped.
    public void Set(string bookName, Guid idA, Guid idB, bool accepted, IReadOnlySet<Guid> liveIds);
}
```

**Decisions:** follow `PendingStore` (`src/NoteEvolution.Core/Links/PendingStore.cs`) for reading (missing or damaged → empty), atomic writing and the folder constant (`VaultSettings.FolderName`). The file is read once per instance and kept in memory.

- [ ] **Step 1: Write the failing tests:** `MissingFile_IsEmpty`; `DamagedFile_IsEmpty_AndNextSetOverwrites`; `Set_ThenNewInstance_ReadsThePair`; `PairInBothOrders_IsOnePair` (Review Focus 3); `Set_False_Removes`; `Set_DropsPairsWithDeadIds_OfThatBookOnly`; `TwoBooks_AreSeparate`.
- [ ] **Step 2: Run the filter** — expected: compile errors.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — expected: pass.
- [ ] **Step 5: Commit** "Repetitions: store of accepted pairs".

---

### Task 4: Book commands in Core

**Files:**
- Modify: `src/NoteEvolution.Core/Books/ManuscriptEditor.cs` (next to `InsertReference`, line ~1080)
- Test: `tests/NoteEvolution.Core.Tests/Books/AppendReferenceTests.cs`

**Interfaces (produced):**

```csharp
/// Appends a cross reference to `targetKey` at the end of the own text of the text block `textBlockKey`
/// (before its sub-points), separated by one space (none if the own text is empty). Rules as InsertReference:
/// the target gets an id:: if it has none, the referring block is stamped updated::.
/// Throws ArgumentException as InsertReference does (unknown block, same block).
public static void AppendReference(Book book, Guid textBlockKey, Guid targetKey, DateTimeOffset now);

/// Whether the own text or a sub-point of `textBlockKey` already holds a reference to `targetKey`'s id.
public static bool RefersTo(Book book, Guid textBlockKey, Guid targetKey);

/// Gives both text blocks an id:: if they have none (no updated::); returns the two ids.
public static (Guid A, Guid B) EnsureIds(Book book, Guid textBlockA, Guid textBlockB);
```

**Decisions:** `AppendReference` builds on the private `Insert` that `InsertReference` uses, with the offset at the end of the content and the markdown `" " + BlockRefs.Format(id)` (without the space for an empty text). `EnsureIds` uses `Block.EnsureId()` and marks the page changed the way `InsertReference` does for a target that gets an id.

- [ ] **Step 1: Write the failing tests:**
  - `Append_PutsReferenceAtEndOfOwnText_BeforeSubPoints` (file text compared).
  - `Append_OwnTextEndingWithReference_AddsSecondWithOneSpace`; `Append_EmptyOwnText_NoLeadingSpace` (Review Focus 4).
  - `Append_TargetWithoutId_GetsId_NoUpdatedOnTarget`; `Append_StampsUpdated_OnReferrer`.
  - `Append_SameBlock_Throws`; `Append_UnknownBlock_Throws`.
  - `RefersTo_True_AlsoFromSubPoint`; `RefersTo_False_WithoutId`.
  - `EnsureIds_AddsMissingIds_KeepsExisting_NoUpdated`.
- [ ] **Step 2: Run** `--filter FullyQualifiedName~AppendReference` — expected: compile errors.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — expected: pass. Run all Core tests — expected: pass.
- [ ] **Step 5: Commit** "Book: append a cross reference, ensure ids of a pair".

---

### Task 5: Background tracker

**Files:**
- Modify: `src/NoteEvolution.AI/Semantic/VectorCache.cs` (optional file name)
- Modify: `src/NoteEvolution.AI/Embeddings/EmbeddingText.cs` (text for a compared block)
- Create: `src/NoteEvolution.UI/State/RepetitionTracker.cs`
- Modify: `src/NoteEvolution.UI/State/VaultSession.cs` (own the tracker and the store, as `_coverage`)
- Test: `tests/NoteEvolution.AI.Tests/…/VectorCacheTests.cs` (existing file, add), `tests/NoteEvolution.UI.Tests/State/RepetitionTrackerTests.cs`

**Interfaces:**
- Consumes: Tasks 1–3.
- Produces:

```csharp
// AI
public static VectorCache Open(string vaultRoot, string modelId, string fileName = "vectors.db");
/// "query: " + the words of the compared block's paragraphs, one paragraph per line (same role on both sides).
public static string EmbeddingText.ForRepetition(ComparedBlock block);

// UI
internal sealed class RepetitionTracker : IDisposable
{
    public RepetitionTracker(ILogger logger, Func<Action, Task> dispatch, AcceptedRepetitions accepted,
        Func<string, float[]?>? embed, Func<VectorCache?> cache, string modelId);
    /// The set of `book`: the cached one if it is for this very snapshot, else RepetitionSet.Empty until computed.
    public RepetitionSet Of(Book? book);
    /// Analyse `book` (a new snapshot replaces a running analysis of the same page).
    public void Schedule(Book book);
    /// The accepted pairs changed: rebuild the sets from what is computed, no embedding.
    public void Refresh();
    public event Action? Changed;    // on the UI thread (through dispatch)
    public void Dispose();
}

// VaultSession
public RepetitionSet RepetitionsOf(Book? book);
public event Action? RepetitionsChanged;
public AcceptedRepetitions AcceptedRepetitions { get; }
internal void ScheduleRepetitions(Book book);
```

**Decisions:**
- One background `Task` per scheduled book, cancelled by a newer schedule of the same page. Steps: `ComparisonText.Of` and `LiteralRepetitions.Find`, then publish a set with the vectors already known (cache hits by `EmbeddingText.Hash(modelId, text)`); then embed the missing blocks one by one through `embed` (`ISemanticIndex.EmbedQuery`), `VectorCache.Put` each, and publish again after every 20 blocks and at the end. A failed embedding (null) is logged once and leaves the block out.
- Vectors are attached by text hash, never by block key, so an edited block never gets the vector of its old text (Review Focus 2).
- `embed` null (AI off) → only literal pairs; `Embedded` stays 0 and `Total` is set to 0 so the set counts as complete.
- The second cache file is `book-vectors.db` (the notes' cache prunes everything that is not a note). No pruning in this plan.
- Log line per near pair, once per session: `"Repetition candidate {Score:F3} listed={Listed}: \"{A}\" ~ \"{B}\""` with `Excerpt.FirstLine(..., 40)`.
- `VaultSession` schedules where the coverage is scheduled today for a changed book (book switch, save, `PageReplaced`), for the book of each shown half; `Of` is keyed by page path and checks `IsFor(book)`.

- [ ] **Step 1: Write the failing tests**
  - `VectorCacheTests.Open_WithFileName_UsesThatFile_AndKeepsVectorsApart`.
  - `RepetitionTrackerTests` (fake `embed` that counts calls and can block on a `TaskCompletionSource`; dispatch runs inline; cache in a temp folder):
    - `Schedule_PublishesLiteralPairs_BeforeEmbeddingEnds` (set not complete, `Embedded < Total`).
    - `Schedule_Finishes_WithSemanticPairs_AndComplete`.
    - `SecondRun_EmbedsOnlyChangedBlocks` (call count).
    - `BlockEditedWhileEmbedding_OldVectorIsNotUsedForNewText` (Review Focus 2).
    - `EmbedNull_LeavesBlockOut_AndLogsOnce`.
    - `NoEmbedder_OnlyLiteral_AndComplete`.
    - `Of_OtherSnapshot_IsEmpty_UntilComputed`.
    - `Refresh_AfterAccept_MarksPairAccepted_WithoutEmbedding`.
- [ ] **Step 2: Run** the two filters — expected: compile errors.
- [ ] **Step 3: Implement** `VectorCache.Open` overload, `ForRepetition`, the tracker and the session wiring.
- [ ] **Step 4: Run** the filters, then all AI and UI tests — expected: pass.
- [ ] **Step 5: Commit** "Repetitions: background tracker with cached block vectors".

---

### Task 6: Commands

**Files:**
- Modify: `src/NoteEvolution.UI/State/ManuscriptCommands.cs`
- Modify: `src/NoteEvolution.UI/Resources/Strings.resx`
- Test: `tests/NoteEvolution.UI.Tests/State/RepetitionCommandsTests.cs`

**Interfaces:**
- Consumes: Task 4 (`AppendReference`, `RefersTo`, `EnsureIds`), Task 3, Task 5.
- Produces (static, in `ManuscriptCommands`):

```csharp
/// Whether the writing actions on a repetition are possible: a book is shown, no open conflict, editor not locked.
public static bool CanChangeRepetition(AppState state);
/// Appends the reference(s): from → to, and with `both` also to → from, in one saved change with one undo step.
public static Task<AdoptMessage?> AppendReferenceAsync(AppState state, Guid from, Guid to, bool both, string undoDescription, ILogger logger);
/// Sets or clears "Gewollt" for the pair; gives both blocks an id first if needed (one undo step when the file changed).
public static Task<AdoptMessage?> SetRepetitionAcceptedAsync(AppState state, Guid a, Guid b, bool accepted, string undoDescription, ILogger logger);
/// Shows `a` in the upper and `b` in the lower half of the split view, each marked.
public static Task OpenRepetitionAsync(AppState state, Guid a, Guid b);
```

**Decisions:**
- Both writing commands go the way `InsertReferenceAsync` goes (line ~187, shared helper at ~411 / ~1588): flush unsaved editor text first, change the freshly loaded book, save, push a `StructureUndo`. A block that no longer exists returns the message `RepetitionGone` and changes nothing.
- After `SetRepetitionAcceptedAsync` the session's tracker is refreshed (`Refresh`).
- `OpenRepetitionAsync`: `state.SetSplit(true)`, then reveal `a` in the first and `b` in the second half with the same calls the outline uses to reveal an element in a given half (`RevealElement`).
- Strings: `UndoAppendReference` = "Verweis einfügen", `UndoRepetitionAccepted` = "Wiederholung als gewollt abhaken", `RepetitionGone` = "Diese Stelle gibt es nicht mehr."

- [ ] **Step 1: Write the failing tests** (set up as `MoveRangeTests` does):
  - `AppendReference_OneDirection_SavesAndUndoRestoresFile` (undo text „Rückgängig: Verweis einfügen“).
  - `AppendReference_Both_IsOneUndoStep`.
  - `AppendReference_BlockGone_ReturnsMessage_FileUnchanged`.
  - `Accept_BlocksWithoutId_GetIds_AndPairIsStored`; `Accept_BlocksWithIds_FileUnchanged_NoUndoStep`; `Unaccept_RemovesPair`.
  - `CanChange_FalseWithConflict_AndWhenLocked`.
  - `Open_SplitsAndRevealsBoth`.
- [ ] **Step 2: Run** `--filter FullyQualifiedName~RepetitionCommands` — expected: compile errors.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — expected: pass.
- [ ] **Step 5: Commit** "Repetitions: reference, accept and open commands".

---

### Task 7: Assistant dialog

**Files:**
- Create: `src/NoteEvolution.UI/Components/Dialogs/RepetitionsDialog.razor`
- Create: `src/NoteEvolution.UI/Components/RepetitionText.razor` (one block with highlights)
- Modify: `src/NoteEvolution.UI/Components/HeaderBar.razor` (menu entry `ne-assistant-repetitions`, greyed out without a book, as `OpenNormalize`), `Shell.razor` (`Dialog.Repetitions`, optional start pair)
- Modify: `Strings.resx`, `wwwroot/css/app.css`
- Test: `tests/NoteEvolution.UI.Tests/Components/RepetitionsDialogTests.cs`, `RepetitionTextTests.cs`

**Interfaces:**
- Consumes: `VaultSession.RepetitionsOf`, `RepetitionsChanged`, commands of Task 6, `RepetitionSet`.
- Produces: `<RepetitionsDialog OnClose="…" Start="(Guid A, Guid B)?" />`; `Shell.ShowRepetitions((Guid, Guid)? pair)` reachable for Task 8 through a cascading parameter or the existing way `SectionBar` opens a dialog (`OnHistory` pattern).

**Decisions:**
- Layout: `Modal` wide; left `.ne-rep-list` (groups „Wörtlich (n)“, „Inhaltlich ähnlich (n)“; entry `.ne-rep-item` with both places „Abschnitt › Textanfang“ via `Excerpt`, strength „n Wörter“ or „n %“), right `.ne-rep-sides` with two `.ne-rep-side` columns (heading path above, `RepetitionText` below, „Verweis einfügen“ under each), below them „In beide Richtungen“, „Gewollt“ / „Nicht mehr gewollt“, „Im Buch öffnen“.
- Filter `select.ne-rep-chapter` („Alle“ + level-1 headings); checkbox `.ne-rep-show-accepted` („Gewollte anzeigen“).
- `RepetitionText` takes a `ComparedBlock` and the word ranges to highlight; it renders each paragraph's runs (bold/italic as `EmphasizedText`), indented by depth, and wraps highlighted character ranges in `<mark class="ne-rep-shared">`. Ranges come from the pair's `SharedRun`s mapped through `ComparedBlock.Words`.
- The dialog reads the set of `State.CurrentBook` on every render and re-renders on `RepetitionsChanged`. The selected pair is kept by its two keys; after an action the next pair of the list is selected (the same one if it is still listed).
- Reference button of a side is disabled with the title „Verweis besteht“ when `ManuscriptEditor.RefersTo` is true. Writing buttons are disabled when `!CanChangeRepetition`.
- Hints: not complete → „Wiederholungen: {0}/{1} Blöcke …“ above the list; no AI → „Inhaltlich ähnliche Stellen brauchen die lokale KI.“; empty → „Keine Wiederholungen gefunden.“ A command's message is shown in the dialog's error line (`role="alert"`).

- [ ] **Step 1: Write the failing tests**
  - `RepetitionTextTests`: `Highlights_TheSharedWords_AcrossBoldRun`; `SubPoints_AreIndented`; `NoRanges_NoMark`.
  - `RepetitionsDialogTests` (session with a fake tracker result, as other dialog tests build their session): `ShowsTwoGroups_WithCounts_AndStrength`; `ChapterFilter_KeepsPairsTouchingTheChapter`; `Accepted_HiddenByDefault_ShownWithSwitch_AsGewollt`; `SelectingAnItem_ShowsBothBlocks_EarlierLeft_WithPaths`; `ReferenceButton_CallsCommand_AndSelectsNextPair`; `ReferenceExists_ButtonDisabled`; `Both_IsOneUndoStep`; `Gewollt_RemovesFromList_AndBack`; `OpenInBook_ClosesAndSplits`; `Conflict_DisablesWritingButtons`; `Incomplete_ShowsProgress`; `NoAi_ShowsHint`; `Empty_ShowsMessage`; `Start_SelectsThatPair`; `SameBookInBothHalves_ReferenceRefreshesBoth` (Review Focus 5: after the command both halves' `CurrentBook` snapshots contain the reference).
  - `HeaderBarTests` (existing): `RepetitionsEntry_DisabledWithoutBook`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~Repetition"` in the UI tests — expected: compile errors.
- [ ] **Step 3: Implement** components, menu entry, strings and styles.
- [ ] **Step 4: Run** — expected: pass.
- [ ] **Step 5: Commit** "Repetitions: assistant with side-by-side view".

---

### Task 8: Book view — section bar and block margin

**Files:**
- Modify: `src/NoteEvolution.UI/Components/SectionBar.razor` (next to `.ne-sec-sources-area`, line ~185)
- Modify: `src/NoteEvolution.UI/Components/EditorPane.razor` (labels to the editor, as `SetBlockDatesAsync` at line ~1016)
- Modify: `src/NoteEvolution.UI/Editor/IEditorInterop.cs`, `TipTapInterop.cs`, `IEditorCallbacks` (new callback)
- Modify: `src/NoteEvolution.UI/Editor/js/editor.js` (+ bundle), `wwwroot/css/app.css`, `Strings.resx`
- Modify: `src/NoteEvolution.UI/State/UiSettings.cs` (`ShowRepetitions`, default true)
- Test: `tests/NoteEvolution.UI.Tests/Components/SectionBarRepetitionTests.cs`, `EditorPaneRepetitionTests.cs`, `src/NoteEvolution.UI/Editor/js/test/repetitions.test.mjs`

**Interfaces:**
- Consumes: `RepetitionSet.Count/In/Of`, `State.CurrentRange`, `Shell.ShowRepetitions`.
- Produces:

```csharp
Task SetBlockRepetitionsAsync(IReadOnlyDictionary<Guid, int> counts);   // IEditorInterop; 0 or absent = no mark
Task OnRepetitionMarkClicked(Guid textBlockKey);                        // IEditorCallbacks, from JS "RepetitionClicked(key)"
```
JS: editor method `setBlockRepetitions(json)` (object key → count) stored like the dates set by `setBlockDates` (a shown-only transaction, attribute `repetitions` on the text block); `TextBlockView` renders `<button class="ne-block-rep" contenteditable="false" title="n Wiederholungen">≈ n</button>` at the right edge below the dates, hidden for 0; a click calls `RepetitionClicked(key)` and is stopped for ProseMirror (`stopEvent`).

**Decisions:**
- Bar: `<button class="ne-sec-reps" aria-expanded>` „Wiederholungen: n“ (with „…“ appended while the set is not complete), only when n > 0; the count is `set.Count(keys of the text blocks in State.CurrentRange)`. Expanded: list `.ne-sec-rep-list`, one `button.ne-sec-rep` per pair: kind („wörtlich“ or „n %“) and both places „Abschnitt › Textanfang“; a click calls `Shell.ShowRepetitions(pair)`. The list closes when the range changes.
- `EditorPane` sends the counts (`set.Of(key).Count` per text block) after a document is shown and on `RepetitionsChanged`, only the changed ones, and an empty map when `UiSettings.ShowRepetitions` is off. `OnRepetitionMarkClicked` reveals the block (cursor on it) and asks the bar to expand its list.
- Strings: `SectionRepetitions` = "Wiederholungen: {0}", `RepetitionLiteral` = "wörtlich", `BlockRepetitionsTitle` = "{0} Wiederholungen".

- [ ] **Step 1: Write the failing tests**
  - JS `repetitions.test.mjs`: `setBlockRepetitions shows ≈ n on the block and hides it for 0`; `a click on the mark reports RepetitionClicked with the block key and changes no selection`; `the marks survive setDocument of the same book only when sent again` (no stale marks after a new document).
  - `SectionBarRepetitionTests`: `NoRepetitions_NoButton`; `Block_ShowsCount`; `Section_CountsPairInsideOnce`; `Incomplete_ShowsEllipsis`; `Expanded_ListsCounterparts_ClickOpensAssistantAtPair`; `AcceptedPairs_NotCounted`.
  - `EditorPaneRepetitionTests`: `SendsCounts_AfterDocument_AndOnChange`; `SwitchOff_SendsEmpty`; `MarkClick_RevealsBlock_AndExpandsBarList`.
- [ ] **Step 2: Run** `npm test` and the UI filter — expected: failures / compile errors.
- [ ] **Step 3: Implement**; `npm run build`.
- [ ] **Step 4: Run** `npm test` and the UI tests — expected: pass.
- [ ] **Step 5: Commit** "Repetitions: counter in the section bar and mark at the block".

---

### Task 9: Outline, switch, status line, README

**Files:**
- Modify: `src/NoteEvolution.UI/Components/OutlinePane.razor`, `OutlineItem.razor`
- Modify: the component that shows the AI status line (`AiStatus` / „KI: lokal – indexiert“; find it by the resource key of that text)
- Modify: `Strings.resx`, `app.css`, `README.md`
- Test: `tests/NoteEvolution.UI.Tests/Components/OutlineRepetitionTests.cs`, status line test file of that component

**Interfaces:** consumes `RepetitionSet.CountIn`, `UiSettings.ShowRepetitions`, `VaultSession.RepetitionsChanged`.

**Decisions:**
- Outline head: button `.ne-outline-repetitions-toggle` („Wiederholungen anzeigen“, `aria-pressed`) next to the coverage toggle; toggling saves `UiSettings.ShowRepetitions` and makes the editor panes resend their marks (raise the settings-changed notification the coverage toggle raises).
- Row: `<span class="ne-outline-reps" title="n Wiederholungen">≈ n</span>` in `.ne-outline-meta` when the switch is on and `CountIn(node) > 0`; „…“ appended while not complete.
- Status line: while the set of the current book is not complete, append „ · Wiederholungen: {0}/{1} Blöcke“.
- README: paragraph „Wiederholungen“ (what counts, the three places, the actions, where „Gewollt“ is stored, the limits of spec 3.2 and 8) and a checklist entry under „Assistenten und Prüfung“ following spec 9 „Im Programmfenster“.

- [ ] **Step 1: Write the failing tests:** `Row_ShowsCountOfSectionWithSubSections`; `Row_NoCount_WhenZero`; `SwitchOff_HidesCounts_AndIsRemembered`; `Incomplete_ShowsEllipsis`; `Rerenders_OnRepetitionsChanged`; status line `ShowsProgress_WhileIncomplete_NotWhenComplete`.
- [ ] **Step 2: Run** the UI filter — expected: failures.
- [ ] **Step 3: Implement**, write the README parts.
- [ ] **Step 4: Run** the whole suite (`dotnet test NoteEvolution.slnx`, `npm test`) — expected: all pass.
- [ ] **Step 5: In the program window** (test instance with its own WebView2 user data folder, real mouse through CDP): a book with one literal and one reworded repetition; check the marks after opening, the bar's list, the assistant, a reference set, „Gewollt“, and the state after a restart. Note what was and was not tried.
- [ ] **Step 6: Commit** "Repetitions: outline counts, switch, status line, README".
