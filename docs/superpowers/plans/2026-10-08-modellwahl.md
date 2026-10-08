# Wählbares KI-Modell – Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The user chooses the local AI model from four catalogued models (each showing its download size and estimated RAM), on first load and later in the settings. After a switch, only the new model remains on disk and the vault is re-indexed with it.

**Architecture:**
- **AI project:** The model catalog carries each model's differences as data: pooling (mean or CLS) and prefixes. `OnnxEmbedder` reads both.
- **Runtime:** `AiRuntime` gets a mutable active model and an `Activate` method. It deletes every other model directory.
- **Switching:** The Shell drives a switch: it disposes the session, activates the new model and opens the vault again. `VectorCache` already recreates `vectors.db` when the model id differs.

**Tech Stack:** .NET 10, Blazor Hybrid (Photino), ONNX Runtime 1.30, xUnit + bUnit.

**Spec:** `docs/superpowers/specs/2026-10-08-modellwahl-design.md`. Read it first; this plan argues from it.

## Global Constraints

- **Working tree:** `C:\Users\Ronny Roscher\claude\NoteEvolution\.claude\worktrees\stufe-1`, branch `modellwahl`.
  - Use absolute paths.
  - Use plain, separate git commands; no bash scripts and no compound git lines.
- **Commands:**
  - Build: `dotnet build NoteEvolution.slnx`
  - Tests: `dotnet test NoteEvolution.slnx`
  - Use the solution file that exists in the repo root (check with `ls`).
  - Baseline: 892 tests green.
- **User-visible strings:** only in `src/NoteEvolution.UI/Resources/Strings.resx` (ruling R6), in German.
- **Thread rules:** The vault and the notes are touched only on the UI thread (S4/S5).
- **R26:** No write path and no vault reopening while a conflict is open.
- **Pinned values:** Revisions, SHA-256 values and sizes come exactly from spec section 2. Never point a URL at `main`.
- **Token limit:** `MaxTokens = 512` for every model.
- **RAM estimate:** 2 × the size of the model's largest file + 150 000 000 bytes. This gives about 0.4, 0.7, 1.3 and 1.3 GB.
- **Commits:**
  - Conventional style, as in the history, e.g. `feat(ai): …` or `test(ui): …`.
  - Every commit message ends with the line `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **The download of the new model fails or is cancelled while a vault with a ready index is open.**
   - The old model stays active and keeps working; the target folder is gone.
   - Tests: Task 4 `DownloadOther_Cancelled_DeletesTargetFolder_ActiveUnchanged` and Task 6 `Switch_DownloadFails_OldModelStaysActive`.
2. **A conflict opens while the download is running.**
   - The switch does not happen and an error is shown.
   - Test: Task 6 `Switch_ConflictOpenedDuringDownload_NoSwitch`.
   - The downloaded folder is left on disk; the next start removes it (Task 4 startup cleanup).
3. **`ui.json` names an unknown model id, or the selected model's files are missing at start.**
   - An unknown id falls back to the default model.
   - When the selected model is missing, nothing is deleted: cleanup only runs when the active model is installed.
   - Test: Task 4 `Start_UnknownId_UsesDefault_MissingActive_DeletesNothing`.
4. **The editor has unsaved text when the switch reopens the vault.**
   - The text is flushed before the session is disposed.
   - Test: Task 6 `Switch_FlushesEditorBeforeDisposingSession`.
5. **Deleting the old model folder fails (a file is locked).**
   - The switch still counts; the error is logged, not thrown.
   - Test: Task 3 `DeleteAllExcept_LockedFile_ReportsError_DeletesTheRest`.

---

### Task 1: Model catalog with four models

**Files:**
- Modify: `src/NoteEvolution.AI/Model/ModelInfo.cs`
- Test: `tests/NoteEvolution.AI.Tests/Model/ModelCatalogTests.cs` (new)
- Move the existing `Catalog_E5Small_PinnedRevisionAndHashes` from `ModelStoreTests.cs` there.

**Interfaces:**
- Produces:
  - `public enum Pooling { Mean, Cls }`
  - `ModelInfo(string Id, string DisplayName, IReadOnlyList<ModelFile> Files, int Dimensions, int MaxTokens, Pooling Pooling = Pooling.Mean, string QueryPrefix = "query: ", string PassagePrefix = "passage: ")`. The defaults keep every existing test constructor valid.
  - `long ModelInfo.MemoryEstimate`
  - `ModelCatalog.E5Small`, `E5Base`, `E5Large` and `BgeM3`
  - `IReadOnlyList<ModelInfo> ModelCatalog.All`, in exactly that order
  - `ModelInfo ModelCatalog.Default`, which is `E5Small`
  - `ModelInfo? ModelCatalog.Find(string? id)`

Steps:

- [ ] **Step 1: Write the failing tests** in `ModelCatalogTests`:
  - `All_FourModelsInOrder_DefaultIsE5Small`:
    - The ids are exactly `["multilingual-e5-small-int8", "multilingual-e5-base-int8", "multilingual-e5-large-int8", "bge-m3-int8"]`.
    - `Default` is the same object as `All[0]`.
  - `Find_KnownAndUnknown`:
    - `Find` of each id returns that entry.
    - `Find("x")` and `Find(null)` return `null`.
  - `Entries_PinnedValuesFromSpec` checks for every model:
    - Its two files: `onnx/model_quantized.onnx` and `sentencepiece.bpe.model`.
    - The ONNX sizes and SHA-256 values from spec section 2.
    - The sentencepiece file: size `5_069_051`, SHA `cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865`.
    - Every URL starts with `https://huggingface.co/Xenova/<repo>/resolve/<revision>/`.
    - Dimensions are 384, 768, 1024 and 1024; MaxTokens is 512 for all.
    - E5 models have `Pooling.Mean` with prefixes `query: ` / `passage: `; BgeM3 has `Pooling.Cls` with prefixes `""` / `""`.
  - `MemoryEstimate_TwiceLargestFilePlus150MB`:
    - It equals `2 * max(file size) + 150_000_000` for every model.
    - Rounded to tenths of a GB (1e9) that gives 0.4, 0.7, 1.3 and 1.3.
  - `Ids_AndUrls_Unique` across `All`.

