# NoteEvolution – Vorberechnete Zuordnung, „Relevant“ in zwei Gruppen und Abdeckung

Stand: 2026-10-16 · Status: Entwurf · Baut auf dem lokalen Sprachmodell (`2026-10-15-ollama-design.md`) und dem verbesserten „Genauer prüfen“ (Master 9fd08e3) auf.

## 1. Ziel

„Genauer prüfen“ wird im Hintergrund für alle noch nicht verwendeten Notizen vorberechnet. Aus diesen Zuordnungen entstehen ohne weitere Rechenzeit drei Dinge:

- „Wohin damit?“ zeigt die Empfehlung des Sprachmodells sofort.
- „Relevant“ zeigt oben die Notizen, die das Modell dem aktuellen Abschnitt zugeordnet hat.
- Eine Abdeckungsansicht zeigt, wo das Buch Material hat, wo es fehlt und welche Notizen keinen passenden Ort haben.

Zielrechner bleibt das Notebook ohne passende Grafikkarte (i5-10210U, 16 GB RAM). Eine Prüfung dauert dort im Mittel etwa 35 s. Die Vorberechnung läuft deshalb nur in den Ruhezeiten der bestehenden Hintergrundberechnung.

### Erfolgskriterien

- **Sofort da:** Ist die Vorberechnung für eine Notiz fertig, zeigt „Wohin damit?“ die Empfehlung mit Begründung ohne Wartezeit.
- **Gleiche Qualität wie der Knopf:** Die vorberechnete Empfehlung entsteht aus genau der Anfrage, die auch „Genauer prüfen“ stellt. Ziel auf dem Referenzbuch: richtiges Unterkapitel an erster Stelle bei mindestens 65 %. Gemessen sind heute 68 %, das Ziel lässt etwas Spielraum für die zusätzliche Passungsangabe.
- **Nur Neues wird gerechnet:** Geänderte Notizen oder Kapitel lösen nur die Prüfungen aus, deren Anfrage sich dadurch ändert.
- **Passung aussagekräftig:** Auf dem Referenzbuch hat jede Notiz einen richtigen Ort. Höchstens 10 % der Notizen bekommen „kaum“. Von den Notizen mit „gut“ sind mindestens 75 % richtig einsortiert, also mehr als im Durchschnitt.
- **Rechner bleibt nutzbar:** weiterhin genau eine Anfrage gleichzeitig. Die Pausen beim Tippen und im Akkubetrieb sowie der Pause-Knopf gelten auch für Prüfungen. Die Abdeckungszahlen werden außerhalb des UI-Threads berechnet.
- **Ohne Sprachmodell wie heute:** „Relevant“ hat dann nur die heutige Liste, die Gliederung zeigt nur übernommene Notizen, der Reiter „Abdeckung“ erklärt, was fehlt.

## 2. Bausteine

| Baustein | Aufgabe |
|---|---|
| `PlacementCheck` (vorhanden) | Baut die Anfrage, liest die Antwort. Neu: die Angabe `passung` und der Abschnitt, den die Embedding-Suche vorne sah. |
| `UnderstandingItems` | Neu: Prüfaufträge (`UnderstandingKind.Placement`) für die unverwendeten Notizen eines Buchs. |
| `UnderstandingWorker` (vorhanden) | Neu: Prüfaufträge als letzte Gruppe der Warteschlange. Sendet die fertige Anfrage, speichert die gelesene Antwort. |
| `UnderstandingStore` (vorhanden) | Speichert Prüfergebnisse wie Zusammenfassungen, nur ohne Vektor. |
| `StoreUnderstanding` / `IUnderstanding` | Neu: `Placement(Book, Guid noteKey)` liefert das Ergebnis und ob es veraltet ist. |
| `VaultSession` | Stellt die Prüfaufträge zusammen (im Hintergrund) und übergibt sie dem Worker. Berechnet die Abdeckung. |
| `CoverageCalculator` (neu, rein) | Zählt pro Abschnitt übernommene und zugeordnete Notizen, leere Abschnitte, Notizen ohne Ort und unsichere Zuordnungen. |
| `OutlinePane`, `OutlineItem` | Schalter „Abdeckung“ und Balken je Überschrift. |
| `RelevantTab` | Gruppen „Hierher eingeordnet“ und „Weitere ähnliche“. |
| `CoverageTab` (neu) | Reiter „Abdeckung“ im Notizbereich. |
| `NoteCard` | Zeigt die gespeicherte Empfehlung sofort, markiert veraltete. |

