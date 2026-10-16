# Vorberechnete Zuordnung, „Relevant“ in zwei Gruppen und Abdeckung – Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Precompute "Genauer prüfen" for every unused note in the background and build on it: instant recommendations, a two-group "Relevant", coverage bars and tab, a progress breakdown and a status dot per note card.

**Architecture:**
- Placement checks become a third kind of `UnderstandingItem` (`UnderstandingKind.Placement`) that carries its finished `PlacementCheckPrompt`. The fingerprint is a hash of that prompt, so a result is current exactly while the prompt the model would see is unchanged.
- The existing worker runs the checks as the last queue group and stores the parsed verdict as JSON.
- The session builds the prompts in the background once the book's summaries are done. It hands them to the worker and keeps in-memory snapshots (placements, note status, coverage) that the UI reads without database access.

**Tech Stack:** .NET 10, Blazor Hybrid (Photino), xUnit + bUnit, Microsoft.Data.Sqlite, Ollama REST (structured output via JSON schema), ONNX embedder.

**Spec:** `docs/superpowers/specs/2026-10-16-abdeckung-design.md` (binding; section numbers below refer to it).

## Global Constraints

- **Concurrency:** exactly one language-model request at a time; typing pause (`UnderstandingWorker.TypingQuiet`), battery pause and the pause switch apply to placement checks too (spec 1).
- **Threading:** vault model is read only on the UI thread; background work (`Task.Run`) only on snapshots; results reach components via `InvokeAsync` (existing pattern in `VaultSession.Feed`).
- **Without the language model:** behavior as today (spec 1, 5, 6.3, 7.2).
- **Text and comments:** UI text German in `Strings.resx` (no literals in markup except glyphs); code comments English, matching surrounding density.
- **Tests:** bUnit waits use `WaitForAssertion`/`WaitForElement` after clicks; default wait timeout is 10 s (`UiTestContext`).
- **Commits:** `git commit -F <file>` (PowerShell mangles quotes); every message ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- **SDK env (PowerShell):** `$env:DOTNET_ROOT="$env:LOCALAPPDATA\Microsoft\dotnet"; $env:PATH="$env:DOTNET_ROOT;$env:PATH"`.
- **Reference data:** `tests/quality/Referenzbuch.md`, `Notizen.md` and `expected.json` stay unchanged.

## Review Focus

1. **Placements before summaries:** a book whose summaries are not all done (or skipped) must get no placement items; once the last summary is stored, placements appear without any user action. Test in Task 5.
2. **Note edited while its check runs:** the result is stored under the old fingerprint and shown as stale, then recomputed; no exception, no stale "current". Test in Task 3.
3. **Book switched while placement prompts are being built in the background:** the late result for the old book must not replace the new book's placements. Test in Task 5.
4. **Stored verdict naming a deleted section or block:** not shown and not counted (spec 8). Tests in Tasks 4 and 9.
5. **Many notes (≥ 300) with coverage visible:** coverage and note status are computed off the UI thread and at most once per second. Session test with a counter in Task 9 (coverage) and Task 6 (status).

---

### Task 1: PlacementCheck – passung, embedding section, JSON, uncertainty; measurement

**Files:**
- Modify: `src/NoteEvolution.AI/Understanding/PlacementCheck.cs`
- Modify: `tests/NoteEvolution.AI.Tests/Understanding/PlacementCheckTests.cs`
- Modify: `tests/quality/measure.cs`

**Interfaces:**
- Produces:
  - `public enum PlacementFit { Good, Partial, Poor }`
  - `PlacementVerdict(Guid SectionKey, Guid? AfterTextBlockKey, string Reason, string? Summary = null, PlacementFit Fit = PlacementFit.Partial, Guid? EmbeddingSectionKey = null)`
  - `PlacementCheckPrompt(string System, string User, IReadOnlyList<CheckTopic> Topics, string Schema, Guid? EmbeddingSectionKey = null)`
  - `public static string PlacementCheck.Serialize(PlacementVerdict verdict)`
  - `public static PlacementVerdict? PlacementCheck.Deserialize(string json)`
  - `public static bool PlacementCheck.IsUncertain(Book book, PlacementVerdict verdict)`