- [ ] **Step 2: Run the tests.** They fail to compile.
  - Command: `dotnet test tests/NoteEvolution.AI.Tests --filter ModelCatalogTests`

- [ ] **Step 3: Implement.**
  - The private `Pinned` helper takes the repo and the revision.
  - Revisions:
    - e5-small `761b726dd34fb83930e26aab4e9ac3899aa1fa78` (unchanged)
    - e5-base `1ec9243030a27d1a115d5c340572074c125b58b2`
    - e5-large `00fc3aeb3dbb95842de2ac1961d33c6319acf57b`
    - bge-m3 `4de13258303883538bd53b696b452bf8099f0858`
  - ONNX files (`onnx/model_quantized.onnx`):
    - e5-base: `df7a9a29309e3ad491e1783adf8baee710262cc06079c7cbab63c630277fac94`, `278_647_662`
    - e5-large: `0a8d65db9a36f810ba5da15249f13145fcdc7890e6656f1fd38cd8b7c4db1fca`, `561_768_762`
    - bge-m3: `0826f8c1ab9edf1801db86c61919d4d108e8bfc0b809ec823ad366882ff0b77d`, `569_694_530`
  - Display names: `multilingual-e5-small (int8)`, `multilingual-e5-base (int8)`, `multilingual-e5-large (int8)`, `bge-m3 (int8)`.

- [ ] **Step 4: Run the AI tests.** They all pass.
  - Command: `dotnet test tests/NoteEvolution.AI.Tests`

- [ ] **Step 5: Commit** with `feat(ai): catalog of four selectable models`.

### Task 2: Embedder follows the model's pooling and prefixes

**Files:**
- Modify:
  - `src/NoteEvolution.AI/Embeddings/EmbeddingText.cs`
  - `src/NoteEvolution.AI/Embeddings/VectorMath.cs`
  - `src/NoteEvolution.AI/Embeddings/OnnxEmbedder.cs`
  - `src/NoteEvolution.AI/Embeddings/IEmbedder.cs` (docs only)
