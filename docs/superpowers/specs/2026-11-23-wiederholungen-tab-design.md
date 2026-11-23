# Wiederholungen: genauer, im Text markiert, als Tab

Baut auf [Wiederholungen im Buch](2026-11-16-wiederholungen-design.md) auf. Dieses Dokument ersetzt dort Abschnitt
3.2 (inhaltlich ähnlich), Abschnitt 5 (Assistent als Dialog) und den Blockmarker aus Abschnitt 6. Alles andere bleibt,
wie es ist: was verglichen wird, wörtliche Folgen ab 8 Wörtern, Hintergrundlauf, „Verweis einfügen“, „Gewollt“ und sein
Speicher.

## 1. Ziel

Die inhaltlichen Treffer sind zu viele: Blöcke zum selben Thema gelten als Wiederholung, obwohl kein Gedanke doppelt
gesagt wird. Der Autor will:

- weniger und treffendere inhaltliche Treffer,
- wörtliche Wiederholungen direkt im Text sehen, ohne etwas zu öffnen,
- die Wiederholungen einzeln durchgehen: die eine Stelle in der Buchansicht, die andere daneben im Tab, beide mitten
  im Buchtext und mit hervorgehoben, was an ihnen ähnlich ist,
- auf einen Blick sehen, wo im Buch die beiden Stellen stehen.

### Erfolgskriterien

- Zwei Blöcke, die nur dasselbe Thema behandeln, aber keinen Satz mit gleicher Aussage enthalten, zählen nicht mehr.
- Ein Satz, der woanders in anderen Worten wiederholt wird, wird gefunden und genau dieser Satz markiert.
- Wörtliche Wiederholungen sind im Text unterstrichen, sobald die Analyse sie kennt.
- Mit offenem Tab stehen die beiden Stellen eines Treffers nebeneinander (Buchansicht links, Tab rechts), mit ‹ › geht
  es zum vorigen und nächsten Treffer, die Miniansicht zwischen beiden zeigt ihre Lage im Buch.

### Nicht-Ziele

- Keine Einstellungen für Schwellen oder Modell in der Oberfläche.
- Kein Vergleich über Buchgrenzen hinweg.
- Kein zweiter Editor im Tab: dort wird nur gelesen, geschrieben wird in der Buchansicht.
- Keine Liste aller Treffer und keine Split-Ansicht für Wiederholungen.

## 2. Inhaltlich ähnlich: grob, dann im Satz bestätigt

**Gewählter Ansatz** (vom Autor bestätigt). Verworfen: nur die Schwelle der Blockvektoren anheben (bleibt themenlastig,
liefert keine Stelle zum Markieren) und alle Sätze des Buchs paarweise vergleichen (bei großen Büchern zu viele Vektoren
und Vergleiche für den Hintergrund).

1. **Vorauswahl auf Blockebene** wie heute (Blockvektor, Kosinus), aber großzügiger: ein Paar ist Kandidat ab
   `0,85` und gehört zu den 5 ähnlichsten eines der beiden Blöcke. Paare mit wörtlicher Folge bleiben ausgeschlossen.
2. **Sätze der beiden Blöcke:** der eigene Text des Blocks und seine Unterpunkte, in Sätze zerlegt (Ende bei `.`, `!`,
   `?` oder `…`, gefolgt von Leerraum und einem Großbuchstaben, einer Ziffer oder einem Anführungszeichen, sowie am
   Absatzende). Sätze unter 5 Wörtern zählen nicht. Jeder Satz bekommt einen eigenen Vektor (dieselbe Rolle `query:`
   wie die Blöcke), gespeichert im selben Vektorspeicher (`book-vectors.db`) unter dem Hash seines Textes.
3. **Bestätigung:** jedes Satzpaar (einer aus A, einer aus B) wird verglichen. Das Blockpaar zählt, wenn mindestens ein
   Satzpaar die **Satzschwelle** erreicht. Anfangswert `0,92`; Schritt 6 legt ihn fest.
4. **Gemeldet** werden je Blockpaar die Satzpaare ab der Satzschwelle, höchstens 3, das ähnlichste zuerst. Ein Satz
   gehört höchstens zu einem gemeldeten Satzpaar desselben Blockpaars.
5. Je Block bleiben höchstens 3 inhaltliche Gegenstücke gelistet (die mit dem ähnlichsten Satzpaar).
6. **Kalibrierung:** Das Protokoll schreibt (begrenzt wie heute) für jedes Kandidatenpaar den besten Satzwert. Vor dem
   Abschluss werden die Werte an einem echten Buch geprüft; die Schwelle wird so gesetzt, dass Themenpaare draußen
   bleiben. Der gewählte Wert wird im Code begründet.

