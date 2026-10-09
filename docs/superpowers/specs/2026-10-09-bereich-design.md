# NoteEvolution – Bereich, Zielorte, Zusammenfügen (Paket B)

Stand: 2026-10-09 · Status: Entwurf · Baut auf Paket A auf (`2026-10-08-manuskript-design.md`) und ändert dessen Abschnitte 2 (Markierung, Quellen, Themenbereich) und 4 (Übernehmen) sowie 6.3 Punkt 3 der Design-Spezifikation vom 2026-10-05 („Wohin damit?“)

## 1. Ziel

Der markierte Bereich lässt sich vom Element am Cursor aus stufenweise erweitern und verkleinern. Daran richten sich aus:
- „Relevant“,
- die Quellenanzahl,
- zwei neue Strukturbefehle: Zusammenfügen und „Unter neue Überschrift“.

„Wohin damit?“ findet für eine Notiz oder einen einzelnen Unteranstrich die passendsten Textblöcke. Ein Treffer lässt sich im Buch im Zusammenhang prüfen, bevor man dort übernimmt.

### Begriffe (zusätzlich zu Paket A)

- **Bereich:** der markierte Teil des Buchs. Er besteht aus einer **Ebene** und dem Element am Cursor:
  - **Ebene 0** (Standard): das Element am Cursor samt allem darunter. Bei einer Überschrift ist das ihr ganzer Abschnitt mit Unterabschnitten, bei einem Textblock der Block mit seinen Details, bei einem Detail das Detail mit seinen tieferen Details.
  - **Ebene k > 0:** das k-te übergeordnete Element samt allem darunter.
    - Für ein Detail kommen nacheinander das übergeordnete Detail, dann der Textblock, dann die Überschriften.
    - Für einen Textblock kommt seine Überschrift, dann deren Eltern-Überschrift.
    - Die oberste Stufe ist das ganze Buch (Wurzel).
  - **Ebene −1:** nur das Element am Cursor ohne Unterelemente:
    - eine Überschrift nur mit ihren eigenen Textblöcken, ohne Unterabschnitte;
    - ein Textblock nur mit seinem eigenen Text, ohne Details;
    - ein Detail nur mit seinem eigenen Text.
- **Bereichskopf:** das Element, das den Bereich bestimmt. Bei Ebene ≤ 0 ist es das Element am Cursor, sonst das k-te übergeordnete Element.

### Erfolgskriterien

- **Erweitern und verkleinern:** In der Leiste an der Markierung gibt es „Bereich ▲“ (höher) und „Bereich ▼“ (tiefer).
  - Der Kasten umfasst immer genau den Bereich, die Quellenanzahl gilt für ihn, und „Relevant“ bezieht sich auf ihn.
  - Wechselt der Cursor in ein anderes Element, springt der Bereich auf Ebene 0 zurück.
- **„Wohin damit?“:** an der Notiz und an jedem Unteranstrich.
  - Es zeigt die fünf passendsten Textblöcke mit Überschriftenpfad, Textauszug und Prozent.
  - Ein Klick auf einen Treffer springt dorthin, ohne die Liste zu schließen.
  - Übernommen wird dort mit „Am Cursor“, „Danach“ oder „Darunter“.
- **„Zusammenfügen“:** fügt die Textblöcke im Bereich je Überschrift zu einem Block zusammen. Alle Verweise zeigen danach auf den verbleibenden Block. Der Befehl ist rückgängig zu machen.
- **„Unter neue Überschrift“:** setzt den Bereich unter eine neue, leere Überschrift. Der Cursor steht im neuen Titel. Der Befehl ist rückgängig zu machen.
- **Wie bisher:** Logseq und Obsidian zeigen alle geschriebenen Dateien korrekt an. Nichts außerhalb der bewusst geänderten Blöcke ändert sich.

### Rahmenbedingungen

- **Wer den Bereich bestimmt:** Die App bestimmt die Elemente des Bereichs aus der Gliederung und gibt sie an den Editor. Der Editor zeichnet nur den Kasten (Ansatz 1).
  - Das ersetzt die Berechnung des Kastens anhand der `#`-Anzahl aus Paket A.
  - Kasten und Befehle stimmen damit auch bei unregelmäßigen Überschriftenebenen überein.
  - Die Sperre der Strukturbefehle für unregelmäßige Abschnitte (Paket A, F3) entfällt deshalb.