- Test:
  - `tests/NoteEvolution.AI.Tests/Embeddings/EmbeddingTextTests.cs`
  - `tests/NoteEvolution.AI.Tests/Embeddings/VectorMathTests.cs`
  - new `tests/NoteEvolution.AI.Tests/Embeddings/BgeM3ModelTests.cs`
  - new `tests/NoteEvolution.AI.Tests/BgeM3ModelFactAttribute.cs`

**Interfaces:**
- Consumes: `ModelInfo.Pooling`, `QueryPrefix`, `PassagePrefix` and `ModelCatalog.BgeM3` (Task 1).
- Produces:
  - `public static string EmbeddingText.ForModel(string markedText, ModelInfo model)`
  - `public static float[] VectorMath.ClsNormalize(ReadOnlySpan<float> hidden, int tokens, int dims)`

Steps:

- [ ] **Step 1: Write the failing tests.**
  - `ForModel_E5_KeepsMarkers`: `ForModel("query: a", E5Small) == "query: a"`, `ForModel("passage: b", E5Small) == "passage: b"`.
  - `ForModel_BgeM3_RemovesMarkers`:
    - `ForModel("query: a", BgeM3) == "a"`
    - `ForModel("passage: b", BgeM3) == "b"`
    - `ForModel("ohne", BgeM3) == "ohne"`
    - `ForModel("xquery: a", BgeM3) == "xquery: a"` (only a leading marker counts)
  - `ClsNormalize_TakesFirstTokenNormalized`:
    - With hidden `[3,4, 9,9]`, tokens 2, dims 2, the result is `[0.6, 0.8]`.
    - A zero first row gives `[0,0]`.
    - A length mismatch throws `ArgumentException`.
  - `BgeM3ModelTests.Embeds1024Normalized_SimilarTextsCloser`, marked `[BgeM3ModelFact]`:
    - Load with `OnnxEmbedder.Load(BgeM3ModelFactAttribute.ModelDirectory, ModelCatalog.BgeM3)` (the internal overload).
    - Embed `query: Wie gehe ich mit Angst um?`, `passage: Angst baut Widerstand auf, Vertrauen baut Schwung auf.` and `passage: Der Zug fährt morgen um acht Uhr.`
    - Every vector has 1024 dimensions and norm ≈ 1 (±1e-3).
    - cos(q, angst) > cos(q, zug).
  - `BgeM3ModelFactAttribute` works like `RealModelFactAttribute`:
    - The directory is `NOTEEVOLUTION_BGE_M3_DIR`, else `%APPDATA%/NoteEvolution/models/bge-m3-int8`.
    - The test is skipped when a catalogued file is missing.

- [ ] **Step 2: Run the tests.** They fail; the bge test is skipped.

- [ ] **Step 3: Implement.**
  - `ForModel` replaces a leading `EmbeddingText.QueryPrefix` with `model.QueryPrefix`, and a leading `PassagePrefix` with `model.PassagePrefix`. Any other text passes through unchanged.
  - `OnnxEmbedder` keeps `ModelInfo`:
    - In `Embed`, map each text with `ForModel` **before** the whitespace check. A bge text that is only a marker then becomes a zero vector; e5 behaves exactly as before.
    - In `EmbedBatch`, pool with `MeanPoolNormalize` or `ClsNormalize` according to `model.Pooling`.
  - Update the class summary: it is no longer e5-only.
  - Update the docs of `IEmbedder.Embed`: texts carry the role marker `query: ` / `passage: `, and the embedder maps it to the model's prefix.

- [ ] **Step 4: Run the AI tests.** They all pass, including the unchanged `RealModelTests` when e5-small is installed.

- [ ] **Step 5: Commit** with `feat(ai): embedder pools and prefixes per model`.

### Task 3: ModelStore deletes other models

**Files:**
- Modify: `src/NoteEvolution.AI/Model/ModelStore.cs`
- Test: `tests/NoteEvolution.AI.Tests/Model/ModelStoreTests.cs`

**Interfaces:**
- Produces:
  - `public IReadOnlyList<string> ModelStore.DeleteAllExcept(ModelInfo keep)`
  - `public string? ModelStore.Delete(ModelInfo model)`
  - Both return error texts (`"<folder>: <message>"`), empty or `null` on success. They never throw for IO or permission errors.

