# Markierung verschieben

## 1. Ziel

Was im Buch markiert ist, lässt sich als Ganzes an eine andere Stelle ziehen: eine Überschrift mit ihrem ganzen
Abschnitt, ein Textblock mit seinen Unterpunkten, ein Unterpunkt mit allen tieferen Unterpunkten. Das geht im selben
Editor, in den Editor der anderen Hälfte und auf die Gliederung beider Hälften. Die innere Struktur bleibt erhalten,
die Tiefe passt sich dem Ziel an. Beim Verschieben eines Abschnitts kommt alles mit, was im Buch unter seiner
Überschrift zu lesen ist, auch wenn es in der Datei nicht unter ihr eingerückt ist.

## 2. Ausgangslage

- Im Editor ist nur ein Textblock ziehbar (am farbigen Balken, samt Unterpunkten): innerhalb des Editors, in die andere
  Hälfte, auf eine Überschrift der Gliederung (`ManuscriptEditor.MoveTextBlocks`). Überschriften, Unterpunkte und die
  Markierung selbst sind nicht ziehbar.
- In der Gliederung lässt sich ein Abschnitt ziehen (auf, vor, hinter einen anderen; `OutlineEditor.MoveSection`). Er
  wandert mit allem, was in der Datei unter seiner Überschrift eingerückt ist. Nachgestellt am 2026-11-15 in einer
  Testinstanz: Kapitel mit Text, Unterabschnitt und Unter-Unterabschnitt wandert vollständig.
- **Befund an „Buch - LoveMagic“ (2026-11-15, nur gelesen):** Die Gliederung folgt der Einrückung in der Datei. 20
  Überschriften haben keine eingerückten Blöcke unter sich; 7 Überschriften stehen nicht dort, wo ihre Ebene sie
  erwarten lässt (z. B. `###` auf derselben Einrückung wie die `##` davor); 9 Textblöcke stehen auf derselben
  Einrückung hinter einer Überschrift. In diesen Fällen zeigt der Editor Text und tiefere Überschriften unter einer
  Überschrift, die in der Datei daneben stehen. Wird diese Überschrift verschoben, bleiben sie zurück: Es sieht aus,
  als wandere nur die Überschrift. Das erklärt die Beobachtung „nur die Überschrift wurde verschoben“.
- Die Markierung im Editor umfasst, je nach Cursor und „Bereich ▲/▼“, eine Überschrift mit Abschnitt, einen Textblock
  mit Unterpunkten oder einen Unterpunkt. Unter ihr steht die Leiste mit den Abschnittsknöpfen.

## 3. Was mitwandert

- **Überschrift:** der Abschnitt in Lesereihenfolge: alles, was im Buch auf die Überschrift folgt, bis zur nächsten
  Überschrift derselben oder einer höheren Ebene (oder dem Anhang, oder dem Buchende). Das sind die in der Datei
  eingerückten Blöcke und zusätzlich die Blöcke, die in der Datei auf derselben Einrückung direkt folgen und im Buch
  zum Abschnitt gelesen werden (Textblöcke und tiefere Überschriften).
- **Textblock:** der Block mit allen Unterpunkten.
- **Unterpunkt:** der Unterpunkt mit allen tieferen Unterpunkten, die unter ihm stehen.

Diese Regel gilt für jedes Verschieben eines Abschnitts, auch für das bestehende Ziehen in der Gliederung. Die
mitgenommenen Nachbarblöcke stehen am Ziel eingerückt unter der Überschrift; die Datei ist dort danach so
geschachtelt, wie das Buch gelesen wird. An der alten Stelle bleibt nichts zurück.

## 4. Geste

- Die Leiste unter der Markierung bekommt vorn einen Griff „⠿“ (wie im Notizbereich), mit dem Tooltip „Markierten
  Bereich verschieben“. Wer daran zieht, verschiebt, was markiert ist.
- Der Griff ist ausgegraut, wenn nichts verschoben werden kann: ungespeicherter Konflikt, gesperrter Editor, Markierung
  im Anhang, Vorspann.
- Während des Ziehens zeigen Editor und Gliederung die Ablagestelle wie heute beim Ziehen von Textblöcken und
  Abschnitten.
- Das Ziehen eines Textblocks am farbigen Balken und das Ziehen in der Gliederung bleiben.

## 5. Ablegen

Ziele: derselbe Editor, der Editor der anderen Hälfte, eine Überschrift (oder der Vorspann) in der Gliederung beider
Hälften, das Ende der Gliederung.

### 5.1 Abschnitt (Überschrift markiert)

- **Im Editor abgelegt:** Der Abschnitt kommt auf dieselbe Ebene wie der Abschnitt, in dem er losgelassen wird, und
  steht direkt hinter diesem (hinter dessen ganzem Inhalt). Wird er im Vorspann oder vor der ersten Überschrift
  losgelassen, wird er der erste Abschnitt der obersten Ebene.
- **In der Gliederung abgelegt:** wie heute (auf: letzter Unterabschnitt; vor: davor, gleiche Ebene; Ende: letzter
  Abschnitt der obersten Ebene).
- Jede Überschrift im Abschnitt bekommt die Ebene, die ihrer neuen Tiefe entspricht (Ebene des Ziels plus Tiefe unter
  der verschobenen Überschrift). Eine übersprungene Ebene im Abschnitt wird dabei geschlossen (`###` direkt unter `#`
  wird `##`). Würde eine tiefer als Ebene 6, wird nicht verschoben; es erscheint der Hinweis „Der Abschnitt würde
  tiefer als Ebene 6“.
