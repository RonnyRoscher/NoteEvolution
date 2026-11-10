# Verwendungen an den einzelnen Blöcken — Entwurf

Stand 2026-11-10, mit dem Nutzer abgestimmt (Nachtragen: Variante A, Assistent).

## 1. Ausgangslage

- **Übernehmen** (`LinkService.Adopt`, Ziehen, „Übernehmen“ im Notizbereich): Nur der oberste kopierte Block bekommt `id::` und `source:: ((Notiz))`. Die Unterpunkte der Notiz werden Details ohne eigene Quelle.
- **Übernehmen in einen Block** (`AdoptInto`): Die Quelle kommt immer an den umschließenden Textblock. Ein Detail bekommt nie eine eigene.
- **Als Überschrift übernehmen** (`AdoptAsHeading`): Jeder direkte Unterpunkt wird ein Textblock mit eigener Quelle. Tiefere Unterpunkte werden Details ohne Quelle.
- **Auf der Notizseite** bekommt nur der übernommene Notizblock `used-in::`. Seine Unterpunkte gelten als verwendet, weil sie die Verwendung ihres Vorfahren erben (`NoteBlock.IsUsedBlock`). Ausnahme: der Vorfahr trägt `used-without-children:: true`.
- **Folge:** Löscht der Nutzer im Buch den Elternblock, um dessen Unterpunkte eine Ebene höher einzusortieren, verschwindet die einzige Verknüpfung. Die Notiz verliert ihre Verwendung, und mit ihr gelten alle Unterpunkte als unverwendet, obwohl ihr Text noch im Buch steht.

Schon heute vorhanden und weiter genutzt:

- Ein Detail mit eigenem `id::` und `source::` ist ein verknüpfter Block. Das kommt heute nur nach „Ausrücken“ oder „Als Überschrift“ vor.
- **Löschen** behandelt verknüpfte Blöcke im ganzen Teilbaum (`SourceValue.LinkedBlocksIn`):
  - „Entfernen“, „Abschnitt löschen“ und das Löschen im Editor (`BookSync.Remove` → `BlockDeleted`).
- **Die Quellenliste der Leiste** zeigt die verknüpften Blöcke des Teilbaums.
- **„Ausrücken“ eines Details** (`ManuscriptEditor.OutdentDetail`) lässt dem Detail seine `id::` und `source::`.

## 2. Ziel

- Jeder übernommene Block hängt selbst an dem Notizblock, aus dem sein Text stammt. Das gilt für den obersten Block und für jedes Detail aus einem Unterpunkt.
- Umbauen im Buch lässt jede Verknüpfung an ihrem Block:
  - Löschen des Elternblocks, Ausrücken, Verschieben und „Als Überschrift“.
  - Nur der gelöschte Block verliert seine Verknüpfung.
  - Nur seine Notizstelle gilt danach als unverwendet.
- Verknüpfungen wandern nur beim Zusammenführen, und zwar in den Block, der bleibt.
- Bestehende Bücher lassen sich mit einem Assistenten auf diesen Stand bringen.

## 3. Übernehmen

### 3.1 Grundregel

- **Jeder kopierte Block** bekommt eine eigene Verknüpfung zu „seinem“ Notizblock. „Seiner“ ist der Block, dessen Text er trägt:
  - `id::`, `source:: ((Notizblock))`,
  - die Daten (`created::`, `updated::`) wie heute beim obersten Block.
- **Jeder dieser Notizblöcke** bekommt `id::` (falls nötig) und seinen eigenen Eintrag `used-in:: [[Buch]] ((Buchblock))`.
- **Der Status** `status:: relevant` bleibt wie heute nur an den neuen obersten Blöcken. Details erben die Anzeige wie bisher.
- **Schreiben auf der Notizseite:**
  - Alle Notizblöcke einer Übernahme liegen auf derselben Notizseite. Sie wird einmal geschrieben.
  - Kann sie nicht geschrieben werden, kommt je Notizblock ein Eintrag nach `pending.json`, wie heute für den einen.

### 3.2 Die einzelnen Wege

