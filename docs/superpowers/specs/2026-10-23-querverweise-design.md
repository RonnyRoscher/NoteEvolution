# NoteEvolution – Querverweise, Verlauf, Ebenen-Knöpfe, „[handled]“-Ablauf und Verbrauchsanzeige

Stand: 2026-10-23 · Status: Entwurf · Mit dem Nutzer im Chat abgestimmt.

## 1. Ziel

Fünf Änderungen:

1. **Querverweise** zwischen Überschriften und Textblöcken eines Buchs, nutzbar in beide Richtungen, im Editor und im PDF.
2. **Zurück und Vor** zwischen den zuletzt aktiven Blöcken.
3. **Ebenen-Knöpfe** „Text · H1 … H6“ im Footer des aktiven Blocks, die den Block umwandeln.
4. **„[handled] umwandeln“** mit gezielter Auswahl; das Fenster bleibt offen.
5. **Verbrauchsanzeige** mit Ollama in der Zeile, ohne Tooltip, mit enger stehenden Kopfzeilen-Knöpfen.

### Erfolgskriterien

- **Verweis anlegen:** Ein Verweis wird in wenigen Klicks angelegt (Knopf, Suchen, Wählen). Ein Klick darauf springt zum Ziel, und das Ziel zeigt, wer darauf verweist.
- **Verweise überleben Änderungen:** Nach Verschieben oder Zusammenführen von Blöcken funktionieren alle Verweise weiter. Ein gelöschtes Ziel wird sichtbar markiert und von der Verknüpfungsprüfung gefunden.
- **PDF:** Verweise sind anklickbar und nennen die Seite des Ziels.
- **Zurück und Vor:** Alt+← führt zur vorigen Stelle, auch über Buchwechsel hinweg.
- **Ebenen umwandeln:** Jede Umwandlung über die Ebenen-Knöpfe erhält allen Text und ist mit „Rückgängig“ zurücknehmbar.

## 2. Querverweise

### 2.1 Format und Ziele

- **Format:** Ein Verweis ist ein Logseq-Blockverweis `((<uuid>))` im Text eines Textblocks oder Unterpunkts des Buchs.
- **Mögliche Ziele:** eine Überschrift oder ein Textblock **desselben Buchs**. Ein Unterpunkt kann nicht Ziel sein.
- **Ziel-ID:** Das Ziel trägt seine ID als Block-Eigenschaft `id::`, wie bei den Notiz-Verknüpfungen. Hat es noch keine, bekommt es beim Anlegen des Verweises eine. Das ist die einzige Änderung am Ziel.
- **Ein Verweis zählt als Textänderung** des verweisenden Blocks: `updated` wird gesetzt. Am Ziel ändert sich `updated` nicht, wenn nur `id::` hinzukommt.
- **Fremde Verweise:** `((…))`-Verweise auf Blöcke außerhalb des Buchs (etwa Notizen) werden wie bisher als Text behandelt und sind nicht Teil dieser Funktion.

### 2.2 Anlegen

- **Knopf und Suche:** Ein Knopf „Verweis einfügen“ im Footer des aktiven Blocks öffnet ein kleines Suchfeld. Es zeigt Überschriften (Pfad) und Textblöcke (Abschnitt › Textanfang) des aktuellen Buchs. Gefiltert wird beim Tippen, groß/klein egal, nach Titel, Pfad und Textanfang.
- **Wählen:** Pfeiltasten und Enter wählen, Escape bricht ab. Der Verweis wird an der Cursorstelle eingefügt.
- **Wann der Knopf verfügbar ist:** nur, wenn der Cursor in einem Textblock oder Unterpunkt steht. Überschriften können keine Verweise enthalten.
- **Ein Vorgang:** Der Editor speichert vorher. Der Verweis und eine eventuelle `id::` am Ziel werden in einem Schritt geschrieben und sind mit einem „Rückgängig“ zurücknehmbar.

### 2.3 Darstellung und Navigation im Editor

- **Im Text:** Ein Verweis erscheint als kleines, nicht editierbares Element.
  - Bei einer Überschrift: „→ Titel“.
  - Bei einem Textblock: „→ Abschnitt: Textanfang …“, etwa 40 Zeichen.
  - Tooltip: der volle Pfad.
  - Mit Rücktaste oder Entf wird er als Ganzes gelöscht.
