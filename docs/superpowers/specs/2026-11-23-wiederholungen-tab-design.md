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
- alle Wiederholungen in einem Tab rechts durchgehen, mit etwas Kontext um jede Stelle,
- bei offenem Tab auch die inhaltlichen Ähnlichkeiten im Text sehen,
- eine andere Stelle in der Split-Ansicht neben der ersten anschauen.

### Erfolgskriterien

- Zwei Blöcke, die nur dasselbe Thema behandeln, aber keinen Satz mit gleicher Aussage enthalten, zählen nicht mehr.
- Ein Satz, der woanders in anderen Worten wiederholt wird, wird gefunden und genau dieser Satz markiert.
- Wörtliche Wiederholungen sind im Text unterstrichen, sobald die Analyse sie kennt.
- Der Tab zeigt beide Arten untereinander; ein Klick führt zur Stelle, „Nebeneinander“ zeigt beide Stellen zugleich.

### Nicht-Ziele

- Keine Einstellungen für Schwellen oder Modell in der Oberfläche.
- Kein Vergleich über Buchgrenzen hinweg.
- Keine neue Art von Ansicht: die Split-Ansicht ist die vorhandene zweite Buchansicht.

## 2. Inhaltlich ähnlich: grob, dann im Satz bestätigt

**Gewählter Ansatz** (vom Autor bestätigt). Verworfen: nur die Schwelle der Blockvektoren anheben (bleibt themenlastig,
liefert keine Stelle zum Markieren) und alle Sätze des Buchs paarweise vergleichen (bei großen Büchern zu viele Vektoren
und Vergleiche für den Hintergrund).

1. **Vorauswahl auf Blockebene** wie heute (Blockvektor, Kosinus), aber großzügiger: ein Paar ist Kandidat ab
   `0,85` und gehört zu den 5 ähnlichsten eines der beiden Blöcke. Paare mit wörtlicher Folge bleiben ausgeschlossen.
2. **Sätze der beiden Blöcke:** der eigene Text des Blocks und seine Unterpunkte, in Sätze zerlegt (Ende bei `.`, `!`,
   `?` oder `…`, gefolgt von Leerraum, sowie am Absatzende). Sätze unter 5 Wörtern zählen nicht. Jeder Satz bekommt
   einen eigenen Vektor (dieselbe Rolle `query:` wie die Blöcke), gespeichert im selben Vektorspeicher
   (`book-vectors.db`) unter dem Hash seines Textes.
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

## 3. Im Text

- **Wörtlich:** jede gemeinsame Folge ist durchgezogen unterstrichen, auf beiden Seiten, **immer**, solange der
  Schalter „Wiederholungen zeigen“ an ist (wie heute für Gliederung und Leiste).
- **Inhaltlich:** die gemeldeten Sätze sind gepunktet unterstrichen, in einer zweiten Farbe, **nur solange der Tab
  „Wiederholungen“ offen ist**.
- Gewollte Paare sind nicht markiert.
- Ein Tooltip auf der Markierung nennt die andere Stelle („Auch in: Kapitel 5 › Lernen“, bei mehreren alle).
- Die Markierungen sind reine Anzeige wie die Datumsmarken: sie gehören nicht zum Dokument, zählen nicht als Änderung
  und kommen mit dem nächsten Analysestand neu. Beim Tippen bleiben sie bis dahin am Text haften oder verschwinden mit
  ihm.
- Der bisherige Blockmarker (Zahl am Block) entfällt. Zähler in der Leiste und `≈ n` in der Gliederung bleiben.

## 4. Der Tab „Wiederholungen“

Ein fünfter Tab rechts neben Relevant, Suche, Tagebuch und Abdeckung. Er ersetzt den Dialog; alles, was bisher den
Dialog öffnete (Zähler in der Leiste, Status in der Kopfleiste), öffnet den Tab und zeigt das betreffende Paar.