- [ ] **Step 1: Write failing tests** in `PlacementCheckTests`:
  - `Prompt_Schema_HasPassungAfterStelle`: property order `["begruendung", "stelle", "passung"]`; `passung.enum` = `["gut", "teilweise", "kaum"]`; `required` contains all three.
  - `Prompt_System_ExplainsPassung`: `prompt.System` contains `"kaum"` and `"passung"`.
  - `Parse_Passung` (Theory): `"gut"` → `Good`, `"teilweise"` → `Partial`, `"kaum"` → `Poor`, missing → `Partial`, `"egal"` → `Partial`; the verdict is not null in every case.
  - `Prompt_EmbeddingSection_IsSectionOfBestPlace`: with `Build(s)` (best hit "Text unter A1.", 0.9) → `prompt.EmbeddingSectionKey == s.A1.Key`; `Parse` copies it into `verdict.EmbeddingSectionKey`.
  - `Prompt_EmbeddingSection_WithoutPlaces_IsBestTopicsSection`: no places, topics `[A1 0.8, C 0.7]` → `s.A1.Key`.
  - `Serialize_RoundTrips`: a verdict with all fields set round-trips through `Serialize`/`Deserialize` (record equality).
  - `Deserialize_Invalid_Null` (Theory): `""`, `"kein json"`, `"{}"`, `"{\"section\":\"x\"}"`.
  - `IsUncertain` (Theory over keys): same section → false; chosen A1 / embedding A (ancestor) → false; chosen A / embedding A1 (descendant) → false; chosen C / embedding A1 → true; `EmbeddingSectionKey` null → false.
- [ ] **Step 2: Run** `dotnet test tests/NoteEvolution.AI.Tests --filter FullyQualifiedName~PlacementCheck`. Expected: FAIL (members missing).
- [ ] **Step 3: Implement.**
  - Schema: append `passung = new { type = "string", @enum = new[] { "gut", "teilweise", "kaum" } }`; required gets `"passung"`.
  - System prompt: replace the JSON sentence with: `Antworte nur mit JSON: {"begruendung": ein kurzer Satz auf Deutsch (höchstens 20 Wörter), "stelle": "N.M", "passung": "gut", "teilweise" oder "kaum"}. "kaum" heißt: keiner der Abschnitte passt inhaltlich wirklich.`
  - `EmbeddingSectionKey`: the offered section that holds the highest-scoring placement hit among all `places`, using the same `SectionOf` walk; if none maps, the offered section that contains the best topic; null if no sections.
  - JSON for `Serialize`: camelCase properties `section`, `after`, `reason`, `summary`, `fit`, `embeddingSection`. `fit` is written as the enum name.
  - `Deserialize`: catches `JsonException`; returns null if `section` is missing or `Guid.Empty`.
- [ ] **Step 4: Run** the filter again. Expected: PASS. Then run all AI tests. Expected: PASS.
- [ ] **Step 5: Measurement.** In `measure.cs`, after the check loop, print three blocks:
  - "Passung / fit": count per value and the correct-sub-chapter rate per value, using the existing `Row` helper.
  - "Unsicher / uncertain": count and correct rate.
  - "Ziele / goals (spec 1)": "kaum ≤ 10 %" and "gut richtig ≥ 75 %", each met or not met.

  Build with `dotnet build tests/quality/measure.cs`. Expected: 0 errors.
- [ ] **Step 6: Commit** "feat(ai): placement check rates the fit and remembers the embedding's section".

### Task 2: Store kind and placement items

**Files:**
- Modify: `src/NoteEvolution.AI/Understanding/UnderstandingStore.cs` (enum, `WithoutVector`)
- Modify: `src/NoteEvolution.AI/Understanding/UnderstandingItems.cs`
- Test: `tests/NoteEvolution.AI.Tests/Understanding/UnderstandingItemsTests.cs`, `UnderstandingStoreTests.cs`

**Interfaces:**
- Consumes: `PlacementCheckPrompt` (Task 1).
- Produces:
  - `public enum UnderstandingKind { Heading, Note, Placement }`
  - `UnderstandingItem(..., Guid Key, PlacementCheckPrompt? Check = null)`: new trailing parameter.
  - `public static string UnderstandingItems.NoteIdentity(string content, string filePath)`: the `"note:" + relative path + "#" + first line (≤ 80)` that `ForNote` uses today.
  - `public static UnderstandingItem UnderstandingItems.ForPlacement(Guid noteKey, string noteIdentity, string bookLinkName, PlacementCheckPrompt prompt, string chatModel)`

- [ ] **Step 1: Write failing tests.**
  - `ForPlacement_FingerprintFollowsPrompt`: equal prompts give equal fingerprints. A different `User`, `System`, `Schema` or chat model gives a different fingerprint. `Identity == $"placement|{book}|{identity}"`. `Kind == Placement`, `Key == noteKey`, `Check` is the same prompt instance.
  - `NoteIdentity_SameAsForNote`: for a note ≥ 120 chars, `ForNote(...).Identity == NoteIdentity(content, path)`. A short note (< 120) still gets an identity.
  - `WithoutVector_IgnoresPlacements`: a stored Placement entry (no vector) is not listed by `WithoutVector`.
