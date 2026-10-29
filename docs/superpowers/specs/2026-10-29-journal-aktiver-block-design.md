# Journal: verwendete Blöcke und aktiver Block — Entwurf

Stand 2026-10-29, mit dem Nutzer im Chat abgestimmt.

## Teil A: Verwendete Blöcke

- **Verwendet** ist ein Notizblock, wenn er selbst oder ein übergeordneter Block ein `used-in::` hat (`NoteBlock.IsUsed`). Das gilt für Hauptpunkte und Unterpunkte jeder Tiefe.
- **„Verwendete ausblenden“ an:** Verwendete Blöcke verschwinden samt ihren Unterpunkten. Alles Unverwendete des Tages bleibt sichtbar. Ein Tag, an dem nichts mehr sichtbar wäre, verschwindet ganz. Das gilt in Journal, Suche und Relevant gleich.
- **„Verwendete ausblenden“ aus:** Verwendete Blöcke und ihre Unterpunkte stehen in grauer Schrift (`.ne-note-used-text`).
- **Markierung:** Jeder Block mit **eigenem** `used-in::`, auch ein Unterpunkt, trägt das ✓ mit der Zahl seiner Verwendungen. Ein Klick springt zum Ziel im Buch, wie heute beim Hauptpunkt: Buch des ersten Eintrags mit Blockverweis, sonst das Buch an seinem Anfang.

## Teil B: Bedienung über den aktiven Block

Gilt in Journal, Suche und Relevant sowie für die fokussierte Notiz oben im Notizbereich.

- **Keine Knöpfe an den Karten:** Die Übernehmen-Knöpfe und „Wohin damit?“ an jeder Notiz und jedem längeren Unterpunkt entfallen. Die Regel, nur mehrzeilige Unterpunkte, die keine Listenpunkte sind, bekämen eigene Knöpfe, entfällt mit.
- **Aktiv machen:** Ein Klick auf die Fläche eines Blocks (Hauptpunkt oder Unterpunkt) macht ihn aktiv. Ein Klick, der Text markiert, tut das nicht; Markieren und Kopieren bleiben möglich. Aktiv ist immer nur ein Block im Notizbereich. Ein Wechsel des Reiters oder des Vaults hebt die Auswahl auf.
- **Rahmen und Bereich:**
  - Der aktive Block bekommt einen Rahmen wie in der Buchansicht.
  - Der Bereich umfasst zuerst den Block samt Unterpunkten (Stufe 0).
  - „Bereich ▼“ (Alt+Pfeil runter) wählt nur den Block selbst (Stufe −1).
  - „Bereich ▲“ (Alt+Pfeil hoch) wählt den übergeordneten Block mit allem darunter; das geht bis zum Hauptpunkt.
  - Die Tastenkürzel wirken, wenn der Fokus im Notizbereich liegt.
- **Leiste unten am Rahmen:**
  - **Übernehmen:** „Am Cursor“, „Danach“, „Darunter“ und, wenn der Bereichskopf Unterpunkte hat, „Als Überschrift“ und „Als Unterüberschrift“. Übernommen wird genau der Bereich. Bei Stufe −1 heißt das: der Block ohne seine Unterpunkte.
  - **„Wohin damit?“** (nur mit lokaler KI). Die Liste erscheint unter der Leiste und verhält sich wie heute: offen lassen, Sprung zum Block, keine neue Berechnung von Relevant.
  - **Das ✓** mit Sprung zum Ziel, wenn der Bereichskopf verwendet ist.
  - **Der Fortschrittspunkt** des Blocks.
  - **Bei Journal-Blöcken** die Tagesmarken „nicht relevant“ und „später“ seines Tages. Sie bleiben auch an der Tageskarte.
  - **„In Logseq öffnen“** für die Seite des Blocks.
- **Ziehen:** Eine Karte lässt sich weiter ins Buch ziehen.
- **Gesperrt:** Eine Aktion, die nicht geht, ist ausgegraut, mit Grund im Tooltip. Beispiele: kein Buch gewählt, das Buch ist schreibgeschützt, offener Konflikt.
- **Legende:** Sie nennt Alt+Pfeil hoch/runter auch für den Notizbereich.

## Nicht enthalten

- Mehrfachauswahl.
- Tastatur-Navigation zwischen Blöcken.
- Eine Änderung der Übernahme-Regeln selbst (wohin, Status Relevant, Quellen).
