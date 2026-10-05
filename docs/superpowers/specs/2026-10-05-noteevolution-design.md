# NoteEvolution – Design-Spezifikation

Stand: 2026-10-05 · Status: freigegeben

## 1. Ziel und Kontext

NoteEvolution ist ein Schreibprogramm, mit dem aus Journal-Notizen ein Buch entsteht. Die Notizen und das Buch liegen als Markdown-Dateien in einem Vault, der am Rechner mit **Logseq** bearbeitet und über **Obsidian** auf das Handy synchronisiert wird.

Der Bildschirm ist zweigeteilt: links das Buch, rechts die Notizen. Notizen werden per „Übernehmen“ als Rohtext ins Buch kopiert und dort ausformuliert. Beide Seiten verweisen aufeinander: Die Notiz weiß, wo sie verwendet wurde, der Buchblock weiß, woher er stammt. Eine lokale KI zeigt zum gerade bearbeiteten Themenbereich passende Notizen. Am Ende entsteht ein PDF, zunächst einfach, später druckfertig.

### Erfolgskriterien

- Notizen lassen sich mit einem Handgriff ins Buch übernehmen; danach ist in Notiz und Buch sichtbar, wohin bzw. woher.
- Die Suche kann bereits verwendete Notizen ausblenden.
- Passende Notizen zum aktuellen Abschnitt erscheinen ohne Zutun.
- Logseq und Obsidian zeigen alle von NoteEvolution geschriebenen Dateien korrekt an; nichts außerhalb der bewusst geänderten Blöcke verändert sich.
- Das Programm läuft unter Windows, Linux und macOS; die Architektur erlaubt später eine Handy-App ohne Neubau der Oberfläche.

### Rahmenbedingungen

- Einzelnutzer, lokal, keine Cloud-Pflicht, keine Zusammenarbeit.
- Die Notizen bleiben im Original erhalten; NoteEvolution ergänzt nur Eigenschaftszeilen (`id::`, `used-in::`). Einzige Ausnahme ist die einmalige, bestätigte Umwandlung von `[handled]` (Abschnitt 5.5).
- Das Buch liegt im Vault.
- Die KI schreibt oder formuliert keinen Buchtext.

## 2. Architektur

Plattform: .NET 10 (LTS). Oberfläche: Blazor Hybrid.

| Projekt | Aufgabe | Abhängigkeiten |
|---|---|---|
| `NoteEvolution.Core` | Logseq-Leser/-Schreiber, Buch- und Notizmodell, Verknüpfungen, Assistenten, Dateiüberwachung, Sicherungen | keine UI |
| `NoteEvolution.AI` | Embeddings (ONNX Runtime), Vektor-Cache und Volltextindex (SQLite), Relevanz, optionales LLM | Core |
| `NoteEvolution.Pdf` | PDF-Erzeugung (QuestPDF) | Core |
| `NoteEvolution.UI` | Razor-Komponenten, TipTap-Editor (lokal gebündelt) | Schnittstellen aus Core/AI/Pdf |
| `NoteEvolution.Desktop` | Photino-Hülle für Windows, Linux, macOS | UI |
| `NoteEvolution.Mobile` (Stufe 5) | MAUI-Blazor-Hülle für iOS, Android | UI |
| `NoteEvolution.*.Tests` | xUnit (Core, AI, Pdf), bUnit (UI) | – |

Grundregeln:

- Core, AI und Pdf kennen keine Oberfläche. Die UI spricht sie nur über Schnittstellen an (u. a. `IVault`, `IBookRepository`, `INoteRepository`, `ILinkService`, `IRelevanceService`, `ISearchService`, `IPdfExporter`).
- Die Hülle liefert nur Plattformdienste: Ordnerauswahl, Dateizugriff, Fenster, Schlüsselspeicher (`IPlatformServices`).
- **Die Dateien im Vault sind die einzige Wahrheit.** Alles unter `.noteevolution/` (Cache, Index, Protokolle, Sicherungen) ist wiederherstellbar bzw. Zusatz. Der Ordner kann von der Synchronisation ausgenommen werden.
- TipTap ist die einzige JavaScript-Komponente. Sie tauscht über eine schmale Interop-Schnittstelle nur Blockinhalte und Ereignisse aus; jede Logik liegt in C#.

