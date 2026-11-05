# Änderungshistorie je Textblock — Entwurf

Stand 2026-11-05, mit dem Nutzer im Chat abgestimmt.

## 1. Ziel

Jeder Textblock eines Buchs bekommt einen eigenen Verlauf, ähnlich der Dateiwiederherstellung von Obsidian, aber je Block. Der Verlauf steht in einer lokalen Datenbank, nicht im Text der Markdown-Datei. Es entsteht keine Version je Tastendruck, sondern nur an sinnvollen Punkten (Abschnitt 4).

Gilt für Textblöcke im Text eines Buchs samt ihren Unterpunkten, nicht für Überschriften, den Anhang und die Notizseiten.

## 2. Ablage

- **Datei:** `.noteevolution/history.db` im Vault (SQLite, wie die übrigen Daten der App). Wer den Vault samt diesem Ordner kopiert, nimmt den Verlauf mit.
- **Inhalt einer Version:** Text und Unterpunkte des Blocks, Status und Quellen, Zeitpunkt und Grund.
- **Aufbewahrung:** Versionen werden nicht automatisch gelöscht.
- **Fehler:** Eine kaputte oder gesperrte Datenbank verhindert nie das Speichern des Buchs. Der Fehler steht im Log, und die App zeigt einmal je Sitzung einen Hinweis. Eine unlesbare Datei wird nicht gelöscht, sondern beiseitegelegt (`history.db.defekt-<Zeit>`), und eine neue angelegt; der Verlauf ist einmalig und nicht neu berechenbar.

## 3. Wiedererkennen eines Blocks

- Ein Block wird über seine `id::` wiedererkannt. Die internen Schlüssel der App gelten nur bis zum nächsten Einlesen.
- **Vergabe:** Ein Block ohne `id::` bekommt sie bei seiner ersten Änderung, im selben Speichervorgang. Blöcke, die nie geändert werden, bleiben in der Datei unverändert.
- **Verschieben:** Ein verschobener Block behält über die `id::` seinen Verlauf.
- **Rückgängig:** Nimmt „Rückgängig“ die erste Änderung samt der `id::` zurück, bleibt der Verlauf beim Block: Hat ein Block ohne `id::` nach einem Rückgängig denselben Text wie der letzte Stand einer eben verschwundenen `id::`, bekommt er diese `id::` zurück.
- **Doppelte `id::`** (etwa ein in Logseq kopierter Block): Der Verlauf bleibt beim ersten Block in Lesereihenfolge; der zweite zählt, solange er die gleiche `id::` trägt, nicht und wird im Log erwähnt.

## 4. Wann eine Version entsteht

Die App vergleicht beim Speichern und beim Neueinlesen einer Buchseite jeden Block mit seinem zuletzt bekannten Stand. Damit sind Tippen, Knöpfe, Übernahmen, „Rückgängig“ und Änderungen in Logseq gleich erfasst. Verglichen wird der Text des Blocks samt Unterpunkten; `updated::` und andere Eigenschaften zählen nicht.

| Fall | Was gespeichert wird | Grund in der Liste |
| --- | --- | --- |
| Erste Änderung eines Blocks | der Stand vor der Änderung, sofort | „Erste Fassung“ |
| Text geändert oder ergänzt, dann 10 Minuten keine Änderung an diesem Block | der Stand nach der Pause | „Pause“ |
| Größeres Löschen | der Stand vor dem Löschen, sofort | „Vor dem Löschen“ |
| App oder Vault schließen, anderer Vault | offene Stände | „Beim Schließen“ |
| Beim Öffnen weicht ein Block mit Verlauf von seiner letzten Version ab (Absturz, Änderung in Logseq bei geschlossener App) | der Stand aus der Datei | „Beim Öffnen“ |
| Vor dem Wiederherstellen | der aktuelle Stand | „Vor dem Wiederherstellen“ |