- [ ] **Step 2: Run** the AI tests filtered to `UnderstandingItems|UnderstandingStore`. Expected: FAIL.
- [ ] **Step 3: Implement.**
  - Fingerprint: `Hash([PromptVersion.ToString(), chatModel, "placement", prompt.System, prompt.User, prompt.Schema])`.
  - `Title` = first line part of the identity; `OwnText` = `prompt.User`; `ChildFingerprints` = `[]`.
  - `WithoutVector` gets `AND kind <> 2`; the numeric value is pinned by a comment and by the test.
- [ ] **Step 4: Run** all AI tests. Expected: PASS.
- [ ] **Step 5: Commit** "feat(ai): placement items and their store kind".

### Task 3: Worker – placement group, per-kind progress, current request

**Files:**
- Modify: `src/NoteEvolution.AI/Understanding/UnderstandingWorker.cs`
- Test: `tests/NoteEvolution.AI.Tests/Understanding/UnderstandingWorkerTests.cs`

**Interfaces:**
- Consumes:
  - `UnderstandingItem.Check` (Task 2)
  - `PlacementCheck.Options`, `Parse`, `Serialize` (Task 1)
- Produces:
  - `SetWork(IReadOnlyList<UnderstandingItem> currentBook, Guid? currentSectionKey, IReadOnlyList<UnderstandingItem> notes, IReadOnlyList<UnderstandingItem> otherBooks, IReadOnlyList<UnderstandingItem>? placements = null)`
  - `public sealed record KindProgress(int Done, int Total, int Failed, TimeSpan? Remaining)`
  - `public sealed record UnderstandingCurrent(UnderstandingKind Kind, Guid Key)`
  - `UnderstandingProgress(int Done, int Total, TimeSpan? Remaining, UnderstandingPause Pause, bool Planned = true, IReadOnlyDictionary<UnderstandingKind, KindProgress>? Kinds = null, UnderstandingCurrent? Current = null)`
  - `RunNextAsync(string system, string user, ChatOptions options, CancellationToken ct, Action? waiting = null, Action? started = null, UnderstandingCurrent? current = null)`

- [ ] **Step 1: Write failing tests** (use the existing worker fixture and `FakeChatModel`):
  - `Placements_LastGroup_AfterOtherBooks`: the call order shows all headings and cores before the first placement call; the placement call uses `item.Check.System`/`User` and `Options.Schema == item.Check.Schema`.
  - `Placement_StoredAsJson_NoVector`: the store has a Placement entry whose `PlacementCheck.Deserialize(text)` equals the parsed verdict; its `Vector` is null; the embed callback was not called for it.
  - `Placement_Unusable_IsFailedAttempt`: an answer `"{}"` makes `store.Attempts(fp) == 1`; after 3 the item counts as `Failed` in `Kinds[Placement]`.
  - `Progress_Kinds_CountEachKind`: with 2 headings, 1 other-book heading, 1 note, 2 placements: `Kinds[Heading].Total == 3`, `Kinds[Note].Total == 1`, `Kinds[Placement].Total == 2`; overall `Total` == headings of the current book + notes + placements (= 5).
  - `Progress_Remaining_PerKindMean`: after 3 timed placements (fake time 30 s each) and 3 timed cores (10 s), with 2 placements and 1 core open, `Kinds[Placement].Remaining == 60 s` and `Kinds[Note].Remaining == 10 s`.
  - `Progress_Current_WhileRequestRuns`: with the fake chat gated, `Progress.Current == new UnderstandingCurrent(Placement, noteKey)` while gated and null after release. For `RunNextAsync(..., current: x)`, `Current == x` while it runs.
  - `Placement_NoteEditedDuringRequest_StoredUnderOldFingerprint` (Review Focus 2): new work with a new placement item arrives while the old one runs. The old result is stored under the old fingerprint, the new item is queued, and no exception occurs.
- [ ] **Step 2: Run** `dotnet test tests/NoteEvolution.AI.Tests --filter FullyQualifiedName~UnderstandingWorker`. Expected: FAIL.
- [ ] **Step 3: Implement.**
  - **Plan building:**
    - `Work` gets `Placements`. The `BuildPlan` groups end with `work.Placements` (changed before new, as for the others).
    - `counted` includes the placements.
    - `Kinds` are counted over the current book, other books, notes and placements by `Kind`: `Done` counts Done and Skipped; `Failed` counts Skipped only.
  - **Processing:**
    - `ProcessAsync` branches on `UnderstandingKind.Placement`: send `item.Check!.System`, `item.Check.User`, `PlacementCheck.Options(item.Check)`.
    - Then `PlacementCheck.Parse(answer, item.Check)`. A null result goes to `Fail(item, ct)`; otherwise `Put` the entry with `Text = PlacementCheck.Serialize(verdict)`, without `Embed`.
  - **Durations:** recorded per kind (window `DurationWindow`, minimum `MinDurations`). A kind's remaining time is open × its mean, falling back to the overall mean, else null. The overall `Remaining` is the sum over the counted kinds, or null if one is null.
  - **Prune:** placement items are part of the plan's items, so the existing prune keeps their fingerprints and identities (add a test `Prune_KeepsLivePlacements`).
  - **Current:** set (and reported) just before `ChatAsync` and cleared after, for background items (kind, `item.Key`) and for `RunNextAsync` (`current` parameter).