## 3. Dateiformate

### 3.1 Logseq-Markdown lesen und schreiben

Erkannt werden:

- Anstriche mit `-` oder `*`, Einrückung mit Tabs oder Leerzeichen (pro Datei gemischt möglich).
- Mehrzeilige Blöcke (Fortsetzungszeilen unter der ersten Blockzeile).
- Seiten-Eigenschaften (`key:: value` am Dateianfang, ohne Anstrich).
- Block-Eigenschaften (`key:: value`-Zeilen direkt unter der ersten Blockzeile), darunter `collapsed::`, `id::`, `source::`, `used-in::`.
- Blockverweise `((uuid))`, Seitenlinks `[[Seite]]`, Tags `#tag`, Überschriften-Blöcke `- # …`, Bilder `![](…)`, Fett/Kursiv.

Schreibregeln:

- **Verlustfrei:** Der Parser behält zu jedem Block seine Originalzeilen. Nur geänderte Blöcke werden neu erzeugt; alle übrigen Zeilen bleiben Byte für Byte erhalten (Einrückungsstil, Zeilenenden, Leerzeilen, Kodierung UTF-8).
- Neue Zeilen übernehmen den Einrückungsstil ihres Elternblocks.
- **Atomar:** Schreiben in eine temporäre Datei im selben Ordner, dann Ersetzen.
- Eine Datei, die nicht sicher geparst werden kann, wird nur angezeigt und nie geschrieben.
- IDs sind UUIDs (Logseq-kompatibel).

### 3.2 Buchdatei

Eine Datei mit der Seiten-Eigenschaft `type:: book` ist ein Buch. Weitere Seiten-Eigenschaften wie `title::` und `dedication::` werden für die Titelseite genutzt.

```markdown
title:: Buch: LoveMagic
type:: book
dedication:: …

- # Liebe und Wahrheit
  collapsed:: true
	- ## Vertrauen
		- Angst baut Widerstand auf, Vertrauen baut Schwung auf.
		  id:: 6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70
		  source:: ((7f3a91c2-…)), ((81bb02aa-…))
			- Gute Interpretationsvarianten zu sehen ist trainierbar …
			- noch ein Beispiel ergänzen #notiz
```

- **Gliederungsknoten:** Anstrich, dessen Inhalt mit `#`, `##`, `###` … beginnt. Die Ebene ist die Zahl der Rauten. Ist eine Kind-Überschrift nicht tiefer als ihr Eltern-Knoten, zeigt die Gliederung eine Warnung; geschrieben wird trotzdem nichts automatisch.
- **Textblock:** Anstrich ohne führende Rauten unterhalb eines Gliederungsknotens. Er erhält `id::`, sobald er verknüpft ist, und `source::` mit den Blockverweisen auf seine Quellen (Komma-getrennt).
- **Unteranstriche eines Textblocks** gehören zum Block. Im PDF werden sie zu Fließtext-Absätzen. Unteranstriche mit dem Tag `#notiz` sind Arbeitsnotizen: Sie werden im Editor gedämpft dargestellt und nicht exportiert.
- Textblöcke direkt unter der Wurzel, also vor der ersten Überschrift, sind erlaubt und gehören zu einem impliziten Vorspann.

### 3.3 Notizdateien

Gelesen werden standardmäßig `journals/` und `pages/` (in den Einstellungen änderbar). Buchdateien werden nicht als Notizen behandelt.

Die Einheit ist der **Block**: ein Anstrich mit seinen Unteranstrichen. Jeder Block, auch ein Unteranstrich, kann einzeln übernommen werden.

```markdown
- Angst baut Widerstand auf, Vertrauen baut Schwung auf
  id:: 7f3a91c2-…
  used-in:: [[Buch - LoveMagic]] ((6650a1c2-…))
	- …
```