## 3. Vorberechnung

### 3.1 Umfang

- **Welche Notizen:** alle Notizen (Hauptpunkte) des Vaults ohne `used-in`-Eintrag, unabhängig von der Länge, neueste zuerst. Notizen auf schreibgeschützten Seiten sind eingeschlossen. Sie werden nur nicht übernommen, eine Zuordnung zeigen sie trotzdem.
- **Für welches Buch:** das aktuelle Buch (`AppState.CurrentBook`). Wechselt das Buch, werden dessen Prüfaufträge gebildet. Ergebnisse anderer Bücher bleiben gespeichert und gelten wieder, wenn ihre Anfrage unverändert ist.
- **Wann:** Prüfaufträge eines Buchs werden erst gebildet, wenn alle Zusammenfassungen dieses Buchs berechnet sind. Sonst würde sich die Anfrage mit jeder neuen Zusammenfassung ändern, und die Ergebnisse wären schlechter und gleich wieder veraltet.

### 3.2 Anfrage und Antwort

- **Die Anfrage** ist genau die von „Genauer prüfen“ (`PlacementCheck.Prompt` mit `PlacementCheck.TopicLimit` Themen, allen Platzierungen und den Zusammenfassungen der Abschnitte). Knopf und Vorberechnung teilen sich also Anfrage und gespeichertes Ergebnis.
- **Neue Angabe `passung`:** Das JSON-Schema bekommt nach `stelle` das Feld `passung` mit den Werten `"gut"`, `"teilweise"` und `"kaum"`. Die Systemanweisung erklärt: „kaum“ heißt, keiner der Abschnitte passt inhaltlich wirklich. Fehlt die Angabe oder ist sie ungültig, gilt „teilweise“, die Antwort bleibt verwertbar. `UnderstandingPrompts.CheckTokens` bleibt bei 120.
- **Abschnitt der Embedding-Suche:** Beim Bau der Anfrage merkt sich `PlacementCheck` den angebotenen Abschnitt, der die beste Platzierung enthält (höchste Ähnlichkeit). Gibt es keine Platzierung, ist es der Abschnitt des besten Themas. Er wird mit dem Ergebnis gespeichert.
- **Unsicher** ist eine Zuordnung, wenn der vom Modell gewählte Abschnitt weder dieser Embedding-Abschnitt ist noch ihn enthält oder in ihm liegt.
- **Das Ergebnis** (`PlacementVerdict`) erhält neu `Fit` (`PlacementFit.Good`, `Partial`, `Poor`) und `EmbeddingSectionKey`. Gespeichert wird es als JSON-Text im Feld `Text` des Eintrags, ohne Vektor: Abschnitt, Stelle (oder neu am Ende), Begründung, Passung, Embedding-Abschnitt, Zusammenfassung.

### 3.3 Fingerabdruck und „veraltet“

- **Fingerabdruck** = SHA-256 über Prompt-Version, Chat-Modell, Systemanweisung, Anfragetext und Schema. Ändert sich irgendetwas, das das Modell zu sehen bekäme, ändert sich der Fingerabdruck. Das gilt für den Notiztext, die angebotenen Kapitel und Abschnitte, deren Zusammenfassungen und die Textstellen.
- **Identität** = `placement|<Buchname>|<Identität der Notiz>`, mit der Notiz-Identität aus `UnderstandingItems`. Darüber findet die App das letzte Ergebnis, auch wenn es veraltet ist.
- **Aktuell / veraltet / fehlt:** Gibt es einen Eintrag zum aktuellen Fingerabdruck, ist das Ergebnis aktuell. Sonst wird das letzte Ergebnis derselben Identität als veraltet gezeigt, falls sein Abschnitt noch im Buch existiert. Sonst fehlt das Ergebnis.
- **Kosten:** Eine Anfrage zusammenzustellen braucht nur Embeddings (vorhanden oder zwischengespeichert) und Kosinus-Vergleiche. Die Session bildet die Anfragen für alle unverwendeten Notizen im Hintergrund (`Task.Run`, nur Schnappschüsse). Sie tut das höchstens einmal je Änderung des Buchs oder der Notizen, gebündelt wie die bestehende Übergabe an den Worker. Ergebnisse werden pro Book-Instanz und Notiz-Fingerabdruck zwischengespeichert.

