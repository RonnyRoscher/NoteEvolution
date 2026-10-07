# NoteEvolution Stufe 2 – Umsetzungsplan (lokale KI)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eine lokale, offline laufende Bedeutungssuche über die Notizen. Sie zeigt zum gerade bearbeiteten Abschnitt passende Notizen (Reiter „Relevant“), mischt die Bedeutungssuche in die Suche, schlägt für eine Notiz passende Buchabschnitte vor („Wohin damit?“) und schlägt im `[handled]`-Assistenten Fundstellen vor.

**Architecture:** Alles KI-Spezifische liegt in `NoteEvolution.AI` und kennt keine Oberfläche.
- Ein ONNX-Embedder (`multilingual-e5-small`, int8) erzeugt normalisierte Vektoren.
- Die Vektoren landen in einem SQLite-Cache im Vault (`.noteevolution/vectors.db`, nach Inhalts-Hash) und in einem In-Memory-Index für die Kosinus-Suche.
- Ein `SemanticIndex` hält den Index im Hintergrund aktuell. Ein `RelevanceService` beantwortet Themenbereich, „Wohin damit?“ und Fundstellen. Ein `HybridSearchService` verbindet FTS5 und Vektoren per Reciprocal Rank Fusion.
- Die UI bekommt einen app-weiten `AiRuntime` für Modell, Download und Embedder; jede `VaultSession` hat ihren eigenen Index.
- Fehlt das Modell oder fällt es aus, läuft alles wie in Stufe 1 mit Volltext weiter.

**Tech Stack:** .NET 10, Microsoft.ML.OnnxRuntime (CPU), Microsoft.ML.Tokenizers (SentencePiece/Unigram), Microsoft.Data.Sqlite, Blazor/bUnit, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-05-noteevolution-design.md` (Abschnitte 5.1, 5.5, 6.1–6.3, 7, 9, 10). Stufe 1 ist umgesetzt (`master`); deren Plan: `docs/superpowers/plans/2026-10-05-noteevolution-stufe-1.md`.

## Global Constraints

- Alle Stufe-1-Regeln gelten weiter:
  - `Directory.Build.props`: Nullable, ImplicitUsings, TreatWarningsAsErrors
  - Core/AI/Pdf ohne UI-Pakete
  - **Die Dateien im Vault sind die einzige Wahrheit**; alles unter `.noteevolution/` ist wiederherstellbar
  - Oberflächentexte nur in `Strings.resx` (deutsch), keine deutschen Literale in `.razor`
  - Verzögerungen über injizierten `TimeProvider`
  - Jeder Schreibweg prüft offene Konflikte (`HasOpenConflict`) bzw. schreibt über `TrySave` (Regel R26 aus Stufe 1)
- **Die KI schreibt oder formuliert keinen Buchtext.** Einzige Schreibaktion dieser Stufe: Eigenschaftszeilen (`id::`, `source::`, `used-in::`) für vom Nutzer bestätigte Fundstellen.
- Standardmodell: `multilingual-e5-small`, quantisiert (int8), etwa 120 MB, ONNX Runtime auf der CPU. Quelle: Hugging Face `Xenova/multilingual-e5-small` mit `onnx/model_quantized.onnx` und `sentencepiece.bpe.model`, auf eine feste Revision gepinnt und per SHA-256 geprüft.
- Das Modell wird **nur nach Bestätigung** heruntergeladen und liegt im Benutzerprofil unter `UserDataDirectory/models/<Modell-Id>/`, nicht im Vault. Danach erfolgt kein Netzwerkzugriff mehr.
- Tokenisierung mit `Microsoft.ML.Tokenizers`. Höchstens 512 Tokens pro Text, längere werden gekürzt. Präfixe nach e5-Konvention: `passage: ` für Notizen, `query: ` für Buch-Seite und Suchtext.
- Eingebetteter Text pro Notizblock: Datum, Text des Elternblocks (Kontext), eigener Text samt Unteranstrichen.
- Speicher: SQLite-Tabelle in `.noteevolution/vectors.db`. Gesucht wird im Speicher per Kosinus-Ähnlichkeit über alle Vektoren. Neu berechnet werden nur Blöcke, deren Inhalts-Hash sich geändert hat.
- Themenbereich (Spec 6.2):
  - Abschnittsansicht: Überschriftenpfad plus Mittelwert der Textblock-Embeddings.
  - Manuskriptansicht: Block am Cursor, seine beiden Nachbarn und der Überschriftenpfad.
  - Aktualisierung entprellt mit **1500 ms**.
- Funktionen (Spec 6.3):
  - **Relevant:** die **30** ähnlichsten Notizblöcke.
  - **Suche:** Volltext- und Bedeutungsrangfolge per Reciprocal Rank Fusion (k = **60**).
  - **Wohin damit?:** die **5** ähnlichsten Abschnitte.
  - **Fundstellen:** die **3** ähnlichsten Textblöcke.
  - Filter „Verwendete ausblenden“ und Zeitraum wie in Stufe 1.
- Fehlerbehandlung (Spec 9): Fehlt das KI-Modell oder liefert es Fehler, fällt die App auf die Volltextsuche zurück und zeigt einen Hinweis.
- Optionales `bge-m3` ist **nicht** Teil dieser Stufe. Der `IEmbedder`/`ModelCatalog`-Schnitt lässt es später zu.

## Review Focus

1. **Modell fehlt, Download abgebrochen oder beschädigt** (Netz weg, SHA-256 passt nicht): Die App bleibt voll nutzbar mit Volltext und zeigt „KI: Modell fehlt“. Eine halbe Datei wird nie geladen (Tests in Task 1 und Task 9).
2. **Sehr lange Notiz (mehr als 512 Tokens) oder leerer/whitespace-only Block bzw. Abschnitt ohne Text:** Die lange Notiz wird gekürzt. Leere Texte erzeugen keinen NaN-Vektor, sondern einen Nullvektor, der aus Ergebnissen herausfällt (Tests in Task 2, Task 4 und Task 6).
3. **Vault-Wechsel oder Schließen während der Hintergrund-Indizierung:** Die Indizierung bricht ab, schreibt nicht in einen geschlossenen Cache und erzeugt keine unbehandelte Ausnahme (Tests in Task 5 und Task 9).
4. **Notizen ändern sich während oder nach der Indizierung** (eigene Speicherungen, Logseq): Geänderte Blöcke werden neu eingebettet, entfernte Blöcke tauchen nicht mehr in Ergebnissen auf (Tests in Task 5 und Task 9).
5. **`vectors.db` beschädigt oder von einem anderen Modell:** Der Cache wird verworfen und neu aufgebaut, ohne Absturz (Test in Task 4).

---

## Dateistruktur

```
src/NoteEvolution.AI/
  Model/       ModelInfo.cs (ModelFile, ModelInfo, ModelCatalog), ModelStore.cs, ModelDownloader.cs, DownloadProgress.cs
  Embeddings/  IEmbedder.cs, E5Tokenizer.cs, OnnxEmbedder.cs, VectorMath.cs, EmbeddingText.cs
  Semantic/    VectorCache.cs, VectorIndex.cs, ISemanticIndex.cs, SemanticIndex.cs
  Relevance/   IRelevanceService.cs, RelevanceService.cs, TopicRequest.cs
  Search/      HybridSearchService.cs   (neu; FullTextSearchService bleibt)
