# Bilder als Block im Buch

## 1. Ziel

Bilder lassen sich als eigener Block in den Buchtext einfügen: über einen Knopf mit Dateiauswahl und aus der
Zwischenablage (Strg+V, z. B. Screenshots). Der Editor zeigt das Bild mit seiner Beschriftung, das PDF enthält es.
Die Größe ist nicht einstellbar: so breit wie Text bzw. Seite, höchstens Originalgröße.

## 2. Ausgangslage

- Das PDF kennt Bilder schon: `![Beschriftung](pfad)` im Text eines Blocks wird als Bild in Textbreite gesetzt
  (`PdfContent.AddBlockText`, `ImageRegex`); Bilder aus dem Netz und fehlende Dateien werden als Hinweis ausgegeben.
  Eine Beschriftung zeigt es nicht.
- Der Editor zeigt die Bild-Schreibweise heute als Text.
- Für Verweise und Zitate gibt es ein Muster: Die Schreibweise im Markdown ist im Editor ein nicht teilbares
  Inline-Element (`blockRef`, `sourceCite`), der Mapper (`EditorDocMapper`) übersetzt in beide Richtungen.

## 3. In der Datei

- Ein Bild ist ein Textblock, dessen Text nur aus der Bild-Schreibweise besteht:
  `![Beschriftung](../assets/name.png)`. Das ist die Schreibweise von Logseq; Logseq zeigt das Bild ebenfalls.
- Die Bilddatei liegt im Ordner `assets` des Vaults. Der Pfad ist relativ zur Buchdatei.
- Eine Größenangabe von Logseq hinter dem Bild (`{:height 1, :width 2}`) bleibt unverändert stehen und wird nicht
  ausgewertet.
- Es gibt keine neue Blockart im Modell: `Book`, `BookSync` und die Verknüpfungen behandeln den Block wie jeden
  Textblock (Status, Datum, Verschieben, Löschen, Rückgängig).

## 4. Im Editor

- Jede Bild-Schreibweise in einem Textblock oder Detail ist ein nicht teilbares Element `image` mit Pfad,
  Beschriftung und dem unveränderten Rest der Schreibweise (Größenangabe). Es wird als Bild in voller Zeile
  angezeigt, höchstens so breit wie der Text und höchstens 400 px hoch, darunter klein die Beschriftung.
- Der Mapper schreibt das Element unverändert als Markdown zurück; ein Block, der nur angezeigt und nicht geändert
  wird, bleibt in der Datei Byte für Byte gleich.
- **Beschriftung ändern:** Doppelklick auf das Bild öffnet ein kleines Fenster „Beschriftung“ (wie „Fundstelle
  ändern“). Die Beschriftung darf leer sein; `]` und Zeilenumbrüche werden entfernt. Die Änderung hat ein
  „Rückgängig“ in der Kopfzeile.
- **Bilddatei laden:** Der Editor bekommt die Bilder über eine app-interne Adresse vom Desktop-Programm. Es liefert
  nur Dateien innerhalb des Vaults und nur Bildformate (PNG, JPEG, GIF, WebP, BMP).
- **Nicht ladbar:** Fehlt die Datei oder liegt sie außerhalb des Vaults, steht an der Stelle „Bild nicht gefunden:
  pfad“. Ein Bild aus dem Netz wird nicht geladen („Bild aus dem Netz: adresse“), wie im PDF.
- Löschen und Verschieben wie bei jedem Textblock. Die Bilddatei bleibt beim Löschen des Blocks in `assets` liegen
  (sie kann an anderer Stelle verwendet sein).

## 5. Einfügen

- **Knopf „Bild einfügen“** in der Leiste des Abschnitts: Dateiauswahl für ein Bild. Die Datei wird nach `assets`
  kopiert und ein neuer Textblock mit dem Bild kommt nach dem Element am Cursor (nach einer Überschrift: als erster
  Textblock ihres Abschnitts; an einem Detail: nach dessen Textblock). Die Beschriftung ist zunächst leer.
- **Zwischenablage:** Strg+V im Editor mit einem Bild in der Zwischenablage speichert es als
  `assets/bild-JJJJMMTT-HHMMSS.png` und fügt es genauso ein. Text in der Zwischenablage wird wie bisher eingefügt.
- **Dateiname:** der Name der gewählten Datei, auf Buchstaben, Ziffern, `-`, `_` und `.` gekürzt. Gibt es in `assets`
  schon eine Datei dieses Namens mit gleichem Inhalt, wird sie verwendet; bei anderem Inhalt bekommt die neue ein
  `-2`, `-3`, … angehängt. Der Ordner `assets` wird bei Bedarf angelegt.
- **Grenzen:** nur die Formate aus Abschnitt 4, höchstens 20 MB; sonst eine Meldung, nichts wird geändert.
- Wie jeder Befehl speichert das Einfügen zuerst den Editor, verweigert bei schreibgeschütztem Buch oder offenem
  Konflikt und hat ein „Rückgängig“ („Bild einfügen“), das den Block wieder entfernt; die kopierte Datei bleibt.
- Der neue Block hat den Status Entwurf und bekommt beide Daten.

## 6. Im PDF

- Wie heute: Bild in Textbreite, nie höher als die Seite.
- Neu: Eine nicht leere Beschriftung steht klein und zentriert unter dem Bild.
- Ein Bildblock mit Status Relevant oder Bemerkung fehlt im PDF wie jeder solche Block.

## 7. Nicht enthalten

- Einstellbare Größe, Ausrichtung, Textumfluss.
- Bilder per Ziehen aus dem Dateimanager.
- Aufräumen unbenutzter Dateien in `assets`.
- Bilder in Notizen (der Notizbereich zeigt die Schreibweise weiter als Text).
- Wortzahl und Sprachmodell: Die Bild-Schreibweise zählt wie bisher als Text des Blocks.

## 8. Tests

- Core: Bild-Schreibweise finden und schreiben (Pfad, Beschriftung, Größenangabe), Beschriftung ändern,
  Bildblock einfügen (Position je Element am Cursor), Datei nach `assets` kopieren (gleicher Name gleicher Inhalt,
  gleicher Name anderer Inhalt, Format, Größe).
- Mapper: Markdown mit Bild ↔ Editor-Dokument, unverändert zurück; Bild neben Text im selben Block.
- Editor (JS): Bild in der Zwischenablage meldet das Einfügen und fügt keinen Text ein; Doppelklick meldet die
  Beschriftung.
- Komponenten: Knopf fügt ein und bietet Rückgängig; Beschriftungsfenster ändert die Datei.
- PDF: Beschriftung unter dem Bild; leere Beschriftung ergibt keine Zeile.
- Desktop: Die interne Bildadresse liefert nur Bilder innerhalb des Vaults.