### 3.4 Reihenfolge und Fortschritt

- **Warteschlange:** Die Prüfaufträge sind die letzte Gruppe. Davor kommen wie bisher das aktuelle Kapitel, das übrige Buch, die Kernaussagen der Notizen und die Überschriften anderer Bücher. Innerhalb der Gruppe gilt: neueste Notiz zuerst, geänderte Einträge (ältere Identität vorhanden) vor neuen.
- **„Genauer prüfen“ von Hand** bleibt eine vorgezogene Anfrage (`RunNextAsync`). Ihr Ergebnis wird unter demselben Fingerabdruck gespeichert. Der Hintergrund überspringt die Notiz dann.
- **Fehler:** nach 3 Fehlversuchen Überspringen bis zur nächsten Änderung, wie bei den anderen Einträgen. Eine unverwertbare Antwort zählt als Fehlversuch.
- **Kopfzeile:** Der Fortschritt zählt die Prüfaufträge mit. Die Restzeit nutzt die gemessene Dauer je Art: Zusammenfassung, Kernaussage und Prüfung werden getrennt gemittelt.
- **Aufräumen:** Prüfergebnisse werden wie die anderen Einträge über `Prune` behalten oder entfernt (lebende Fingerabdrücke und Identitäten).

## 4. „Wohin damit?“

- **Aktuelles Ergebnis:** Die Empfehlung steht beim Öffnen sofort oben in der Liste (Thema › Stelle, Begründung). Sie wird so angezeigt wie heute nach „Genauer prüfen“. Neu ist ein kleines Passungszeichen: gut, teilweise oder kaum, mit Tooltip.
- **Veraltetes Ergebnis:** Die Empfehlung steht ebenfalls oben, mit dem Hinweis „veraltet“. Der Knopf „Genauer prüfen“ bleibt sichtbar.
- **Kein Ergebnis:** wie heute.
- **Knopf „Genauer prüfen“:** erscheint nur, wenn das Ergebnis fehlt oder veraltet ist und das Sprachmodell bereit ist.
- **Neues Ergebnis während die Liste offen ist:** Kommt ein Ergebnis im Hintergrund an, ersetzt es die Empfehlung. Die übrige Liste lädt nicht neu.

## 5. „Relevant“ in zwei Gruppen

- **„Hierher eingeordnet“ (oben):** unverwendete Notizen, deren Ergebnis im aktuellen Buch auf den aktuellen Abschnitt oder einen seiner Unterabschnitte zeigt. Aktuelle und veraltete Ergebnisse zählen mit, veraltete sind markiert. Sortiert nach Datum, neueste zuerst. Der Tooltip zeigt die Begründung und die Passung. Die Filter des Notizbereichs (Datum, „Verwendete ausblenden“) gelten auch hier.
- **„Weitere ähnliche“ (darunter):** die heutige Liste ohne die Notizen der oberen Gruppe.
- **Leere obere Gruppe:** keine Gruppenüberschriften, Aussehen wie heute.
- **Klick auf einen Abschnitt der Abdeckung** (Abschnitt 6) wählt den Abschnitt und öffnet diesen Reiter.

## 6. Abdeckung

### 6.1 Zahlen (`CoverageCalculator`)

Eingaben: das aktuelle Buch, alle Notizen und die Prüfergebnisse (aktuell oder veraltet). Für jeden Abschnitt (Überschrift) gilt:

- **Übernommen:** verwendete Notizen mit einem `used-in`-Eintrag auf dieses Buch, dessen Block-ID in diesem Abschnitt liegt (direkt, nicht in Unterabschnitten). Einträge ohne Block-ID oder mit unbekanntem Block zählen nur in der Buchsumme „übernommen ohne Stelle“.
- **Zugeordnet:** unverwendete Notizen, deren Ergebnis diesen Abschnitt nennt. Ist die Stelle ein Textblock, gilt der Abschnitt dieses Blocks.
- **Summen:** Eine Überschrift zeigt ihre eigenen Zahlen plus die aller Unterabschnitte.
- **Leer:** Ein unterster Abschnitt (ohne Unterabschnitte) mit 0 übernommenen und 0 zugeordneten Notizen.
- **Ohne passenden Ort:** unverwendete Notizen mit Passung „kaum“.
- **Unsicher:** zugeordnete Notizen, deren Zuordnung nach 3.2 unsicher ist. Sie zählen in ihrem Abschnitt mit und werden im Tooltip ausgewiesen.
- **Geprüft:** unverwendete Notizen mit aktuellem Ergebnis im Verhältnis zu allen unverwendeten Notizen.

Die Rechnung ist rein und liefert einen unveränderlichen Schnappschuss (`Coverage`).

### 6.2 Balken in der Gliederung

- **Schalter:** oben in der Gliederung schaltet „Abdeckung“ die Balken ein und aus. Gespeichert wird das in `UiSettings.ShowCoverage`, Vorgabe aus.
- **Balken:** Jede Überschrift zeigt rechts einen kleinen Balken aus zwei Teilen, übernommen (Grün) und zugeordnet (Orange), dazu die Gesamtzahl. Die Balkenlänge ist relativ zur größten Gesamtzahl unter den Überschriften derselben Ebene.
- **Leere unterste Abschnitte:** Sie zeigen statt des Balkens eine 0 in Warnfarbe.
- **Tooltip:** z. B. „3 übernommen · 2 zugeordnet (1 unsicher)“.
- **Klick:** Ein Klick auf Balken oder Zahl wählt den Abschnitt (wie ein Klick auf die Überschrift) und öffnet „Relevant“.
- **Ohne Sprachmodell oder ohne Prüfergebnisse:** nur die grünen Anteile. Die rote 0 erscheint erst, wenn alle unverwendeten Notizen geprüft sind; sonst wäre „leer“ nur vorläufig.

### 6.3 Reiter „Abdeckung“

Neuer Reiter im Notizbereich, nach „Journal“. Der Vorgabe-Reiter bleibt „Journal“.

- **Kennzahlen:** geprüft „84/120“, mit Restzeit, solange gerechnet wird. Dazu leere Abschnitte, Notizen ohne passenden Ort und unsichere Zuordnungen.
- **Abschnittsliste:** alle untersten Abschnitte als Pfad mit Balken und Zahl. Sortierbar nach „Buchreihenfolge“ (Vorgabe) oder „Wenigste zuerst“. Ein Klick wählt den Abschnitt im Editor.
- **„Ohne passenden Ort“:** Diese Notizen erscheinen als gewohnte Notizkarten (`NoteCard`) mit allen Knöpfen, neueste zuerst. Darunter stehen, eingeklappt, „Unsichere Zuordnungen“ als Notizkarten mit der Empfehlung.
- **Ohne Sprachmodell:** Der Reiter zeigt den Hinweis „Die Abdeckung braucht das Sprachmodell (Einstellungen › Sprachmodell)“. Die Abschnittsliste zeigt dann nur die übernommenen Notizen.
- **Vor der ersten fertigen Prüfung:** Kennzahlen und Liste zeigen das, was schon da ist, mit dem Fortschritt „geprüft 0/120“.

### 6.4 Aktualisierung

- **Auslöser:** Die Abdeckung wird neu berechnet bei einem neuen Prüfergebnis (`UnderstandingStore.EntryChanged`), bei Änderungen an Seiten oder Notizen (bestehende Ereignisse) und beim Wechsel des Buchs.
- **Ablauf:** gebündelt mit 1 s Verzögerung. Die Rechnung läuft im Hintergrund auf Schnappschüssen, das Ergebnis wird über den UI-Thread übergeben. Das neueste Ergebnis gewinnt, ältere werden verworfen.
- **Wer rechnet:** nur, wenn Balken oder Reiter sichtbar sind. Sonst wird beim nächsten Sichtbarwerden gerechnet.