src/NoteEvolution.Core/Links/  ILinkService.cs, LinkService.cs (Link), Assistants/HandledConverter.cs (Fundstellen)
src/NoteEvolution.UI/
  State/       AiRuntime.cs, AiStatus.cs, VaultSession.cs (Ai-Verdrahtung)
  Components/  RelevantTab.razor, NotesPane.razor, NoteCard.razor (Wohin damit?), HeaderBar.razor (KI-Anzeige),
               Dialogs/AiModelDialog.razor, Dialogs/SettingsDialog.razor, Dialogs/HandledWizard.razor
src/NoteEvolution.Desktop/Program.cs  (AiRuntime registrieren)
tests/NoteEvolution.AI.Tests/  Model/, Embeddings/, Semantic/, Relevance/, Search/, FakeEmbedder.cs, RealModelFactAttribute.cs
```

---

### Task 1: Modellkatalog, Modellablage und Download

**Files:**
- Create: `src/NoteEvolution.AI/Model/ModelInfo.cs`, `ModelStore.cs`, `ModelDownloader.cs`, `DownloadProgress.cs`
- Test: `tests/NoteEvolution.AI.Tests/Model/ModelStoreTests.cs`, `ModelDownloaderTests.cs`

**Interfaces:**
- Produces:
  - `record ModelFile(string RelativePath, string Url, string Sha256, long Size)`
  - `record ModelInfo(string Id, string DisplayName, IReadOnlyList<ModelFile> Files, int Dimensions, int MaxTokens)`
  - `static class ModelCatalog { static ModelInfo E5Small { get; } }` mit `Id = "multilingual-e5-small-int8"`, `Dimensions = 384`, `MaxTokens = 512`. Dateien:
    - `onnx/model_quantized.onnx`
    - `sentencepiece.bpe.model`

    URLs der Form `https://huggingface.co/Xenova/multilingual-e5-small/resolve/<REVISION>/<pfad>`. `<REVISION>` ist der Commit-Hash aus `https://huggingface.co/api/models/Xenova/multilingual-e5-small` (Feld `sha`). Der Implementer ermittelt ihn einmal, lädt beide Dateien und trägt Revision, SHA-256 und Größe fest ein.
  - `sealed class ModelStore(string userDataDirectory)` mit:
    - `string DirectoryOf(ModelInfo m)` = `<userDataDirectory>/models/<m.Id>`
    - `string PathOf(ModelInfo m, ModelFile f)`
    - `bool IsInstalled(ModelInfo m)`: alle Dateien vorhanden und mit der katalogisierten Größe. Kein SHA beim Start, das wäre zu langsam; geprüft wird beim Download.
  - `record DownloadProgress(long BytesDone, long BytesTotal)`
  - `sealed class ModelDownloadException(string message) : Exception`
  - `sealed class ModelDownloader(HttpClient http, ModelStore store)` mit `Task DownloadAsync(ModelInfo model, IProgress<DownloadProgress>? progress, CancellationToken ct)`:
    - streamt jede Datei nach `<ziel>.part`, prüft SHA-256 und benennt erst dann atomar um;
    - bei Abbruch oder Fehler wird die `.part`-Datei gelöscht;
    - eine falsche Prüfsumme gibt `ModelDownloadException` mit Dateiname;
    - schon vollständige Dateien (richtige Größe) werden übersprungen.

- [ ] **Step 1: Failing tests schreiben.** Die Tests laufen mit einem `HttpMessageHandler`-Fake, der Bytes ausliefert, und kleinen Test-`ModelInfo`s. Kein Netz.

```csharp
[Fact] public async Task Download_WritesFilesAtomically_ReportsProgress_IsInstalled() { /* 2 Dateien, Fortschritt endet bei BytesTotal, IsInstalled true, keine .part-Dateien */ }
[Fact] public async Task Download_ShaMismatch_Throws_LeavesNoFile() { /* Review Focus 1 */ }
[Fact] public async Task Download_Cancelled_LeavesNoPartFile_NotInstalled() { }
[Fact] public async Task Download_SkipsAlreadyCompleteFiles() { /* Handler zählt Requests */ }
[Fact] public void IsInstalled_FalseForMissingOrWrongSize() { }
[Fact] public void Catalog_E5Small_PinnedRevisionAndHashes() { /* URLs ohne "/main/", Sha256 64 hex, Size > 0 */ }
```