- [ ] **Step 4: Run** all AI tests. Expected: PASS.
- [ ] **Step 5: Commit** "feat(ai): worker runs placement checks last and reports progress per kind".

### Task 4: StoreUnderstanding – placements lookup

**Files:**
- Modify: `src/NoteEvolution.AI/Understanding/StoreUnderstanding.cs`
- Test: `tests/NoteEvolution.AI.Tests/Understanding/StoreUnderstandingTests.cs`

**Interfaces:**
- Consumes: `PlacementCheck.Deserialize` (Task 1), `UnderstandingKind.Placement` (Task 2).
- Produces:
  - `public sealed record NotePlacement(PlacementVerdict Verdict, bool Stale)`
  - `public enum PlacementState { Missing, Current, Stale, Failed }`
  - `public void UpdatePlacements(string bookLinkName, IReadOnlyDictionary<Guid, (string Fingerprint, string Identity)> notes)`
  - `public NotePlacement? Placement(Book book, Guid noteKey)`
  - `public PlacementState PlacementStateOf(Guid noteKey)`: for the book given last.

- [ ] **Step 1: Write failing tests:**
  - `Placement_Current`
  - `Placement_StaleFromIdentity`: only an older fingerprint of the identity is stored, so `Stale == true`.
  - `Placement_Missing_Null`
  - `Placement_OtherBook_Null`: `book.LinkName` differs from the one given to `UpdatePlacements`.
  - `Placement_SectionGone_Null`
  - `Placement_AfterBlockGone_Null`
  - `Placement_InvalidJson_Null`
  - `PlacementState_Failed_AfterMaxAttempts`
  - `Placement_CachedUntilEntryChanged`: count store reads through a thin wrapper, or assert that a second call after `Put` sees the new entry.
  - `Placement_StoreClosed_Null`
- [ ] **Step 2: Run** the filter `StoreUnderstanding`. Expected: FAIL.
- [ ] **Step 3: Implement** with the existing patterns:
  - `Read(...)` wrapper (store errors → none).
  - A per-fingerprint cache of deserialized entries (current) and per-identity cache (latest), both cleared on `EntryChanged` for that fingerprint or identity. If the store has no identity information in the event, clear both caches.
  - Validity checks against the `Book` passed in: section exists; `after` is null or exists.
- [ ] **Step 4: Run** all AI tests. Expected: PASS.
- [ ] **Step 5: Commit** "feat(ai): look up a note's stored placement, current or stale".

### Task 5: Session – build placement work, store manual checks, placement snapshot

**Files:**
- Modify: `src/NoteEvolution.UI/State/VaultSession.cs`
- Test: `tests/NoteEvolution.UI.Tests/LanguageModelTests.cs` (session tests); extend `FakeChatModel` answers if needed (`tests/NoteEvolution.AI.Tests/FakeChatModel.cs`)

**Interfaces:**
- Consumes:
  - Tasks 1–4
  - `RelevanceService.Topics`/`Placements`
  - `UnderstandingParts`
- Produces:
  - `public NotePlacement? Placement(Guid noteKey)`: for the current book; null without the model; UI thread.
  - `public event Action? PlacementsChanged`: raised on the UI dispatcher when a stored placement or the placement map changed.
  - `NoteSnapshot(Guid Key, string Text, string Content, string FilePath, bool Used, IReadOnlyList<UsedInEntry> Usages)` (private, extended).

- [ ] **Step 1: Write failing session tests** (pattern of `Session_CheckPlacement_*`; the fake chat answers JSON calls with `{"begruendung":"…","stelle":"1.1","passung":"gut"}`):
  - `Session_Placements_AfterAllSummaries`: until all summaries of the book are stored, no chat call has a schema; afterwards every unused note gets exactly one placement call and `session.Placement(key)` is not null with `Stale == false` (Review Focus 1).
  - `Session_Placements_OnlyUnusedNotes`: a note with `used-in::` gets no placement call and `Placement` null.
  - `Session_ManualCheck_StoredAndNotRecomputed`: `CheckPlacementAsync` before the background reaches the note. Its verdict is returned and stored, and the background makes no second placement call for that note.
  - `Session_NoteEdited_PlacementStaleThenCurrent`: edit the note text, then `Placement(key).Stale == true` until the new call is answered, then false.
  - `Session_PromptBuildThrows_LoggedOtherWorkContinues` (spec 8): a throwing `Topics` for one note is logged; headings and cores are still handed to the worker.
  - `Session_BookSwitchedWhileBuilding_OldResultDropped` (Review Focus 3): gate prompt building via a slow `Topics` (wrap relevance or use a large book) and switch the book. The worker's placement items afterwards belong to the new book's identities only.
