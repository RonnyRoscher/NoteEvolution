# NoteEvolution – Blockstatus, Übernehmen-Knöpfe, ruhige Relevant-Liste

Stand: 2026-10-12 · Status: Entwurf · Baut auf Paket A (`2026-10-08-manuskript-design.md`) und Paket B (`2026-10-09-bereich-design.md`) auf. Ersetzt die Behandlung von `#notiz` als eigenes Konzept (Design-Spezifikation vom 2026-10-05, 3.2 und 7) durch den Status „Bemerkung“.

## 1. Ziel

Jeder Block im Buch bekommt einen Arbeitsstand. Er steuert die Darstellung im Buch und welche Blöcke ins PDF kommen.

Dazu kommen zwei Vereinfachungen beim Übernehmen:
- Die drei Arten zu übernehmen werden eigene Knöpfe.
- Die Relevant-Liste bleibt ruhig, solange man mit „Wohin damit?“ Zielorte prüft.

### Erfolgskriterien

- **Status:** Jeder Textblock und jedes Detail hat einen von fünf Stati: Entwurf, Relevant, Bemerkung, Formulierung, Fertig.
  - Er lässt sich in der Leiste am aktuellen Block ändern, und jede Änderung ist rückgängig zu machen.
  - Er bleibt in der Logseq-Datei erhalten.
- **Darstellung:** Relevant ist grau, Bemerkung hat einen beigen Hintergrund. Formulierung und Fertig tragen eine farbige Randmarke.
- **PDF:** Blöcke mit Relevant oder Bemerkung fehlen samt ihren Details.
- **Übernehmen:** Übernommene Notiztexte, die als neuer Block oder neues Detail entstehen, haben den Status Relevant.
- **„Bemerkung anfügen“:** legt am aktuellen Textblock ein neues Detail mit Status Bemerkung an.
- **Knöpfe:** An Notizen stehen die Übernehmen-Knöpfe nebeneinander, an Unteranstrichen untereinander.
- **Relevant-Liste:** Solange in „Relevant“ eine „Wohin damit?“-Liste offen ist, ändert sich die Relevant-Liste nicht.
- **Wie bisher:** Logseq und Obsidian zeigen alle Dateien korrekt an. Nur bewusst geänderte Blöcke ändern sich.

## 2. Status

### Stati und Speicherung

| Status | In der Datei | Darstellung im Buch | PDF |
|---|---|---|---|
| Entwurf | keine Zeile (Standard) | normal | ja |
| Relevant | `status:: relevant` | grauer Text | nein |
| Bemerkung | `status:: bemerkung` oder (bestehend) das Tag `#notiz` | beiger Hintergrund hinter dem Text; im dunklen Design gedämpftes Braun | nein |
| Formulierung | `status:: formulierung` | normaler Text, gelber Strich am rechten Rand | ja |
| Fertig | `status:: fertig` | normaler Text, grüner Strich am rechten Rand | ja |

- **Gilt für:** jeden Textblock und jedes Detail. Überschriften haben keinen Status.
- **Lesen:**
  - Ein unbekannter oder leerer `status::`-Wert gilt als Entwurf, und die Zeile bleibt unverändert in der Datei.
  - Groß- und Kleinschreibung spielt keine Rolle.
  - Ein Block mit `#notiz` gilt als Bemerkung. Hat er zusätzlich eine `status::`-Zeile, gewinnt die Zeile.
- **Schreiben:**
  - Ein Statuswechsel setzt oder entfernt nur die `status::`-Zeile, bei Entwurf wird sie entfernt.
  - Wird ein Block mit `#notiz` auf einen anderen Status als Bemerkung gesetzt, wird das Tag entfernt.
  - Wird ein Block mit `#notiz` auf Bemerkung gesetzt, bleibt die Datei unverändert.
  - Alles andere am Block bleibt unverändert (verlustfreies Schreiben).

### Ändern

