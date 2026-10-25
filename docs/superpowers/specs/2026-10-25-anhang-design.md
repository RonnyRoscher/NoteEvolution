# Anhang: Glossar, Menschen und Quellen — Entwurf

Stand 2026-10-25. Ziel: Am Buchende ein Glossar, eine Liste der Menschen, die inspiriert haben, und ein
Quellenverzeichnis führen. Im Buchtext wird dezent darauf verwiesen, im Editor wie im PDF.

## 1. Speicherung

### 1.1 Anhang-Abschnitte

- **Drei Überschriften der obersten Ebene am Buchende**, erkannt an einer Eigenschaft, nicht am Titel. Wer die
  Überschrift umbenennt, behält den Abschnitt.

  | Abschnitt | Eigenschaft | Standardtitel |
  | --- | --- | --- |
  | Glossar | `anhang:: glossar` | „Glossar“ |
  | Menschen | `anhang:: menschen` | „Menschen, die mich inspiriert haben“ |
  | Quellen | `anhang:: quellen` | „Quellen“ |

- **Anlage:** Ein Abschnitt entsteht mit seinem ersten Eintrag. Danach bleibt er hinter allen anderen Überschriften
  der obersten Ebene, in der Reihenfolge Glossar, Menschen, Quellen.
- **Neue Abschnitte:** Ein neuer Abschnitt im Buch, etwa über „Neuer Abschnitt danach“ oder „Als Überschrift“,
  wird vor dem Anhang eingefügt.
- **Doppelte Eigenschaft:** Kommt eine `anhang::`-Art doppelt vor, gilt der erste Abschnitt. Die
  Verknüpfungsprüfung meldet den zweiten.
- **Abschnitt ohne Einträge:** Er bleibt stehen. Im PDF erscheint er nicht.

### 1.2 Einträge

- **Form:** Jeder Eintrag ist ein direktes Kind seines Abschnitts mit eigener `id::`, angelegt wie bei
  Querverweisen. Erste Zeile und Folgezeilen des Blocks sind Text, die Eigenschaften stehen als Logseq-Eigenschaften
  darunter.
- **Glossar:**
  - Erste Zeile: der Begriff.
  - Folgezeilen: die Erklärung.
  - `formen::`: weitere Wortformen, durch Komma getrennt (z. B. `achtsam, Achtsamkeitsübungen`).
- **Menschen:**
  - Erste Zeile: der Name.
  - Folgezeilen: die Kurzbeschreibung.
  - `kuerzel::`: im Buch eindeutig, ohne Rücksicht auf Groß- und Kleinschreibung, 1–6 Zeichen (Buchstaben,
    Ziffern, Punkt, Bindestrich).
  - `themen::`: Themenbereiche, durch Komma getrennt.
- **Quellen:**
  - Erste Zeile: der Titel.
  - `art::`: `buch`, `video`, `artikel`, `webseite` oder `sonstiges`.
  - `autoren::`: Blockverweise `((id))` auf Menschen-Einträge, durch Komma getrennt, in der angegebenen Reihenfolge.
  - `jahr::`, `url::` und `herausgeber::` (Verlag, Kanal, Zeitschrift), alle drei optional.
- **Unbekannte Eigenschaften** bleiben unverändert erhalten.

### 1.3 Abgrenzung zum übrigen Buch

Die Anhang-Abschnitte zählen nicht zum Buchtext:
- keine Wortzahl in Gliederung und Buchzeile;
- nicht in „Zuletzt geändert“;
- nicht im Reiter „Relevant“, nicht in „Wohin damit?“ und nicht in der Abdeckung;
- keine Statusanzeige;
- nicht als Ziel in „Verweis einfügen“.

Der Editor zeigt sie nicht. Die Gliederung zeigt statt der drei Überschriften unten eine Zeile
**„Anhang: Glossar · Menschen · Quellen“**. Jedes Wort darin öffnet das Fenster seiner Liste, auch wenn der
Abschnitt noch nicht existiert.

## 2. Fenster „Glossar“, „Menschen“, „Quellen“

- **Aufbau:** Links steht die Liste der Einträge, alphabetisch; Quellen stehen nach ihrer Nummer, nicht zitierte am
  Ende. Darüber ist ein Suchfeld. Rechts stehen die Felder des gewählten Eintrags. Dazu gibt es die Knöpfe „Neu“
  und „Löschen“.
- **Speichern:** beim Wechsel des Eintrags und beim Schließen. Bei einem Fehler bleibt das Fenster offen und zeigt
  die Meldung; die Eingaben gehen nicht verloren.
- **Prüfung beim Speichern:**
  - Ein Begriff, ein Name oder ein Titel darf nicht leer sein.
  - Kürzel müssen eindeutig sein.
  - Für eine neue Person schlägt das Fenster ein Kürzel vor: Initialen aus dem Namen, bei einer Kollision mit
    angehängter Ziffer.