- [ ] **Step 2: Run** filter `LanguageModelTests`. Expected: FAIL.
- [ ] **Step 3: Implement.**
  - **Snapshot (UI thread):** `Feed` captures `Used` and `Usages` in `NoteSnapshot` (still only reads on the UI thread).
  - **Readiness:** `Work(...)` gets `bool placementsReady`. It is true when every item of the current book has a stored entry or ≥ `MaxAttempts` attempts (store reads in the background part only; the cached path treats unknown as not ready).
  - **Prompt building:**
    - When ready, the background part builds, for each unused snapshot note in snapshot order, `PlacementCheck.Prompt(book, note.Text, relevance.Topics(key, EmbeddingText.PassagePrefix + note.Text, book, PlacementCheck.TopicLimit), places, section => understanding.Heading(book, section)?.Text)`.
    - Then `UnderstandingItems.ForPlacement(key, NoteIdentity(content, path), book.LinkName, prompt, chatModel)`.
    - Cache per `(Book instance, note Text)` in `UnderstandingParts` (ConditionalWeakTable keyed by Book, dictionary by text).
  - **Apply:** `Apply` passes them as `placements` and calls `UpdatePlacements(book.LinkName, key → (fp, identity))`.
  - **Re-feed:** subscribe to the store's `EntryChanged`. A Heading entry while placements are not ready schedules a feed (existing debounce). A Placement entry raises `PlacementsChanged` (marshalled like `UnderstandingChanged`).
  - **Manual check:** `CheckPlacementAsync` uses the same prompt builder. It stores the parsed verdict with `store.Put(new UnderstandingEntry(Placement, item.Fingerprint, item.Identity, Serialize(verdict), chatModel, null, null))` and passes `current: new(Placement, noteKey)` to `RunNextAsync`.
- [ ] **Step 4: Run** all UI and AI tests. Expected: PASS.
- [ ] **Step 5: Commit** "feat(ui): session precomputes placements for unused notes".

### Task 6: Note status – `NoteProgress` and the dot on the note card

**Files:**
- Create: `src/NoteEvolution.UI/State/NoteProgress.cs`
- Modify: `src/NoteEvolution.UI/State/VaultSession.cs` (status snapshot)
- Modify: `src/NoteEvolution.UI/Components/NoteCard.razor`, `src/NoteEvolution.UI/wwwroot/css/app.css`, `src/NoteEvolution.UI/Resources/Strings.resx`
- Test: `tests/NoteEvolution.UI.Tests/NoteProgressTests.cs` (new), `tests/NoteEvolution.UI.Tests/NoteStatusUiTests.cs` (new)

**Interfaces:**
- Consumes:
  - `PlacementState`, `PlacementStateOf` (Task 4)
  - `UnderstandingProgress.Current` (Task 3)
  - `SemanticIndex.VectorOf`
- Produces:
  - `public enum NoteDot { None, Running, Failed, Current, Stale, Pending }`
  - `public sealed record NoteStatusInput(bool Used, bool LanguageModel, bool? Embedded, bool NeedsCore, bool CoreDone, bool CoreFailed, PlacementState Placement, UnderstandingKind? Running, PlacementFit? Fit)`: `Embedded` is null without local AI.
  - `public static (NoteDot Dot, string Tooltip) NoteProgress.Of(NoteStatusInput input, IStringLocalizer<Strings> l)`
  - `VaultSession`:
    - `public NoteStatusInput? NoteStatus(Guid noteKey)`
    - `public event Action? NoteStatusChanged`: raised at most once per second.

- [ ] **Step 1: Write failing `NoteProgressTests`:**
  - Priority as in the spec 7.2 table: Running > Failed (core or placement) > Current > Stale > Pending.
  - `Used` → `None`; `LanguageModel == false` → `None`.
  - Tooltip parts, joined with " · ":
    - always the embedding stage, unless `Embedded` is null;
    - core stage only if `NeedsCore`;
    - placement stage, with "Passung gut/teilweise/kaum" when `Current`.
  - Exact strings: `"Embedding fertig · Kernaussage fertig · Zuordnung ausstehend"`, `"Zuordnung aktuell · Passung gut"`, `"Wird gerade berechnet: Zuordnung"`, `"Zuordnung veraltet (Notiz oder Buch geändert) · wird neu berechnet"`, `"Zuordnung fehlgeschlagen, wird nach der nächsten Änderung erneut versucht"`. The stage list is appended after the first sentence for Running, Failed and Stale.