Steps:

- [ ] **Step 1: Write the failing tests.**
  - `DeleteAllExcept_LeavesOnlyKept`:
    - Create the folders `models/a/x.bin`, `models/b/y.bin` and `models/keep/z.bin`.
    - After the call, only `models/keep` exists. The result is empty.
    - A missing `models` folder gives an empty result.
  - `DeleteAllExcept_LockedFile_ReportsError_DeletesTheRest` (Windows; skip elsewhere with `Assert.Skip` or a guard):
    - Hold `models/a/x.bin` open with `FileShare.None`.
    - The result has one entry containing `a`; `models/b` is gone.
  - `Delete_RemovesModelFolder_MissingIsFine`: after the call the folder is gone and the result is `null`; a second call also returns `null`.

- [ ] **Step 2: Run the tests.** They fail.

- [ ] **Step 3: Implement** with `Directory.Delete(path, recursive: true)`. Catch `IOException` and `UnauthorizedAccessException`.

- [ ] **Step 4: Run the tests.** They pass.

- [ ] **Step 5: Commit** with `feat(ai): model store removes other model folders`.

### Task 4: AiRuntime with active model, download of any model, Activate

**Files:**
- Modify:
  - `src/NoteEvolution.UI/State/AiRuntime.cs`
  - `src/NoteEvolution.UI/State/UiSettings.cs`
  - `src/NoteEvolution.Desktop/Program.cs`
  - `src/NoteEvolution.UI/Components/Dialogs/AiModelDialog.razor` (only the call `Ai.DownloadAsync(Ai.Model, …)`, so the code compiles)
  - `tests/NoteEvolution.UI.Tests/UiTestContext.cs`
- Test:
  - new `tests/NoteEvolution.UI.Tests/AiRuntimeTests.cs` (plain xUnit, using `TempDir` and `FakeModelHandler`)
  - `tests/NoteEvolution.UI.Tests/UiSettingsTests.cs`

**Interfaces:**
- Consumes: `ModelCatalog.All/Default/Find` (Task 1) and `ModelStore.Delete/DeleteAllExcept` (Task 3).
- Produces, in `AiRuntime`:
  - Constructor: `AiRuntime(ModelStore store, ModelDownloader downloader, Func<ModelInfo, IEmbedder> loadEmbedder, ILogger<AiRuntime> logger, IReadOnlyList<ModelInfo>? catalog = null, string? activeModelId = null)`
    - `catalog` defaults to `ModelCatalog.All`.
    - The active model is the catalog entry with `activeModelId`, otherwise `catalog[0]`.
  - `IReadOnlyList<ModelInfo> Models`
  - `ModelInfo Model { get; }`: the active model, which can change through `Activate`; thread-safe read.
  - `Task DownloadAsync(ModelInfo model, IProgress<DownloadProgress>? progress, CancellationToken ct)`, replacing the old parameterless-model overload.
  - `void Activate(ModelInfo model)`
- Produces, in `UiSettings`: `string? AiModelId`.
- Produces, in `UiTestContext`:
  - `UseAi(bool installed, Func<IEmbedder> loadEmbedder, IReadOnlyList<ModelInfo>? catalog = null)`
  - `TestModel(string id)`: one-file test model, same file data, distinct id.
  - Existing callers stay unchanged.

Steps:

- [ ] **Step 1: Write the failing tests** in `AiRuntimeTests`. They use two test models, `a` and `b`, and a counting embedder factory.
  - `DownloadOther_InstallsIt_ActiveUnchanged`:
    - With `a` active and installed, `DownloadAsync(b)` → `IsInstalled(b)`.
    - `Model` is still `a`; `ModelInstalled` is true.
    - `ModelChanged` is raised twice.
  - `DownloadOther_Cancelled_DeletesTargetFolder_ActiveUnchanged`:
    - The handler hangs; cancelling throws `OperationCanceledException`.
    - `Directory.Exists(store.DirectoryOf(b))` is false; `a` still exists.
  - `DownloadOther_Fails_DeletesTargetFolder`: with HTTP 404, the call throws `ModelDownloadException` and the `b` folder is gone.
  - `DownloadActive_NotInstalled_FailureKeepsNothingButDoesNotThrowOnCleanup`:
    - `a` is active and missing; the download fails.
    - `ModelInstalled` is false and no exception other than the download error is thrown.
  - `Activate_DisposesOldEmbedder_LoadsNewOnNextAccess_DeletesOldFolder_RaisesModelChanged`:
    1. Access `Embedder` → factory called with `a`.
    2. Download `b`, then `Activate(b)`:
       - the old embedder (an `IDisposable` fake) is disposed
       - `Model == b`
       - the `a` folder is gone
       - `ModelChanged` is raised
    3. Access `Embedder` again → factory called with `b`.
  - `Activate_NotInstalled_Throws_SameModel_NoOp`:
    - `Activate(b)` without files → `InvalidOperationException`.
    - `Activate(a)` while `a` is active and installed → no event, embedder not disposed.
  - `Activate_ResetsLoadError`: the factory throws for `a` (`LoadError` set); after `Activate(b)` with a working factory, `Embedder` is not null and `LoadError` is null.
  - `Start_InstalledActive_DeletesOtherFolders`: the folders of `a` and `b` exist on disk and the runtime is created with `activeModelId: "a"` → `b` is gone.
  - `Start_UnknownId_UsesDefault_MissingActive_DeletesNothing`:
    - With `activeModelId: "zzz"`, `Model` is `catalog[0]`.
    - `catalog[0]` is not installed while the `b` folder exists → `b` still exists.
  - In `UiSettingsTests`, `AiModelId_RoundTrips`: save `"bge-m3-int8"`, load it back; the default is `null`.

- [ ] **Step 2: Run the tests.** They fail.
  - Command: `dotnet test tests/NoteEvolution.UI.Tests --filter "AiRuntimeTests|UiSettingsTests"`

- [ ] **Step 3: Implement.**
  - **`Model`:** Hold it in a field read under `_gate`.
  - **`DownloadAsync`:**
    - On a failure or cancel where `model.Id != Model.Id`, call `_store.Delete(model)` and log any error.
    - Afterwards recompute `_installed` for the **active** model.
  - **`Activate`:**
    - Under `_loadGate`, then `_gate`:
      1. Throw `ObjectDisposedException` if disposed.
      2. Return when the id equals the active model's id and `_installed` is true.
      3. Throw `InvalidOperationException` when `!_store.IsInstalled(model)`.
      4. Dispose the old embedder (`as IDisposable`).
      5. Reset `_embedder`, `_loadAttempted` and `_loadError`.
      6. Set `Model = model` and `_installed = true`.
    - Outside the locks, call `DeleteAllExcept(model)`, log each error as a warning and call `RaiseModelChanged()`.
  - **Constructor:** When the active model is installed, call `DeleteAllExcept(Model)` and log the errors.
  - **`Load()`:** Calls `_loadEmbedder(Model)`.
  - **`Program.cs`:**
    - Load `UiSettings` into a local, register that instance, and pass `ModelCatalog.All` and `settings.AiModelId` to the runtime.
    - The factory is `m => OnnxEmbedder.Load(models, m)`.
  - **Callers of the old constructor:** Update the callers in tests (`UseAi`, `AiTests`, `RelevantTabTests`, `WhereToTests`) to the `Func<ModelInfo, IEmbedder>` shape. `UseAi` wraps its `Func<IEmbedder>`.

- [ ] **Step 4: Run all tests.** They pass.
  - Command: `dotnet test` on the solution.

- [ ] **Step 5: Commit** with `feat(ui): ai runtime switches its active model`.

### Task 5: Model dialog with selection list, settings section

**Files:**
- Create: `src/NoteEvolution.UI/State/ModelSizes.cs`
- Modify:
  - `src/NoteEvolution.UI/Components/Dialogs/AiModelDialog.razor`
  - `src/NoteEvolution.UI/Components/Dialogs/SettingsDialog.razor`
  - `src/NoteEvolution.UI/Resources/Strings.resx`
  - `src/NoteEvolution.UI/wwwroot/css/app.css`, for the list (follow the existing `ne-ai-model-*` / dialog styles)
