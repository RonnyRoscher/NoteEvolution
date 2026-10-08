# NoteEvolution – Manuskript als einzige Ansicht (Paket A)

Stand: 2026-10-08 · Status: Entwurf · Ändert Abschnitt 5.2 und Abschnitt 7 der Design-Spezifikation vom 2026-10-05 sowie 6.2 (Themenbereich)

Paket B baut darauf auf und ist nicht Teil dieses Dokuments. Es umfasst:
- den Bereich von „Relevant“ erweitern,
- „Wohin damit?“ für Unternotizen mit Vorschau und Übernehmen am Zielort,
- markierte Abschnitte zusammenfügen,
- „Unter neue Überschrift“.

## 1. Ziel

Das Buch wird nur noch in einer Ansicht bearbeitet: dem Manuskript, das immer das ganze Buch zeigt. Was heute nur die Abschnittsansicht oder die Gliederung kann, geht künftig direkt im Text:
- Abschnitte anlegen, einrücken und entfernen,
- den aktuellen Abschnitt erkennen,
- Notizen gezielt an eine Stelle übernehmen.

### Begriffe

- **Element:** ein Eintrag des Buchs. Es gibt drei Arten:
  - eine **Überschrift** (`#`, `##` …) mit allem, was zu ihr gehört,
  - ein **Textblock** (Anstrich ohne Rauten unter einer Überschrift oder im Vorspann),
  - ein **Detail** (Unteranstrich eines Textblocks in beliebiger Tiefe, im Editor ein eingerückter Absatz; im PDF Fließtext).
- **Aktueller Abschnitt:** das Element, in dem der Cursor steht.
- **Abschnitt einer Überschrift:** die Überschrift mit ihren eigenen Textblöcken und allen Unterabschnitten.

### Erfolgskriterien

- **Ansicht:** Der Editor zeigt immer das ganze Buch. Abschnittsansicht und Ansichtsumschalter gibt es nicht mehr.
- **Markierung:** Der aktuelle Abschnitt ist mit einem Kasten und einem Balken am linken Rand markiert und bestimmt „Relevant“.
- **Struktur:** Abschnitte lassen sich im Editor per Knopf und Tastenkürzel anlegen, einrücken, ausrücken, entfernen und löschen. Jede dieser Änderungen ist mit „Rückgängig“ zurückzunehmen.
- **Übernehmen:** Eine Notiz oder ein einzelner Unteranstrich lässt sich auf drei Arten übernehmen: am Cursor, danach, darunter.
- **Gliederung:** Die Gliederung links springt zur Überschrift und hebt den Abschnitt am Cursor hervor. Umsortieren per Ziehen bleibt.
- **Dateien:** Logseq und Obsidian zeigen alle geschriebenen Dateien weiterhin korrekt an. Nichts außerhalb der bewusst geänderten Blöcke ändert sich (wie bisher).

### Rahmenbedingungen

- Text wird weiter im Editor getippt und eine Sekunde nach der letzten Eingabe gespeichert.
- **Strukturänderungen sind Befehle (Ansatz 1):**
  - Sie ändern die Buchdatei über die Core-Logik (Gliederungs- und Verknüpfungsfunktionen, Sicherung, Schreiben).
  - Danach lädt der Editor das Buch neu und setzt den Cursor wieder an die passende Stelle.
  - Vor jedem Befehl wird der offene Editortext gespeichert. Gelingt das nicht, unterbleibt der Befehl, und ein Hinweis erscheint.
- **R26:** Während für die Buchdatei ein Konflikt offen ist, sind alle Befehle gesperrt, wie das Schreiben heute.
- **Verknüpfungen** hängen weiter am Textblock (`id::`, `source::`). Details tragen keine eigenen Verknüpfungen. Eine in ein Detail übernommene Notiz ist mit dem umgebenden Textblock verknüpft.

## 2. Eine Ansicht für das ganze Buch

### Editor

