# NoteEvolution – Datum je Textblock, „Zuletzt geändert“ und „Springen zum Tag“

Stand: 2026-10-19 · Status: Entwurf · Baut auf der Buchansicht (Editor, Gliederung), dem lokalen Sprachmodell (`2026-10-15-ollama-design.md`, Abschnitts-Zusammenfassungen) und der Abdeckung (`2026-10-16-abdeckung-design.md`) auf.

## 1. Ziel

- **Datum je Textblock:** Jeder Textblock im Buch zeigt in seinem Kopf, wann er erstellt und wann er zuletzt geändert wurde.
- **„Zuletzt geändert“:** Eine Liste links über der Gliederung zeigt die zuletzt geänderten Textblöcke. Ein Klick springt zum Block.
- **Höhe einstellbar:** Ein Schieber zwischen Liste und Gliederung stellt die Höhe ein. Sie bleibt über einen Neustart erhalten.
- **„Springen zum Tag“:** Das Datumsfeld steht im Journal-Reiter in der Filterzeile darüber und braucht keine eigene Zeile mehr.

### Erfolgskriterien

- Ein neu angelegter oder übernommener Textblock zeigt sofort „erstellt … · geändert …“ mit dem aktuellen Datum.
- Ändert man Text oder Status eines Blocks und speichert, ändert sich nur sein Änderungsdatum. Andere Blöcke und unveränderte Dateien bleiben unberührt.
- Bestehende Blöcke ohne Daten werden nicht rückwirkend beschriftet. Beim Öffnen und Speichern eines Buchs ändert sich keine Zeile, die der Nutzer nicht geändert hat.
- „Zuletzt geändert“ zeigt nach einer Änderung den Block oben, ohne Neustart.
- Die Höhe der Liste ist nach einem Neustart dieselbe.

## 2. Speicherung der Daten

- **Format:** Zwei Block-Eigenschaften in der Buchdatei, wie `id::` und `source::`:
  - `created:: 2026-10-19T14:32`
  - `updated:: 2026-10-19T14:32`

  Das ist Ortszeit, minutengenau, ISO 8601 ohne Zone (`yyyy-MM-ddTHH:mm`). Gelesen werden auch Werte mit Sekunden oder mit Zone. Unlesbare Werte gelten als fehlend und bleiben beim Speichern unverändert stehen.
- **Nur Textblöcke im Buch:** Überschriften, Notizen im Journal und Unterpunkte (Absätze) innerhalb eines Textblocks bekommen keine Daten. Eine Änderung an einem Unterpunkt ändert das Datum seines Textblocks.
- **Wann gesetzt wird:**
  - **Neuer Textblock**, ob im Editor getippt, aus einer Notiz übernommen, durch „Bemerkung anfügen“ angelegt oder durch Teilen eines Blocks entstanden: `created` und `updated` = jetzt.
  - **Geänderter Textblock**, wenn beim Speichern sein Text (einschließlich Unterpunkten) oder sein Status (`status::`) anders ist als vorher: `updated` = jetzt. `created` bleibt.
  - **Zusammengeführte Blöcke**, wenn der Editor zwei Blöcke zu einem verbindet: Der verbleibende Block bekommt `updated` = jetzt.
  - **Nicht als Änderung zählen:** Verschieben oder Umsortieren ohne Textänderung, Umbenennen einer Überschrift, Änderungen an anderen Eigenschaften (`id`, `source`), und Speichern ohne Unterschied.
- **Bestehende Blöcke ohne Daten:** Sie bleiben ohne Daten, bis sie das erste Mal geändert werden. Dann bekommen sie `updated`; `created` bleibt leer.
- **Zeitquelle:** die App-Uhr (`IClock`), damit Tests feste Zeiten setzen können.
- **Externe Änderungen:** Ändert ein anderes Programm die Datei, setzt NoteEvolution keine Daten nach. Die Daten werden aus der Datei gelesen, wie sie dort stehen.
- **PDF-Export:** Eigenschaften erscheinen wie bisher nicht.

## 3. Anzeige im Kopf des Textblocks

- **Ort und Aussehen:** im Editor rechts oben in jedem Textblock, in kleiner grauer Schrift (`--fg-muted`, etwa 11 px), nicht editierbar und ohne Einfluss auf den Text.
- **Inhalt:** `erstellt 12.10.2026 · geändert 19.10.2026 14:32`.
  - Fehlt ein Wert, entfällt sein Teil.
  - Fehlen beide, wird nichts angezeigt.
  - Das Änderungsdatum hat eine Uhrzeit; das Erstellungsdatum nur, wenn es am selben Tag wie das Änderungsdatum liegt.
- **Tooltip:** beide Zeitpunkte vollständig.
- **Neue Blöcke:** Ein im Editor neu getippter Block zeigt sein Datum nach dem nächsten Speichern (spätestens nach der Speicherpause des Editors). Bis dahin zeigt er nichts.
- **Technik:** Die Daten kommen als Attribute `created`/`updated` des Textblock-Knotens in den Editor (`EditorDocMapper`). Der Editor gibt sie nie zurück; sie werden nur beim Speichern serverseitig gesetzt.

## 4. „Zuletzt geändert“

### 4.1 Inhalt