- **Was beim Element am Cursor bleibt:** Die Befehle aus Paket A wirken weiterhin auf das Element am Cursor, nicht auf den Bereich: neu danach, neu darunter, ein- und ausrücken, Überschrift entfernen und löschen.
- **Was am Bereich hängt:** Zusammenfügen und „Unter neue Überschrift“ wirken auf den Bereich.
- **Wie in Paket A:** R26, Speichern vor jedem Befehl, Rückgängig je Befehl, Verknüpfungen am Textblock, Texte nur in `Strings.resx`.

## 2. Bereich

### Bedienung

- **Knöpfe:** In der Leiste stehen „Bereich ▲“ und „Bereich ▼“.
  - ▲ ist gesperrt, wenn der Bereich schon das ganze Buch ist.
  - ▼ ist auf Ebene −1 gesperrt.
- **Anzeige:** Neben den Knöpfen steht der Bereichskopf, zum Beispiel „Bereich: ## Vertrauen“, „Bereich: Textblock“ oder „Bereich: ganzes Buch“.
- **Tastenkürzel:** Alt+Pfeil hoch und Alt+Pfeil runter haben dieselbe Wirkung.
- **Zurücksetzen:** Wechselt der Cursor in ein anderes Element oder lädt der Editor ein anderes Buch, gilt wieder Ebene 0. Tippen im selben Element lässt den Bereich unverändert.

### Inhalt des Bereichs

Die App liefert den Bereich als **Liste der Editor-Knoten**: die Schlüssel der Überschriften, der Textblöcke und gegebenenfalls der Details. Grundlage ist der Baum der Gliederung (`Book`), nicht die `#`-Anzahl.

Der Editor markiert genau diese Knoten. Die Leiste sitzt am unteren Rand des letzten markierten Knotens. Liegen markierte Knoten nicht zusammenhängend im Dokument, zeichnet der Editor einen Kasten je zusammenhängendem Stück; die Leiste hängt am letzten. Das betrifft etwa Textblöcke einer Überschrift, die in der Datei nach ihren Unterabschnitten stehen.

### Themenbereich für „Relevant“ (ersetzt Paket A, Abschnitt 2)

- **Textblock oder Detail auf Ebene 0:** wie in Paket A, also der Textblock am Cursor, seine beiden Nachbarn und der Überschriftenpfad.
- **Überschrift auf Ebene 0** und **jeder Bereich auf Ebene ≥ 1:** Überschriftenpfad des Bereichskopfs plus Mittelwert aller Textblöcke im Bereich, Unterabschnitte eingeschlossen, normiert.
  - Ist der Bereichskopf ein Textblock oder Detail, gilt der Überschriftenpfad seiner Überschrift.
  - Hat der Bereich keine Textblöcke, gilt nur der Überschriftenpfad.
- **Ebene −1:**
  - Überschrift: wie bisher in Paket A, also nur ihre eigenen Textblöcke.
  - Textblock oder Detail: nur dessen eigener Text (`EmbeddingText.ForTextBlock` bzw. der Text des Details) plus Überschriftenpfad.
- **Aktualisierung:** wie bisher entprellt, 1,5 s nach einer Änderung des Bereichs.

### Quellen

Die Quellenanzahl und die aufklappbare Liste aus Paket A gelten für den Bereich. Gezählt werden verschiedene Notizen aller Textblöcke im Bereich. Auf Ebene −1 einer Überschrift zählen nur ihre eigenen Textblöcke.

## 3. „Wohin damit?“ (ersetzt 6.3 Punkt 3)

- **Wo:** Der Knopf „Wohin damit?“ steht an der Notizkarte und an jedem Unteranstrich.
- **Was gesucht wird:**
  - für die Notiz: ihr Text wie bisher (`EmbeddingText.ForNote`);
  - für einen Unteranstrich: sein Text samt seinen Unteranstrichen, als `passage:`.
- **Ergebnis:** die fünf ähnlichsten Textblöcke des aktuellen Buchs (Kosinus).
  - Jeder Treffer zeigt den Überschriftenpfad, einen Textauszug (erste Zeile, wie bei den Fundstellen im `[handled]`-Assistenten) und die Trefferquote in Prozent.
  - Ohne verfügbare KI ist der Knopf ausgeblendet, wie bisher.
- **Sprung:** Ein Klick auf einen Treffer speichert den Editor und springt zum Textblock (wie ein Sprung aus Paket A). Der Cursor steht am Anfang des Blocks, die Markierung zeigt ihn.
  - Die Trefferliste bleibt offen. So lassen sich mehrere Treffer nacheinander ansehen.
  - Die Trefferliste ist an die Karte gebunden. Sie schließt nur über „Wohin damit?“ selbst oder wenn sich das Buch ändert (anderes Buch gewählt).