- Er zeigt immer das ganze Buch: den Vorspann und alle Überschriften mit ihrem Inhalt. Der Inhalt hängt nicht mehr davon ab, welcher Abschnitt in der Gliederung gewählt ist.
- Kopfleiste:
  - Die Umschalter „Abschnitt“ und „Manuskript“ entfallen.
  - Der Schalter „Quellen anzeigen“ und die Randmarken für Blöcke mit Quellen bleiben.
- `ViewMode`, `AppState.Mode` und alle Unterscheidungen danach entfallen.

### Aktueller Abschnitt und Markierung

- **Was er ist:** Der Editor meldet bei jeder Cursorbewegung das Element am Cursor: Überschrift, Textblock oder Detail (Textblock-Schlüssel plus Absatzposition).
  - Daraus ergeben sich der aktuelle Abschnitt und die Überschrift, zu der er gehört (`CurrentSectionKey`). Beides steht in `AppState`.
  - Ohne Cursor im Buch bleibt der zuletzt aktuelle Abschnitt gültig.
- **Markierung:** ein Kasten mit einem Balken am linken Rand um den aktuellen Abschnitt.
  - Bei einer Überschrift umfasst er ihren ganzen Abschnitt, bei einem Textblock den Block mit seinen Details, bei einem Detail das Detail mit seinen tieferen Details.
  - Die Abschnittsknöpfe (Abschnitt 3) sitzen am unteren Rand des Kastens. Reicht der Kasten über das Fenster hinaus, bleiben sie am unteren Fensterrand sichtbar.

### Themenbereich für „Relevant“ (ersetzt 6.2)

- Bei einem Textblock oder Detail: der Textblock am Cursor, seine beiden Nachbarn und der Überschriftenpfad (`EmbeddingText.ForCursor`, wie bisher im Manuskript).
- Bei einer Überschrift: der Abschnitt dieser Überschrift (Überschriftenpfad plus Mittelwert der Textblöcke, wie bisher in der Abschnittsansicht).
- Die Aktualisierung ist wie bisher entprellt (1,5 s), die erste Berechnung erfolgt sofort.

### Gliederung (links)

- Ein Klick auf einen Knoten scrollt den Editor zu dieser Überschrift und setzt den Cursor an ihren Anfang. Der Knoten wird damit zum aktuellen Abschnitt.
- Der Knoten des aktuellen Abschnitts ist hervorgehoben. Bei einem Textblock oder Detail ist es die Überschrift, zu der er gehört.
- Ziehen zum Umsortieren, Umbenennen per Doppelklick und „+“ für eine Unterüberschrift bleiben.

### Weitere Stellen

- **PDF-Export „Aktueller Abschnitt“:** der Abschnitt der Überschrift, zu der der aktuelle Abschnitt gehört. Im Vorspann gilt das ganze Buch.
- **Sprungziele:** Sie scrollen zu ihrem Ziel und setzen den Cursor dorthin, statt den Inhalt umzuschalten. Das gilt für „Wohin damit?“, den Klick auf ✓ an einer Notizkarte und Einträge der Verknüpfungsprüfung.

## 3. Struktur ändern

Die Knöpfe sitzen am unteren Rand der Markierung; jeder Befehl hat ein Tastenkürzel. Nach jedem Befehl steht der Cursor im neuen bzw. verschobenen Element. Jeder Befehl ist ein Eintrag für „Rückgängig“ in der Kopfleiste.