- Test:
  - `tests/NoteEvolution.UI.Tests/AiTests.cs`, updating `Open_ModelMissing_ShowsDialog_NotNowRemembered` and `NoGermanLiterals_InNewComponents`
  - new `tests/NoteEvolution.UI.Tests/AiModelDialogTests.cs`
  - new `tests/NoteEvolution.UI.Tests/ModelSizesTests.cs`

**Interfaces:**
- Consumes: `AiRuntime.Models/Model/ModelInstalled/IsDownloading/DownloadAsync/Activate` (Task 4) and `ModelInfo.TotalSize/MemoryEstimate`.
- Produces:
  - `static class ModelSizes`:
    - `long Megabytes(long bytes)`: moved from the dialog, unchanged.
    - `string Gigabytes(long bytes)`: one decimal, in the current culture.
  - `AiModelDialog` parameters:
    - `EventCallback OnClose`
    - `EventCallback<ModelInfo> OnSwitch`, raised after the selected model was downloaded while another model is installed (Task 6 handles it).
  - CSS classes:
    - each option is a `label.ne-ai-model-option`
    - inside it `input[type=radio][name=ne-ai-model]` and `span.ne-ai-model-name` (the display name; data, not a resource)
    - `span.ne-ai-model-active` on the active model
  - Buttons:
    - `.ne-ai-download` / `.ne-ai-not-now` when no model is installed
    - `.ne-ai-switch` / `.ne-ai-close` when one is installed
    - `.ne-ai-cancel` while downloading
  - Hints: `.ne-ai-switch-hint`, `.ne-ai-switch-conflict`.
  - Settings: `.ne-settings-ai-choose` replaces `.ne-settings-ai-download`.

**Strings (exact values):**

| Key | Value |
|---|---|
| `AiModelTitle` | `KI-Modell wählen` |
| `AiModelText` | `Für die KI-Funktionen (Relevant, Suche nach Bedeutung, Wohin damit?) braucht NoteEvolution ein Sprachmodell. Größere Modelle finden Passendes genauer, brauchen aber mehr Speicher und indizieren langsamer.` |
| `AiModelOption` | `Download ca. {0} MB · Arbeitsspeicher ca. {1} GB` |
| `AiModelActive` | `aktiv` |
| `AiModelNote_multilingual-e5-small-int8` | `schnell, solide Qualität` |
| `AiModelNote_multilingual-e5-base-int8` | `ausgewogen` |
| `AiModelNote_multilingual-e5-large-int8` | `beste e5-Qualität, deutlich langsamer` |
| `AiModelNote_bge-m3-int8` | `sehr gute mehrsprachige Qualität, langsam` |
| `AiSwitch` | `Wechseln` |
| `AiModelSwitchHint` | `Beim Wechsel wird der Index neu aufgebaut und das bisherige Modell gelöscht.` |
| `AiModelSwitchConflict` | `Solange ein Konflikt offen ist, kann das Modell nicht gewechselt werden.` |
| `SettingsAiActive` | `{0} – Download ca. {1} MB · Arbeitsspeicher ca. {2} GB` |
| `SettingsAiChoose` | `Modell wählen…` |

- Remove `SettingsAiInstalled`; the state comes from `SettingsAiActive` plus the existing `SettingsAiMissing`/`SettingsAiDownloading`.
- An installed model shows no extra state line.
- A note key that is not found (test models) renders no note: check `ResourceNotFound`.

Steps:

- [ ] **Step 1: Write the failing tests.**
  - **`ModelSizesTests`:**
    - `Megabytes` behaves as before: 118 308 185 → 120, 5 069 051 → 5.
    - Under culture `de-DE`, `Gigabytes(386_616_370)` gives `"0,4"`; under `en-US`, `Gigabytes(1_273_537_524)` gives `"1.3"`.
  - **`AiModelDialogTests`:** Catalog of the test models `a` and `b`; `Ai` from `UseAi`.
    - `Lists_AllModels_WithSizes_ActiveMarked`:
      - There are two `.ne-ai-model-option` elements.
      - The first contains `a`'s display name and `Text("AiModelOption", …)` with its sizes.
      - When `a` is installed, the first option has `.ne-ai-model-active` and its radio is checked.
    - `FirstLoad_DownloadsSelected_ActivatesIt_SavesSetting_Closes`:
      - No model is installed. Select `b`, click `.ne-ai-download`.
      - Wait until `Ai.Model.Id == "b"` and `Ai.ModelInstalled`.
      - `Settings.AiModelId == "b"` and it is persisted (`UiSettings.Load`).
      - `OnClose` is raised; `OnSwitch` is not.
    - `Installed_SwitchDisabledForActive_EnabledForOther`:
      - `.ne-ai-switch` is disabled while `a` (the active one) is selected and enabled after selecting `b`.
      - `.ne-ai-switch-hint` is shown; `.ne-ai-download` is absent.
    - `Installed_Switch_DownloadsThenRaisesOnSwitch`:
      - Select `b`, click switch → `OnSwitch` receives `b`.
      - `Ai.Model` is still `a`: the dialog does not activate.
    - `Downloading_SelectionLocked_CancelShown`: with a hanging handler, every radio is disabled and `.ne-ai-cancel` is shown.
    - `Installed_OpenConflict_SwitchDisabled_HintShown`:
      - Open a session with a conflict, using the existing conflict helpers of `UiTestContext`/`ConflictTests`.
      - `.ne-ai-switch` is disabled and `.ne-ai-switch-conflict` is shown.
  - **`AiTests`:**
    - `Open_ModelMissing_ShowsDialog_NotNowRemembered` clicks `.ne-settings-ai-choose` instead of `.ne-settings-ai-download`.
    - `NoGermanLiterals_InNewComponents`:
      - asserts `.ne-settings-ai-choose`
      - treats `.ne-ai-model-name` text as data in `UiTexts`, like `.ne-ai-model-error`
      - also renders the dialog with an installed model to check the switch variant.
  - A settings test (in `AiTests`), `Settings_ShowsActiveModelWithSizes_AndChooseButton`:
    - `.ne-settings-ai` contains `Text("SettingsAiActive", name, mb, gb)`.
    - `.ne-settings-ai-choose` is present whether the model is installed or not.

- [ ] **Step 2: Run the tests.** They fail.

- [ ] **Step 3: Implement.**
  - **Selection:** The dialog keeps `_selected` (initially `Ai.Model`).
  - **First-load path** (`!Ai.ModelInstalled`):
    1. `DownloadAsync(_selected)`.
    2. `Ai.Activate(_selected)`.
    3. Set `Settings.AiModelId`, then save as in `NotNowAsync`.
    4. Raise `OnClose`.
  - **Switch path:**
    - Before downloading, check `State.Session?.HasAnyOpenConflict`.
    - Run `DownloadAsync(_selected)`.
    - After the download, check again (Task 6 tests this). On a conflict, set `_error` to the `AiModelSwitchConflict` text and return without switching. Otherwise `await OnSwitch.InvokeAsync(_selected)`.
    - Exceptions from `OnSwitch` show in `_error`, like download errors.
  - **Close button:** `.ne-ai-close` raises `OnClose`.
  - **Settings:** shows `SettingsAiActive` for `ai.Model` plus the state line, and always shows `.ne-settings-ai-choose` → `State.RequestAiModelDialog`. The button is disabled while `ai.IsDownloading`.

- [ ] **Step 4: Run all tests.** They pass.

- [ ] **Step 5: Commit** with `feat(ui): choose the ai model with its sizes`.

### Task 6: Shell performs the switch

**Files:**
- Modify: `src/NoteEvolution.UI/Components/Shell.razor`
- Test: new `tests/NoteEvolution.UI.Tests/ModelSwitchTests.cs`

**Interfaces:**
- Consumes:
  - `AiRuntime.Activate` (Task 4)
  - `AiModelDialog.OnSwitch` (Task 5)
  - the existing `OpenVaultAsync`, `State.FlushEditor` and `SaveSettings`
- Produces: `private async Task SwitchAiModelAsync(ModelInfo target)`, wired as `<AiModelDialog OnClose="CloseAiDialog" OnSwitch="SwitchAiModelAsync" />`.