- [ ] **Step 2: Fehlschlag prüfen.** `dotnet test tests/NoteEvolution.AI.Tests --filter "FullyQualifiedName~Model"` → FAIL (Kompilierfehler).
- [ ] **Step 3: Implementieren.** Revision und Hashes ermitteln (`curl` auf die API und die Dateien, `sha256sum`) und in `ModelCatalog` eintragen. Die Dateien dabei ins Standardverzeichnis `%APPDATA%\NoteEvolution\models\multilingual-e5-small-int8\` legen; die Integrationstests ab Task 2 nutzen sie.
- [ ] **Step 4: Tests grün.** Gleicher Befehl → PASS.
- [ ] **Step 5: Commit** `feat(ai): model catalog, store and verified download`

---

### Task 2: Tokenizer, ONNX-Embedder und Vektormathematik

**Files:**
- Create: `src/NoteEvolution.AI/Embeddings/IEmbedder.cs`, `E5Tokenizer.cs`, `OnnxEmbedder.cs`, `VectorMath.cs`
- Modify: `src/NoteEvolution.AI/NoteEvolution.AI.csproj` (`Microsoft.ML.OnnxRuntime`, `Microsoft.ML.Tokenizers`, aktuelle stabile Versionen)
- Test: `tests/NoteEvolution.AI.Tests/Embeddings/VectorMathTests.cs`, `RealModelTests.cs`, `tests/NoteEvolution.AI.Tests/RealModelFactAttribute.cs`

**Interfaces:**
- Consumes: `ModelStore`, `ModelCatalog.E5Small` (Task 1)
- Produces:
  - `interface IEmbedder { int Dimensions { get; } IReadOnlyList<float[]> Embed(IReadOnlyList<string> texts, CancellationToken ct = default); }`: Die Texte tragen ihr Präfix schon. Das Ergebnis ist L2-normalisiert; leerer Text gibt einen Nullvektor.
  - `sealed class E5Tokenizer` mit `static E5Tokenizer Load(string sentencePieceModelPath)` und `long[] Encode(string text, int maxTokens)`. Die IDs folgen der XLM-R/fairseq-Zuordnung:
    - Rahmen: `<s>` = 0, `</s>` = 2, `<pad>` = 1, `<unk>` = 3;
    - SentencePiece-ID `i > 0` ergibt `i + 1`, SentencePiece-`<unk>` (0) ergibt 3;
    - die Sequenz ist `<s> … </s>`, auf `maxTokens` gekürzt, wobei `</s>` erhalten bleibt.
  - `sealed class OnnxEmbedder : IEmbedder, IDisposable` mit `static OnnxEmbedder Load(ModelStore store, ModelInfo model)`:
    - CPU-`InferenceSession`;
    - Batch mit Padding; die Eingabenamen liest er aus `session.InputMetadata` (`input_ids`, `attention_mask`, ggf. `token_type_ids` mit Nullen);
    - Mean-Pooling über `last_hidden_state` mit Maske, dann L2-Normalisierung.
  - `static class VectorMath` mit:
    - `float[] MeanPoolNormalize(ReadOnlySpan<float> hidden, int tokens, int dims, ReadOnlySpan<long> mask)`
    - `float[] Normalize(float[] v)`: Nullvektor bleibt Nullvektor
    - `float[] Mean(IReadOnlyList<float[]> vs)`
    - `float Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)`: bei normalisierten Vektoren das Skalarprodukt, mit Nullvektor 0
  - `RealModelFactAttribute : FactAttribute`: setzt `Skip`, wenn das Modell im Standardverzeichnis bzw. in `NOTEEVOLUTION_MODEL_DIR` fehlt.

- [ ] **Step 1: Failing tests schreiben**

```csharp
[Fact] public void MeanPoolNormalize_IgnoresMaskedTokens_UnitLength() { }
[Fact] public void Normalize_ZeroVector_StaysZero_NoNaN() { }                 // Review Focus 2
[Fact] public void Cosine_ZeroVector_IsZero() { }
[RealModelFact] public void Tokenizer_IdsMatchTokenizerJsonVocabulary() {
    // Implementer legt tokenizer.json (aus demselben Repo, gleiche Revision) als Testdatei unter tests/NoteEvolution.AI.Tests/Embeddings/Data/ ab
    // oder lädt sie in Task 1 mit; geprüft wird: für "query: Vertrauen baut Schwung auf." und "Ängste überwinden" ist jede Nicht-Rahmen-ID
    // gleich dem Index des Stücks in tokenizer.json model.vocab; Rahmen 0 … 2.
}
[RealModelFact] public void Tokenizer_LongText_TruncatedTo512_EndsWithEos() { }       // Review Focus 2
[RealModelFact] public void Embedder_SimilarMeaningScoresHigher() {
    // cos("query: Wie gehe ich mit Angst um?", "passage: Angst baut Widerstand auf, Vertrauen baut Schwung auf.")
    //   > cos(dieselbe query, "passage: Der Zug fährt morgen um acht Uhr.")
}
[RealModelFact] public void Embedder_BatchEqualsSingle_384Dims_UnitLength() { }
```

- [ ] **Step 2: Fehlschlag prüfen.** `dotnet test tests/NoteEvolution.AI.Tests --filter "FullyQualifiedName~Embeddings"` → FAIL.
- [ ] **Step 3: Implementieren.** `SentencePieceTokenizer.Create(stream, addBeginOfSentence: false, addEndOfSentence: false)`, dann die ID-Zuordnung oben. Ist `tokenizer.json` mehr als 2 MB groß, wird sie nicht eingecheckt; der Test lädt sie dann aus dem Modellverzeichnis (Task 1 lädt sie zusätzlich herunter, ohne Eintrag im Katalog für die App).
- [ ] **Step 4: Tests grün.** Gleicher Befehl → PASS, die RealModel-Tests sind ausgeführt und nicht übersprungen; die Zahl der übersprungenen Tests steht im Bericht.
- [ ] **Step 5: Commit** `feat(ai): e5 tokenizer, onnx embedder and vector math`

---

### Task 3: Einbettungstexte

**Files:**
- Create: `src/NoteEvolution.AI/Embeddings/EmbeddingText.cs`
- Test: `tests/NoteEvolution.AI.Tests/Embeddings/EmbeddingTextTests.cs`

**Interfaces:**
- Consumes: `NoteBlock` (Core: `Date`, `Block`, `Block.Parent`), `Book`, `OutlineNode`, `TextBlock`, `Paragraph`
- Produces: `static class EmbeddingText` mit
  - `const string PassagePrefix = "passage: "` und `const string QueryPrefix = "query: "`
  - `string ForNote(NoteBlock note)`: `passage: ` + Zeilen, verbunden mit `\n`:
    - das Datum als `yyyy-MM-dd`, wenn vorhanden;
    - die erste Zeile des Elternblocks, wenn vorhanden;
    - der `Content` des Blocks;
    - der `Content` jedes Nachfahren in Tiefensuche.

    Leere Zeilen entfallen.
  - `string ForHeadingPath(Book book, OutlineNode node)`: `query: ` + die Titel von der Wurzel bis zum Knoten, verbunden mit ` / `. Für die Wurzel (Vorspann) ist es `book.Title`.
  - `string ForTextBlock(TextBlock tb)`: `query: ` + `Text` und alle Absätze ohne `IsNote`, verbunden mit `\n`.
  - `string ForCursor(Book book, TextBlock cursor)`: `query: ` + Überschriftenpfad des Abschnitts, Vorgänger-, Cursor- und Nachfolger-Textblock im selben Abschnitt (soweit vorhanden), verbunden mit `\n`.
  - `string ForSearch(string userText)` = `query: ` + getrimmter Text
  - `string Hash(string modelId, string text)`: SHA-256-Hex (klein) über `modelId + "\n" + text`

- [ ] **Step 1: Failing tests**: `ForNote_JournalChild_DateParentOwnAndDescendants`, `ForNote_PageBlockWithoutDateOrParent`, `ForHeadingPath_RootIsBookTitle_NestedJoinedWithSlash`, `ForTextBlock_SkipsNoteParagraphs`, `ForCursor_FirstBlockHasNoPredecessor`, `Hash_DependsOnModelAndText`. Beispieldaten sind `Samples.SpecBook` und kleine `TestVault`s.
- [ ] **Step 2–4:** `dotnet test tests/NoteEvolution.AI.Tests --filter "FullyQualifiedName~EmbeddingText"`: FAIL → implementieren → PASS.
- [ ] **Step 5: Commit** `feat(ai): embedding texts for notes, sections, cursor and search`

---

### Task 4: Vektor-Cache (SQLite) und In-Memory-Index

**Files:**
- Create: `src/NoteEvolution.AI/Semantic/VectorCache.cs`, `VectorIndex.cs`
- Test: `tests/NoteEvolution.AI.Tests/Semantic/VectorCacheTests.cs`, `VectorIndexTests.cs`

**Interfaces:**
- Produces:
  - `sealed class VectorCache : IDisposable` mit `static VectorCache Open(string vaultRoot, string modelId)`:
    - Datei `<vault>/.noteevolution/vectors.db`;
    - Tabelle `vectors(hash TEXT PRIMARY KEY, file TEXT NOT NULL, vector BLOB NOT NULL)` und Tabelle `meta(key TEXT PRIMARY KEY, value TEXT)` mit `model = <modelId>`;
    - weicht `model` ab oder ist die Datei beschädigt (`SqliteException` beim Öffnen oder Prüfen), wird die Datei gelöscht und neu angelegt;
    - Vektoren als Little-Endian-`float32`-Blob.

    Methoden:
    - `IReadOnlyDictionary<string, float[]> Get(IReadOnlyCollection<string> hashes)`
    - `void Put(IReadOnlyList<(string Hash, string File, float[] Vector)> items)` in einer Transaktion
    - `void Prune(IReadOnlySet<string> liveHashes)`
    - Lock um alle Methoden; nach `Dispose` wirft jede Methode `ObjectDisposedException`.
  - `sealed class VectorIndex` (thread-safe) mit:
    - `void Set(Guid key, string pagePath, float[] vector)`
    - `void RemovePage(string pagePath)`: Pfadvergleich ohne Groß-/Kleinschreibung
    - `void Remove(Guid key)`
    - `int Count`
    - `float[]? Get(Guid key)`
    - `IReadOnlyList<(Guid Key, float Score)> Nearest(float[] query, int k, Func<Guid, bool> include)`: absteigend nach Kosinus; Nullvektor-Einträge und Score ≤ 0 fallen weg.

- [ ] **Step 1: Failing tests**:
  - `Cache_PutGet_RoundTripsVectorsExactly`
  - `Cache_Prune_KeepsOnlyLive`
  - `Cache_OtherModel_IsDiscarded`
  - `Cache_CorruptFile_IsRecreated` (Review Focus 5: Datei mit Müllbytes)
  - `Cache_AfterDispose_Throws`
  - `Index_Nearest_OrdersByCosine_RespectsIncludeAndK`
  - `Index_RemovePage_RemovesItsKeys`
  - `Index_ZeroVector_NeverReturned`
- [ ] **Step 2–4:** `dotnet test tests/NoteEvolution.AI.Tests --filter "FullyQualifiedName~Semantic.Vector"`: FAIL → implementieren → PASS.
- [ ] **Step 5: Commit** `feat(ai): sqlite vector cache and in-memory cosine index`

---

### Task 5: Semantischer Index (Hintergrund-Indizierung)

**Files:**
- Create: `src/NoteEvolution.AI/Semantic/ISemanticIndex.cs`, `SemanticIndex.cs`
- Test: `tests/NoteEvolution.AI.Tests/Semantic/SemanticIndexTests.cs`, `tests/NoteEvolution.AI.Tests/FakeEmbedder.cs`

**Interfaces:**
- Consumes: `IEmbedder` (Task 2), `EmbeddingText` (Task 3), `VectorCache`, `VectorIndex` (Task 4), `INoteRepository`, `NoteBlock`
- Produces:
  - `enum SemanticState { Off, Indexing, Ready, Failed }`
  - `record SemanticStatus(SemanticState State, int Done, int Total, string? Error)`
  - `interface ISemanticIndex` mit:
    - `SemanticStatus Status { get; }` und `event Action? StatusChanged`, ausgelöst auf dem aufrufenden Hintergrund-Thread
    - `Task RebuildAsync(INoteRepository notes, CancellationToken ct)`
    - `Task UpdatePageAsync(INoteRepository notes, string pagePath, CancellationToken ct)`
    - `bool IsReady`: `State == Ready`, oder `State == Indexing` mit `Count > 0`; Teilergebnisse sind erlaubt
    - `IReadOnlyList<(Guid Key, float Score)> Nearest(float[] query, int k, Func<Guid, bool> include)`
    - `float[]? VectorOf(Guid noteKey)`
    - `float[]? EmbedQuery(string text)`: `null`, wenn der Embedder fehlschlägt; der Fehler wird geloggt, der Status bleibt
  - `sealed class SemanticIndex(IEmbedder embedder, VectorCache cache, string modelId, ILogger? logger = null) : ISemanticIndex, IDisposable`

    `RebuildAsync`:
    1. alle Notizen → Text und Hash;
    2. Cache-Treffer direkt in den Index;
    3. der Rest in Batches zu **16** einbetten; nach jedem Batch `Put`, `Done` erhöhen und `StatusChanged` auslösen;
    4. am Ende `Prune` mit allen lebenden Hashes und `Ready`.

    Eine Embedder-Ausnahme setzt `Failed` mit Meldung. `ct` wird zwischen den Batches geprüft; Abbruch wirft `OperationCanceledException` und lässt den Status bei `Indexing`. Nach `Dispose` tut `RebuildAsync`/`UpdatePageAsync` nichts mehr. Die Arbeit läuft per `Task.Run` im Hintergrund.

    `UpdatePageAsync`: `RemovePage`, dann nur die Blöcke dieser Seite wie oben, ohne `Prune`.
  - `FakeEmbedder : IEmbedder` (Tests): deterministischer Vektor je Text (z. B. Zeichenhistogramm über 16 Dimensionen, normalisiert), zählt Aufrufe und Texte; per Eigenschaft auf „wirft“ umschaltbar.

- [ ] **Step 1: Failing tests**:
  - `Rebuild_EmbedsAllNotes_ProgressReachesTotal_Ready`
  - `Rebuild_Twice_SecondUsesCacheOnly` (der Embedder wird beim zweiten Mal nicht aufgerufen)
  - `Rebuild_ChangedNote_OnlyThatOneReembedded`
  - `UpdatePage_RemovedBlock_NotReturnedAnymore` (Review Focus 4)
  - `Rebuild_EmbedderThrows_Failed_NearestEmpty`
  - `Rebuild_Cancelled_StopsBetweenBatches_NoPutAfterCancel`
  - `Dispose_DuringRebuild_NoExceptionNoWrites` (Review Focus 3)
  - `Nearest_PartialIndexWhileIndexing_IsReady`
- [ ] **Step 2–4:** `dotnet test tests/NoteEvolution.AI.Tests --filter "FullyQualifiedName~SemanticIndex"`: FAIL → implementieren → PASS.
- [ ] **Step 5: Commit** `feat(ai): background semantic index with hash-based reuse`

---

### Task 6: Relevanzdienst (Themenbereich, Wohin damit?, Fundstellen)

**Files:**
- Create: `src/NoteEvolution.AI/Relevance/TopicRequest.cs`, `IRelevanceService.cs`, `RelevanceService.cs`
- Test: `tests/NoteEvolution.AI.Tests/Relevance/RelevanceServiceTests.cs`

**Interfaces:**
- Consumes: `ISemanticIndex` (Task 5), `EmbeddingText` (Task 3), `VectorMath` (Task 2), `INoteRepository.Matches`, `Book`
- Produces:
  - `record TopicRequest(Book Book, Guid SectionKey, Guid? CursorTextBlockKey, bool Manuscript)`
  - `record SectionHit(Guid SectionKey, double Score)` und `record PlacementHit(Guid TextBlockKey, double Score)`
  - `interface IRelevanceService` mit:
    - `bool IsAvailable` (= `index.IsReady`)
    - `IReadOnlyList<SearchHit> Relevant(TopicRequest topic, NoteFilter filter, int limit = 30)`
    - `IReadOnlyList<SectionHit> WhereTo(NoteBlock note, Book book, int limit = 5)`
    - `IReadOnlyList<PlacementHit> Placements(NoteBlock note, Book book, int limit = 3)`

    Alle drei geben eine leere Liste, wenn nicht verfügbar oder der Themenvektor ein Nullvektor ist.
  - `sealed class RelevanceService(ISemanticIndex index, INoteRepository notes) : IRelevanceService`. Vektoren für Buch-Texte werden je Hash im Speicher zwischengespeichert, mit höchstens 5000 Einträgen; das älteste fällt zuerst heraus.
- Themenvektor (Spec 6.2):
  - Abschnittsansicht oder Manuskript ohne Cursor: `Normalize(E(ForHeadingPath) + Mean(E(ForTextBlock(t)) für die Textblöcke direkt im Abschnitt))`. Ohne Textblöcke zählt nur der Pfad.
  - Manuskript mit Cursor: `E(ForCursor)`.
  - Abschnittsvektor für „Wohin damit?“: dieselbe Formel je `OutlineNode` des Buchs. Die Wurzel zählt nur, wenn sie Textblöcke hat.
  - Notizvektor: `index.VectorOf(note.Key)`, sonst `E(ForNote)`.

- [ ] **Step 1: Failing tests** (FakeEmbedder mit festen Vektoren je Text über ein Wörterbuch):
  - `Relevant_Top30_ByTopicSimilarity_HonoursHideUsedAndDates`
  - `Relevant_ManuscriptCursor_UsesNeighbours`
  - `Relevant_EmptySection_OnlyHeadingPath`
  - `Relevant_NotAvailable_Empty`
  - `WhereTo_Top5Sections_RootOnlyWithText`
  - `Placements_Top3TextBlocks`
  - `ZeroTopicVector_Empty_NoNaN` (Review Focus 2)
- [ ] **Step 2–4:** `dotnet test tests/NoteEvolution.AI.Tests --filter "FullyQualifiedName~Relevance"`: FAIL → implementieren → PASS.
- [ ] **Step 5: Commit** `feat(ai): relevance service for topic, where-to and placements`

---

### Task 7: Hybride Suche (Reciprocal Rank Fusion)

**Files:**
- Create: `src/NoteEvolution.AI/Search/HybridSearchService.cs`
- Test: `tests/NoteEvolution.AI.Tests/Search/HybridSearchTests.cs`

**Interfaces:**
- Consumes: `ISearchService`/`FullTextSearchService` (Stufe 1), `ISemanticIndex` (Task 5), `EmbeddingText.ForSearch`, `INoteRepository.Matches`
- Produces: `sealed class HybridSearchService(ISearchService fullText, ISemanticIndex semantic, INoteRepository notes) : ISearchService`
  - `Rebuild` und `UpdatePage` leiten nur an `fullText` weiter.
  - `Search`:
    1. Volltext-Treffer mit `Limit = 100`;
    2. Bedeutungs-Treffer mit `semantic.Nearest(EmbedQuery(ForSearch(text)), 100, k => notes.Get(k) is { } n && notes.Matches(n, filter))`;
    3. Score = Σ `1 / (60 + rang)` über beide Listen, mit Rang ab 1;
    4. absteigend sortieren und auf `query.Limit` kürzen.
  - Ist der Index nicht bereit, `EmbedQuery` `null` oder `FtsQuery.Build(text)` `null`, kommen unverändert die Volltext-Treffer.

- [ ] **Step 1: Failing tests**:
  - `Rrf_CombinesBothLists_DocInBothRanksFirst`
  - `Rrf_SemanticOnlyHitIncluded`
  - `Filters_AppliedToSemanticHits`
  - `SemanticNotReady_EqualsFullText`
  - `EmbedQueryFails_EqualsFullText`
  - `NoSearchableWord_Empty`
- [ ] **Step 2–4:** `dotnet test tests/NoteEvolution.AI.Tests --filter "FullyQualifiedName~HybridSearch"`: FAIL → implementieren → PASS.
- [ ] **Step 5: Commit** `feat(ai): hybrid search with reciprocal rank fusion`

---

### Task 8: Vollständige Verknüpfung für bestätigte Fundstellen (Core)

**Files:**
- Modify: `src/NoteEvolution.Core/Links/ILinkService.cs`, `LinkService.cs`, `src/NoteEvolution.Core/Assistants/HandledConverter.cs`, `src/NoteEvolution.UI/State/VaultSession.cs` (Konstruktor von `HandledConverter`)
- Test: `tests/NoteEvolution.Core.Tests/Links/LinkTests.cs`, `tests/NoteEvolution.Core.Tests/Assistants/HandledConverterTests.cs`

**Interfaces:**
- Produces:
  - `void ILinkService.Link(Book book, Guid textBlockKey, Guid noteBlockKey)`:
    - Notizseite schreibgeschützt (Regel R13) → `InvalidOperationException` vor jeder Änderung;
    - `EnsureId` an Textblock und Notiz;
    - Notiz-ID an `source::` anhängen, falls sie fehlt;
    - Buch speichern; scheitert das, wie bei `Adopt` neu laden und weiterwerfen;
    - an der Notiz `used-in:: [[book.LinkName]] ((textBlockId))` setzen und einen Eintrag ohne Blockverweis für dasselbe Buch ersetzen;
    - Notiz speichern; scheitert das, über `pending.json` wie bei `Adopt`;
    - kein Undo-Eintrag.
  - `HandledConverter(INoteRepository notes, IPageWriter writer, ILinkService links)` mit `HandledResult Apply(IEnumerable<NoteBlock> selected, Book book, IReadOnlyDictionary<Guid, Guid>? placements = null)`:
    - `placements` bildet einen Notiz-Schlüssel auf einen Textblock-Schlüssel ab;
    - eine Notiz mit Fundstelle bekommt nur die Präfix-Entfernung im Speicher und dann `links.Link(...)`, das speichert;
    - Notizen ohne Fundstelle wie bisher;
    - `Converted` zählt beide.

- [ ] **Step 1: Failing tests**:
  - `Link_AddsSourceAndUsedIn_BothSides_ByteExactOtherwise`
  - `Link_ReplacesIdlessEntryForSameBook`
  - `Link_AlreadyLinked_NoDuplicate`
  - `Link_ReadOnlyNote_ThrowsBeforeChange`
  - `Link_NoteWriteFails_Pending`
  - `Handled_WithPlacement_StripsPrefixAndLinksFully`
  - `Handled_MixedPlacedAndUnplaced`
- [ ] **Step 2–4:** `dotnet test tests/NoteEvolution.Core.Tests --filter "FullyQualifiedName~LinkTests|FullyQualifiedName~HandledConverter"`: FAIL → implementieren → PASS. Danach den ganzen Testlauf.
- [ ] **Step 5: Commit** `feat(core): full link for confirmed placements`

---

### Task 9: KI-Laufzeit, Sitzungs-Verdrahtung, KI-Anzeige und Modell-Dialog

**Files:**
- Create: `src/NoteEvolution.UI/State/AiRuntime.cs`, `AiStatus.cs`, `src/NoteEvolution.UI/Components/Dialogs/AiModelDialog.razor`
- Modify:
  - `src/NoteEvolution.UI/State/VaultSession.cs`, `UiSettings.cs` (`AiModelDeclined`)
  - `src/NoteEvolution.UI/Components/HeaderBar.razor`, `Shell.razor`, `Dialogs/SettingsDialog.razor`
  - `src/NoteEvolution.UI/Resources/Strings.resx`
  - `src/NoteEvolution.Desktop/Program.cs`
- Test: `tests/NoteEvolution.UI.Tests/AiTests.cs` (+ Hilfen in `UiTestContext`)

**Interfaces:**
- Consumes: Tasks 1–7
- Produces:
  - `enum AiState { Off, ModelMissing, Downloading, Indexing, Ready, Failed }`
  - `record AiStatus(AiState State, int Done, int Total, string? Error)`
  - `sealed class AiRuntime : IDisposable` (App-Singleton), Konstruktor `(ModelStore store, ModelDownloader downloader, Func<IEmbedder> loadEmbedder, ILogger<AiRuntime> logger)`:
    - `bool ModelInstalled`
    - `IEmbedder? Embedder`: lazy beim ersten Zugriff geladen, wenn installiert; ein Ladefehler wird geloggt, ergibt `null` und wird nicht erneut versucht
    - `Task DownloadAsync(IProgress<DownloadProgress>?, CancellationToken)`
    - `event Action? ModelChanged`
  - `VaultSession.OpenAsync(…, AiRuntime? ai = null)` und `VaultSession`:
    - `ISemanticIndex? Semantic`
    - `IRelevanceService Relevance` (bei fehlender KI eine Variante, die nie verfügbar ist)
    - `AiStatus AiStatus` und `event Action? AiStatusChanged`, über den Dispatcher
    - `Search` liefert `HybridSearchService` bei vorhandener KI, sonst wie bisher
  - Ablauf beim Öffnen (Spec 5.1, Schritt 4): Nach dem FTS-Aufbau startet `Semantic.RebuildAsync` im Hintergrund, ohne das Öffnen zu blockieren, mit einem `CancellationTokenSource` der Sitzung; `Dispose` bricht ab und entsorgt den Cache.
  - Nach Abschluss eines Downloads (`ModelChanged`) startet eine offene Sitzung die Indizierung.
  - `Vault.PageReplaced` → `Semantic.UpdatePageAsync` für diese Seite, 2000 ms pro Pfad entprellt (TimeProvider). Das deckt eigene Speicherungen und externe Änderungen ab.
  - Kopfleiste: Die Stufe-1-Anzeige „KI: aus“ wird abhängig vom Status zu:
    - „KI: lokal“
    - „KI: lokal – indexiert {Done}/{Total}“
    - „KI: Modell fehlt“
    - „KI: lädt Modell …“
    - „KI: Fehler“ mit der Meldung als Tooltip
    - „KI: aus“
  - `AiModelDialog`:
    - erscheint nach dem Öffnen eines Vaults, wenn das Modell fehlt und `!Settings.AiModelDeclined`;
    - Text mit Größe (≈ 120 MB) und Hinweis „einmalig, danach offline“;
    - Knöpfe „Herunterladen“, „Nicht jetzt“ (setzt `AiModelDeclined`) und „Abbrechen“ während des Downloads;
    - Fortschrittsbalken und Fehlermeldung.
  - `SettingsDialog`: Abschnitt „KI-Modell“ mit Status und Knopf „Herunterladen“, wenn es fehlt.
  - `Program.cs`: `AiRuntime` als Singleton; `HttpClient` mit 30 min Timeout; `UserDataDirectory` als Ablage.

- [ ] **Step 1: Failing tests** (bUnit, `AiRuntime` mit `FakeEmbedder`-Fabrik und Fake-Downloader):
  - `Open_WithModel_IndexesInBackground_HeaderShowsProgressThenLocal`
  - `Open_ModelMissing_ShowsDialog_NotNowRemembered`
  - `Download_Completes_StartsIndexing`
  - `Download_Fails_ShowsError_AppStillUsable` (Review Focus 1)
  - `EmbedderFails_HeaderShowsError_SearchFallsBackToFullText`
  - `VaultSwitchDuringIndexing_OldIndexingCancelled_NoException` (Review Focus 3)
  - `PageReplaced_ReindexesThatPage` (Review Focus 4)
  - `NoGermanLiterals_InNewComponents`
- [ ] **Step 2–4:** `dotnet test tests/NoteEvolution.UI.Tests --filter "FullyQualifiedName~AiTests"`: FAIL → implementieren → PASS.
- [ ] **Step 5: Startversuch** (wie Regel R3 aus Stufe 1): Mit installiertem Modell die Desktop-App gegen einen künstlichen Vault starten. Die Kopfleiste zeigt Fortschritt und dann „KI: lokal“, es gibt keine Ausnahmen im Log. Danach den Prozess beenden.
- [ ] **Step 6: Commit** `feat(ui): ai runtime, session wiring, status and model dialog`

---

### Task 10: Reiter „Relevant“ und hybride Suche im Notizbereich

**Files:**
- Create: `src/NoteEvolution.UI/Components/RelevantTab.razor`
- Modify: `src/NoteEvolution.UI/Components/NotesPane.razor`, `Strings.resx`, `app.css`
- Test: `tests/NoteEvolution.UI.Tests/RelevantTabTests.cs`

**Interfaces:**
- Consumes: `VaultSession.Relevance`, `VaultSession.AiStatus`/`AiStatusChanged` (Task 9), `AppState` (`CurrentBook`, `CurrentSectionKey`, `CursorTextBlockKey`, `Mode`), `NoteCard` (Stufe 1, `Percent`)
- Produces:
  - Reiter in der Reihenfolge der Spec: *Relevant*, *Suche*, *Journal*.
  - Startreiter: *Relevant*, wenn die KI verfügbar ist, sonst *Suche*.
  - `RelevantTab`:
    - berechnet `Relevance.Relevant(new TopicRequest(book, section, cursor, mode == Manuscript), filter, 30)`;
    - neu 1500 ms nach der letzten Änderung von Buch, Abschnitt, Cursor, Ansicht oder Filter (TimeProvider);
    - rechnet per `Task.Run` und übernimmt nur das Ergebnis der neuesten Anfrage;
    - zeigt `NoteCard`s mit Prozent wie im Suchreiter (bester Treffer = 100 %);
    - ist die KI nicht verfügbar, erscheint ein lokalisierter Hinweis je Status, mit Knopf „Modell herunterladen“ bei fehlendem Modell (öffnet den `AiModelDialog`);
    - ohne aktuelles Buch erscheint der Hinweis „Kein Buch gewählt“.
  - Der Suchreiter bleibt unverändert; er nutzt `session.Search`, das in Task 9 hybrid wird.

- [ ] **Step 1: Failing tests**:
  - `Relevant_ShowsTop30ForSection_AfterDebounce`
  - `Relevant_RapidSectionChanges_OnlyLatestApplied`
  - `Relevant_HideUsedFilter_Applied`
  - `Relevant_AiUnavailable_ShowsHint`
  - `Tabs_Order_RelevantSearchJournal_DefaultDependsOnAi`
  - `Search_WithAi_UsesHybrid`
- [ ] **Step 2–4:** `dotnet test tests/NoteEvolution.UI.Tests --filter "FullyQualifiedName~RelevantTab"`: FAIL → implementieren → PASS.
- [ ] **Step 5: Commit** `feat(ui): relevant tab and hybrid search`

---

### Task 11: „Wohin damit?“ und Fundstellen im `[handled]`-Assistenten

**Files:**
- Modify:
  - `src/NoteEvolution.UI/Components/NoteCard.razor`, `Dialogs/HandledWizard.razor`
  - `Strings.resx`, `app.css`
  - `README.md` (Abschnitt KI, Modellablage, manuelle Abnahme)
- Test: `tests/NoteEvolution.UI.Tests/WhereToTests.cs`, Ergänzungen in `DialogTests.cs` (HandledWizard)

**Interfaces:**
- Consumes: `IRelevanceService.WhereTo`/`Placements` (Task 6), `HandledConverter.Apply(…, placements)` (Task 8)
- Produces:
  - **NoteCard:** Knopf „Wohin damit?“, nur sichtbar bei `Relevance.IsAvailable` und vorhandenem Buch.
    - Klick → `Task.Run(WhereTo(note, CurrentBook, 5))` → Liste mit Abschnittstitel und Prozent.
    - Klick auf einen Eintrag: Editor-Flush, `CurrentSectionKey` setzen, `Notify`.
    - Keine Treffer → lokalisierter Hinweis.
  - **HandledWizard:** Je Eintrag eine Auswahl.
    - Der Standard ist „Stelle unbekannt“; dazu kommen bis zu 3 Fundstellen (Textauszug von 60 Zeichen und Prozent), wenn die KI verfügbar ist.
    - Die Fundstellen werden beim Wechsel des Buchs im Hintergrund berechnet.
    - Eine gewählte Fundstelle gilt als bestätigt (Spec 5.5, Schritt 4) und geht als `placements` an `Apply`.
    - Ohne KI ändert sich nichts.
  - **README:** neuer Abschnitt „Lokale KI“:
    - Modellquelle und Lizenz (MIT, `intfloat/multilingual-e5-small`);
    - Ablageort und Größe;
    - Offline-Betrieb;
    - `vectors.db` darf gelöscht werden;
    - Abnahme-Checkliste: Relevant-Reiter folgt dem Abschnitt, Suche findet sinnverwandte Notizen ohne gleiche Wörter, Wohin damit?, Fundstellen-Vorschlag, Verhalten ohne Netz.

- [ ] **Step 1: Failing tests**:
  - `WhereTo_ShowsFiveSections_ClickNavigates`
  - `WhereTo_HiddenWithoutAi`
  - `Handled_PlacementChosen_FullLinkWritten`
  - `Handled_DefaultUnknownPlace_AsBefore`
  - `Handled_WithoutAi_NoPlacementColumn`
- [ ] **Step 2–4:** `dotnet test tests/NoteEvolution.UI.Tests --filter "FullyQualifiedName~WhereTo|FullyQualifiedName~DialogTests"`: FAIL → implementieren → PASS.
- [ ] **Step 5: Gesamtlauf:** `dotnet test NoteEvolution.slnx` → alles grün (RealModel-Tests ausgeführt, nicht übersprungen). `dotnet build NoteEvolution.slnx -c Release` → 0 Warnungen.
- [ ] **Step 6: Commit** `feat(ui): where-to suggestions and handled placements`