| Weg | oberster Block / Text an der Einfügestelle | Details aus Unterpunkten |
|---|---|---|
| `Adopt` (neuer Textblock, auch Ziehen) | Textblock → Notizblock | jedes Detail → sein Unterpunkt |
| `AdoptInto` am Cursor | das Element am Cursor (Textblock oder Detail) bekommt die Quelle des Notizblocks; ein Detail bekommt dafür `id::` | jedes eingefügte Detail → sein Unterpunkt |
| `AdoptInto` nach einem Detail / als erstes Kind | das neue Detail → Notizblock | seine Details → ihre Unterpunkte |
| `AdoptAsHeading` | wie heute (jeder direkte Unterpunkt ein Textblock mit Quelle; weitere Zeilen des Notizblocks → erster Textblock mit Quelle des Notizblocks) | neu: jedes tiefere Detail → sein Unterpunkt |
| `Link` (bestätigte Zuordnung) | unverändert: nur Textblock ↔ Notizblock | — |

- **Am Cursor:** Steht die Notiz schon in der Quelle des Elements, wird sie nicht doppelt eingetragen. Der umschließende Textblock bekommt die Quelle nicht mehr, wenn das Element ein Detail ist.
- **„Ohne Unterpunkte“ und „Verwendete ausblenden“:** Wie heute wird nur kopiert, was gezeigt bzw. gewählt ist. Nur die kopierten Unterpunkte bekommen eine Verknüpfung. Weggelassene bleiben, wie sie sind.

### 3.3 Verwendet-Zustand auf der Notizseite

- Die Verwendung eines Notizblocks gilt bei neuen Übernahmen nur für seinen eigenen Text. Seine Unterpunkte haben eigene Einträge.
- Deshalb setzt jede neue Übernahme am Notizblock die vorhandene Markierung `used-without-children:: true`, wenn er Unterpunkte hat. Bedeutung wie bisher: „jede Verwendung nahm ihn ohne seine Unterpunkte“. Das gilt auch für jeden kopierten Unterpunkt mit eigenen Unterpunkten.
- Die Regel für die Markierung über mehrere Verwendungen bleibt (`UsedAloneOnly`): Sie steht nur, solange jede Verwendung des Blocks ihn allein nahm. Eine alte Übernahme mit Erben hält die Markierung also fern, und ihr Erben bleibt richtig.
- `NoteBlock.IsUsedBlock`, `IsHiddenWhenUsedHidden` und `ShownChildren` bleiben unverändert. Damit gilt:
  - Ein Unterpunkt ist verwendet, solange er einen eigenen Eintrag hat (oder ein alter Vorfahr ihn vererbt).
  - „Verwendete ausblenden“ blendet jeden verwendeten Unterpunkt einzeln aus.
  - Ein Hauptpunkt mit noch unverwendeten Unterpunkten bleibt als deren Kopf sichtbar.
  - Das ✓ im Journal steht an jedem Unterpunkt einzeln.

## 4. Umbauen im Buch

- **Löschen** („Entfernen“, „Abschnitt löschen“, Löschen im Editor): unverändert. Jeder verknüpfte Block im gelöschten Teilbaum verliert seine Verwendung. Weil jetzt auch Details verknüpft sind, gilt genau ihre Notizstelle danach als unverwendet.
- **Elternblock im Editor löschen, Unterpunkte rücken auf:**
  - Die Unterpunkte bleiben dieselben Blöcke mit ihren Eigenschaften.
  - `BookSync` darf sie nicht als gelöscht melden. Nur der gelöschte Block erzeugt `BlockDeleted`.
  - Das wird durch einen Test festgeschrieben (Abschnitt 8). Weicht `BookSync` heute davon ab, wird es behoben.