- `used-in::` enthält pro Verwendung einen Seitenlink auf das Buch und, falls bekannt, einen Blockverweis. Mehrere Verwendungen werden Komma-getrennt aufgelistet.
- Ein Seitenlink ohne Blockverweis bedeutet „verwendet, Stelle unbekannt“.
- Der Seitenlink ist in Logseq und Obsidian anklickbar, der Blockverweis in Logseq und NoteEvolution.
- Ein Block gilt als verwendet, wenn er selbst oder einer seiner Vorfahren ein `used-in::` trägt.

## 4. Datenmodell (Core)

- `Vault`: Wurzelpfad, Einstellungen, Sammlung von `Page`.
- `Page`: Datei, Seiten-Eigenschaften, Baum aus `Block`, Originalzeilen.
- `Block`: Inhalt, Eigenschaften, Kinder, Originalzeilenbereich, Flag „geändert“.
- `Book`: Sicht auf eine `Page` mit `type:: book`; liefert `OutlineNode` (Überschrift, Ebene, Kinder) und `TextBlock` (Inhalt, Absätze, Arbeitsnotizen, Quellen).
- `NoteBlock`: Sicht auf einen Block einer Notizseite mit Datum (aus dem Journal-Dateinamen), Kontextpfad und Verwendungen.
- `Usage`: Paar aus Notizblock-ID und Buchblock-ID (die Buchblock-ID darf fehlen).

## 5. Kernabläufe

### 5.1 Vault öffnen

1. Alle Notiz- und Buchdateien parsen und das Modell aufbauen.
2. Dateiüberwachung starten.
3. Verknüpfungsprüfung ausführen (5.4).
4. Ab Stufe 2: KI-Index im Hintergrund aktualisieren; neu berechnet werden nur Blöcke, deren Inhalts-Hash sich geändert hat.

### 5.2 Übernehmen

Auslöser: Knopf „Übernehmen“ an einer Notizkarte oder an einem einzelnen Unteranstrich, oder Ziehen und Ablegen ins Buch.

1. Zielposition: hinter dem aktuellen Textblock, ohne Cursor am Ende des aktuellen Abschnitts, beim Ablegen an der Ablagestelle.
2. Der Notizblock wird samt Unteranstrichen als neuer Textblock kopiert (ohne Eigenschaftszeilen) und erhält `id::` und `source::`.
3. Zuerst wird die Buchdatei geschrieben, dann die Notizdatei (`id::` falls nötig, Eintrag in `used-in::`).
4. Scheitert Schritt 3, wird der Vorgang in `.noteevolution/pending.json` vermerkt und beim nächsten Speichern bzw. Start nachgeholt.
5. Der Vorgang ist als Ganzes rückgängig zu machen.

### 5.3 Bearbeiten im Buch

- **Verschieben** von Blöcken oder Abschnitten: Die IDs bleiben, Verweise bleiben gültig.
- **Teilen** eines Textblocks: Der neue Teil bekommt eine eigene ID; beide Teile behalten alle Quellen, und die Notizen erhalten den zusätzlichen Verweis. Überflüssige Quellen entfernt der Nutzer über den Chip.
- **Quelle entfernen** (× am Chip): `source::` des Blocks und `used-in::` der Notiz werden angepasst.
- **Löschen** eines Textblocks: Die zugehörigen `used-in::`-Einträge werden entfernt. Rückgängig ist möglich.
- **Automatisches Speichern** etwa eine Sekunde nach der letzten Eingabe.

### 5.4 Verknüpfungsprüfung

Beim Start und auf Knopfdruck:

- Buchblock mit Quelle, deren Notiz keinen passenden `used-in::`-Eintrag hat: wird ergänzt.
- `used-in::`-Eintrag, dessen Buchblock nicht existiert: Hinweisliste; der Nutzer wählt „Stelle unbekannt“ oder „Eintrag entfernen“.
- `source::`-Verweis auf eine nicht existierende Notiz: Hinweis, der Chip wird als defekt markiert.