- **Autoren einer Quelle:**
  - Das Feld nimmt Namen mit Vorschlägen aus der Menschen-Liste.
  - Ein Name, der in der Liste nicht vorkommt, wird beim Speichern als Mensch angelegt, mit vorgeschlagenem Kürzel
    und leeren Themen und Beschreibung.
  - Ein bekannter Name, ohne Rücksicht auf Groß- und Kleinschreibung, verweist auf den vorhandenen Eintrag.
- **Löschen eines Eintrags, auf den verwiesen wird:** Gemeint sind Menschen im Text, Menschen als Autor und Quellen
  im Text. Das Fenster fragt nach: „N Verweise im Buch werden zu ‚Verweis fehlt‘.“ Nach dem Löschen behandelt die
  Verknüpfungsprüfung diese Verweise wie Querverweise ohne Ziel.
- **Rückgängig:** Jede gespeicherte Änderung lässt sich über „Rückgängig“ der Kopfzeile zurücknehmen, wie die
  übrigen Befehle am Buch.

## 3. Verweise im Buchtext

### 3.1 Menschen

- **Einfügen:** Der neue Knopf „Mensch einfügen“ in der Leiste öffnet eine Auswahl wie „Verweis einfügen“: Suche
  über Name und Kürzel, Pfeiltasten, Enter, dazu „Neu anlegen …“ (öffnet das Fenster „Menschen“ mit einem neuen
  Eintrag; nach dem Speichern wird der Verweis eingefügt).
- **In der Datei** steht ein Blockverweis `((id))` auf den Eintrag. Logseq zeigt ihn mit dem Namen.
- **Im Editor:** ein Inline-Element mit dem Kürzel, klein und hochgestellt.
  - Tooltip: Name und Themen.
  - Ein Klick öffnet das Fenster „Menschen“ beim Eintrag.
  - Ein Verweis ohne Ziel zeigt „Verweis fehlt“ in Rot, wie bei Querverweisen.
- **Abgrenzung zu Querverweisen:** `CrossRefIndex` behandelt einen Blockverweis auf einen Anhang-Eintrag nicht als
  Querverweis. Ein solcher Verweis hat kein „Verweise hierher“ und bekommt keine Querverweis-Beschriftung.

### 3.2 Quellen

- **Einfügen:** Der neue Knopf „Quelle einfügen“ öffnet eine Auswahl wie oben, mit „Neu anlegen …“, und ein
  optionales Feld „Fundstelle“ (z. B. „S. 12“, „Min. 14:30“).
- **In der Datei:** `{{quelle ((id))}}`, mit Fundstelle `{{quelle ((id)) S. 12}}`. Logseq zeigt das als Makro an.
- **Nummerierung:**
  - Eine Quelle bekommt ihre Nummer beim ersten Zitat in der Lesereihenfolge des PDFs: Überschriften und Textblöcke
    in Buchreihenfolge, ohne Blöcke mit Status Relevant oder Bemerkung und ohne den Anhang.
  - Jedes weitere Zitat derselben Quelle trägt dieselbe Nummer.
  - Nicht zitierte Quellen haben keine Nummer.
- **Im Editor:** ein Inline-Element „[3]“, hochgestellt, mit Fundstelle „[3, S. 12]“.
  - Tooltip: die vollständige Angabe.
  - Ein Klick öffnet das Fenster „Quellen“ beim Eintrag.
  - Nach jedem Speichern werden die Nummern neu vergeben; derselbe Weg schiebt heute die
    Querverweis-Beschriftungen nach.
  - Ein Zitat in einem Block mit Status Relevant oder Bemerkung zeigt die Nummer der Quelle, falls sie anderswo eine
    hat, sonst „[–]“.
- **Bearbeiten:** Die Fundstelle eines vorhandenen Zitats ändert man mit einem Doppelklick auf das Element.

### 3.3 Glossar

- **Erkennung:** Nichts im Text wird verändert, die App erkennt die Stelle selbst. Pro Begriff wird genau eine
  Stelle markiert: das erste Vorkommen des Begriffs oder einer seiner Formen.
  - Es zählt die Lesereihenfolge des PDFs (siehe 3.2) und nur Textblöcke, keine Überschriften.
  - Treffer sind ganze Wörter, ohne Rücksicht auf Groß- und Kleinschreibung.
  - Die längste Form hat Vorrang, wenn sich Formen überlappen.
  - Text in Querverweis-, Menschen- und Quellenelementen zählt nicht.
- **Im Editor:** eine gepunktete Unterstreichung als Markierung (Decoration), ohne Änderung am Dokument.
  - Tooltip: Begriff und Erklärung.
  - Ein Klick mit Strg öffnet das Fenster „Glossar“ beim Eintrag.
  - Die Markierungen werden nach jedem Speichern und nach jeder Änderung am Glossar neu berechnet, wie die Nummern.
- **Verschieben** von Text verschiebt die Markierung von selbst, weil sie jedes Mal neu bestimmt wird.

## 4. PDF