Steps:

- [ ] **Step 1: Write the failing tests.**
  - **Setup:** Render `Shell` with a vault and `UseAi(installed: true, …, catalog [a, b])`, where `a` is active and its index is ready. Open the model dialog via settings → `.ne-settings-ai-choose`. Select `b` and click `.ne-ai-switch`.
  - **`Switch_ActivatesNew_ReopensVault_ReindexesWithNewModel`:**
    - Wait for `Ai.Model.Id == "b"`.
    - `State.Session` is a new instance and the old one is disposed.
    - The same book is current.
    - `AiStatus` reaches Ready.
    - `vectors.db`'s meta model is `b`: check with `VectorCache.Open(root, "b")`, or by reading the meta table as `VectorCacheTests` does.
    - The `a` folder is gone.
    - `Settings.AiModelId == "b"` is persisted.
    - The model dialog and the settings are closed.
  - **`Switch_FlushesEditorBeforeDisposingSession`:**
    - Set `State.FlushEditor` to a delegate that records whether `State.Session` is still the old, undisposed session.
    - It is called exactly once, before the dispose.
  - **`Switch_DownloadFails_OldModelStaysActive`:**
    - Return 404 for the download.
    - The error is shown in the dialog. `Ai.Model` is `a`; the session is the same instance with a ready index; the `b` folder is gone.
  - **`Switch_ConflictOpenedDuringDownload_NoSwitch`:**
    - Hang the handler. Start the switch, open a conflict in the session, then release the handler (set `Hang = false` and let the request complete; adapt `FakeModelHandler` with a `TaskCompletionSource` release if needed).
    - `.ne-ai-model-error` contains the conflict text. `Ai.Model` is `a`; the session is unchanged.
  - **`Switch_WithoutVault_ActivatesAndSaves`:**
    - With no vault open, use the model dialog from the settings (as above) with `a` installed.
    - The switch to `b` activates it and saves the setting; no session is created.

- [ ] **Step 2: Run the tests.** They fail.

- [ ] **Step 3: Implement `SwitchAiModelAsync`.**
  1. Remember the root.
  2. If a session is open:
     - Flush the editor: log and continue on failure, as `OpenVaultAsync` does.
     - Unsubscribe `PagesChanged`.
     - Set `State.Session = null`, `State.CurrentBook = null`, and `_subscribed = null`.
     - Dispose the old session.
  3. In a `try`, call `_ai.Activate(target)`. On success, set `Settings.AiModelId = target.Id` and call `SaveSettings()`.
  4. In the `finally`:
     - Set `_aiDialogOpen = false`.
     - If a root was remembered, `await OpenVaultAsync(root)`. It closes the settings dialog and restores the remembered book.
     - Otherwise call `State.Notify()`.
  - Exceptions from `Activate` propagate after the `finally`, so the dialog shows them; the vault is reopened in any case.

- [ ] **Step 4: Run all tests.** They pass.

- [ ] **Step 5: Commit** with `feat(ui): switching the ai model reopens the vault with it`.

### Task 7: README and verification (controller)

**Files:**
- Modify: `README.md`, sections "Lokale KI" and "Manuelle Abnahme"

Steps:

- [ ] **Step 1: Update "Lokale KI".**
  - Add the four models with their download size and RAM, as in spec section 2.
  - Describe the selection on first load and via Einstellungen → „Modell wählen…“.
  - Say that a switch deletes the old model and rebuilds the index.
- [ ] **Step 1b: Add checks to "Manuelle Abnahme".**
  - Switch from e5-small to e5-base with a vault open.
  - Afterwards only one folder remains under `%APPDATA%\NoteEvolution\models`.
- [ ] **Step 2: Run the full suite.** It is green; record the new test count.
- [ ] **Step 3: Smoke run (R3).**
  - Download bge-m3 into a scratch folder and run `BgeM3ModelTests` with `NOTEEVOLUTION_BGE_M3_DIR`. It must pass, not be skipped.
  - Start the app and switch e5-small → e5-base in a copy of a test vault.
  - Check `models/` and the vectors meta.
- [ ] **Step 4: Commit** with `docs: model selection in readme`.