### 5.5 Assistent: `[handled]` umwandeln

1. Alle Notizblöcke finden, deren Inhalt mit `[handled]` beginnt, und als Liste zeigen.
2. Der Nutzer wählt das Buch, auf das sie sich beziehen.
3. Nach Bestätigung: `[handled] ` am Blockanfang entfernen und `used-in:: [[<Buch>]]` setzen.
4. Ab Stufe 2 optional: Für jeden Eintrag schlägt die KI die wahrscheinlichste Buchstelle vor; bestätigte Vorschläge werden zu vollständigen Verknüpfungen.

### 5.6 Assistent: Entwurf ins Mischformat umwandeln

1. Sicherung der Buchdatei anlegen.
2. Heuristik schlägt Gliederungsknoten vor: Anstriche mit Kindern, kurzem Text (≤ 80 Zeichen) und ohne Satzendezeichen. Die Ebene folgt der Verschachtelungstiefe der Gliederungsknoten.
3. Vorschau als Baum; der Nutzer schaltet einzelne Knoten zwischen Überschrift und Textblock um.
4. Nach Bestätigung: `# `-Präfixe setzen, `type:: book` ergänzen. Alles andere, auch `collapsed::`, bleibt unverändert.

### 5.7 Zusammenspiel mit Logseq und Synchronisation

- Externe Änderung an einer Datei ohne ungespeicherte eigene Änderungen: wird still neu geladen.
- Externe Änderung an einem Block, der lokal ungespeicherte Änderungen hat: Konfliktdialog mit den Optionen „meine Fassung“, „externe Fassung“ und „beide nebeneinander“. Bei „beide nebeneinander“ wird die externe Fassung als zusätzlicher Block direkt dahinter eingefügt.
- Vor dem ersten Schreiben eines Tages wird jede betroffene Datei nach `.noteevolution/backups/<datum>/` kopiert. Sicherungen älter als 30 Tage werden gelöscht.
- Empfehlung an den Nutzer, im Programm als Hinweis: dasselbe Buch nicht gleichzeitig in Logseq bearbeiten.

## 6. KI (Stufen 2 und 3)

### 6.1 Lokale Basis

- Standardmodell: `multilingual-e5-small`, quantisiert (int8), etwa 120 MB, ONNX Runtime auf der CPU. Optional `bge-m3` (etwa 600 MB) für höhere Qualität.
- Tokenisierung mit `Microsoft.ML.Tokenizers`.
- Das Modell wird beim ersten Start nach Bestätigung heruntergeladen und im Benutzerprofil abgelegt (nicht im Vault, damit es nicht synchronisiert wird); danach ist alles offline.
- **Eingebetteter Text pro Notizblock:** Datum, Text des Elternblocks (Kontext), eigener Text samt Unteranstrichen; auf das Tokenlimit gekürzt.
- **Speicher:** SQLite-Tabelle mit Block-ID, Datei, Inhalts-Hash und Vektor. Gesucht wird direkt im Speicher per Kosinus-Ähnlichkeit über alle Vektoren.
- **Volltext:** SQLite FTS5 über alle Notizblöcke (bereits in Stufe 1).

### 6.2 Aktueller Themenbereich

- Abschnittsansicht: Embedding aus dem Überschriftenpfad und dem Mittelwert der Textblock-Embeddings des Abschnitts.
- Manuskriptansicht: Block am Cursor, seine beiden Nachbarn und der Überschriftenpfad.
- Aktualisierung entprellt (etwa 1,5 s) nach Abschnitts- oder Cursorwechsel.

### 6.3 Funktionen

1. **Relevant:** die 30 ähnlichsten Notizblöcke zum Themenbereich, gefiltert nach „Verwendete ausblenden“ und Zeitraum.
2. **Suche:** Volltext- und Bedeutungsrangfolge kombiniert per Reciprocal Rank Fusion; dieselben Filter.
3. **Wohin damit?:** Für einen Notizblock die fünf ähnlichsten Abschnitte des Buchs.
4. **Fundstellen für `[handled]`:** für einen Notizblock die ähnlichsten Textblöcke des Buchs (siehe 5.5).