## 7. Fehlerbehandlung

- **Fehlerhafte gespeicherte Ergebnisse:** Unlesbares JSON gilt als fehlendes Ergebnis und wird neu berechnet.
- **Verschwundene Stellen:** Nennt ein Ergebnis einen Abschnitt oder Block, der nicht mehr im Buch ist, wird es nicht angezeigt und nicht gezählt. Die neue Anfrage hat ohnehin einen anderen Fingerabdruck.
- **Störungen im Store:** Store-Fehler und ein geschlossener Store liefern „kein Ergebnis“ (wie bei `StoreUnderstanding` heute), die Oberfläche fällt auf das Verhalten ohne Sprachmodell zurück.
- **Fehler beim Bau der Anfragen:** Wirft der Bau der Prüfanfragen im Hintergrund eine Ausnahme, wird sie protokolliert. Die übrigen Gruppen der Warteschlange laufen weiter.
- **Fehler in der Abdeckungsrechnung:** Eine Ausnahme wird protokolliert, die Anzeige behält den letzten Stand.

## 8. Tests und Messung

### 8.1 Automatische Tests

- **`PlacementCheck`:** Schema mit `passung`; Lesen von gut, teilweise, kaum, fehlend und ungültig; Embedding-Abschnitt (beste Platzierung, sonst bestes Thema); Regel für „unsicher“.
- **Prüfaufträge:** nur unverwendete Notizen, neueste zuerst; erst nach allen Zusammenfassungen des Buchs; Fingerabdruck ändert sich mit Notiztext, Zusammenfassung oder Textstelle und bleibt bei unbeteiligten Kapiteln gleich; Identität je Buch.
- **Worker:** Prüfaufträge als letzte Gruppe nach den anderen; Ergebnis gespeichert; unverwertbare Antwort ist ein Fehlversuch; ein manuelles Ergebnis wird nicht noch einmal berechnet.
- **`StoreUnderstanding.Placement`:** aktuell, veraltet, fehlt; verschwundener Abschnitt; unlesbares JSON.
- **`CoverageCalculator`:** übernommen mit und ohne Block-ID; zugeordnet über Abschnitt und über Textblock; Summen; leer erst nach vollständiger Prüfung; ohne Ort; unsicher.
- **UI (bUnit):**
  - „Wohin damit?“ zeigt die gespeicherte Empfehlung sofort, veraltet mit Hinweis.
  - „Relevant“ zeigt zwei Gruppen ohne Doppelte und ohne leere Gruppe.
  - Die Gliederung zeigt Balken, Tooltip und rote 0; Schalter und Einstellung bleiben erhalten.
  - Der Reiter zeigt Kennzahlen, Sortierung, „ohne Ort“ und den Hinweis ohne Sprachmodell.
  - Ein Klick wählt den Abschnitt bzw. öffnet „Relevant“.

### 8.2 Messung

`tests/quality/measure.cs` misst zusätzlich:

- den Anteil „richtiges Unterkapitel an 1. Stelle“ wie bisher;
- die Verteilung der Passung (gut, teilweise, kaum);
- den Anteil richtig einsortierter Notizen je Passungswert;
- den Anteil unsicherer Zuordnungen und wie oft diese falsch sind.

Die Ergebnisse werden mit den Zielwerten aus Abschnitt 1 berichtet, auch wenn sie verfehlt werden. Die Referenzdaten bleiben unverändert.

## 9. Nicht enthalten

- **Eigene Prüfung pro Abschnitt für „Relevant“** (Weg B aus der Besprechung): Sie wird erst nachgezogen, wenn Mehrfachtreffer fehlen.
- **Vorberechnung für Bücher, die nicht geöffnet sind,** und für verwendete Notizen.
- **Automatisches Einsortieren oder Übernehmen:** Das Modell schlägt nur vor.
- **Mehrere Notizen in einer Anfrage.**
