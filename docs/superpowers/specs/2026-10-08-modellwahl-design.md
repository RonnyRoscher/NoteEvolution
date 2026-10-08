# NoteEvolution – Wählbares KI-Modell

Stand: 2026-10-08 · Status: Entwurf · Ergänzt Abschnitt 6.1 der Design-Spezifikation vom 2026-10-05

## 1. Ziel

Bisher kennt NoteEvolution nur ein lokales Modell, `multilingual-e5-small` (int8). Künftig wählt der Nutzer aus vier Modellen. Zu jedem sieht er den Download und den geschätzten Arbeitsspeicher.

Gewählt wird beim ersten Laden des Modells. Später lässt es sich in den Einstellungen wechseln, auch während ein Vault offen ist.

### Erfolgskriterien

- Beim ersten Laden zeigt der Modell-Dialog alle vier Modelle mit Download-Größe, geschätztem Arbeitsspeicher und einer kurzen Einordnung. Der Nutzer wählt eines aus und lädt es herunter.
- In den Einstellungen steht das aktive Modell. Über „Modell wählen…“ öffnet sich derselbe Dialog, in dem sich ein anderes Modell laden und aktivieren lässt.
- Nach dem Wechsel verwendet die App nur noch das neue Modell. Der Index des offenen Vaults wird damit neu aufgebaut.
- Auf der Platte liegt danach nur noch das neue Modell: Der alte Modellordner wird gelöscht, die Vektordatei des Vaults neu angelegt.
- Während der Download läuft, arbeitet das alte Modell weiter. Scheitert der Download oder bricht der Nutzer ihn ab, bleibt alles beim alten Modell. Halb geladene Dateien werden entfernt.
- Die Wahl gilt app-weit für alle Vaults und bleibt über einen Neustart erhalten.

### Rahmenbedingungen

- Es ist immer höchstens ein Modell aktiv, und auf Dauer liegt nur ein Modell auf der Platte. Nur während eines Wechsels liegen kurzzeitig zwei dort.
- Jede Datei ist auf eine feste Hugging-Face-Revision festgelegt und wird per SHA-256 geprüft, wie bisher.
- Alle Modelle laufen mit ONNX Runtime auf der CPU, in der int8-Fassung.

## 2. Modellkatalog

| Id | Anzeige | ONNX-Datei | Download gesamt | Arbeitsspeicher ca. | Dim. | Pooling | Präfixe |
|---|---|---|---|---|---|---|---|
| `multilingual-e5-small-int8` | e5-small | `onnx/model_quantized.onnx`, 118 308 185 B | ≈ 123 MB | 0,4 GB | 384 | Mittelwert | `query: ` / `passage: ` |
| `multilingual-e5-base-int8` | e5-base | `onnx/model_quantized.onnx`, 278 647 662 B | ≈ 284 MB | 0,7 GB | 768 | Mittelwert | `query: ` / `passage: ` |
| `multilingual-e5-large-int8` | e5-large | `onnx/model_quantized.onnx`, 561 768 762 B | ≈ 567 MB | 1,3 GB | 1024 | Mittelwert | `query: ` / `passage: ` |
| `bge-m3-int8` | bge-m3 | `onnx/model_quantized.onnx`, 569 694 530 B | ≈ 575 MB | 1,3 GB | 1024 | erstes Token (CLS) | keine |

### Feste Revisionen und Prüfsummen

Alle Modelle kommen aus den Repositories `Xenova/<name>` auf Hugging Face.

| Repository | Revision | SHA-256 der ONNX-Datei |
|---|---|---|
| `multilingual-e5-small` | `761b726dd34fb83930e26aab4e9ac3899aa1fa78` | unverändert wie bisher |
| `multilingual-e5-base` | `1ec9243030a27d1a115d5c340572074c125b58b2` | `df7a9a29309e3ad491e1783adf8baee710262cc06079c7cbab63c630277fac94` |
| `multilingual-e5-large` | `00fc3aeb3dbb95842de2ac1961d33c6319acf57b` | `0a8d65db9a36f810ba5da15249f13145fcdc7890e6656f1fd38cd8b7c4db1fca` |
| `bge-m3` | `4de13258303883538bd53b696b452bf8099f0858` | `0826f8c1ab9edf1801db86c61919d4d108e8bfc0b809ec823ad366882ff0b77d` |