- **Klick:** Ein Klick springt zum Ziel, macht es zum aktiven Block und trägt den Sprung in den Verlauf ein (Abschnitt 3).
- **Gelöschtes Ziel:** Gibt es das Ziel im Buch nicht mehr, erscheint der Verweis rot als „→ Verweis fehlt“. Er bleibt im Text, bis der Nutzer ihn löscht.
- **Verweise hierher:** Am Ziel zeigt der Footer „↩ N Verweise hierher“, aufklappbar wie die Quellen. Jeder Eintrag nennt die verweisende Stelle (Abschnitt › Textanfang); ein Klick springt dorthin. Ohne Verweise gibt es keinen Eintrag.
- **Aktualität:** Die Anzeige berechnet die Verweise aus dem aktuellen Buch. Es gibt keinen eigenen Speicher; nach jedem Speichern und jeder externen Änderung ist sie aktuell.

### 2.4 Verschieben, Zusammenführen, Löschen

- **Verschieben:** Ein verschobener Block behält seine `id::`. Verweise funktionieren ohne Änderung weiter, das gilt auch beim Teilen per Strg+Enter für den verbleibenden Teil.
- **Zusammenführen:** Beim Zusammenführen von Textblöcken (Abschnittsleiste) und von Unterpunkten in ihren Block ersetzt die App alle Verweise auf die verschwundenen Blöcke im ganzen Buch durch die ID des verbleibenden Blocks. Das geschieht im selben Schreibvorgang und wird mit demselben „Rückgängig“ zurückgenommen.
- **Überschrift entfernen:** Verweise auf die Überschrift zeigen danach auf den Textblock, der aus ihrem Titel entsteht. Er übernimmt die `id::` (siehe auch 4.2).
- **Löschen:** Verweise auf einen gelöschten Block bleiben stehen und werden als „Verweis fehlt“ angezeigt (2.3).
- **Verknüpfungsprüfung:** Sie listet fehlende Verweise mit ihrer Stelle in einer eigenen Gruppe „Querverweise ohne Ziel“. Sie bietet an, sie zu entfernen; dabei bleibt der übrige Text erhalten.
- **Externe Änderungen:** Werden sie zusammengeführt, ändert die App keine Verweise.

### 2.5 PDF

- **Darstellung:** Ein Verweis erscheint als „→ Titel (S. 12)“. Bei einem Textblock als Ziel steht dort der Titel des Abschnitts, in dem er steht, und die Seite des Blocks selbst.
- **Link:** Der ganze Verweis ist ein interner Link zur Stelle. Dafür bekommen Ziel-Textblöcke im PDF einen eigenen Sprungpunkt; Überschriften haben schon einen.
- **Ausgelassenes Ziel:** Ist das Ziel im PDF nicht enthalten (Status Relevant oder Bemerkung) oder fehlt es, erscheint nur „→ Titel“ ohne Seite und ohne Link. Bei einem fehlenden Ziel steht dort „→ (Verweis fehlt)“.
- **Keine Rückverweise im PDF.**

## 3. Zurück und Vor

- **Knöpfe:** ◀ und ▶ links neben „Rückgängig“, mit Tooltips „Zurück (Alt+←)“ und „Vor (Alt+→)“. Sie sind ausgegraut, wenn es in die Richtung nichts gibt.
- **Was ein Eintrag ist:** Ein Verlaufseintrag ist ein aktiver Block: Buch und Element-Schlüssel (Überschrift, Textblock oder Unterpunkt).
- **Sprünge** kommen sofort in den Verlauf. Dazu zählen Klicks in der Gliederung, in „Zuletzt geändert“, auf einen Querverweis, auf „Verweise hierher“, auf eine Quelle, auf ein Suchergebnis oder einen Abdeckungseintrag sowie ein Buchwechsel.
- **Normale Cursorwechsel** zu einem anderen Block (Klick oder Pfeiltasten im Editor) kommen erst in den Verlauf, wenn der neue Block 2 s lang aktiv bleibt.
- **Keine Dopplungen:** Folgen zwei gleiche Einträge aufeinander, wird nur einer gespeichert.
- **Wie ein Browser:** Nach einem Zurück verwirft eine neue Auswahl alle „Vor“-Einträge.
- **Grenzen:** höchstens 50 Einträge, ältere fallen weg. Der Verlauf gilt nur für die laufende Sitzung und wird beim Vault-Wechsel geleert.
- **Ziel anfahren:** Zurück und Vor springen zum Eintrag, wählen dazu bei Bedarf dessen Buch und machen den Block aktiv. Der Editor speichert vorher.
- **Fehlende Blöcke:** Gibt es den Block nicht mehr, wird der Eintrag übersprungen und entfernt.
- **Tastenkürzel:** Alt+← und Alt+→ gelten im ganzen Fenster, auch im Editor.