| Befehl | Kürzel | Überschrift (Ebene n) | Textblock | Detail (Tiefe d) |
|---|---|---|---|---|
| **Neuer Abschnitt danach** | Alt+Enter | neue leere Überschrift der Ebene n nach dem ganzen Abschnitt | neuer leerer Textblock direkt danach | neues leeres Detail der Tiefe d direkt nach dem Detail und seinen tieferen Details |
| **Neuer Unterabschnitt** | Alt+Umschalt+Enter | neue leere Überschrift der Ebene n+1 als erster Unterabschnitt, also nach den eigenen Textblöcken und vor bestehenden Unterabschnitten | neues leeres Detail der Tiefe 1 als erstes Detail | neues leeres Detail der Tiefe d+1 direkt unter dem Detail, als erstes |
| **Einrücken** | Tab in der Überschriftenzeile | eine Ebene tiefer samt Unterabschnitten; wird letzter Unterabschnitt der vorherigen Überschrift gleicher Ebene; ohne eine solche nicht möglich | (wie bisher) | (wie bisher) |
| **Ausrücken** | Umschalt+Tab in der Überschriftenzeile | eine Ebene höher samt Unterabschnitten; steht danach direkt hinter dem Abschnitt der bisherigen Eltern-Überschrift; auf Ebene 1 nicht möglich | (wie bisher) | (wie bisher) |
| **Überschrift entfernen** | Rücktaste am Anfang des Überschriftentitels | Die Überschrift verschwindet. Ihre Textblöcke und Unterabschnitte rücken an derselben Stelle in den umgebenden Abschnitt; Unterüberschriften werden eine Ebene höher. Kein Text geht verloren. | – | – |
| **Abschnitt löschen** | (nur Knopf) | Überschrift samt ganzem Abschnitt | Textblock | Detail samt tieferen Details |

Die Zeile „wie bisher“ bedeutet: Tab und Umschalt+Tab ändern in Textblöcken die Tiefe des Absatzes, wie heute.

### Regeln für alle Befehle

- **Neue Überschriften** sind leer. Der Cursor steht in ihrem Titel. Eine leere Überschrift ist erlaubt, bis ein Titel vergeben ist; die Gliederung zeigt sie mit einem Platzhalter.
- **Löschen von Textblöcken** (auch innerhalb einer gelöschten Überschrift) entfernt wie bisher die zugehörigen `used-in::`-Einträge der Notizen (5.3). Rückgängig stellt Blöcke und Einträge wieder her.
- **Ebenen** ändern sich nur für die betroffenen Überschriften. Ihre `#`-Anzahl wird entsprechend angepasst.
- **Logseq-Format:** Die Einrückung der Anstriche in der Datei folgt der neuen Struktur. Alles andere bleibt unverändert, auch `collapsed::` und Eigenschaften.
- **Nicht möglich:** Ein nicht möglicher Befehl ist ausgegraut und hat keine Wirkung. Dazu gehört Einrücken ohne vorherige Überschrift gleicher Ebene, Ausrücken auf Ebene 1 oder ein offener Konflikt.

## 4. Übernehmen auf drei Arten (ersetzt 5.2, Schritt 1)

An der Notizkarte und an jedem Unteranstrich wird „Übernehmen“ zu einem geteilten Knopf: Ein Klick übernimmt am Cursor, der Pfeil daneben öffnet ein Menü mit allen drei Varianten.

| Variante | Überschrift | Textblock | Detail (Tiefe d) |
|---|---|---|---|
| **Am Cursor** | wie „Danach“ | Text direkt an der Cursorstelle im selben Block. Steht der Cursor mitten im Absatz, rückt der Rest des Absatzes hinter den eingefügten Text. Unteranstriche der Notiz werden Details eine Ebene tiefer. | ebenso, an der Cursorstelle im Detail |
| **Danach** | als erster Textblock direkt unter der Überschrift | neuer Textblock direkt danach | neues Detail der Tiefe d direkt danach (nach seinen tieferen Details) |
| **Darunter** | wie „Danach“ | als erste Details (Tiefe 1) des Textblocks | als erste Details der Tiefe d+1 unter dem Detail |

### Regeln beim Übernehmen

- **Text:** Übernommen werden der Text der Notiz bzw. des Unteranstrichs und seine Unteranstriche, ohne Eigenschaftszeilen.
- **Verknüpfung:**
  - Ein neuer Textblock (Variante „Danach“ bei Textblock oder Überschrift) bekommt `id::` und `source::`, wie heute.
  - In allen anderen Fällen bekommt der umgebende Textblock die Notiz als weitere Quelle (`source::`, bei Bedarf `id::`).
  - Die Notiz bekommt `used-in::` mit dem Verweis auf diesen Textblock.
  - Reihenfolge, Ausstehend-Vermerk und Rückgängig bleiben wie heute (5.2, Schritte 3 bis 5).