### 6.4 Optionales Sprachmodell (Stufe 3)

- Anbindung über `Microsoft.Extensions.AI`; Anbieter Ollama (lokale URL) oder Claude-API. Der API-Schlüssel liegt im Schlüsselspeicher des Betriebssystems.
- Nachsortieren der besten 20 Treffer mit einer einzeiligen Begründung pro Treffer.
- Prüfen der Fundstellen-Vorschläge im `[handled]`-Assistenten.
- Gesendet werden nur der aktuelle Abschnitt und die Kandidaten. Ist ein Cloud-Anbieter aktiv, zeigt die Kopfleiste das dauerhaft an.

## 7. Oberfläche

Drei Bereiche, Trennlinien verstellbar, Layout wird gespeichert, helles und dunkles Design. Oberflächentexte auf Deutsch, zentral als Ressourcen verwaltet.

**Kopfleiste:** Buchauswahl, Umschalter Abschnitt/Manuskript, PDF-Export, Verknüpfungsprüfung, Einstellungen, KI-Anzeige (lokal / Ollama / Cloud / aus).

**Gliederung (links, einklappbar):**
- Baum aus Gliederungsknoten mit Wortzahl und Quellenanzahl.
- Überschriften anlegen und per Doppelklick umbenennen.
- Abschnitte per Ziehen und Ablegen umsortieren.
- Auswahl eines Knotens setzt den aktuellen Abschnitt.

**Editor (Mitte, TipTap):**
- *Abschnittsansicht:* nur der gewählte Abschnitt. Jeder Textblock ist abgesetzt, Unteranstriche als eingerückte Absätze, `#notiz`-Anstriche kursiv und gedämpft. Unter dem Block stehen Quellen-Chips: Ein Klick öffnet die Notiz rechts, × entfernt die Verknüpfung.
- *Manuskriptansicht:* der gewählte Teil bzw. das Kapitel als durchgehender, voll bearbeitbarer Text. Überschriften sind sichtbar, Blöcke nur durch Abstand getrennt, Quellen als Randmarken, Chips zuschaltbar.
- *Tastatur:* `Enter` neuer Absatz im Block, `Strg+Enter` neuer Block, `Tab` / `Umschalt+Tab` ein- und ausrücken, `Strg+Umschalt+N` schaltet `#notiz` um, Fett und Kursiv wie gewohnt.
- Serifenschrift; Schriftgröße und Zeilenbreite einstellbar.

**Notizen (rechts, einklappbar):**
- Reiter *Relevant* (ab Stufe 2), *Suche* und *Journal* (chronologisch, mit Datumssprung).
- Notizkarte: Datum, Trefferquote, Status. Verwendete Einträge zeigen ✓; ein Klick springt zur Buchstelle.
- Lange Blöcke sind nach drei Zeilen eingeklappt.
- Aktionen: Übernehmen (ganz oder pro Unteranstrich), Ziehen ins Buch, Wohin damit? (ab Stufe 2), In Logseq öffnen (`logseq://`-Link).
- Schalter „Verwendete ausblenden“ und Datumsfilter.

**Handy (Stufe 5):** Buch und Notizen als zwei Reiter mit Wischgeste, Gliederung als Seitenmenü.

## 8. PDF

### 8.1 Einfaches PDF (Stufe 1, QuestPDF)

- Titelseite aus `title::` und `dedication::`.
- Inhaltsverzeichnis mit Seitenzahlen; Überschriften nach Ebene.
- Textblöcke werden zu Absätzen, Unteranstriche zu Fließtext. `#notiz`-Anstriche, Eigenschaftszeilen und Quellen entfallen. Fett und Kursiv bleiben erhalten.
- Bilder aus `![](…)` werden eingebettet; fehlende Bilder führen zu einem Platzhalter und einer Warnung.
- A4 oder A5, Seitenzahlen, mitgelieferte Schrift EB Garamond (OFL).
- Export des ganzen Buchs oder eines gewählten Teils bzw. Kapitels.