Die Satzvektoren entstehen nur für Blöcke, die in einem Kandidatenpaar stehen, im Hintergrund nach den Blockvektoren,
unter denselben Regeln (erst nach dem Notizindex, ohne Modell keine).

## 3. Markierungen in der Buchansicht

- **Wörtlich, immer:** jede gemeinsame Folge ist durchgezogen unterstrichen, auf beiden Seiten, solange der Schalter
  „Wiederholungen zeigen“ an ist.
- **Inhaltlich, nur mit offenem Tab:** die gemeldeten Sätze sind gepunktet unterstrichen, in einer zweiten Farbe.
- **Der aktuelle Treffer, nur mit offenem Tab:** seine Stelle in der Buchansicht (der Ausgangstext) ist deutlich
  hervorgehoben: der Block bekommt eine Randlinie, und was die Ähnlichkeit ausmacht, ist farbig hinterlegt (bei
  wörtlichen die gemeinsamen Folgen, bei inhaltlichen die ähnlichen Sätze). Dieselbe Hervorhebung trägt die Gegenstelle
  im Tab (Abschnitt 4).
- Gewollte Paare sind nicht unterstrichen (der aktuelle Treffer schon, wenn „Gewollte anzeigen“ an ist).
- Ein Tooltip auf einer Unterstreichung nennt die andere Stelle („Auch in: Kapitel 5 › Lernen“, bei mehreren alle).
- Die Markierungen sind reine Anzeige wie die Datumsmarken: sie gehören nicht zum Dokument, zählen nicht als Änderung
  und kommen mit dem nächsten Analysestand neu. Beim Tippen bleiben sie bis dahin am Text haften oder verschwinden mit
  ihm.
- Der bisherige Blockmarker (Zahl am Block) entfällt. Zähler in der Leiste und `≈ n` in der Gliederung bleiben.

## 4. Der Tab „Wiederholungen“

Ein fünfter Tab rechts neben Relevant, Suche, Tagebuch und Abdeckung. Er ersetzt den Dialog. Er zeigt immer **einen**
Treffer: dessen Ausgangstext steht in der Buchansicht links, die Gegenstelle im Tab.

```
 Buchansicht (Editor)                 │ Tab „Wiederholungen“
                                      │ ‹  3 / 8  ›  wörtlich   ⇄  [Gewollt] [Verweis ▾]  ⚙
 # Kapitel 2 › Schlaf                 │┌──┐ # Kapitel 5 › Lernen
 …am Abend davor.                     ││  │ …wie oben gesagt.
┃Schlaf festigt das Gedächtnis, weil… ││▬A│┃Schlaf festigt das Gedächtnis, weil…
┃das Gehirn nachts Erlebtes sortiert. ││ ╲│┃das Gehirn nachts Erlebtes sortiert.
 Deshalb lohnt es sich…               ││▬B│ Ein weiterer Punkt…
                                      │└──┘ (scrollbar durch das ganze Buch)
```

- **Kopfzeile:** ‹ und › gehen zum vorigen und nächsten Treffer, dazwischen „n / N“ und die Art (wörtlich oder
  inhaltlich). Die Reihenfolge: erst alle wörtlichen, dann alle inhaltlichen; innerhalb jeder Art nach der Stelle des
  Ausgangstexts im Buch. Am Ende geht es nicht im Kreis, die Pfeile sind dort ausgegraut.
- **Ausgangstext:** die Stelle, die früher im Buch steht. Öffnet man den Tab über den Zähler eines Blocks in der
  Leiste, ist dieser Block der Ausgangstext, und der Tab startet bei seinem ersten Treffer. **⇄** tauscht die beiden
  Seiten: die Gegenstelle wird zum Ausgangstext in der Buchansicht und umgekehrt.
- **Beim Wechsel des Treffers** springt die Buchansicht zum Ausgangstext (wie „Im Buch öffnen“ bisher), und der Tab
  scrollt zur Gegenstelle.
- **Gegenstelle im Tab:** eine Leseansicht des **ganzen** Buchs (Überschriften und Textblöcke, formatiert wie im Buch,
  ohne Bearbeiten), die an der Gegenstelle steht und frei nach oben und unten scrollt. Die Gegenstelle ist hervorgehoben
  wie der Ausgangstext (Abschnitt 3). Wörtliche Unterstreichungen zeigt sie wie die Buchansicht. Ist die Gegenstelle im
  selben Bildschirmbereich der Buchansicht sichtbar, ändert das nichts: die beiden Ansichten scrollen unabhängig.
