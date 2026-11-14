# Druck-PDF

## 1. Ziel

Der PDF-Export bekommt neben A4 und A5 eine Druckausgabe für ein gedrucktes Buch, zuerst für BoD (Books on Demand):
frei wählbares Endformat, einstellbarer Beschnitt, gespiegelte Ränder, Blocksatz mit deutscher Silbentrennung. Das
einfache PDF (A4, A5) bleibt unverändert.

Diese Spec ersetzt Abschnitt 8.2 der Hauptspec (`2026-10-05-noteevolution-design.md`); er wird mit ihr angepasst.

## 2. Ausgangslage

- `QuestPdfExporter` setzt Titelseite, Inhaltsverzeichnis und Text in EB Garamond 11 pt, linksbündig, mit rundum
  gleichem Rand. `PdfOptions` kennt nur `PdfPageSize` (A4, A5). `PdfContent` liefert die Elemente (Überschrift,
  Absatz aus Läufen, Bild).
- Vorab-Test am 2026-11-14 (QuestPDF 2026.9.1, Wegwerf-Code): freie Seitengröße, Beschnitt, selbst gezeichnete
  Schnittmarken, Blocksatz, Umbruch an weichen Trennstellen (U+00AD) und gespiegelte Ränder funktionieren.
  QuestPDF trennt nicht selbst, kann eine einzelne letzte Zeile auf der neuen Seite nicht verhindern und schreibt
  weder PDF/X noch TrimBox und BleedBox. Entscheidung: QuestPDF bleibt, Typst wird nicht eingebunden.
- BoD verlangt: PDF genau im Endformat, alle Seiten gleich groß, Einzelseiten, Schriften eingebettet. Schnittmarken
  sollen nicht gesetzt werden. Beschnitt (5 mm je Seite) ist nur bei Bildern nötig, die bis an den Papierrand
  reichen; solche Bilder setzt NoteEvolution nicht. RGB ist erlaubt. Die Seitenzahl muss bei BoD exakt angegeben
  werden, leere Seiten zählen mit.

## 3. Einstellungen im Dialog

Der Dialog „PDF exportieren“ bekommt unter „Format“ eine dritte Wahl **Druck**. Ist sie gewählt, erscheinen:

- **Endformat:** Auswahl mit 12 × 19 cm, 13,5 × 21,5 cm, 14,8 × 21 cm, 15,5 × 22 cm, 17 × 22 cm und „Eigenes
  Format“. Bei „Eigenes Format“ zwei Zahlenfelder Breite und Höhe in mm (ganze oder halbe mm), erlaubt 100 bis
  300 mm.
- **Beschnitt:** Zahlenfeld in mm je Seite, 0 bis 10, Vorgabe 0.
- **Schnittmarken:** Häkchen, Vorgabe aus; nur wählbar, wenn der Beschnitt größer als 0 ist.

Liegt ein Wert außerhalb der Grenzen oder ist ein Feld leer, ist „Exportieren“ gesperrt und am Feld steht der
erlaubte Bereich. Die Wahl (einfach oder Druck, A4 oder A5, Endformat, eigene Maße, Beschnitt, Schnittmarken) wird
in `ui.json` gemerkt und beim nächsten Öffnen vorbelegt; ein ungültiger gespeicherter Wert fällt auf die Vorgabe
zurück.

Nach dem Export zeigt der Dialog bei jeder Ausgabe die **Seitenzahl** des PDFs („142 Seiten“).

## 4. Seite

- **Ohne Beschnitt:** Die PDF-Seite hat genau das Endformat.
- **Mit Beschnitt:** Die Seite ist an jeder Kante um den Beschnitt größer. Der Satzspiegel bezieht sich weiter auf
  das Endformat, das mittig auf der Seite liegt. Der Beschnittbereich bleibt weiß (es gibt keine randabfallenden
  Inhalte).
- **Mit Schnittmarken:** Die Seite ist an jeder Kante zusätzlich um 7 mm größer. An jeder Ecke des Endformats
  stehen zwei Marken (0,25 pt, schwarz, 5 mm lang), die im Abstand des Beschnitts von der Ecke beginnen und so
  außerhalb des Beschnitts liegen.
- **Ränder, bezogen auf das Endformat:** Breite unter 140 mm: innen 18, außen 14, oben 16, unten 20 mm. Sonst:
  innen 22, außen 17, oben 20, unten 24 mm. Innen ist auf ungeraden (rechten) Seiten links, auf geraden Seiten
  rechts. Die Ränder sind nicht einstellbar.
- **Schrift:** EB Garamond, 10,5 pt bei Breite unter 140 mm, sonst 11 pt; Zeilenabstand 1,35. Überschriften,
  Bildunterschriften und Inhaltsverzeichnis behalten ihr Verhältnis zur Grundschrift.
- **Seitenzahl:** unten in der Mitte des Satzspiegels, im unteren Rand. Titelseite und leere Seiten tragen keine.

## 5. Aufbau

1. Titelseite (Seite 1, rechts).
2. Leere Rückseite.
3. Inhaltsverzeichnis ab Seite 3, wenn es Überschriften gibt.
4. Der Text beginnt auf einer rechten Seite; endet das Inhaltsverzeichnis auf einer rechten Seite, folgt eine
   leere Seite.