- **Übernehmen am Treffer:** über den geteilten Knopf aus Paket A, mit dem Treffer als Element am Cursor. Ein Klick übernimmt „Am Cursor“, also an den Anfang des Blocks; das Menü bietet „Danach“ und „Darunter“.
  - Eine übernommene Notiz verschwindet nicht aus der Liste.
  - Eine Notiz, die schon im Treffer-Block Quelle ist, zeigt am Treffer ein ✓.

## 4. Zusammenfügen

**Auslöser:** der Knopf „Zusammenfügen“ in der Leiste.

1. **Gruppen bilden:** Für jede Überschrift im Bereich, und für die Wurzel, falls der Bereich das ganze Buch ist, bilden ihre eigenen Textblöcke im Bereich eine Gruppe, in Dateireihenfolge. Gruppen mit weniger als zwei Textblöcken bleiben unverändert.
2. **Zusammenfügen je Gruppe:** Der erste Textblock bleibt mit seinem Schlüssel, seiner `id::` und seinen Eigenschaften.
   - Der Text jedes weiteren Blocks wird mit einer Leerzeile (`\n\n`) an den Text des ersten angehängt, als weiterer Absatz desselben Anstrichs.
   - Die Details der weiteren Blöcke werden in Reihenfolge an die Details des ersten angehängt.
   - Die weiteren Blöcke werden entfernt.
3. **Verweise:**
   - Der verbleibende Block bekommt die Vereinigung aller `source::`-Einträge der Gruppe, ohne Dopplungen und in Reihenfolge.
   - Jede Notiz, deren `used-in::` auf einen entfernten Block zeigte, zeigt danach auf den verbleibenden. Ein Eintrag, der dadurch doppelt würde, entfällt.
   - Reihenfolge der Schreibvorgänge wie bei allen Verknüpfungen: erst das Buch, dann die Notizen. Eine Notiz, die sich nicht schreiben lässt, kommt in `pending.json`.
4. **Wann der Befehl geht:** Er ist nur aktiv, wenn mindestens eine Gruppe zwei oder mehr Textblöcke hat. Auf Ebene −1 eines Textblocks oder Details ist er gesperrt.
5. **Rückgängig („Zusammenfügen“):**
   - Es stellt das Buch und die betroffenen Notizen byteweise wieder her.
   - Das gelingt, solange die Datei seit dem Befehl unverändert ist; sonst wird es abgelehnt (wie die Strukturbefehle in Paket A). Für die Notizen gilt dasselbe Verfahren wie beim Löschen: Was sich seither geändert hat, bekommt den Verweis wieder ergänzt, statt überschrieben zu werden.
6. **Danach:** Der Cursor steht am Anfang des ersten verbleibenden Blocks, der Bereich ist wieder auf Ebene 0.

## 5. „Unter neue Überschrift“

**Auslöser:** der Knopf „Unter neue Überschrift“ in der Leiste.

1. **Ebene der neuen Überschrift:** Sei S die Überschrift, zu der der Bereichskopf gehört: bei einer Überschrift deren Eltern-Überschrift, bei einem Textblock oder Detail die Überschrift seines Abschnitts, im Vorspann die Wurzel. Die neue Überschrift N bekommt die Ebene von S + 1 und einen leeren Titel.
2. **Was unter N kommt:**
   - **Bereichskopf ist eine Überschrift:** N steht an ihrer Stelle in S. Die Überschrift samt ganzem Abschnitt wird erster Inhalt von N und rückt mit allen enthaltenen Überschriften eine Ebene tiefer. Ebene 0 und −1 verhalten sich hier gleich.
   - **Bereichskopf ist ein Textblock:** N steht an seiner Stelle in S. Der Textblock und alle in der Datei folgenden eigenen Textblöcke von S werden Inhalt von N, denn im Manuskript gehören sie sichtbar zu N. Unterabschnitte von S bleiben bei S.
   - **Bereichskopf ist ein Detail:** Der Befehl ist gesperrt, denn Details lassen sich nicht unter eine Überschrift stellen.