- **Miniansicht** am linken Rand des Tabs, also zwischen den beiden Texten: ein schmaler senkrechter Streifen für das
  ganze Buch, Kapitelgrenzen als feine Linien, alle Treffer als blasse Striche, die beiden Stellen des aktuellen
  Treffers als deutliche Marken (A für den Ausgangstext, B für die Gegenstelle), verbunden durch eine Linie. Dazu je ein
  Rahmen für den sichtbaren Bereich der Buchansicht und des Tabs. Ein Klick in den Streifen scrollt den Tab dorthin.
- **Aktionen** in der Kopfzeile: „Gewollt“ / „Nicht mehr gewollt“ und „Verweis ▾“ (vom Ausgangstext zur Gegenstelle,
  umgekehrt, beide Richtungen). Ausgegraut bei offenem Konflikt oder gesperrtem Editor. Nach „Gewollt“ geht es zum
  nächsten Treffer (gewollte zählen nicht mit, außer „Gewollte anzeigen“ ist an).
- **⚙** öffnet die Filter: Kapitel (nur Treffer, deren eine Stelle im Kapitel steht) und „Gewollte anzeigen“.
- **Ändert sich das Buch** (Tippen, Analyse fertig), bleibt der Tab beim selben Paar, solange es noch da ist; sonst beim
  nächsten nach derselben Stelle. Die Zählung n / N folgt.
- **Während der Analyse** zeigt der Tab die schon bekannten Treffer und in der Kopfzeile den Fortschritt („inhaltlich:
  120 von 480 Blöcken“); ohne Modell den Hinweis, dass inhaltliche Ähnlichkeit ein Modell braucht.
- **Keine Treffer:** „Keine Wiederholungen gefunden.“ statt Kopfzeile und Ansicht.
- Was bisher den Dialog öffnete (Zähler in der Leiste, Status in der Kopfleiste), öffnet den Tab.

## 5. Fehler

Wie bisher: scheitert ein Vektor, fällt dieser Satz oder Block heraus (Protokoll); scheitert ein Verweis oder
`repetitions.json`, bleibt alles unverändert und der Tab zeigt die Meldung.

## 6. Tests

- Kern: Satzzerlegung (Satzenden, Abkürzungen ohne Großbuchstaben danach, kurze Sätze, Unterpunkte); Bestätigung
  (Themenpaar ohne nahes Satzpaar fällt heraus, Paar mit nahem Satz zählt, höchstens 3 Satzpaare, ein Satz nur einmal);
  Ausschluss wörtlicher Paare bleibt; Reihenfolge der Treffer und Wahl des Ausgangstexts.
- Tracker: Satzvektoren nur für Kandidatenblöcke, aus dem Speicher wiederverwendet; ohne Modell nur wörtlich.
- Editor (JS): Unterstreichungen beider Arten und die Hervorhebung des aktuellen Treffers an den richtigen
  Textstellen; sie ändern das Dokument nicht; inhaltliche und Hervorhebung nur mit offenem Tab.
- UI: Kopfzeile mit n / N, ‹ › bis an die Enden, Buchansicht springt zum Ausgangstext, Tab steht an der Gegenstelle mit
  Hervorhebung, ⇄ tauscht, Einstieg über die Leiste startet beim Block, Miniansicht mit Marken an den richtigen
  Stellen, Aktionen wie bisher, Verhalten bei Buchänderung, leerer Zustand.

## Entscheidungen des Bauenden (vom Autor nicht festgelegt)

- Satzschwelle 0,92 als Start, Vorauswahl 0,85 und 5 Kandidaten je Block, 3 Satzpaare je Paar.
- Satzende-Regel und Mindestlänge 5 Wörter.
- Die Gegenstelle im Tab ist nur zum Lesen; ein zweiter Editor ist ausgeschlossen.
- Ausgangstext ist die frühere Stelle, ⇄ tauscht; Einstieg über die Leiste macht den Block zum Ausgangstext.
- Erst wörtliche, dann inhaltliche Treffer; kein Umlauf am Ende.
- Miniansicht mit allen Treffern blass, den beiden Stellen deutlich, Sichtrahmen beider Ansichten; Klick scrollt den Tab.
- Kapitelfilter und „Gewollte anzeigen“ hinter ⚙.
- Der Schalter „Wiederholungen zeigen“ steuert weiter alle Unterstreichungen; der Blockmarker entfällt.
- Tooltip statt Klickaktion auf der Unterstreichung (ein Klick in den Text bleibt Bearbeiten).