- **Ausrücken, Einrücken, Verschieben, „Als Überschrift“, „Neue Überschrift danach“:** Die Blöcke behalten `id::` und `source::`, wie heute schon. Keine Änderung nötig.
- **Teilen** (`BlockSplit`): unverändert. Der abgeteilte Block bekommt die Quellen des geteilten und die Notizen einen Eintrag für ihn.
- **Eine Verknüpfung entfernen** (×-Knopf an einer Quelle der Leiste):
  - Sie entfernt die Quelle an dem Block, der sie trägt. Das kann jetzt auch ein Detail des Textblocks sein.
  - `RemoveSource` nimmt dafür den Schlüssel des tragenden Blocks statt immer den des Textblocks.

## 5. Zusammenführen

- **`MergeTextBlocks`:** unverändert. Der erste Block bekommt die Vereinigung der Quellen der zusammengeführten Textblöcke, und die Notizen zeigen auf ihn. Die Details wandern mit ihren eigenen Verknüpfungen mit.
- **`MergeDetails`** (Details in den eigenen Text eines Textblocks oder Details zusammenführen), geändert:
  - Die Quellen der zusammengeführten Details gehen an das Element, in dessen Text sie aufgehen (den Kopf). Bisher gingen sie an den umschließenden Textblock.
  - Jede Notiz, deren `used-in::` auf ein zusammengeführtes Detail zeigte, zeigt danach auf den Kopf. Der Kopf bekommt dafür bei Bedarf eine `id::`.
  - Ist der Kopf der Textblock selbst, ändert sich nichts gegenüber heute.
- Die Undo-Regeln beider Befehle bleiben.

## 6. Rückgängig

- „Rückgängig“ einer Übernahme nimmt alle Verknüpfungen und Einträge zurück, die sie gesetzt hat, auch die der Unterpunkte und die Markierung `used-without-children`.
- Es gilt dieselbe Bedingung wie heute: Das Buch ist noch so, wie die Übernahme es ließ.
- **Notizen:** Eine Notizseite, die noch so ist, wie die Übernahme sie ließ, bekommt ihre alten Zeilen zurück. Sonst wird jeder Eintrag einzeln entfernt und die Markierung so gesetzt, wie die übrigen Verwendungen es verlangen.

## 7. Assistent „Verknüpfungen der Unterpunkte nachtragen“

Neuer Eintrag im Menü „Assistenten“: „Verknüpfungen der Unterpunkte nachtragen …“.

### 7.1 Was er sucht

- Er geht über alle Bücher des Vaults und darin über jeden verknüpften Block B (Textblock oder Detail mit `source::`).
- Für jede Quellnotiz N von B prüft er jedes Detail D im Teilbaum von B, das noch keine eigene `source::` hat:
  - **Text:** D passt zu einem Unterpunkt S von N (beliebige Tiefe), wenn Ds Text gleich dem Text ist, den eine Übernahme aus S machen würde. Das heißt: ohne Eigenschaftszeilen, ohne `#notiz`-Markierung, Leerraum am Anfang und Ende ignoriert.
  - **Eindeutig:** Unter N hat genau ein Unterpunkt diesen Text, und kein anderes Detail in B passt zum selben S.
  - Hat B mehrere Quellnotizen, zählt ein Treffer nur, wenn genau eine von ihnen passt.
- D bleibt unverändert, wenn es keinen eindeutigen Treffer hat. Das ist etwa der Fall, wenn der Nutzer den Text im Buch umformuliert hat.
- **Markierung:** Ein Notizblock bekommt `used-without-children:: true` nur, wenn danach jeder seiner Unterpunkte in jeder Tiefe einen eigenen Eintrag hat. Sonst bleibt sein Erben. Ein nicht nachgetragener Unterpunkt bleibt damit über seinen Hauptpunkt verwendet wie bisher.

### 7.2 Ablauf

1. **Vorschau:**
   - Wie viele Unterpunkte in wie vielen Büchern verknüpft werden.
   - Wie viele Details ohne eindeutigen Treffer bleiben.
   - Die Liste der betroffenen Bücher mit ihren Zahlen.
   - Gibt es nichts nachzutragen, sagt das der Dialog: „Alle Unterpunkte sind schon verknüpft.“
2. **„Nachtragen“** schreibt:
   - jede betroffene Buchseite einmal: `id::` und `source::` an den Details;
   - jede betroffene Notizseite einmal: `id::`, `used-in::` und die Markierung.