5. Kapitel (oberste Ebene) beginnen wie bisher auf einer neuen Seite, links oder rechts.

Die Seitenzählung läuft ab der Titelseite durch; die Seitenzahlen im Inhaltsverzeichnis und in Verweisen
(„S. n“) sind diese Zahlen.

## 6. Satz

- **Blocksatz** für Absätze, letzte Zeile linksbündig. Überschriften, Titelseite, Inhaltsverzeichnis und
  Bildunterschriften bleiben, wie sie sind.
- **Silbentrennung** für Absatztext und Bildunterschriften, nicht für Überschriften, Titel und
  Inhaltsverzeichnis. Vor dem Setzen werden weiche Trennstellen (U+00AD) in die Wörter eingefügt; QuestPDF bricht
  dort um und zeigt den Trennstrich nur am Zeilenende.
  - Verfahren: Liang-Muster (wie TeX), eigene kleine Umsetzung im Projekt `NoteEvolution.Pdf`, keine neue
    NuGet-Abhängigkeit. Die Musterdatei für die reformierte deutsche Rechtschreibung (`hyph-de-1996` aus dem
    Projekt tex-hyphen, MIT-Lizenz) wird mitgeliefert, samt Lizenztext. Die Lizenz wird beim Einbinden am Kopf der
    Datei geprüft; ist sie anders als angenommen, wird vor dem Einbinden nachgefragt.
  - Mindestens 2 Buchstaben vor und 3 nach der Trennstelle; Wörter unter 6 Buchstaben, Wörter mit Ziffern und
    Wörter, die schon U+00AD enthalten, bleiben unberührt. Trennstellen entstehen nur zwischen Buchstaben.
  - Die Muster werden einmal je Programmlauf geladen.
- **Schusterjungen:** Ein Absatz beginnt nur auf einer Seite, wenn dort noch zwei Zeilen Platz haben; sonst
  beginnt er auf der nächsten.
- **Hurenkinder:** werden nicht verhindert (Grenze von QuestPDF). Das steht so in der README.
- Das einfache PDF bekommt weder Blocksatz noch Silbentrennung.

## 7. Schnittstelle

```csharp
public sealed record PrintLayout(float WidthMm, float HeightMm, float BleedMm = 0, bool CropMarks = false);
public sealed record PdfOptions(PdfPageSize Size, PrintLayout? Print = null);
public sealed record PdfExportReport(IReadOnlyList<string> Warnings, int PageCount);
```

- `Print` ungleich `null` wählt die Druckausgabe; `Size` wird dann nicht ausgewertet.
- `Export` wirft `ArgumentOutOfRangeException`, wenn Breite oder Höhe außerhalb 100 bis 300 mm oder der Beschnitt
  außerhalb 0 bis 10 mm liegt. `CropMarks` ohne Beschnitt wird ignoriert.
- Die Grenzen stehen als Konstanten an `PrintLayout`; der Dialog benutzt dieselben.
- `UiSettings` bekommt die gemerkten Werte (Abschnitt 3).

## 8. Fehler und Grenzen

- Ein Wort, das breiter ist als die Zeile und keine Trennstelle hat (z. B. eine lange Adresse), bricht wie heute
  um; der Export schlägt nicht fehl.
- Fehlt die Musterdatei oder ist sie nicht lesbar, wird ohne Silbentrennung gesetzt und der Bericht enthält die
  Warnung „Silbentrennung nicht verfügbar“.
- Nicht enthalten: PDF/X, TrimBox und BleedBox, CMYK, einstellbare Ränder, Kapitelbeginn immer rechts, lebende
  Kolumnentitel, Umschlag.

## 9. Tests

- **Trennung:** bekannte Wörter („Silbentrennung“, „Gedächtnis“, „Wiederholung“) erhalten die erwarteten
  Trennstellen; kurze Wörter, Wörter mit Ziffern und schon getrennte Wörter bleiben gleich; Satzzeichen und
  Leerraum bleiben erhalten; ohne U+00AD ist der Text unverändert.
- **Seitengröße:** Endformat ohne Beschnitt; mit Beschnitt; mit Beschnitt und Marken; ungültige Maße werfen.
- **Ränder:** Die linke Textkante liegt auf ungeraden Seiten beim inneren, auf geraden beim äußeren Rand
  (gemessen an der Textposition im PDF), auch mit Beschnitt.
- **Aufbau:** Seite 2 ist leer; der Text beginnt auf einer ungeraden Seite, bei kurzem und bei zweiseitigem
  Inhaltsverzeichnis; die Seitenzahl im Inhaltsverzeichnis stimmt mit der Seite der Überschrift überein.
- **Bericht:** `PageCount` stimmt mit der Seitenzahl des PDFs überein, auch beim einfachen PDF.
- **Inhalt:** Der Text des Druck-PDFs ist nach Entfernen der Trennstellen derselbe wie beim einfachen PDF;
  Verweise mit Seitenzahl und Bilder funktionieren weiter.
- **Dialog:** Felder erscheinen nur bei „Druck“; Grenzen sperren den Knopf; die Optionen kommen beim Exporter an;
  die Einstellungen werden gemerkt und vorbelegt; die Seitenzahl wird angezeigt.
- Die bestehenden Tests des einfachen PDFs bleiben unverändert grün.