## 4. Ebenen-Knöpfe im Footer des aktiven Blocks

### 4.1 Bedienung

- **Knöpfe:** Im Footer des aktiven Blocks steht neben den bisherigen Knöpfen eine Gruppe „Text · H1 · H2 · H3 · H4 · H5 · H6“.
- **Hervorhebung:** Die aktuelle Ebene ist hervorgehoben, bei einem Textblock oder Unterpunkt „Text“. Ein Klick auf eine andere Ebene wandelt den aktiven Block um.
- **Speichern:** Der Editor speichert vorher.
- **Danach:**
  - Jede Umwandlung ist ein Schritt mit „Rückgängig“.
  - Sie setzt `updated` an den betroffenen Textblöcken.
  - Neue Textblöcke bekommen `created` und `updated`.
  - Der umgewandelte Block bleibt der aktive.
- **Ausgegraut:** Ein Knopf, dessen Umwandlung nicht möglich ist, ist ausgegraut und sagt im Tooltip, warum.

### 4.2 Umwandlungen

- **Text → Überschrift Hn** (Textblock oder Unterpunkt):
  - Die erste Zeile des Blocks wird der Titel einer neuen Überschrift der Ebene n, die an der Stelle des Blocks steht.
  - Weiterer Text der Hauptzeile wird der erste Textblock darunter.
  - Jeder direkte Unterpunkt wird ein Textblock unter der Überschrift, seine tieferen Unterpunkte bleiben dessen Unterpunkte.
  - Die bisher nachfolgenden Textblöcke des Abschnitts gehören danach zur neuen Überschrift.
  - Bei einem Unterpunkt wird nur er mit seinen Unterpunkten umgewandelt. Er steht dann als Überschrift hinter seinem Textblock.
  - `id::`, `source::` und Status des Blocks gehen auf den ersten Textblock unter der Überschrift über, damit Verknüpfungen zu Notizen und Querverweise erhalten bleiben. Querverweise auf diesen Block zeigen also auf diesen ersten Textblock.
  - Nicht möglich, wenn n kleiner ist als die Ebene des umgebenden Abschnitts: Die neue Überschrift würde dann Abschnitte verschlucken, die nicht zu ihr gehören. Ausnahme ist die Wurzel bzw. der Vorspann; dort ist jede Ebene möglich.
- **Überschrift Hm → Hn:**
  - Die Überschrift ändert ihre Ebene. Ihre Unterabschnitte verschieben sich um dieselbe Differenz, damit die Gliederung erhalten bleibt.
  - Nicht möglich, wenn dadurch eine Ebene unter 1 oder über 6 fiele, oder wenn n kleiner wäre als die Ebene ihrer übergeordneten Überschrift plus 1. Dann würde sie aus dem Elternabschnitt herausfallen; dafür gibt es Ausrücken.
- **Überschrift → Text:** wie das heutige „Überschrift entfernen“. Der Titel wird ein Textblock, der Inhalt rückt in den übergeordneten Abschnitt. Die `id::` der Überschrift geht auf diesen Textblock über, damit Querverweise erhalten bleiben.

## 5. „[handled] umwandeln“

- **Start:** Beim Öffnen sind alle Haken aus.
- **Automatisches Anhaken:** Wählt man in der Stellenauswahl einer Notiz eine Stelle, wird ihr Haken gesetzt. Wählt man wieder „Stelle unbekannt“, wird er entfernt. Von Hand bleibt jeder Haken frei setzbar.
- **„Umwandeln“:** schreibt nur die angehakten Notizen, mit dem bisherigen Ablauf (Bestätigung, Sicherung, Fehlerbehandlung).
- **Danach:**
  - Das Fenster bleibt offen.
  - Umgewandelte Notizen verschwinden aus der Liste, und eine Meldung „N umgewandelt“ erscheint.
  - Für die übrigen Notizen bleiben Stellenauswahl und Haken, wie sie waren.
  - Ist die Liste leer, steht dort „Keine [handled]-Notizen mehr“.
- **Schließen:** Der Knopf „Schließen“ schließt das Fenster. „Umwandeln“ ist ohne Haken ausgegraut.