3. **Grenze:** Würde eine Überschrift dadurch tiefer als Ebene 6, ist der Befehl gesperrt.
4. **Danach:** Der Cursor steht im leeren Titel von N.
5. **Rückgängig („Unter neue Überschrift“):** Es stellt die vorherige Struktur her, auch wenn inzwischen der Titel von N oder Text in den verschobenen Blöcken geändert wurde (vergleichbar mit „Überschrift entfernen“ in Paket A).
   - Geht nicht mehr eindeutig, wird es abgelehnt. Das ist der Fall, wenn N fehlt oder ein verschobener Block an anderer Stelle steht.
   - Wurde der Titel von N geändert, geht er beim Rückgängigmachen verloren. Das ist gewollt, denn N verschwindet.

## 6. Fehlerbehandlung

Wie in Paket A:
- **Ungesicherter Editortext oder Konflikt:** Ist der Editortext nicht gespeichert oder gibt es einen offenen Konflikt für das Buch, wird der Befehl abgelehnt, und der Hinweis erscheint in der Leiste.
- **Konflikt bei Notizseiten (nur Zusammenfügen):** Auch ein offener Konflikt auf einer Notizseite, deren `used-in::` geändert würde, lehnt ab.
- **Schreibfehler:** Lässt sich das Buch nicht schreiben, bleibt die Datei unverändert, und der Editor zeigt wieder ihren Stand.
- **Externe Änderung während eines Befehls:** Es gilt die bestehende Konfliktbehandlung.
- **„Wohin damit?“ ohne Treffer:** Der Hinweis „Keine passenden Stellen gefunden.“ erscheint, wie bisher „NoteWhereToNone“.

## 7. Tests

- **Bereich (Core/UI):**
  - Zu jeder Art von Element ergeben die Ebenen −1, 0, 1, … die richtigen Knotenlisten.
  - Unregelmäßige Ebenen und Textblöcke nach Unterabschnitten (nicht zusammenhängende Teile) werden richtig behandelt.
  - Ein Cursorwechsel setzt auf Ebene 0 zurück, Tippen im selben Element nicht.
  - ▲ und ▼ sind an den Grenzen gesperrt.
- **Themenbereich:**
  - „Relevant“ nutzt den richtigen Thementext bzw. -vektor je Ebene, zum Beispiel ein Bereich auf Ebene 1 mit Unterabschnitten.
  - Die Quellenanzahl gilt je Bereich.
- **„Wohin damit?“:**
  - Notiz und Unteranstrich liefern Treffer mit Pfad, Auszug und Prozent.
  - Ein Klick springt (`RevealElement`) und lässt die Liste offen.
  - Übernehmen am Treffer mit allen drei Varianten.
  - Eine Notiz, die schon Quelle ist, zeigt ✓.
  - Ohne KI ist der Knopf ausgeblendet.
- **Zusammenfügen (Core):**
  - Je Gruppe stimmen Text, Details, Quellenvereinigung und `used-in::`-Umschreibung (Dopplung entfällt) Zeile für Zeile, mit `LineDiff`.
  - Es gibt mehrere Gruppen in einem Bereich, und Gruppen mit nur einem Block bleiben unverändert.
  - Rückgängig ist byteweise, und eine Notiz, die sich nicht schreiben lässt, kommt in `pending.json`.
- **„Unter neue Überschrift“ (Core):**
  - Bereichskopf Überschrift, Textblock (mit folgenden Textblöcken) und Vorspann.
  - Sperre bei Detail und bei Ebene 6.
  - Rückgängig nach Titeländerung und nach Textänderung in verschobenen Blöcken.
  - Ablehnung, wenn ein Block an anderer Stelle steht.
- **UI:**
  - Die Knöpfe der Leiste und ihre Sperren.
  - Befehle bei Konflikt oder ungesichertem Text abgelehnt.
  - Der Editor bekommt die Knotenliste des Bereichs (`FakeEditorInterop`).
- **Manuelle Abnahme (README):**
  - Erweitern und Verkleinern mit Kasten und Leiste im echten Fenster, auch nicht zusammenhängend.
  - Alt+Pfeil hoch und runter.
  - Ein Treffer von „Wohin damit?“ ansehen und dort übernehmen.
  - Zusammenfügen und „Unter neue Überschrift“ in Logseq prüfen.

## 8. Nicht im Umfang

- freie Mehrfachauswahl mit Maus oder Umschalttaste
- Zusammenfügen über Überschriften hinweg
- Bereiche, die mehrere nebeneinanderliegende Elemente ohne gemeinsames übergeordnetes Element umfassen
- Sprachmodell-Begründungen für Treffer (Stufe 3)