Jedes Modell lädt außerdem `sentencepiece.bpe.model`. Die Datei ist in allen vier Repositories gleich: 5 069 051 B, SHA-256 `cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865`.

### Weitere Festlegungen

- **Token-Grenze:** Für alle Modelle gilt `MaxTokens = 512`. bge-m3 könnte längere Texte verarbeiten, doch Indizierungszeit und Arbeitsspeicher würden stark steigen. Journal-Notizen sind fast immer kürzer.
- **Arbeitsspeicher:** Der Wert ist eine feste Schätzung im Katalog: etwa die doppelte ONNX-Datei plus 150 MB Laufzeit. Die Oberfläche beschriftet ihn als „ca.“.
  - Die Vektoren der Notizen zählen nicht mit. Sie sind dagegen klein, etwa 40 MB bei 10 000 Notizen und 1024 Dimensionen.
- **Einordnung:** Jedes Modell bekommt einen kurzen Satz aus `Strings.resx` (R6):
  - e5-small: „schnell, solide Qualität“
  - e5-base: „ausgewogen“
  - e5-large: „beste e5-Qualität, deutlich langsamer“
  - bge-m3: „sehr gute mehrsprachige Qualität, langsam“
- **Standard:** e5-small bleibt das voreingestellte Modell. Wer es schon installiert hat, muss nichts tun.

## 3. Komponenten

### 3.1 `ModelInfo` und `ModelCatalog` (AI)

`ModelInfo` bekommt drei neue Felder:
- `Pooling` mit den Werten `Mean` und `Cls`
- `QueryPrefix`
- `PassagePrefix`

`ModelCatalog` bekommt:
- vier Einträge, nämlich `E5Small`, `E5Base`, `E5Large` und `BgeM3`
- `All` mit allen Einträgen in dieser Reihenfolge
- `Default`, das `E5Small` ist
- `Find(id)`, das `null` liefert, wenn die Id unbekannt ist

Die geschätzte RAM-Größe ergibt sich aus der Größe der ONNX-Datei, wie in Abschnitt 2 beschrieben.

### 3.2 Präfixe (AI)

`EmbeddingText` setzt weiterhin `query: ` bzw. `passage: ` vor jeden Text. Diese Präfixe sind jetzt **Rollenmarker**: Sie sagen, ob ein Text sucht oder gesucht wird.

`OnnxEmbedder` ersetzt den Marker am Textanfang durch das Präfix des Modells. Bei e5 bleibt der Text dadurch gleich, bei bge-m3 entfällt der Marker. Texte ohne Marker gehen unverändert durch.

Dadurch bleiben alle Aufrufer und Tests von `EmbeddingText` unverändert. Die Hashes im Vektor-Cache enthalten die Modell-Id, Einträge verschiedener Modelle vermischen sich also nicht. `IEmbedder` beschreibt die Rollenmarker in seiner Dokumentation.

### 3.3 Pooling (AI)

`OnnxEmbedder` wählt das Pooling nach `ModelInfo.Pooling`:
- `Mean` wie bisher: Mittelwert über die Maske, danach L2-normiert.
- `Cls`: der Vektor des ersten Tokens aus `last_hidden_state`, L2-normiert. Die Funktion dafür kommt neu in `VectorMath`.

Der Ladevorgang prüft wie bisher die Ein- und Ausgaben. bge-m3 kommt ohne `token_type_ids` aus, das ist bereits vorgesehen.

### 3.4 `ModelStore` (AI)

Neu ist `DeleteAllExcept(ModelInfo keep)`. Es löscht unter `models/` jeden Ordner außer dem von `keep`.

Fehler wie eine gesperrte Datei werden pro Ordner gesammelt und nicht geworfen. Der Aufrufer protokolliert sie, beim nächsten Start wird es erneut versucht.

### 3.5 `AiRuntime` (UI)

- **`Model`** ist künftig veränderlich und heißt das aktive Modell. `ModelInstalled` bezieht sich auf das aktive Modell.
- **`DownloadAsync(ModelInfo model, progress, ct)`** lädt ein beliebiges Katalogmodell herunter, ohne das aktive zu ändern. Es gilt weiterhin nur ein Download zur Zeit.
  - `IsDownloading` und `ModelChanged` verhalten sich wie bisher.
  - Scheitert der Download oder wird er abgebrochen, löscht die Methode den Ordner des Zielmodells, sofern es nicht das aktive Modell ist.