- **Auswahl in der Leiste:** In der Leiste am aktuellen Block steht eine Auswahl mit den fünf Stati. Sie zeigt den Status des Elements am Cursor und ändert ihn.
  - Auf einer Überschrift ist die Auswahl ausgegraut.
  - Bei einem offenen Konflikt oder einer schreibgeschützten Datei ist sie gesperrt (R26), wie die anderen Befehle.
- **Tastenkürzel:** Strg+Umschalt+N schaltet das Element am Cursor zwischen Bemerkung und Entwurf um. Ein Textblock oder Detail mit anderem Status wird zu Bemerkung.
  - Heute schaltet das Kürzel das Tag `#notiz` an allen markierten Unteranstrichen um. Künftig ist es ein Befehl auf das Element am Cursor.
- **Ablauf:** Jede Änderung ist ein Befehl wie die Strukturbefehle aus Paket A.
  - Erst wird der Editor gespeichert, dann werden Konflikt und Schreibschutz geprüft.
  - Danach wird die Datei geschrieben, und es entsteht ein Eintrag für „Rückgängig“.
  - „Rückgängig“ lehnt ab, wenn die Datei sich seither geändert hat.

### Übernommene Texte

- **Neuer Textblock:** Übernehmen „Danach“ an einem Textblock sowie jede Variante an einer Überschrift legen einen neuen Textblock an. Er bekommt `status:: relevant`.
- **Neue Details:** Übernehmen „Danach“ an einem Detail und „Darunter“ an einem Textblock oder Detail legen neue Details an. Das oberste neue Detail bekommt `status:: relevant`, seine mitkopierten Unteranstriche bekommen keine eigene Zeile.
- **„Am Cursor“:** Der Text wird in einen bestehenden Block eingefügt; dessen Status bleibt. Die Unteranstriche der Notiz werden neue Details. Jeder Unteranstrich der obersten Ebene bekommt `status:: relevant`, tiefere keine eigene Zeile.
- **Ziehen und Ablegen:** Ein durch Ziehen übernommener neuer Block bekommt ebenfalls `status:: relevant`.
- **Notiz-Tags:** Hat die Notiz selbst `#notiz`, wird das Tag nicht kopiert; Eigenschaftszeilen werden schon heute nicht kopiert.

### „Bemerkung anfügen“

- **Wo:** ein neuer Knopf in der Leiste am aktuellen Block, aktiv bei einem Textblock oder Detail.
- **Was er tut:** Er legt am Ende der Details des Textblocks, zu dem das Element am Cursor gehört, ein leeres Detail der Tiefe 1 mit `status:: bemerkung` an. Der Cursor steht darin.
- **Rückgängig:** Wie bei den Strukturbefehlen, die neue Bemerkung verschwindet wieder.

### PDF

Blöcke mit Status Relevant oder Bemerkung werden nicht exportiert. Das gilt auch für alle Details unter ihnen, unabhängig von deren eigenem Status. Alle anderen werden wie bisher exportiert.

### Editor und Darstellung

- **Status im Editor:** Jeder Textblock und jedes Detail trägt seinen Status als Attribut (z. B. `data-status`). Der Status kommt aus dem Buch; im Editor tippen ändert ihn nicht.
- **Darstellung:** über CSS wie in der Tabelle. Die bisherige kursive, gedämpfte Darstellung von `#notiz` entfällt zugunsten der Bemerkung.
- **Speichern des Editortexts:** Die Status-Eigenschaften und `#notiz` bleiben erhalten. Ein im Editor neu entstandener Block oder ein neues Detail (Strg+Enter, Enter) hat Entwurf, auch wenn es aus einem Block mit anderem Status geteilt wurde.

## 3. Übernehmen-Knöpfe (ersetzt Paket A, Abschnitt 4, geteilter Knopf)