```
[Kapitel: alle ▾]   ☐ Gewollte anzeigen
Wörtlich (3)
  Kapitel 2 › Schlaf
    …am Abend davor.
    Schlaf festigt das Gedächtnis, weil…        ← die Folge hervorgehoben
    Deshalb lohnt es sich…
  ↔ Kapitel 5 › Lernen
    …wie oben gesagt.
    Schlaf festigt das Gedächtnis, weil…
    Ein weiterer Punkt…
  [Verweis →] [← Verweis] [Gewollt] [Nebeneinander]
Inhaltlich (5)
  …
```

- **Reihenfolge:** erst alle wörtlichen, darunter alle inhaltlichen, keine Untertabs. Innerhalb jeder Gruppe nach der
  Stelle der ersten Seite im Buch.
- **Ein Eintrag** ist ein Blockpaar. Jede Seite zeigt ihren Ort (Pfad der Überschriften) und die Stelle mit Kontext:
  der Satz davor und der Satz danach im selben Textblock, sonst „…“. Bei mehreren Folgen oder Satzpaaren in einem Paar
  steht jede Stelle für sich.
- **Klick auf eine Stelle:** zeigt sie im Buch (Hauptansicht), wie „Im Buch öffnen“ bisher.
- **„Nebeneinander“:** schaltet die Split-Ansicht ein, wenn sie aus ist, zeigt Seite A in der ersten und Seite B in der
  zweiten Ansicht.
- **Aktionen** wie bisher: Verweis in eine oder beide Richtungen, „Gewollt“ / „Nicht mehr gewollt“, Kapitelfilter,
  „Gewollte anzeigen“. Ausgegraut bei offenem Konflikt oder gesperrtem Editor.
- **Während der Analyse** zeigt der Tab, was schon bekannt ist, und darunter den Fortschritt („inhaltlich: 120 von 480
  Blöcken“); ohne Modell den Hinweis, dass inhaltliche Ähnlichkeit ein Modell braucht.
- **Leer:** „Keine Wiederholungen gefunden.“

## 5. Fehler

Wie bisher: scheitert ein Vektor, fällt dieser Satz oder Block heraus (Protokoll); scheitert ein Verweis oder
`repetitions.json`, bleibt alles unverändert und der Tab zeigt die Meldung.

## 6. Tests

- Kern: Satzzerlegung (Satzenden, Abkürzungen ohne Großbuchstaben danach, kurze Sätze, Unterpunkte); Bestätigung
  (Themenpaar ohne nahes Satzpaar fällt heraus, Paar mit nahem Satz zählt, höchstens 3 Satzpaare, ein Satz nur einmal);
  Ausschluss wörtlicher Paare bleibt.
- Tracker: Satzvektoren nur für Kandidatenblöcke, aus dem Speicher wiederverwendet; ohne Modell nur wörtlich.
- Editor (JS): Unterstreichungen beider Arten an den richtigen Textstellen, verschwinden ohne Änderung des Dokuments,
  inhaltliche nur mit offenem Tab.
- UI: Tab mit beiden Gruppen in dieser Reihenfolge, Kontext, Klick zeigt die Stelle, „Nebeneinander“ schaltet die
  Split-Ansicht ein und zeigt beide Stellen, Leiste öffnet den Tab beim Paar, Aktionen wie bisher.

## Entscheidungen des Bauenden (vom Autor nicht festgelegt)

- Satzschwelle 0,92 als Start, Vorauswahl 0,85 und 5 Kandidaten je Block, 3 Satzpaare je Paar.
- Satzende-Regel und Mindestlänge 5 Wörter.
- Kontext: ein Satz davor und danach im selben Block.
- Der Schalter „Wiederholungen zeigen“ steuert weiter alle Markierungen.
- Der Blockmarker entfällt zugunsten der Unterstreichung.
- Tooltip statt Klickaktion auf der Markierung (ein Klick in den Text bleibt Bearbeiten).
