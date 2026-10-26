# Einordnen nach Ebene und Einrück-Knöpfe — Entwurf

Stand 2026-10-26, mit dem Nutzer im Chat abgestimmt.

## 1. Problem

Die Ebenen-Knöpfe ändern bei einer Überschrift nur die Zahl der `#`. Den Block verschieben sie nie in der Datei. Eine Überschrift, die in der Datei unter eine gleich- oder höherrangige eingerückt ist (Warnung „nicht tiefer als ‚A‘“), lässt sich darum nicht richtig einordnen:
- Ihre eigene Ebene zu wählen tut nichts.
- Eine höhere Ebene ist gesperrt.
- Eine tiefere macht sie zur Unterüberschrift.

## 2. Regel: Die Ebene bestimmt den Platz

Nach jeder Ebenen-Änderung einer Überschrift stimmt ihr Platz in der Datei mit der Markdown-Bedeutung der Ebenen überein. Die Lesereihenfolge aller Elemente bleibt dabei unverändert.

- **Herausrücken:** Solange die gewählte Ebene L nicht tiefer ist als die der übergeordneten Überschrift P, wandert der Block direkt hinter P, in P's übergeordneten Abschnitt. Die Elemente, die in P nach ihm kamen (Textblöcke und Unterabschnitte), werden dabei seine letzten Kinder. So bleibt die Reihenfolge erhalten; der Schritt wiederholt sich, bis L tiefer ist als die neue übergeordnete Überschrift oder der Block oben steht.
- **Hineinrücken:** Solange direkt vor dem Block, im selben Abschnitt, eine Überschrift S steht, deren Ebene kleiner als L ist, wird der Block S's letztes Kind. Der Schritt wiederholt sich.
- **Gleiche Ebene:** Wählt man die Ebene, die der Block schon hat, wird er ebenfalls nach dieser Regel eingeordnet. So repariert ein Klick die Warnung.
- **Unterüberschriften:** Die Ebenen im Teilbaum des Blocks verschieben sich um denselben Betrag wie heute; tiefer als H6 geht es nicht.
- **Entfallende Sperre:** Die Sperre „nicht über die übergeordnete Überschrift“ entfällt. An ihre Stelle tritt das Herausrücken. H1 bis H6 bleiben die Grenzen.
- **Anhang:** Er bleibt unberührt; eingefügt wird immer vor ihm.
- **Rückgängig:** Ein Schritt nimmt alles zurück.
- **Umschalt+Tab und „Ausrücken“ an einer Überschrift:** Ebene minus eins, nach derselben Regel. Die folgenden Elemente des Abschnitts kommen also mit.
- **Tab an einer Überschrift:** bleibt wie heute.

## 3. Befehl „Gliederung nach Ebenen ordnen“

- **Ort:** im Menü „Assistenten“ der Kopfzeile.
- **Vorschau:** „N Überschriften werden neu eingeordnet“ und dazu die Liste der Titel.
- **Ausführen:** Jede gewarnte Überschrift wird in Lesereihenfolge mit ihrer eigenen Ebene nach der Regel aus Abschnitt 2 eingeordnet. Überschriften, die in einem Textblock stehen, macht der Befehl zu Überschriften ihrer Ebene, so wie die Ebenen-Knöpfe es heute tun.
- **Rückgängig:** Ein Schritt nimmt den ganzen Befehl zurück.
- **Danach:** Das Buch hat keine Einrückungs-Warnungen mehr.

## 4. Knöpfe „⇤ Ausrücken“ und „Einrücken ⇥“

Die beiden Knöpfe stehen in der Leiste und wirken auf das Element am Cursor. Tab und Umschalt+Tab tun dasselbe.

| Element | Einrücken ⇥ | ⇤ Ausrücken |
| --- | --- | --- |
| Textblock | wird Unterpunkt (Stufe 1) des direkt davor stehenden Textblocks desselben Abschnitts; seine Unterpunkte rücken mit; sonst gesperrt | gesperrt (Grund im Tooltip) |
| Unterpunkt Stufe 1 | eine Stufe tiefer, unter den Unterpunkt davor; ohne Unterpunkt davor gesperrt | wird eigener Textblock direkt hinter seinem Textblock |
| Unterpunkt tiefer | eine Stufe tiefer, unter den Unterpunkt davor | eine Stufe höher |
| Überschrift | wie Tab | Ebene minus eins (Abschnitt 2) |

- **Mitnehmen:** Wie bei den Ebenen kommt mit, was danach stand. Wird ein Unterpunkt der Stufe 1 zum eigenen Textblock, werden seine folgenden Geschwister dessen Unterpunkte.
- **Neuer Textblock:** Er bekommt neue Daten (`created::`/`updated::`) und keine Quellen; `source::` bleibt am alten Block. Der alte Block bekommt `updated::`.
- **Gesperrt:** Ein Knopf, der nicht geht, ist ausgegraut, und der Tooltip nennt den Grund.
- **Legende:** Sie nennt „Tab / Umschalt+Tab“ auch für die Knöpfe.

## 5. Nicht enthalten

- Das Verschieben eines Textblocks in einen anderen Abschnitt über die Knöpfe; dafür gibt es die Ebenen-Knöpfe und das Ziehen.
- Ein Einrücken über mehrere Textblöcke auf einmal.