## 6. Verbrauchsanzeige und Kopfzeile

- **Ollama in Klammern:** Ollama steht neben den App-Werten, z. B. „CPU 63 % (App 12 % · Ollama 40 %) · RAM 9,8/15,8 GB (App 0,6 · Ollama 4,9 GB) · Frei 112 GB · Analyse 38 MB“. Läuft Ollama nicht, entfällt sein Teil.
- **Kein Tooltip** an der Anzeige.
- **Knöpfe:** Die Knöpfe der Kopfzeile stehen direkt hinter „Rückgängig“ (bzw. hinter ◀ ▶ Rückgängig), mit kleinem Abstand: Assistenten, PDF-Export, Verknüpfungsprüfung, Einstellungen. Der freie Platz liegt rechts vor den Anzeigen.

## 7. Fehlerbehandlung

- **Querverweis anlegen:**
  - Ist die Buchdatei schreibgeschützt oder hat sie einen offenen Konflikt, erscheint die übliche Meldung. Es wird nichts geändert.
  - Ändert ein anderes Programm die Datei, wird wie bei anderen Buchänderungen zusammengeführt.
- **Unlesbare Verweise:** Ein `((…))`, das keine gültige ID enthält, bleibt Text.
- **Ebenen-Umwandlung:** Scheitert sie, wird die Seite von der Platte neu geladen (wie bei den Abschnittsbefehlen) und eine Meldung erscheint.
- **„[handled]“:** Scheitert eine Notiz beim Umwandeln, bleibt sie in der Liste. Die übrigen angehakten werden trotzdem geschrieben, und die Meldung nennt beide Zahlen.

## 8. Tests

- **Querverweise, Kern:**
  - Lesen und Schreiben von `((id))`.
  - Anlegen setzt `id::` am Ziel nur, wenn es fehlt.
  - Beim Zusammenführen und bei „Überschrift entfernen“ werden die Verweise umgestellt.
  - Ein fehlendes Ziel wird erkannt.
  - Die Verknüpfungsprüfung listet fehlende Verweise und entfernt sie.
- **Querverweise, Editor-Abbildung:** `((id))` wird zum Verweis-Element und beim Speichern unverändert zurückgeschrieben, sodass die Datei byte-gleich bleibt.
- **Querverweise, Oberfläche:**
  - Das Suchfeld filtert, Enter fügt ein, Escape bricht ab.
  - Ein Klick springt zum Ziel.
  - „Verweise hierher“ wird mit seinen Einträgen angezeigt.
  - Ein fehlendes Ziel wird rot dargestellt.
- **Querverweise, PDF:** Text „→ Titel (S. n)“ und interner Link. Ein ausgelassenes oder fehlendes Ziel erscheint ohne Seite und ohne Link.
- **Verlauf:**
  - Sprünge werden sofort eingetragen, normale Cursorwechsel erst nach 2 s (mit gefälschter Uhr).
  - Aufeinanderfolgende gleiche Einträge werden zusammengefasst.
  - Eine neue Auswahl verwirft die „Vor“-Einträge.
  - Mehr als 50 Einträge: die ältesten fallen weg.
  - Ein fehlender Block wird übersprungen.
  - Liegt ein Eintrag in einem anderen Buch, wird dieses gewählt.
  - Alt+←/→ und der Zustand der Knöpfe.
- **Ebenen:** jede Umwandlung aus 4.2 einschließlich der Unterpunkt-Hierarchie, der Übergabe von `id::`, `source::` und Status, der ausgegrauten Fälle und von Rückgängig.
- **„[handled]“:**
  - Start ohne Haken.
  - Eine Stellenwahl setzt den Haken, „Stelle unbekannt“ entfernt ihn.
  - Nur angehakte Notizen werden geschrieben.
  - Das Fenster bleibt offen und die Liste wird kleiner.
  - Ist die Liste leer, erscheint der Hinweis.
- **Anzeige:** Ollama-Teil vorhanden bzw. fehlend; kein Tooltip; Reihenfolge der Kopfzeilen-Knöpfe.

## 9. Nicht enthalten

- Verweise zwischen verschiedenen Büchern oder auf Notizen.
- Rückverweise im PDF.
- Ein gespeicherter Verlauf über Sitzungen hinweg.
- Anlegen von Verweisen durch Tippen von `((` im Editor. Das kann später dazukommen.