- Nicht möglich: in den eigenen Abschnitt hinein, in den Anhang. Ablegen an der Stelle, an der er schon steht, ändert
  nichts.

### 5.2 Textblock

- **Im Editor:** Er bleibt Textblock und steht hinter dem Element, über dem er losgelassen wird: hinter einem
  Textblock (auch wenn über einem von dessen Unterpunkten losgelassen wird, dann hinter dem ganzen Block); auf einer
  Überschrift als erster Textblock ihres Abschnitts.
- **In der Gliederung:** ans Ende des Textes der Überschrift (wie heute).

### 5.3 Unterpunkt

- **Auf einem Textblock abgelegt** (auf dessen eigenem Text): Er wird dessen erster Unterpunkt, Stufe 1.
- **Auf einem Unterpunkt abgelegt:** Er steht direkt hinter diesem und dessen tieferen Unterpunkten, auf derselben
  Stufe wie dieser.
- **Auf einer Überschrift abgelegt (Editor oder Gliederung):** Er wird ein eigener Textblock (im Editor erster
  Textblock des Abschnitts, in der Gliederung letzter); seine tieferen Unterpunkte bleiben seine Unterpunkte. Der neue
  Textblock wird behandelt wie beim „Ausrücken“ eines Unterpunkts der ersten Stufe (neue Daten, keine Quellen).
- Die tieferen Unterpunkte behalten ihren Abstand zum verschobenen Unterpunkt.
- Nicht möglich: auf sich selbst oder einen der eigenen tieferen Unterpunkte.

## 6. Erhalt und Rückgängig

- Verschobene Blöcke behalten `id::`, Quellen (`source::`), Status und Datum; „Verschieben zählt nicht“ als Änderung
  (kein neues `updated::`), außer wo 5.3 einen neuen Textblock entstehen lässt. Die Notizen bleiben unberührt, ihre
  `used-in::` zeigen weiter auf dieselben Blöcke. Verweise auf verschobene Blöcke bleiben gültig.
- Vor dem Verschieben wird ungespeicherter Editortext gesichert, wie bei den anderen Strukturbefehlen.
- Jede Verschiebung ist ein Schritt für „Rückgängig“ in der Kopfzeile („Rückgängig: Bereich verschieben“); er stellt
  auch die frühere Einrückung der mitgenommenen Nachbarblöcke wieder her.
- Nach dem Verschieben ist der verschobene Bereich markiert und sichtbar, in der Hälfte, in der er abgelegt wurde.

## 7. Fehler

- Schlägt das Speichern fehl, bleibt das Buch unverändert und die Kopfzeile zeigt die Meldung, wie bei den anderen
  Strukturbefehlen.
- Hat sich das Buch zwischen Ziehbeginn und Ablegen geändert (äußere Änderung), wird im Buch verschoben, wie es beim
  Ablegen ist; die äußere Änderung bleibt erhalten. Nicht verschoben wird nur, wenn der gezogene Bereichskopf nicht
  mehr da ist oder eine andere Art Element geworden ist oder das Element, auf dem abgelegt wurde, nicht mehr da ist;
  dann erscheint der Hinweis „Dorthin lässt sich der markierte Bereich nicht verschieben.“ und die Datei bleibt
  unverändert.
- Ablegen außerhalb eines Ziels ändert nichts.

## 8. Nicht enthalten

- Verschieben per Taste oder Knopf (nur Ziehen).
- Mehrere getrennte Elemente auf einmal.
- Verschieben zwischen zwei Büchern.
- Automatisches Bereinigen der übrigen Schachtelung (dafür gibt es „Gliederung einordnen“).

## 9. Tests

- **Kern, Abschnitt:** Verschieben mit Unterabschnitten und Text; Ebenen passen sich an; Nachbarblöcke auf derselben
  Einrückung (Text, tiefere Überschrift) kommen mit und stehen am Ziel eingerückt; Grenze an der nächsten Überschrift
  gleicher oder höherer Ebene; Ebene 6 überschritten, eigener Abschnitt, Anhang: abgelehnt, Datei unverändert; gleiche
  Stelle: keine Änderung; `id::`, Quellen und Datum unverändert; Rückgängig stellt die Datei Byte für Byte wieder her.
- **Kern, Unterpunkt:** auf Textblock, auf Unterpunkt (Stufen passen sich an, tiefere behalten ihren Abstand), auf
  Überschrift (wird Textblock); auf sich selbst oder eigenen tieferen Unterpunkt: abgelehnt; Rückgängig.
- **Kern, Textblock:** hinter Textblock, auf Überschrift als erster; bestehende Tests bleiben.
- **Gliederung:** Das bestehende Ziehen nimmt Nachbarblöcke mit (Regel aus 3).
- **Oberfläche:** Griff an der Leiste, ausgegraut in den genannten Fällen; Ablegen im selben Editor, in der anderen
  Hälfte und auf der Gliederung ruft die richtige Verschiebung auf; danach ist der Bereich markiert; Rückgängig-Text.
- **Editor-Skript:** Die Ablagestelle (Element unter dem Zeiger) wird für Überschrift, Textblock und Unterpunkt
  richtig gemeldet.
- **Im Programmfenster nachstellen:** je ein Fall für Abschnitt, Textblock und Unterpunkt, im selben Editor, in der
  anderen Hälfte und auf der Gliederung.