- **Ohne Cursor im Buch:** Es gilt der zuletzt aktuelle Abschnitt. Gab es keinen, wird die Notiz als neuer Textblock am Ende des Buchs eingefügt.
- **Ziehen und Ablegen:** bleibt wie heute, also neuer Textblock an der Ablagestelle.
- **Sperren:** Bei offenem Konflikt oder schreibgeschützter Buchdatei ist „Übernehmen“ gesperrt, wie heute.

## 5. Fehlerbehandlung

- **Ungesicherter Editortext:** Lässt sich der offene Editortext vor einem Befehl oder einem Übernehmen nicht speichern, unterbleibt die Aktion. Der Editor zeigt den Hinweis, dass der Text nicht gespeichert werden konnte, und nichts wird verworfen.
- **Fehler beim Schreiben:**
  - Ist die Buchdatei gesperrt oder schreibgeschützt, bleibt die Datei unverändert. Der Hinweis erscheint im Editor, und der Editor zeigt wieder den Stand der Datei.
  - Wie bisher wird vorher eine Sicherung angelegt.
- **Externe Änderung zwischen Lesen und Schreiben:** Es gilt die bestehende Konfliktbehandlung (5.7).
- **Unbekanntes Element am Cursor** (etwa ein Block, der nach einem Neuladen fehlt): Der aktuelle Abschnitt fällt auf die nächstliegende Überschrift zurück, sonst auf den Anfang des Buchs.

## 6. Tests

- **Core:** Für jeden Strukturbefehl und jede Art von Element (Abschnitt 3):
  - Die erzeugte Buchdatei entspricht Zeile für Zeile der Erwartung. Unbeteiligte Zeilen bleiben byteweise gleich (`LineDiff`).
  - Rückgängig stellt die Datei wieder her.
  - Randfälle: Vorspann, erste und letzte Überschrift, leere Überschrift, Überschrift ohne vorherige Überschrift gleicher Ebene, Ebene 1 ausrücken, Überschrift mit eigenem Text und Unterabschnitten entfernen.
- **Core:** Für jede Übernehmen-Variante und jede Art von Element (Abschnitt 4):
  - Text, Position, `source::`/`id::` am richtigen Textblock, `used-in::` an der Notiz.
  - Rückgängig, Unteranstriche der Notiz, Übernehmen eines einzelnen Unteranstrichs, Cursor mitten im Absatz.
- **Editor-Zuordnung:** Das ganze Buch wird in das Editordokument und zurück übertragen. Die Cursormeldung ergibt das richtige Element samt Überschrift.
- **bUnit:**
  - kein Ansichtsumschalter mehr
  - Markierung und Knöpfe folgen dem Cursor
  - Gliederungsklick setzt den aktuellen Abschnitt; Hervorhebung in der Gliederung
  - „Relevant“ nutzt den richtigen Themenbereich für Überschrift bzw. Textblock
  - Befehle gesperrt bei Konflikt; Befehl unterbleibt bei ungesichertem Text
  - geteilter Übernehmen-Knopf mit drei Varianten
  - PDF „Aktueller Abschnitt“
- **Manuelle Abnahme (README):** Die Tastenkürzel im echten Fenster, die Markierung beim Scrollen und ein großes Buch (mehrere hundert Blöcke) mit flüssigem Tippen und Scrollen.

## 7. Nicht im Umfang (Paket B oder später)

- Bereich von „Relevant“ erweitern
- „Wohin damit?“ für Unternotizen, Vorschau, Übernehmen am Zielort
- markierte Abschnitte zusammenfügen
- „Unter neue Überschrift“
- Textblöcke per Tab zu Details eines anderen Blocks machen
- Überschriften durch Tippen von `#` anlegen