### 8.2 Druck-PDF (Stufe 4)

- Wählbares Endformat (z. B. 12,5 × 19 cm), 3 mm Beschnitt, Schnittmarken.
- Gespiegelte Innen- und Außenränder.
- Deutsche Silbentrennung, Schutz vor Hurenkindern und Schusterjungen.
- PDF/X für die Druckerei.
- Zu Beginn der Stufe entscheidet ein kurzer Test, ob QuestPDF genügt oder Typst als Satzsystem eingebunden wird.

## 9. Fehlerbehandlung

| Situation | Verhalten |
|---|---|
| Datei nicht sicher parsbar | nur anzeigen, nie schreiben, Hinweis mit Dateiname und Zeile |
| Schreiben fehlgeschlagen | Wiederholung; offene Vorgänge in `pending.json`, Nachholen beim nächsten Speichern bzw. Start |
| Externe Änderung im Konflikt | Konfliktdialog (5.7) |
| KI-Modell fehlt oder liefert Fehler | Rückfall auf Volltextsuche, Hinweis |
| Ollama oder Cloud nicht erreichbar | lokale Rangfolge bleibt, dezenter Hinweis |
| Bild für PDF fehlt | Platzhalter, Warnliste nach dem Export |

Protokolle werden unter `.noteevolution/logs/` mit täglicher Rotation geschrieben.

## 10. Tests

- **Core, test-first:**
  - Round-Trip: Einlesen und Zurückschreiben ohne Änderung ergibt eine byte-identische Datei.
  - Gezielte Änderungen verändern nur die betroffenen Zeilen.
  - Übernehmen, Teilen, Löschen, Quelle entfernen, Verknüpfungsprüfung, beide Assistenten, `pending.json`, Konflikterkennung — jeweils in temporären Test-Vaults.
- **Testdaten:** künstlich erzeugte Dateien, die den Aufbau der echten Beispiele nachbilden (Tabs und Leerzeichen gemischt, `*`- und `-`-Anstriche, `collapsed::`, Seiten-Eigenschaften, tiefe Verschachtelung, `[handled]`, Fett, englische und deutsche Zeilen). Keine persönlichen Texte im Repository.
- **AI:** Ersatz-Embedder mit festen Vektoren für deterministische Tests von Rangfolge, Filtern und Fusion; ein optionaler Integrationstest mit dem echten Modell (eigene Testkategorie).
- **Pdf:** Erzeugen und mit PdfPig wieder auslesen; geprüft werden Überschriften, Reihenfolge, das Fehlen von `#notiz` und Eigenschaften sowie die Seitenzahl im Inhaltsverzeichnis.
- **UI:** bUnit für Gliederung, Notizkarte und Filter; die Editor-Interop wird über eine Ersatzschicht getestet.

## 11. Stufen

| Stufe | Inhalt |
|---|---|
| 1 | Core (Format, Modell, Verknüpfungen, Dateiüberwachung, Sicherungen), Desktop-Oberfläche mit Abschnitts- und Manuskriptansicht, Übernehmen, Markierungen, Verknüpfungsprüfung, Volltextsuche mit Filtern, beide Assistenten, einfaches PDF |
| 2 | Lokale KI: Relevant-Reiter, Bedeutungssuche, Wohin damit?, Fundstellen-Vorschläge für `[handled]` |
| 3 | Optionales Sprachmodell (Ollama/Claude) |
| 4 | Druck-PDF |
| 5 | Handy-App (MAUI Blazor Hybrid) |

Jede Stufe erhält einen eigenen Umsetzungsplan.

## 12. Bewusst nicht enthalten

- KI-gestütztes Schreiben oder Umformulieren von Buchtext.
- Zusammenarbeit oder Mehrbenutzerbetrieb.
- Eigene Synchronisation (übernimmt Obsidian).
- Bearbeiten von Notizinhalten in NoteEvolution; Notizen werden nur gelesen und um Eigenschaftszeilen ergänzt.