- **Auswahl:** die 20 Textblöcke des aktuellen Buchs mit dem neuesten `updated`, neueste zuerst. Blöcke ohne `updated` kommen nicht vor.
- **Pro Eintrag:**
  - **Änderungsdatum:** „heute 14:32“, „gestern 09:10“, sonst „17.10.2026“.
  - **Zusammenfassung:** der erste Satz der Abschnitts-Zusammenfassung des Sprachmodells für den Abschnitt des Blocks (`IUnderstanding.Heading`, wie in „Wohin damit?“).
  - **Ohne Zusammenfassung**, also ohne Sprachmodell, wenn noch nicht berechnet oder beim Prolog: der Textanfang des Blocks (etwa 80 Zeichen).
  - **Darunter**, klein: der Abschnittspfad („Schlaf › Abendroutine“).
  - **Tooltip:** der Textanfang des Blocks.
- **Klick:** springt im Editor zum Block (`RevealElement`, wie in der Abdeckung). Wird der Abschnitt dabei gewechselt, speichert der Editor vorher.
- **Leer:** Gibt es keinen Eintrag, steht dort der Hinweis „Noch keine Änderungen mit Datum“.
- **Ohne Buch:** Ist kein Buch gewählt, wird die Liste ausgeblendet. Die Gliederung zeigt dann ihren bisherigen Platzhalter.

### 4.2 Aktualisierung

- **Wann:** nach jedem Speichern einer Buchseite, beim Wechsel des Buchs, bei externen Änderungen (`PagesChanged`) und wenn neue Zusammenfassungen ankommen.
- **Wie:** Die Liste wird auf dem UI-Thread aus dem Buch-Schnappschuss berechnet: ein Durchlauf über die Textblöcke, Sortierung, die ersten 20. Das ist auch bei großen Büchern unkritisch.

### 4.3 Schieber und Höhe

- **Anordnung:** In der linken Leiste stehen von oben nach unten „Zuletzt geändert“, ein waagerechter Schieber und die Gliederung.
- **Ziehen:** Der Schieber ändert die Höhe der Liste. Mindesthöhe ist 60 px für die Liste und 120 px für die Gliederung.
- **Speichern:** Die Höhe steht in `UiSettings.RecentListHeight` (Pixel, Vorgabe 180). Sie wird beim Loslassen gespeichert und beim Start wiederhergestellt. Passt sie nicht in die Leiste, wird sie auf das Erlaubte begrenzt, ohne den gespeicherten Wert zu ändern.
- **Tastatur:** Der Schieber ist fokussierbar. Pfeil hoch/runter ändert die Höhe in Schritten von 20 px.
- **Umsetzung:** Wie beim bestehenden `Splitter` des Notizbereichs, nur senkrecht. Entweder als Variante des vorhandenen Bausteins oder als kleiner eigener Baustein, wenn das klarer ist.

## 5. „Springen zum Tag“

Im Journal-Reiter steht das Datumsfeld „Springen zum Tag“ in der Filterzeile des Notizbereichs, hinter „Von“/„Bis“, statt in einer eigenen Zeile darunter. Verhalten und Beschriftung bleiben gleich. In den anderen Reitern erscheint es nicht.

## 6. Fehlerbehandlung

- **Unlesbare Datumswerte:** gelten als fehlend (Abschnitt 2). Es gibt keine Fehlermeldung, und die Werte werden nicht überschrieben, solange der Block nicht geändert wird.
- **Schreibgeschützte Buchseiten:** werden wie bisher nicht gespeichert und bekommen keine Daten.
- **Settings-Datei nicht schreibbar:** Die Höhe gilt für diese Sitzung und wird protokolliert (wie bei den anderen `UiSettings`).

## 7. Tests

- **`BookSync`:**
  - Ein neuer Block bekommt beide Daten.
  - Text- und Statusänderungen setzen `updated` und lassen `created` unverändert.
  - Verschieben, Umsortieren, Umbenennen einer Überschrift und Speichern ohne Unterschied ändern nichts. Die Datei ist byte-gleich.
  - Ein bestehender Block ohne Daten bekommt bei einer Änderung nur `updated`.
  - Bei einer Teilung bekommt der neue Block beide Daten.
  - Unlesbare Werte bleiben stehen.
- **Übernehmen und „Bemerkung anfügen“:** Der neue Textblock hat beide Daten.
- **Lesen:** Formate mit Sekunden und mit Zone werden verstanden; Unlesbares gilt als fehlend.
- **Editor-Abbildung:** `created`/`updated` kommen als Knotenattribute in den Editor, und das Zurückabbilden ignoriert sie.
- **„Zuletzt geändert“:**
  - Auswahl und Reihenfolge, höchstens 20.
  - Formate „heute“, „gestern“ und Datum.
  - Zusammenfassung vorhanden oder nicht vorhanden (dann Textanfang).
  - Ein Klick springt zum Block.
  - Leer-Hinweis; ohne Buch ausgeblendet.
  - Aktualisierung nach dem Speichern.
- **Schieber:** Ziehen und Tastatur ändern die Höhe innerhalb der Grenzen. Der Wert wird gespeichert und nach einem neuen Rendern wiederhergestellt.
- **Journal:** „Springen zum Tag“ steht in der Filterzeile; die alte eigene Zeile gibt es nicht mehr.

## 8. Nicht enthalten

- Rückwirkendes Datieren bestehender Blöcke, etwa aus dem Dateidatum oder einer Versionsgeschichte.
- Daten für Überschriften und Journal-Notizen.
- Eine Versionsgeschichte oder ein Vergleich früherer Fassungen.
- Eine eigene Sprachmodell-Zusammenfassung je Textblock.