- [ ] **Step 2: Write failing bUnit tests** in `NoteStatusUiTests`:
  - The card of an unused note shows `.ne-note-dot.ne-note-dot-pending` with a `title` and the same `aria-label`.
  - After the placement is stored → `ne-note-dot-current`.
  - While its request is gated → `ne-note-dot-running`.
  - A used note has no `.ne-note-dot`; without the language model there is no dot.
  - `NoteStatusChanged` with 300 notes is raised ≤ 1 per second of fake time (Review Focus 5).
- [ ] **Step 3: Run** both test classes. Expected: FAIL.
- [ ] **Step 4: Implement.**
  - **Snapshot:**
    - The session keeps `IReadOnlyDictionary<Guid, NoteStatusInput>`, rebuilt in `Task.Run` after each `Apply`, `EntryChanged` and `ProgressChanged`.
    - Debounced to 1 s via `TimeProvider`.
    - Inputs: core done/failed via store `Get`/`Attempts` of the note item; `Embedded` via `Index.VectorOf`.
    - `Running` is filled live from `Worker.Progress.Current` in `NoteStatus(key)`.
  - **Card:**
    - `NoteCard` renders `<span class="ne-note-dot ne-note-dot-{dot}" title=… aria-label=…>` in `.ne-note-head` and subscribes to `NoteStatusChanged`.
    - CSS: 8 px circle. Colors come from the existing CSS variables for success, warning, danger, accent and muted; check `app.css` for their names. Pulse via `@keyframes`, off under `@media (prefers-reduced-motion: reduce)`.
- [ ] **Step 5: Run** all UI tests. Expected: PASS.
- [ ] **Step 6: Commit** "feat(ui): status dot per note card".

### Task 7: "Wohin damit?" shows the stored recommendation

**Files:**
- Modify: `src/NoteEvolution.UI/Components/NoteCard.razor`, `Strings.resx`, `app.css`
- Test: `tests/NoteEvolution.UI.Tests/WhereToUnderstandingTests.cs`

**Interfaces:**
- Consumes: `VaultSession.Placement`, `PlacementsChanged` (Task 5); the existing `Recommend(list, book, verdict)`.

- [ ] **Step 1: Write failing tests:**
  - `WhereTo_StoredPlacement_ShownAtOnce_NoCheckButton`
  - `WhereTo_StalePlacement_ShownWithHint_CheckButton`: `.ne-note-whereto-stale` with text "veraltet".
  - `WhereTo_FitMarker`: `.ne-note-whereto-fit-good|partial|poor` with tooltip "Passung gut" etc.
  - `WhereTo_PlacementArrivesWhileOpen_ReplacesRecommendation_ListNotReloaded`: the item elements of the list keep their identity (same `@key` order, no extra `Placements` call; count calls via a fake relevance or the existing spy).
- [ ] **Step 2: Run** filter `WhereToUnderstanding`. Expected: FAIL.
- [ ] **Step 3: Implement.**
  - Opening the list, and `PlacementsChanged` while it is open, set `list.Recommendation = Recommend(list, book, placement.Verdict)` and `list.RecommendationStale = placement.Stale`.
  - "Genauer prüfen" is shown only if `Placement(key)` is null or stale (plus the existing readiness and `LanguageModelAway` conditions).
- [ ] **Step 4: Run** all UI tests. Expected: PASS.
- [ ] **Step 5: Commit** "feat(ui): Wohin damit shows the precomputed recommendation".

### Task 8: "Relevant" in two groups

**Files:**
- Modify: `src/NoteEvolution.UI/Components/RelevantTab.razor`, `Strings.resx`, `app.css`
- Test: `tests/NoteEvolution.UI.Tests/RelevantTabTests.cs`

**Interfaces:**
- Consumes: `VaultSession.Placement`, `PlacementsChanged` (Task 5); `session.Notes.Matches(note, filter)`.

- [ ] **Step 1: Write failing tests:**
  - `Relevant_PlacedHere_FirstGroup_NewestFirst`: notes placed into the current section or a sub-section appear under `.ne-relevant-group-placed` (heading "Hierher eingeordnet").
  - `Relevant_Similar_SecondGroup_WithoutDuplicates`: heading "Weitere ähnliche".
  - `Relevant_NoPlaced_NoGroupHeadings`
  - `Relevant_PlacedStale_Marked`: `.ne-relevant-stale`.
  - `Relevant_Filters_ApplyToPlacedGroup`: date filter.
  - `Relevant_PlacedTooltip_ReasonAndFit`