- **`Activate(ModelInfo model)`** setzt ein installiertes Modell aktiv:
  1. Es gibt den Embedder des bisherigen Modells frei.
  2. Es setzt den Ladezustand zurück; der nächste Zugriff auf `Embedder` lädt das neue Modell.
  3. Es ruft `ModelStore.DeleteAllExcept(model)` auf.
  4. Es löst `ModelChanged` aus.

  Ist das Modell nicht installiert, wirft die Methode `InvalidOperationException`. Ist es schon aktiv, tut sie nichts.
- Der Konstruktor bekommt statt `Func<IEmbedder>` jetzt `Func<ModelInfo, IEmbedder>`, damit sich das jeweils aktive Modell laden lässt.
- **Beim Start** ist das Modell aktiv, das in `UiSettings.AiModelId` steht. Fehlt die Id oder ist sie unbekannt, gilt `ModelCatalog.Default`.
  - Ist das aktive Modell installiert, räumt `DeleteAllExcept` Reste auf, etwa nach einem Absturz mitten im Wechsel.

### 3.6 `UiSettings` (UI)

Neu ist `AiModelId` vom Typ `string?`; `null` bedeutet das Standardmodell. `Program.cs` liest die Id und übergibt das passende Modell an `AiRuntime`.

### 3.7 `AiModelDialog` (UI)

Der Dialog dient für das erste Laden und für den Wechsel.

- **Liste:** Er zeigt die vier Modelle als Auswahl mit je einer Zeile, etwa „e5-base – Download ca. 280 MB · Arbeitsspeicher ca. 0,7 GB – ausgewogen“.
  - Das aktive Modell ist vorausgewählt und als „aktiv“ markiert.
- **Ohne installiertes Modell:**
  - Die Knöpfe heißen „Herunterladen“ und „Nicht jetzt“, wie bisher.
  - Nach dem Download wird das Modell über `Activate` aktiv, und der Dialog schließt.
- **Mit installiertem Modell:**
  - Die Knöpfe heißen „Wechseln“ und „Abbrechen“. „Wechseln“ ist gesperrt, solange das aktive Modell gewählt ist.
  - Ein Hinweis sagt, dass der Index neu aufgebaut und das bisherige Modell gelöscht wird.
  - Nach dem Download übergibt der Dialog das neue Modell an `OnSwitch`, wie in Abschnitt 4 beschrieben.
- **Während des Downloads** sind ein Fortschrittsbalken und „Abbrechen“ sichtbar. Die Auswahl ist dann gesperrt.
- **Fehlermeldungen** erscheinen wie bisher im Dialog.

### 3.8 `SettingsDialog` (UI)

Der Abschnitt „KI-Modell“ zeigt:
- das aktive Modell mit Download-Größe und geschätztem Arbeitsspeicher
- den Zustand: installiert, fehlt oder wird geladen
- immer den Knopf „Modell wählen…“, der den Modell-Dialog über `State.RequestAiModelDialog` öffnet

Der bisherige Knopf „Herunterladen“ entfällt; diese Aufgabe übernimmt jetzt „Modell wählen…“.

### 3.9 `Shell` (UI)

Neu ist `SwitchAiModelAsync(ModelInfo target)`; es wird von `AiModelDialog.OnSwitch` aufgerufen. Der Ablauf steht in Abschnitt 4.

Das Angebot nach dem Öffnen eines Vaults (`OfferAiModel`) bleibt, wie es ist.

## 4. Ablauf eines Wechsels

1. Der Nutzer wählt im Modell-Dialog ein anderes Modell und klickt „Wechseln“.
2. `AiRuntime.DownloadAsync(target)` lädt es herunter. Das alte Modell bleibt so lange aktiv, Suche und Vorschläge arbeiten weiter.
   - Bei einem Fehler oder Abbruch wird der Zielordner gelöscht, und die Meldung erscheint im Dialog. Ende des Ablaufs.
3. Der Dialog ruft `Shell.SwitchAiModelAsync(target)` auf. Ist ein Vault offen, geschieht Folgendes:
   1. Der Editor sichert seinen offenen Text (`State.FlushEditor`).
   2. Die Sitzung wird freigegeben. Sie beendet ihre Indizierung und gibt den gemeinsamen Embedder frei, bevor die Laufzeit ihn entsorgt.