- **An der Notizkarte:** vier Knöpfe nebeneinander: „Am Cursor“, „Danach“, „Darunter“, „Wohin damit?“.
  - „Wohin damit?“ ist nur bei verfügbarer KI sichtbar, wie heute.
- **An jedem Unteranstrich:** dieselben vier Knöpfe untereinander, rechts neben dem Unteranstrich.
- **Übernommen:** Der Unteranstrich zeigt wie heute ✓, wenn er schon verwendet ist.
- **Entfällt:** Der Pfeil mit dem Menü.
- **Gesperrt:** wie bisher (`CanAdopt`).
- **Hinweise:** Die Knöpfe haben Tooltips, die die Variante erklären.

## 4. Relevant-Liste bleibt ruhig (ersetzt Paket B, 3, für den Reiter „Relevant“)

- **Eingefroren:** Solange im Reiter „Relevant“ mindestens eine „Wohin damit?“-Liste offen ist, berechnet der Reiter seine Liste nicht neu.
  - Das gilt auch dann nicht, wenn sich Cursor, Bereich, Filter oder Notizen ändern, etwa durch Sprünge zu Treffern oder durch Übernehmen.
- **Sichtbar bleibt:** Die angezeigten Karten bleiben in derselben Reihenfolge.
- **Danach:** Wird die letzte offene Liste geschlossen, rechnet der Reiter einmal mit dem aktuellen Stand neu.
- **Andere Reiter:** Journal und Suche verhalten sich wie bisher (Paket B, angeheftete Karten).

## 5. Fehlerbehandlung

- **Statuswechsel und „Bemerkung anfügen“:** wie die Strukturbefehle aus Paket A.
  - Ungesicherter Editortext oder ein Konflikt verhindert den Befehl.
  - Ein Schreibfehler lässt die Datei unverändert und zeigt einen Hinweis.
- **Unbekannter `status::`-Wert:** Er gilt als Entwurf, bleibt in der Datei und wird erst bei einem Statuswechsel ersetzt.

## 6. Tests

- **Core:**
  - Status lesen: Tabelle der Werte, `#notiz`, unbekannte Werte, Groß- und Kleinschreibung.
  - Status setzen: jede Kombination, mit `LineDiff`. Nur die `status::`-Zeile und gegebenenfalls `#notiz` ändern sich. Byteweises Rückgängig.
  - „Bemerkung anfügen“: Position, Eigenschaft, Rückgängig.
  - Übernehmen: `status:: relevant` an genau dem neuen Block bzw. obersten neuen Detail, für jede Variante. „Am Cursor“ lässt den Zielblock unverändert, außer dem eingefügten Text.
  - PDF-Inhalt: Relevant und Bemerkung samt Unterbaum fehlen, die anderen bleiben.
  - Editor-Abbild und Speichern: Der Status wird ins Editordokument übertragen. Das Speichern des Editortexts erhält Status-Zeilen und `#notiz`.
- **UI (bUnit):**
  - Auswahl in der Leiste: zeigt und ändert den Status, ist gesperrt auf Überschriften und bei einem Konflikt.
  - Strg+Umschalt+N über den Befehl.
  - „Bemerkung anfügen“.
  - Die vier Knöpfe an Notiz und Unteranstrich (Reihenfolge, Anordnung über eine CSS-Klasse).
  - Relevant-Liste eingefroren bei offener Trefferliste, auch nach Sprung und Übernehmen; Neuberechnung nach dem Schließen.
- **Manuelle Abnahme (README):**
  - Farben und Randmarken im hellen und dunklen Design.
  - Statusauswahl und Strg+Umschalt+N im echten Fenster.
  - Ein PDF ohne Relevant- und Bemerkung-Blöcke.
  - Wie Logseq `status::` anzeigt.

## 7. Nicht im Umfang

- Status für Überschriften
- Filtern oder Zählen nach Status, etwa in der Gliederung
- eigene Farben einstellen
- Status in der Notizspalte