- [ ] **Step 2: Run** filter `RelevantTab`. Expected: FAIL.
- [ ] **Step 3: Implement.**
  - The placed group is computed on the UI thread from `Notes.All()` (unused, top-level). It contains each note where `Placement(key)` names a section inside the current section: the verdict's section, or the section of its `after` block, walking up to the current section.
  - The list is recomputed on `PlacementsChanged` with the existing debounce of the tab.
- [ ] **Step 4: Run** all UI tests. Expected: PASS.
- [ ] **Step 5: Commit** "feat(ui): Relevant shows the notes placed here first".

### Task 9: Coverage calculation and session snapshot

**Files:**
- Create: `src/NoteEvolution.UI/State/Coverage.cs`
- Modify: `src/NoteEvolution.UI/State/VaultSession.cs`
- Test: `tests/NoteEvolution.UI.Tests/CoverageTests.cs` (new), session tests in `LanguageModelTests.cs`

**Interfaces:**
- Consumes: `NotePlacement` (Task 4), `PlacementCheck.IsUncertain` (Task 1), `UsedInEntry`.
- Produces:
  - `public sealed record CoverageNote(Guid Key, DateOnly? Date, bool Used, IReadOnlyList<UsedInEntry> Usages, NotePlacement? Placement)`
  - `public sealed record SectionCoverage(Guid SectionKey, int Used, int Assigned, int Uncertain, int TotalUsed, int TotalAssigned, int TotalUncertain, bool IsLeaf)`
  - `public sealed record Coverage(IReadOnlyDictionary<Guid, SectionCoverage> Sections, int UsedWithoutPlace, IReadOnlyList<Guid> Homeless, IReadOnlyList<Guid> Uncertain, int Checked, int Unused)`, with `public bool Complete => Checked == Unused` and `public bool IsEmpty(Guid sectionKey)` (leaf with totals 0).
  - `public static Coverage CoverageCalculator.Compute(Book book, IReadOnlyList<CoverageNote> notes)`
  - `VaultSession`:
    - `public Coverage? Coverage`
    - `public event Action? CoverageChanged`
    - `public void WatchCoverage(object owner, bool visible)`: computes only while some owner watches.

- [ ] **Step 1: Write failing `CoverageTests`:**
  - Used with a block id inside a section counts in that section's `Used`. A block id that is a sub-block of a text block resolves to that text block's section. Without an id, or with an unknown one, it counts in `UsedWithoutPlace`. A used-in entry for another book is ignored.
  - Assigned via the verdict's section and via its `after` block.
  - Chapter totals = own + children.
  - `Homeless` = unused with `Fit == Poor`, newest first.
  - `Uncertain` via `IsUncertain`.
  - `Checked` counts only non-stale placements.
  - `Complete` and `IsEmpty`.
  - A placement whose section is gone is not counted (Review Focus 4).
- [ ] **Step 2: Write failing session tests:**
  - `Coverage_ComputedOnlyWhileWatched`
  - `Coverage_RecomputedAfterPlacementStored_Debounced1s`: fake time; ≤ 1 computation per second with 300 notes (Review Focus 5).
  - `Coverage_ComputeThrows_KeepsLastAndLogs`
- [ ] **Step 3: Run** both. Expected: FAIL.
- [ ] **Step 4: Implement.**
  - A used-in block id resolves through a map from `Block.Id` (the block and all its descendants of each `TextBlock.Block`) to `TextBlock.Section`.
  - The session builds the `CoverageNote` list on the UI thread: notes, their usages, `Placement(key)`. `Compute` runs in `Task.Run`; the newest wins; the result is applied via the UI dispatcher.
  - Triggers: `PlacementsChanged`, `PagesChanged`, book switch. Debounced 1 s on `TimeProvider`.
- [ ] **Step 5: Run** all UI tests. Expected: PASS.
- [ ] **Step 6: Commit** "feat(ui): coverage per section".

### Task 10: Coverage bars in the outline

**Files:**
- Modify: `src/NoteEvolution.UI/Components/OutlinePane.razor`, `OutlineItem.razor`, `src/NoteEvolution.UI/State/UiSettings.cs`, `Strings.resx`, `app.css`; `NotesPane.razor` (open "Relevant" on request)
- Test: `tests/NoteEvolution.UI.Tests/CoverageOutlineTests.cs` (new)

**Interfaces:**
- Consumes: `VaultSession.Coverage`, `CoverageChanged`, `WatchCoverage` (Task 9).
- Produces:
  - `UiSettings.ShowCoverage` (bool, default false, persisted like `HideUsed`)
  - `AppState.RequestNotesTab(NotesTab tab)` plus `event Action<NotesTab>? NotesTabRequested`, with `public enum NotesTab { Relevant, Search, Journal, Coverage }`. `NotesPane` maps it to its private `Tab`.