4. `AiRuntime.Activate(target)` gibt den alten Embedder frei und löscht den alten Modellordner.
5. `UiSettings.AiModelId` wird auf das neue Modell gesetzt und gespeichert.
6. War ein Vault offen, öffnet die Shell ihn erneut. Gleiche Seite und gleiches Buch kommen über die gemerkte Auswahl zurück.
   - `VectorCache.Open` sieht die neue Modell-Id und legt `vectors.db` neu an. Die Indizierung startet mit dem neuen Modell, die Kopfzeile zeigt den Fortschritt.
7. Der Modell-Dialog schließt; die Einstellungen schließen mit dem erneuten Öffnen des Vaults.

**Konflikte:** Gibt es einen offenen Konflikt, ist „Wechseln“ gesperrt und ein Hinweis erscheint. Das entspricht R26 und dem Ändern der Notizordner.

**Fehler beim erneuten Öffnen:** Scheitert das erneute Öffnen, zeigt die Shell die bekannte Fehlermeldung. Das neue Modell bleibt trotzdem aktiv.

## 5. Fehlerbehandlung

- **Download:** Fehler und Abbrüche behandelt das System wie bisher; zusätzlich wird der Zielordner gelöscht.
- **Löschen des alten Modells:** Scheitert es, etwa an einer gesperrten Datei, wird das protokolliert, und der Wechsel gilt trotzdem. Der nächste Start räumt auf.
- **Laden des neuen Modells:** Scheitert es, gilt dasselbe wie bisher: Die Kopfzeile zeigt den Fehler (`LoadError`), und die Suche arbeitet nur im Volltext.
  - Der Nutzer kann im Modell-Dialog ein anderes Modell wählen.
  - Fehlen die Dateien des aktiven Modells, kann er es erneut herunterladen: Er wählt es aus, das gilt dann als erstes Laden.
- **`ui.json` mit unbekannter Modell-Id:** Es gilt das Standardmodell.

## 6. Tests

- **ModelCatalog:**
  - Ids, Dateien und Revisionen sind eindeutig.
  - `Find` findet jedes Modell und liefert für unbekannte Ids `null`.
  - Die Größen ergeben die Summen aus Abschnitt 2.
- **VectorMath:** CLS-Pooling liefert den normierten Vektor des ersten Tokens.
- **OnnxEmbedder:** Die Rollenmarker werden ersetzt bzw. entfernt. Das wird ohne echtes Modell geprüft, über eine reine Hilfsfunktion.
- **ModelStore:** `DeleteAllExcept` lässt nur den behaltenen Ordner übrig und meldet Fehler, statt zu werfen.
- **AiRuntime:**
  - Download eines anderen Modells, ohne das aktive zu ändern.
  - Ein Abbruch löscht den Zielordner.
  - `Activate` gibt den alten Embedder frei, lädt beim nächsten Zugriff mit dem neuen Modell, löscht den alten Ordner und löst `ModelChanged` aus.
  - Beim Start wird `AiModelId` gelesen, eine unbekannte Id fällt auf den Standard zurück, und es wird aufgeräumt.
- **bUnit:**
  - Der Modell-Dialog zeigt vier Einträge mit Größen.
  - Beim ersten Laden: Download und Aktivierung.
  - Beim Wechsel ruft er `OnSwitch` mit dem gewählten Modell auf.
  - „Wechseln“ ist beim aktiven Modell und bei offenem Konflikt gesperrt.
  - Die Einstellungen zeigen das aktive Modell und „Modell wählen…“.
  - `Shell.SwitchAiModelAsync` gibt die Sitzung frei, aktiviert das neue Modell, speichert `AiModelId` und öffnet den Vault erneut.
- **Echte Modelle:** Die bestehenden Tests mit echtem Modell (`NOTEEVOLUTION_MODEL_DIR`) laufen weiter nur mit e5-small.
  - Für bge-m3 gibt es einen optionalen Test über `NOTEEVOLUTION_BGE_M3_DIR`. Er prüft Ein- und Ausgaben, Dimension 1024 und dass ähnliche Texte näher beieinander liegen.
- **Smoke-Lauf (R3):** Die App startet. Der Wechsel e5-small → e5-base wird einmal real durchgeführt. Danach liegt nur noch `multilingual-e5-base-int8` unter `models/`, und `vectors.db` trägt die neue Id.

## 7. Nicht im Umfang

- mehrere Modelle gleichzeitig installiert oder aktiv
- fp32-Fassungen
- Modelle außerhalb des Katalogs
- GPU
- längerer Kontext bei bge-m3
- Wahl des Modells pro Vault