3. **Fehler und Konflikte** werden behandelt wie bei „[handled] umwandeln“:
   - Ungespeicherter Editortext, ein offener Konflikt oder ein inzwischen geänderter Vault: Es wird nichts geschrieben, mit derselben Meldung.
   - Eine schreibgeschützte Notizseite: Ihr Eintrag kommt nach `pending.json`.
4. **Rückgängig:** Eine Undo-Aktion „Verknüpfungen nachtragen“ gibt jeder Buch- und Notizseite, die noch so ist, wie der Assistent sie ließ, ihre alten Bytes zurück. Eine inzwischen geänderte Seite bleibt, wie sie ist, und die Meldung nennt sie.

Der Assistent ändert keinen Text und keine Reihenfolge, nur Eigenschaftszeilen. Ein zweiter Lauf findet nichts mehr.

## 8. Tests (Auswahl, je Abschnitt)

- **Adopt:**
  - Eine Notiz mit zwei Ebenen Unterpunkten ergibt im Buch Textblock und Details mit je eigener `source::`, auf der Notizseite je eigene `used-in::`.
  - Der Hauptpunkt trägt `used-without-children`.
  - Die Notizseite wird einmal geschrieben.
- **AdoptInto:** jede der drei Positionen. Am Cursor in einem Detail trägt das Detail die Quelle, nicht der Textblock.
- **AdoptAsHeading:** tiefere Details sind verknüpft.
- **„Ohne Unterpunkte“ / „Verwendete ausblenden“:** Nur Kopiertes ist verknüpft. Weggelassene Unterpunkte bleiben unverwendet bzw. behalten ihren Stand.
- **Elternblock im Editor löschen** (Sync mit aufrückenden Unterpunkten):
  - Nur ein `BlockDeleted` (für den Elternblock).
  - Die Unterpunkte bleiben verknüpft.
  - Auf der Notizseite ist nur der Hauptpunkt unverwendet.
- **„Entfernen“ eines Details:** Nur dessen Unterpunkt und tiefere werden unverwendet.
- **MergeDetails:** Ein Detail wird in ein Detail zusammengeführt. Die Quelle geht an den Kopf, die Notiz zeigt auf den Kopf, Undo stellt alles her.
- **Undo einer Übernahme:** Alle Einträge und die Markierung sind weg.
- **Gemischt:** Eine Notiz mit einer alten Übernahme (Erben) und einer neuen. Die Markierung fehlt, Unterpunkte gelten als verwendet. Nach dem Löschen der alten Übernahme gilt für jeden Unterpunkt nur noch seine eigene Verwendung.
- **Assistent:**
  - eindeutiger Treffer, doppelter Text, umformuliertes Detail, mehrere Quellnotizen;
  - Markierung nur bei vollständig nachgetragenen Notizen;
  - zweiter Lauf leer;
  - Undo;
  - Abbruch bei offenem Konflikt;
  - schreibgeschützte Notizseite.
- **UI:**
  - Das ✓ und „Verwendete ausblenden“ je Unterpunkt nach einer Übernahme.
  - Die Quellenliste der Leiste zeigt die Quellen der Details.
  - × an einer Detailquelle entfernt sie dort.
- **Prüfung der Verknüpfungen** (`LinkChecker`): Verknüpfte Details gelten als gültig. Ein `used-in::` auf ein Detail ist kein Fehler.
- **Sprung** von einer Verwendung in der Notiz zum Buchblock findet auch ein Detail.

## 9. Nicht Teil dieses Entwurfs

- Ändert der Nutzer einen Unterpunkt in der Notiz, zeigt das weiter nur der Hauptpunkt an, wie heute.
- Die Abdeckung zählt weiter jede Notiz einmal. Ob einzelne Unterpunkte unverwendet sind, zeigt sie nicht gesondert.
- Die Wahl, wohin eine Notiz gehört („Wohin damit?“, Zuordnungen), bleibt bei der ganzen Notiz.