- **Größeres Löschen:** Der Text ist gegenüber seinem längsten Stand seit der letzten Version um mindestens 200 Zeichen oder mindestens ein Viertel kürzer geworden; so zählt auch langsames Löschen über mehrere Speichervorgänge. Blöcke mit weniger als 40 Zeichen zählen nicht. Gesichert wird dieser längste Stand; das anschließende Weiterschreiben wird nach der Pause zu einer eigenen Version.
- **Blöcke ohne `id::` bei Änderungen in Logseq:** Die App kann sie nach dem Neueinlesen nicht wiedererkennen. Ihr neuer Text gilt als Ausgangsstand ohne Version; von ihrer nächsten Änderung in der App an haben sie einen Verlauf. Blöcke mit `id::` werden auch bei Änderungen in Logseq verglichen.
- **10-Minuten-Zähler:** je Block; jede weitere Änderung an diesem Block startet ihn neu. Die Zeit läuft über den `TimeProvider` der App.
- **Gleicher Text:** Stimmt der Text mit der letzten Version überein, entsteht keine neue.

## 5. Gelöschte Blöcke

- **Verschwindet ein Block aus dem Buch,** wird sein letzter Stand gesichert, und er gilt als gelöscht, mit Buch, Abschnittspfad und Vorgänger (`id::` des Blocks davor, soweit vorhanden).
- **Keine Löschung:** Steht sein Text beim selben Vergleich in einem anderen Block des Buchs (Zusammenführen), ist er nicht gelöscht. Sein Verlauf bleibt erhalten, er erscheint aber nicht in der Liste.
- **Liste:** „Gelöschte Blöcke“ im Menü „Assistenten“, je Buch: Datum, Abschnitt, Textanfang, neueste zuerst. Von dort lässt sich der Verlauf ansehen.
- **Wiederherstellen eines gelöschten Blocks:** hinter seinen früheren Vorgänger, sonst ans Ende seines früheren Abschnitts, sonst ans Ende des Buchs. Er behält `id::`, Verlauf und seinen zuletzt gesicherten Status und verschwindet aus der Liste. Quellen kommen zurück, soweit die Notizen noch existieren; deren Verwendungen werden wieder eingetragen.

## 6. Historie ansehen und wiederherstellen

- **Knopf „Historie“** in der Leiste am aktiven Textblock; am Unterpunkt öffnet er den Verlauf seines Textblocks. Ohne Versionen ist er ausgegraut, der Tooltip nennt den Grund. An Überschriften fehlt er.
- **Fenster:**
  - Links die Versionen, neueste zuerst, mit Datum, Uhrzeit und Grund.
  - Rechts die gewählte Version im Vergleich zur aktuellen Fassung, wortweise: Entferntes rot und durchgestrichen, Neues grün.
  - Unten „Wiederherstellen“ und „Schließen“.
- **Wiederherstellen:** Text und Unterpunkte des Blocks werden durch die gewählte Version ersetzt; Quellen, Status und übrige Eigenschaften des Blocks bleiben. Das wirkt wie eine Bearbeitung im Editor: Die Verwendungen in den Notizen folgen, und „Rückgängig“ nimmt es zurück.
- **Gesperrt:** Bei schreibgeschütztem Buch oder offenem Konflikt ist „Wiederherstellen“ ausgegraut, mit Grund im Tooltip. Ansehen bleibt möglich.

## 7. Knopf „Entfernen“

Die Leiste hat schon den Befehl zum Löschen des Elements am Cursor, er heißt aber überall „Abschnitt löschen“, auch an Textblöcken und Unterpunkten.

- **Beschriftung nach Element:** an Textblock und Unterpunkt „Entfernen“, an einer Überschrift weiter „Abschnitt löschen“. Der Tooltip sagt, was mitgeht (der Block samt Unterpunkten).
- **Verhalten wie bisher:** Unterpunkte gehen mit; bei Bereich „ohne Unterelemente“ und vorhandenen Unterpunkten ist der Knopf gesperrt; die Verwendungen in den Notizen werden entfernt; „Rückgängig“ holt alles zurück. Es gibt keine Nachfrage.
- **Mit Verlauf:** Ein so entfernter Block erscheint in „Gelöschte Blöcke“.

## 8. Nicht enthalten

- Verlauf der ganzen Datei oder des Buchs, von Überschriften und Notizen.
- Automatisches Löschen alter Versionen.
- Vergleich zweier alter Versionen untereinander.
- Rückwirkender Verlauf für Änderungen vor dieser Funktion.