- **Im Text:**
  - Menschen erscheinen als hochgestelltes Kürzel.
  - Quellen erscheinen als hochgestellte Nummer, bei einer Fundstelle als „³ S. 12“. Die Fundstelle steht in
    normaler Größe.
  - Das erste Vorkommen eines Glossarbegriffs ist gepunktet unterstrichen.
  - Alle drei verlinken innerhalb des PDFs auf ihren Eintrag im Anhang.
- **Anhang** nach dem Buchtext. Jeder Teil beginnt mit eigener Überschrift auf neuer Seite und steht im
  Inhaltsverzeichnis.
  - **Glossar:** alphabetisch, Begriff fett, Erklärung dahinter. Nur Begriffe, die im Text vorkommen.
  - **Menschen, die mich inspiriert haben:** alphabetisch nach Name, dann „(Kürzel)“, die Themen kursiv und die
    Beschreibung.
  - **Quellen:** nach Nummer, nur zitierte Quellen. Format: „[3] Autor; Autor: *Titel*. Herausgeber, Jahr. URL“.
    Fehlende Teile entfallen samt Trennzeichen. Autorennamen verlinken auf ihren Eintrag bei den Menschen.
- **Teilexport:** Beim Export „Aktueller Abschnitt“ enthält der Anhang nur, was in diesem Abschnitt verwendet wird.
  Die Nummern gelten für das ganze Buch.

## 5. Verknüpfungsprüfung

Neue Gruppen:
- „Verweise auf fehlende Einträge“: Menschen und Quellen. Sie werden nach Rückfrage entfernt, wie Querverweise ohne
  Ziel.
- „Doppelte Anhang-Abschnitte“.
- „Doppelte Kürzel“, etwa nach einer Bearbeitung in Logseq.

## 6. Umsetzung (Bausteine)

- **Core, `Books/Appendix.cs`:**
  - erkennt die Abschnitte und liest und schreibt die Einträge (`GlossaryEntry`, `PersonEntry`, `SourceEntry`);
  - legt Abschnitte an, hält sie am Ende, schlägt Kürzel vor.
- **Core, `Books/AppendixIndex.cs`:**
  - Lesereihenfolge des PDF-Texts, Quellennummern, Glossar-Treffer (Block, Absatz, Offset und Länge);
  - Verweiszähler je Eintrag, fehlende Ziele.
- **Core, `Books/SourceCitations.cs`:** Parsen und Formatieren von `{{quelle ((id)) Fundstelle}}`, wie `BlockRefs`.
- **Ausschluss des Anhangs:** in Wortzahl, `RecentChanges`, `BookElements` für Relevant, Abdeckung und Status,
  `ReferencePicker`, `CrossRefIndex` und `UnderstandingItems`.
- **UI:**
  - drei Dialoge (`GlossaryDialog`, `PeopleDialog`, `SourcesDialog`) auf `Modal`;
  - die Anhang-Zeile in `OutlinePane`;
  - die Knöpfe „Mensch einfügen“ und „Quelle einfügen“ in `SectionBar`, mit Auswahl auf Basis von
    `ReferencePicker`.
- **Editor (`editor.js`):**
  - neue Inline-Atome `personRef` und `sourceCite`;
  - Glossar-Markierungen als ProseMirror-Decorations;
  - Beschriftungen und Nummern kommen über eine Interop-Methode wie `setRefLabels`;
  - `EditorDocMapper` bildet die neuen Knoten ab.
- **PDF:** `PdfContent` und `QuestPdfExporter` für die Darstellung im Text und den Anhang.
- **Rückgängig:** Dialog-Änderungen gehen über den bestehenden `StructureUndo` der Buchseite.

## 7. Tests

- **Core:**
  - Abschnitte anlegen und am Ende halten.
  - Einträge lesen und schreiben, mit Rundlauf und ohne Verlust unbekannter Eigenschaften.
  - Kürzel vorschlagen und Eindeutigkeit prüfen.
  - Quellennummern in Lesereihenfolge, ohne Relevant und Bemerkung, Wiederholung behält die Nummer.
  - Glossar: erstes Vorkommen, ganze Wörter, Formen, längste Form, nicht in Verweis-Elementen.
  - `{{quelle}}` parsen und formatieren.
  - Ausschluss aus Wortzahl, Relevant und Querverweisen.
- **UI (bUnit):**
  - Fenster: anlegen, bearbeiten, Rückfrage beim Löschen, Autoren automatisch anlegen, Rückgängig.
  - Einfüge-Knöpfe mit Auswahl und „Neu anlegen“.
  - Anhang-Zeile in der Gliederung.
  - Editor-Dokument mit den neuen Knoten (Fake-Interop).
- **Editor (jsdom):** Atome rendern, Decorations an der richtigen Stelle, Doppelklick auf die Fundstelle.
- **PDF:** Anhang-Reihenfolge, nur verwendete Einträge, Links vorhanden, Teilexport.

## 8. Nicht enthalten

- Ein buchübergreifendes Glossar oder eine buchübergreifende Menschen-Liste.
- Zitierstile wie APA oder Harvard.
- Fußnoten am Seitenende.
- BibTeX-Import.
- Glossar-Treffer in Überschriften.
- Mehrere Markierungen pro Begriff.