- [ ] **Step 1: Write failing tests:**
  - The toggle `.ne-outline-coverage-toggle` (`aria-pressed`) shows or hides `.ne-outline-coverage`, and `Settings.ShowCoverage` is saved.
  - The bar has `.ne-outline-coverage-used`/`-assigned` widths in % relative to the largest total among siblings, and a `.ne-outline-coverage-count` text.
  - Tooltip text `"3 übernommen · 2 zugeordnet (1 unsicher)"`.
  - An empty leaf shows `.ne-outline-coverage-empty` "0" only when `Coverage.Complete`.
  - A click selects the section (`State.CurrentSectionKey`) and requests the Relevant tab.
  - Without the language model: only the used part.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.** `OutlinePane` calls `WatchCoverage(this, Settings.ShowCoverage)` and passes the coverage to the items. A click on the bar stops propagation and does what a click on the title does, plus `RequestNotesTab(NotesTab.Relevant)`.
- [ ] **Step 4: Run** all UI tests. Expected: PASS.
- [ ] **Step 5: Commit** "feat(ui): coverage bars in the outline".

### Task 11: Coverage tab

**Files:**
- Create: `src/NoteEvolution.UI/Components/CoverageTab.razor`
- Modify: `NotesPane.razor`, `Strings.resx`, `app.css`
- Test: `tests/NoteEvolution.UI.Tests/CoverageTabTests.cs` (new)

**Interfaces:**
- Consumes:
  - Task 9 (`Coverage`, `WatchCoverage`)
  - Task 10 (`NotesTab.Coverage`)
  - `NoteCard`
  - `UnderstandingProgress.Kinds[Placement]` (Task 3)

- [ ] **Step 1: Write failing tests:**
  - The tab `.ne-tab-coverage` comes after Journal; Journal stays the default.
  - Metrics: `.ne-coverage-checked` "84/120" (plus remaining time while open work exists), `.ne-coverage-empty`, `.ne-coverage-homeless`, `.ne-coverage-uncertain`.
  - Section list of leaves with path and bar. Sort select "Buchreihenfolge"/"Wenigste zuerst" changes the order. A click selects the section.
  - "Ohne passenden Ort" shows `NoteCard`s, newest first. "Unsichere Zuordnungen" is a collapsed `<details>`.
  - Without the language model: hint `"Die Abdeckung braucht das Sprachmodell (Einstellungen › Sprachmodell)"`, and the list shows used counts only.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.** The tab watches coverage while it is the active tab.
- [ ] **Step 4: Run** all UI tests. Expected: PASS.
- [ ] **Step 5: Commit** "feat(ui): coverage tab".

### Task 12: Header progress breakdown

**Files:**
- Modify: `src/NoteEvolution.UI/Components/HeaderBar.razor`, `Strings.resx`, `app.css`; `VaultSession.cs` (embedding count)
- Test: `tests/NoteEvolution.UI.Tests/LanguageModelUiTests.cs`

**Interfaces:**
- Consumes: `UnderstandingProgress.Kinds` (Task 3).
- Produces: `VaultSession.EmbeddingProgress : (int Done, int Total)?`, null without a semantic index. It counts top-level notes with `Index.VectorOf(key) != null`, computed with the status snapshot (Task 6).

- [ ] **Step 1: Write failing tests:**
  - `.ne-llm-text` is a button (`aria-expanded`). Click, Enter or Space opens `.ne-llm-breakdown` with rows `.ne-llm-row-embedding|headings|cores|placements`.
  - Each row shows "fertig/gesamt", `<progress>` and the remaining time while open.
  - The pause line shows "Pausiert: …" with the existing pause texts; a skipped count "3 übersprungen" appears when Failed > 0.
  - Escape and a click outside close it.
  - Without local AI there is no embedding row.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement** the popup following the existing `.ne-readonly-popup` pattern in `HeaderBar.razor`.
- [ ] **Step 4: Run** all UI tests. Expected: PASS.
- [ ] **Step 5: Commit** "feat(ui): progress breakdown in the header".

### Task 13 (controller): README, suite, measurement, app check

- [ ] **README:** "Sprachmodell" section — precomputation, Relevant groups, coverage (outline, tab), status dot, breakdown. Manual acceptance items for each.
- [ ] **Suite:** full `dotnet test` green.
- [ ] **Measurement:** `dotnet run tests/quality/measure.cs -- --model qwen2.5:7b --keep <scratch>/qvault`. Report the correct-sub-chapter rate (goal ≥ 65 %) and the fit goals (kaum ≤ 10 %, gut richtig ≥ 75 %) honestly; reference data untouched.
- [ ] **App:** start the app and look at the outline bars, the tab, the dot and the breakdown (PrintWindow screenshot).
