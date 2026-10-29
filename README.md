# NoteEvolution

NoteEvolution ist ein Schreibprogramm, mit dem aus Journal-Notizen ein Buch entsteht. Notizen und Buch liegen als
Markdown-Dateien in einem Logseq-Vault; Notizen werden per „Übernehmen“ (oder Ziehen) ins Buch kopiert und dort
ausformuliert, und beide Seiten verweisen aufeinander (`used-in::` an der Notiz, `source::` am Buchblock). Am Ende
entsteht ein PDF. Stufe 1 umfasst Editor, Notizenbereich, Verknüpfungsprüfung, Assistenten und den einfachen
PDF-Export, Stufe 2 die lokale KI (siehe „Lokale KI“); die Spezifikation steht in `docs/superpowers/specs/2026-10-05-noteevolution-design.md`.

## Voraussetzungen

- .NET 10 SDK (siehe `global.json`)
- Windows: WebView2-Laufzeit (unter Windows 11 vorinstalliert)
- Node.js 20 nur, wenn das Editor-Bündel (TipTap) neu gebaut werden soll; das fertige Bündel liegt im Repository

## Starten, bauen, testen

```sh
dotnet run --project src/NoteEvolution.Desktop
dotnet build NoteEvolution.slnx
dotnet test NoteEvolution.slnx
```

Der Build behandelt Warnungen als Fehler.

### Editor-Bündel neu bauen

```sh
cd src/NoteEvolution.UI/Editor/js
npm ci
node build.mjs
```

`node_modules` wird nicht eingecheckt. Die JavaScript-Tests des Editors (`test/`, mit jsdom) laufen im selben Ordner mit
`npm test`.

**Version:** Die Programmversion steht im Fenstertitel („NoteEvolution v0.1.3.2“). Sie zählt die Commits mit
Codeänderungen (unter `src/` und `tests/` sowie Projekt- und Builddateien; ohne Merges und reine Doku-Commits), Stelle
für Stelle dezimal: auf 0.1.3.9 folgt 0.1.4.0. Berechnet wird sie beim Bauen aus der Git-Historie
(`Directory.Build.targets`); ohne Git ist sie 0.0.0.0.

## Logo und Startbildschirm

Das Logo (Notizzettel, die zu einem Buch werden) liegt als Quelle in
`src/NoteEvolution.Desktop/Assets/NoteEvolution.svg` (App-Symbol mit grüner Kachel) und `NoteEvolution-mark.svg` (nur das
Motiv, für den Startbildschirm); `NoteEvolution.ico` (16–256 px) ist Programm- und Fenstersymbol unter Windows,
`NoteEvolution.png` das Fenstersymbol unter Linux und macOS. Bis die Oberfläche läuft und der zuletzt geöffnete Vault geöffnet ist,
zeigt das Hauptfenster einen grünen Startbildschirm mit Logo, Statuszeile und laufenden Punkten (in
`src/NoteEvolution.Desktop/wwwroot/index.html`); danach blendet er aus, spätestens nach 30 s.

## Das Manuskript

Der Editor zeigt immer das ganze Buch. Der **aktuelle Abschnitt** ist das Element am Cursor (Überschrift, Textblock
oder Detail-Unterpunkt); ein Kasten mit Balken links markiert ihn, und er bestimmt „Relevant“. Ein Klick in der
Gliederung springt zur Überschrift.

Jeder Textblock zeigt rechts oben klein und grau, wann er erstellt und zuletzt geändert wurde („erstellt 12.10.2026 ·
geändert 19.10.2026 14:32“). Die Daten stehen als `created::` und `updated::` im Block der Buchdatei; neue Blöcke
bekommen beide, eine Änderung von Text oder Status setzt `updated` (Verschieben zählt nicht). Bestehende Blöcke ohne
Daten bleiben so, bis sie geändert werden. Über der Gliederung steht **„Zuletzt geändert“**: die 20 zuletzt geänderten
Textblöcke mit Datum, dem ersten Satz der Abschnitts-Zusammenfassung (sonst dem Textanfang) und dem Abschnitt; ein
Klick springt hin. Mehrere Blöcke desselben Abschnitts, die am selben Tag zuletzt geändert wurden, stehen als ein
Eintrag „N Blöcke“ da; ein Klick springt zur Überschrift. Rechts in der Zeile „GLIEDERUNG“ schaltet ein kleiner Knopf
die Abdeckung ein und aus. Der Schieber zwischen Liste und Gliederung stellt die Höhe ein (auch mit Pfeil hoch/runter); die
Liste lässt sich mit ▾/▸ einklappen. Höhe und Klappzustand bleiben nach einem Neustart erhalten. Ganz oben in der
Gliederung steht der Titel des Buchs mit der Wortzahl des ganzen Buchs; ein Klick springt an den Anfang.

**Geteilte Ansicht:** Der Knopf „⇅“ links neben ◀ (oder Strg+Alt+S) zeigt das Buch in zwei Hälften übereinander, jede mit
eigener Gliederung und eigenem Editor, so lässt sich an zwei Stellen des Buchs zugleich arbeiten. Ein Klick in eine
Hälfte macht sie zur aktiven (schmaler Akzentstrich oben); nur sie hat die Abschnittsleiste, und Sprünge (Zuletzt
geändert, ◀ ▶, Quellen) landen in ihr. Der Schieber dazwischen stellt das Verhältnis ein (ziehen oder Pfeil hoch/runter,
20 bis 80 %). Der Einklapp-Knopf der Gliederung gilt für beide Hälften. Teilung und Verhältnis bleiben nach einem
Neustart erhalten.

Am unteren Rand der Markierung stehen die Abschnittsknöpfe und die Quellen:

| Befehl | Tastenkürzel |
| --- | --- |
| Neuer Abschnitt danach (gleiche Ebene) | Alt+Enter |
| Neuer Unterabschnitt (als erstes Element) | Alt+Umschalt+Enter |
| Element ein-/ausrücken („Einrücken ⇥“ / „⇤ Ausrücken“) | Tab / Umschalt+Tab |
| Überschrift entfernen (der Titel wird ein Textblock, der Inhalt bleibt) | Rücktaste am Anfang des Titels |
| Abschnitt löschen | nur Knopf |

**Teilen** (neben „Zusammenfügen“) teilt den Textblock an der Cursorstelle in zwei, wie Strg+Enter; Strg+Z im Editor
nimmt es zurück. Alle Tastenkürzel listet der Knopf **„Tastenkürzel“** in der Kopfzeile.

**Quellen suchen** (nur mit lokaler KI, auf einem Textblock oder Detail): zeigt die bis zu fünf Notizen, die dem
eigenen Text des Blocks am ähnlichsten sind, mit Datum, ganzem Text samt Unterpunkten und Prozentwert (über die
ganze Breite der Leiste); schon anderswo verwendete Notizen
stehen mit „verwendet“ dabei (Tooltip: in welchen Büchern), die bisherigen Quellen des Blocks fehlen. „Als Quelle
verknüpfen“ verknüpft wie der `[handled]`-Assistent: der Block bekommt `source::`, die Notiz
`used-in:: [[Buch]] ((id))`; das × am Quellen-Chip nimmt es zurück.

Jeder Befehl lässt sich mit „Rückgängig“ in der Kopfzeile zurücknehmen, solange danach nichts an der Buchdatei
geändert wurde. „N Quellen ▸“ klappt die Quellen des Abschnitts auf; ein Klick springt im Reiter Journal zur Notiz.
Ist „Verwendete ausblenden“ eingeschaltet, erscheint die Notiz stattdessen als Karte oben im Notizbereich.

**Ebene:** Die Knöpfe **Text · H1 … H6** in der Leiste zeigen die Ebene des Elements am Cursor hervorgehoben; ein
Klick auf eine andere Ebene wandelt es um. Ein Textblock wird so zur Überschrift (erste Zeile als Titel, der Rest als
Textblock darunter, seine Unterpunkte behalten ihre Hierarchie und stehen unter der neuen Überschrift), eine
Überschrift wechselt die Ebene oder wird mit „Text“ wieder ein Textblock. Die Ebene bestimmt dabei den Platz in der
Datei: Eine Überschrift, die nicht tiefer ist als die übergeordnete, wandert hinter sie in deren Abschnitt, und was in
diesem Abschnitt nach ihr kam, kommt mit; steht direkt vor ihr eine höhere Überschrift, wird sie deren letzter
Unterabschnitt. Die Reihenfolge des Textes bleibt immer gleich. Steht eine Überschrift in der Datei falsch eingerückt
(Warnung in der Gliederung), ist der Knopf ihrer eigenen Ebene gestrichelt umrandet („Nach Ebene neu einordnen“), und
ein Klick ordnet sie ein. Nicht möglich (ausgegraut, mit Grund im Tooltip) sind Ebenen, die eine Unterüberschrift unter
H6 oder über H1 brächten, und eine Ebene über dem Abschnitt eines Textblocks. Steht in der ersten Zeile eines Blocks,
der zur Überschrift wird, ein Verweis (Querverweis, Mensch oder Quelle), wandert er aus dem Titel an den Anfang des
ersten Textblocks unter der neuen Überschrift (in einer eigenen Zeile); der Verweis bleibt gültig. „Überschrift entfernen“ behält den Titel jetzt als Textblock (mit derselben
`id::`, damit Verweise darauf gültig bleiben).

**Einrücken:** „⇤ Ausrücken“ und „Einrücken ⇥“ in der Leiste (wie Umschalt+Tab und Tab) wirken auf das Element am
Cursor:

| Element | Einrücken ⇥ (Tab) | ⇤ Ausrücken (Umschalt+Tab) |
| --- | --- | --- |
| Textblock | wird letzter Unterpunkt des Textblocks direkt davor im selben Abschnitt, mit seinen Unterpunkten | nicht möglich |
| Unterpunkt der ersten Stufe | eine Stufe tiefer, unter den Unterpunkt davor | wird eigener Textblock direkt hinter seinem Textblock; die folgenden Unterpunkte werden seine |
| tieferer Unterpunkt | eine Stufe tiefer | eine Stufe höher |
| Überschrift | eine Ebene tiefer, unter die Überschrift ihrer Ebene davor | eine Ebene höher; was danach im Abschnitt stand, kommt mit |

Ein neuer Textblock bekommt neue Daten und keine Quellen (`source::` bleibt am alten Block, der `updated::` bekommt).
Verweise auf einen eingerückten Textblock zeigen danach auf den Textblock, der ihn aufnimmt. Was nicht geht, ist
ausgegraut, und der Tooltip nennt den Grund.

**Gliederung nach Ebenen ordnen** (Menü „Assistenten“): zeigt, wie viele und welche Überschriften des Buchs nicht nach
ihrer Ebene eingerückt sind, und ordnet sie mit „Einordnen“ alle nach ihrer Ebene ein, Überschriften in einem Textblock
werden Überschriften ihrer Ebene: Eine „# Hoch“ im Textblock eines H2-Abschnitts bleibt eine H1 und steht danach hinter
dem H1-Abschnitt (nicht als H3 darin); was nach ihr kam, gehört nach der Gliederung zu ihr. Die Lesereihenfolge bleibt
dabei gleich. Ein „Rückgängig“ nimmt alles zurück. Enthält der Titel einen Verweis, wandert er in den Textblock unter
der Überschrift (wie bei den Ebenen-Knöpfen). Bei einer nicht sicher lesbaren (schreibgeschützten) Buchdatei zeigt der
Dialog stattdessen den Hinweis auf den Schreibschutz.

**Querverweise:** „Verweis einfügen“ in der Leiste öffnet eine Auswahl aller Überschriften und Textblöcke des Buchs
(mit Suchfeld, Pfeiltasten und Enter); der Verweis steht als Logseq-Blockverweis `((id))` im Text und erscheint im
Editor als „→ Titel“. Ein Klick darauf springt zum Ziel. Verweise wandern mit, wenn Blöcke verschoben, umgewandelt
oder zusammengefügt werden. Hat das Element am Cursor eingehende Verweise, zeigt die Leiste „↩ N Verweise hierher“;
aufgeklappt springt jeder Eintrag zur verweisenden Stelle. Ein Verweis ohne Ziel steht rot als „Verweis fehlt“ da;
die Verknüpfungsprüfung listet solche unter „Querverweise ohne Ziel“ und entfernt sie nach einer Rückfrage. Verweise
auf Notizen außerhalb des Buchs bleiben unverändert. Im PDF steht ein Verweis als „→ Titel (S. n)“; Titel und
Seitenzahl sind anklickbar.

**Anhang: Glossar, Menschen, Quellen:** Am Buchende kann ein Buch ein Glossar, eine Liste der Menschen, die
inspiriert haben, und ein Quellenverzeichnis führen. Jeder Teil ist eine Überschrift der obersten Ebene, erkannt an
`anhang:: glossar`, `anhang:: menschen` bzw. `anhang:: quellen` (nicht am Titel; Standardtitel „Glossar“, „Menschen,
die mich inspiriert haben“, „Quellen“); ein Teil entsteht mit seinem ersten Eintrag und steht immer hinter dem
Buchtext, in der Reihenfolge Glossar, Menschen, Quellen. Neue Abschnitte („+ Abschnitt“, „Neuer Abschnitt danach“,
„Als Überschrift“, Ziehen ans Ende) landen vor dem Anhang. Der Anhang zählt nicht zum Buchtext: keine Wortzahl, nicht
in „Zuletzt geändert“, „Relevant“, „Wohin damit?“ und Abdeckung, nicht in der Auswahl „Verweis einfügen“; der Editor
zeigt ihn nicht.

- **Fenster:** Unten in der Gliederung steht die Zeile „Anhang:“ mit den Knöpfen „Glossar“, „Menschen“ und „Quellen“;
  jeder öffnet das Fenster seiner Liste (auch wenn der Abschnitt noch fehlt). Links stehen die Einträge mit Suchfeld
  (Glossar und Menschen alphabetisch, Quellen nach Nummer, Quellen ohne Nummer danach nach Titel), rechts die Felder
  des gewählten Eintrags, dazu „Neu“ und „Löschen“. Gespeichert wird beim Wechsel des Eintrags und beim Schließen; ein
  Prüffehler (leerer Name oder Begriff, ungültiges oder doppeltes Kürzel) hält das Fenster offen. Ein Glossareintrag
  hat Begriff, Formen (`formen::`) und Erklärung; ein Mensch Name, Kürzel (`kuerzel::`, 1–6 Zeichen, im Buch
  eindeutig; das Fenster schlägt die Initialen vor), Themen (`themen::`) und Beschreibung; eine Quelle Titel, Art
  (`art::`), Autoren (`autoren::`), Jahr, Herausgeber und URL. Neue Autoren, die es unter den Menschen noch nicht gibt,
  legt das Speichern dort an. „Löschen“ eines Eintrags, auf den verwiesen wird, fragt vorher nach („Trotzdem
  löschen“); die Verweise werden zu „Verweis fehlt“. Jede Änderung lässt sich mit „Rückgängig“ zurücknehmen.
- **Einfügen:** „Mensch einfügen“ und „Quelle einfügen“ in der Leiste öffnen eine Auswahl wie bei „Verweis einfügen“
  (Suche, Pfeiltasten, Enter, zuletzt „Neu anlegen …“, das den Eintrag anlegt und danach einfügt). Ein Mensch steht
  als `((id))` im Text und im Editor als hochgestelltes Kürzel; ein Klick öffnet das Fenster „Menschen“ beim Eintrag.
  Eine Quelle steht als `{{quelle ((id))}}` bzw. mit **Fundstelle** als `{{quelle ((id)) S. 12}}`, im Editor als
  hochgestellte Nummer („[3]“, mit Fundstelle „[3, S. 12]“); die Nummer vergibt das erste Zitat in der
  Lesereihenfolge des PDFs (ohne Relevant und Bemerkung, ohne Anhang), eine Quelle ohne Zitat dort hat keine („[–]“).
  Die Fundstelle eines vorhandenen Zitats ändert man mit einem **Doppelklick** auf die Nummer („Fundstelle ändern“,
  „Übernehmen“). Ein Klick auf die Nummer öffnet das Fenster „Quellen“.
- **Glossar-Markierung:** Pro Begriff (oder eine seiner Formen) wird das erste Vorkommen in einem Textblock (ganzes
  Wort, ohne Rücksicht auf Groß- und Kleinschreibung, längste Form zuerst) gepunktet unterstrichen; im Text ändert
  sich nichts. Der Tooltip zeigt Begriff und Erklärung, **Strg+Klick** öffnet das Fenster „Glossar“ beim Eintrag.
- **PDF:** Menschen erscheinen als hochgestelltes Kürzel, Quellen als hochgestellte Nummer (mit Fundstelle in
  normaler Größe dahinter), das erste Vorkommen eines Glossarbegriffs gepunktet unterstrichen; alle drei verlinken auf
  ihren Eintrag; ein Mensch ohne Kürzel erscheint mit seinem Namen. Nach dem Buchtext folgen die Teile Glossar
  (alphabetisch), Menschen (alphabetisch, „Name (Kürzel)“) und Quellen (nach Nummer, „[3] Autor; Autor: Titel.
  Herausgeber, Jahr. URL“), je auf neuer Seite und im Inhaltsverzeichnis; aufgeführt ist nur, was im exportierten Text
  vorkommt, unter den Menschen auch die Autoren der zitierten Quellen. Eine Fundstelle steht im PDF so, wie sie
  geschrieben ist. Beim **Teilexport** („Aktueller Abschnitt“) enthält der Anhang nur, was in diesem
  Abschnitt vorkommt; die Quellennummern gelten für das ganze Buch.
- **Verknüpfungsprüfung:** „Verweise auf fehlende Einträge“ (Quellenangaben ohne Quelle und Autoren ohne Menschen-
  Eintrag; „Verweise entfernen“ nach Rückfrage), „Doppelte Anhang-Abschnitte“ und „Doppelte Kürzel“ (nur Hinweise).
  Ein gelöschter Mensch im Text erscheint, weil er von einem Querverweis nicht zu unterscheiden ist, weiter unter
  „Querverweise ohne Ziel“.
- **Logseq:** Der Anhang ist gewöhnliches Logseq-Markdown und lässt sich dort bearbeiten. Ein Eintrag ohne `id::` ist voll
  nutzbar (ein Glossarbegriff wird markiert und steht im PDF, Menschen und Quellen stehen in den Auswahlen und unter
  den Autoren) und bekommt seine `id::`, sobald er zum ersten Mal eingefügt oder als Autor eingetragen wird, oder beim
  ersten Speichern im Fenster; unbekannte Eigenschaften bleiben; doppelte Kürzel werden gemeldet, nicht
  gelöscht. Tippen und Speichern im Editor lässt den Anhang unverändert.

**Vor und zurück:** ◀ und ▶ links neben „Rückgängig“ (oder Alt+Pfeil links/rechts) springen zu früher ausgewählten
Blöcken, auch über Buchgrenzen. Ein Block zählt, wenn der Cursor mindestens 2 s darin war oder per Sprung (Verweis,
„Zuletzt geändert“, Quellen) erreicht wurde; gemerkt werden die letzten 50, nur für die laufende Sitzung.

Am Anfang der Filterzeile steht, wie viele Notizen (Hauptpunkte) es gibt, bzw. wie viele davon der Filter
durchlässt („812 von 1.234 Notizen“; im Journal zählen auch „‚nicht relevant‘ ausblenden“ und „‚später‘ ausblenden“).

Der Reiter **Journal** ist beim Start ausgewählt und zeigt jede Journal-Datei als eine Karte (ein Tag, mit Datum
und „In Logseq öffnen“); ihre Hauptpunkte stehen darin als Kästen mit Linie, rechts neben dem Text ✓ (wenn
verwendet) und der Fortschrittspunkt, darunter die Unterpunkte. „Suche“ und „Relevant“ zeigen weiter einzelne
Hauptpunkte als Treffer. Die Karten haben keine Knöpfe; die Aktionen stehen in der Leiste des aktiven Blocks (siehe
unten). Im
Journal steht „Springen zum Tag“ in der Filterzeile („Von“/„Bis“ sind vorerst ausgeblendet). Rechts oben auf jeder
Tageskarte markieren zwei Häkchen den Tag als **„nicht relevant“** (für das gewählte Buch; in der Datei als
`not-relevant-for:: [[Buch]]`) oder **„später“** (`later:: true`); die Filter „‚nicht relevant‘ ausblenden“ und
„‚später‘ ausblenden“ blenden solche Tage aus und bleiben nach einem Neustart erhalten.

**Aktiver Block:** Ein Klick auf einen Hauptpunkt oder Unterpunkt (in Journal, Suche, Relevant oder der Karte oben)
macht ihn zum aktiven Block; ein Klick, der Text markiert, tut das nicht, Markieren und Kopieren bleiben möglich. Aktiv
ist immer nur ein Block; ein Wechsel des Reiters oder des Vaults hebt die Auswahl auf, ebenso wenn der Block nicht mehr
zu sehen ist (z. B. verwendet bei „Verwendete ausblenden“). Wie in der Buchansicht hat sein Bereich einen Rahmen:
zuerst der Block mit allen Unterpunkten; „Bereich ▲“ (Alt+Pfeil hoch, wenn der Fokus im Notizbereich liegt) nimmt den
übergeordneten Block mit allem darunter, bis zum Hauptpunkt; „Bereich ▼“ (Alt+Pfeil runter) verkleinert bis zum Block
allein, ohne seine Unterpunkte. Unten am Rahmen steht die Leiste: Bereich ▲/▼, die Arten zu übernehmen, **Wohin
damit?** (nur mit lokaler KI), ✓ und Fortschrittspunkt des Bereichskopfs, bei Journal-Blöcken die Tagesmarken
„nicht relevant“ und „später“ (dieselben wie an der Tageskarte) und „In Logseq öffnen“. Was nicht geht, ist ausgegraut
und nennt den Grund im Tooltip (kein Buch gewählt, Buch schreibgeschützt, Notizdatei nicht sicher lesbar, offener
Konflikt). Ins Buch gezogen wird mit dem Griff „⠿“ vorn in der Leiste (der Bereichskopf); die Karte selbst lässt sich
nicht ziehen, damit ihr Text markiert werden kann.

Übernommen wird genau der Bereich, auf eine von drei Arten: **Am Cursor** (in den aktuellen Block an der
Cursorstelle), **Danach** (neues Element nach dem aktuellen) und **Darunter** (als erstes Unterelement). Bei „ohne
Unterpunkte“ kommt nur der Block selbst ins Buch; `used-in::` steht dann nur an ihm, dazu `used-without-children:: true`
(es geht mit seiner letzten Verwendung): seine Unterpunkte bleiben unverwendet, sichtbar und nicht grau. Die Quelle hängt immer am
umgebenden Textblock. Neue Blöcke und Details aus „Danach“ und „Darunter“ bekommen den Status Relevant; am Cursor
behält der Block seinen Status, nur die dabei entstehenden Details werden Relevant.
Hat der Bereichskopf Unterpunkte (und nimmt der Bereich sie mit), bietet die Leiste zusätzlich **Als Überschrift** (neue Überschrift auf der Ebene des aktuellen
Abschnitts, an dessen Ende) und **Als Unterüberschrift** (eine Ebene tiefer, nach den Textblöcken des Abschnitts und vor
seinen Unterabschnitten; nicht unter Ebene 6). Die erste Zeile wird der Titel, jeder direkte Unterpunkt ein eigener
Textblock (Relevant, verknüpft); bestehender Text bleibt in seinem Abschnitt. „Rückgängig“ nimmt das zurück, solange
unter der neuen Überschrift nichts geändert wurde.

**Status:** Jeder Textblock und jedes Detail hat einen Arbeitsstand. Die Auswahl in der Leiste (Tooltip „Status des Blocks“) zeigt und
ändert ihn für das Element am Cursor (auf Überschriften ausgegraut); jede Änderung lässt sich mit „Rückgängig“
zurücknehmen.

| Status | In der Datei | Darstellung | PDF |
| --- | --- | --- | --- |
| Entwurf (Standard bei selbst geschriebenem Text) | keine Zeile | normal | ja |
| Relevant (Standard bei übernommenen Notiztexten) | `status:: relevant` | grauer Text | nein |
| Bemerkung | `status:: bemerkung` oder `#notiz` | beige-braune Schrift (dunkel: helles Beige) | nein |
| Formulierung | `status:: formulierung` | gelber Strich am rechten Rand | ja |
| Fertig | `status:: fertig` | grüner Strich am rechten Rand | ja |

Relevant und Bemerkung fehlen im PDF samt allen Details darunter. Strg+Umschalt+N schaltet das Element am Cursor
zwischen Bemerkung und Entwurf um; „Bemerkung anfügen“ legt am Ende der Details des aktuellen Textblocks ein leeres
Detail mit Status Bemerkung an, der Cursor steht darin. Wird ein Block mit `#notiz` auf einen anderen Status gesetzt,
verschwindet das Tag. Ein unbekannter `status::`-Wert gilt als Entwurf und bleibt unverändert in der Datei.

**Bereich:** Mit „Bereich ▲“ / „Bereich ▼“ (oder Alt+Pfeil hoch/runter) wird der markierte Bereich stufenweise
erweitert (übergeordnetes Element samt allem darunter, bis zum ganzen Buch) bzw. verkleinert (bis zum Element allein,
„ohne Unterelemente“). Kasten, Quellenanzahl und „Relevant“ folgen dem Bereich; wechselt der Cursor das Element, gilt
wieder der Standard. Auf den Bereich wirken zwei weitere Knöpfe:

- **Zusammenfügen:** die Textblöcke im Bereich werden je Überschrift zu einem Block (Texte durch eine Leerzeile
  getrennt, Details wandern mit); Quellen werden vereinigt, die `used-in::`-Verweise der Notizen zeigen danach auf
  den verbleibenden Block. Auf einem Textblock (oder Detail) samt Details fügt „Zusammenfügen“ die Details in seinen
  Text ein; Bemerkungen (auch `#notiz`) bleiben als Details erhalten. Der verbleibende Block behält immer seinen
  eigenen Status; der Status der eingefügten Blöcke geht nicht über.
- **Unter neue Überschrift:** der Bereich kommt unter eine neue, leere Überschrift eine Ebene unter seiner Überschrift
  (bei einem Textblock samt der direkt folgenden Textblöcke bis zur nächsten Unterüberschrift); der Cursor steht im
  neuen Titel.

**Wohin damit?** in der Leiste des aktiven Blocks (siehe oben) zeigt unter der Leiste die fünf passendsten Textblöcke
des Buchs für den Bereichskopf mit Überschriftenpfad, Textauszug und Prozent (✓, wenn der Block dort schon Quelle
ist). Ein Klick springt zum Block, die Liste bleibt offen; übernommen wird dort mit „Danach“ oder „Darunter“. Solange im Reiter „Relevant“ eine solche
Liste offen ist, bleibt die Relevant-Liste unverändert; nach dem Schließen der letzten Liste wird sie einmal neu
berechnet.

## Wo was liegt

| Was | Ort |
| --- | --- |
| Protokolle, solange kein Vault offen ist | `<Benutzerdaten>/logs` |
| Protokolle eines geöffneten Vaults (täglich neue Datei) | `<vault>/.noteevolution/logs` |
| Fenster- und Anzeigeeinstellungen (`ui.json`, u. a. zuletzt geöffneter Vault) | `<Benutzerdaten>/ui.json` |
| Vault-Einstellungen (Notizordner) | `<vault>/.noteevolution/settings.json` |
| Tägliche Sicherungen vor dem ersten Schreiben einer Datei (30 Tage) | `<vault>/.noteevolution/backups/<datum>/` |
| Noch nicht geschriebene Notiz-Änderungen (z. B. schreibgeschützte Notiz) | `<vault>/.noteevolution/pending.json` |
| KI-Modell (nach dem Download; immer nur eines) | `<Benutzerdaten>/models/<Modell-Id>/` |
| Gespeicherte Bedeutungsvektoren der Notizen | `<vault>/.noteevolution/vectors.db` |

`<Benutzerdaten>` ist der Ordner `NoteEvolution` im Anwendungsdatenordner des Systems (Windows:
`%APPDATA%\NoteEvolution`, Linux und macOS: `~/.config/NoteEvolution`).

Die Dateien im Vault sind die einzige Wahrheit. Alles unter `.noteevolution/` ist Zusatz oder wiederherstellbar und kann
von der Synchronisation (Obsidian, Syncthing usw.) ausgenommen werden; der Suchindex wird bei jedem Öffnen im Speicher
neu aufgebaut. Dasselbe Buch sollte nicht gleichzeitig in Logseq bearbeitet werden.

## Lokale KI

Die KI-Funktionen sind ein Zusatz: „Relevant“ (die Notizen, die zum Abschnitt oder zur Textstelle passen), die Suche
nach Bedeutung (ergänzt die Volltextsuche), „Wohin damit?“ an jeder Notiz und jedem längeren Unteranstrich (die fünf
passendsten Textblöcke des Buchs) und Fundstellen-Vorschläge im `[handled]`-Assistenten. Sie schreiben und formulieren keinen Buchtext. Einzige
Schreibaktion sind die Verknüpfungszeilen (`id::`, `source::`, `used-in::`) für Fundstellen, die du im Assistenten
selbst gewählt und bestätigt hast. Ohne Modell arbeitet die App unverändert mit der Volltextsuche.

- **Modelle:** du wählst eines von vier Modellen, alle quantisiert (int8), mit ONNX Runtime auf der CPU:

  | Modell | Download | Arbeitsspeicher ca. | Einordnung |
  |---|---|---|---|
  | e5-small (Standard) | 123 MB | 0,4 GB | schnell, solide Qualität |
  | e5-base | 284 MB | 0,7 GB | ausgewogen |
  | e5-large | 567 MB | 1,3 GB | beste e5-Qualität, deutlich langsamer |
  | bge-m3 | 575 MB | 1,3 GB | sehr gute mehrsprachige Qualität, langsam |

  Quelle ist Hugging Face, Repositories `Xenova/multilingual-e5-small`, `-base`, `-large` und `Xenova/bge-m3`
  (`onnx/model_quantized.onnx` und `sentencepiece.bpe.model`), jeweils auf eine feste Revision gepinnt und per SHA-256
  geprüft. Die ursprünglichen Modelle (`intfloat/multilingual-e5-*`, `BAAI/bge-m3`) stehen unter der MIT-Lizenz.
- **Download und Wahl:** nur nach deiner Bestätigung. Beim ersten Mal (Angebot beim Öffnen eines Vaults, Knopf im
  Reiter „Relevant“) wählst du das Modell im Dialog aus; später in den Einstellungen unter „KI-Modell“ →
  „Modell wählen…“. Danach arbeitet alles offline, die App greift nicht mehr auf das Netz zu.
- **Wechsel:** das neue Modell wird heruntergeladen, während das alte weiterarbeitet. Danach sichert der Editor,
  der Vault wird mit dem neuen Modell neu geöffnet und neu indexiert, und das alte Modell wird gelöscht. Scheitert
  der Download oder brichst du ab, bleibt alles beim alten Modell. Solange ein Konflikt offen ist, ist der Wechsel
  gesperrt. Die Wahl gilt für alle Vaults und steht in `ui.json` (`AiModelId`).
- **Ablage:** das Modell liegt im Benutzerprofil, nicht im Vault: unter Windows `%APPDATA%\NoteEvolution\models\<Modell-Id>\`
  (z. B. `multilingual-e5-small-int8`), sonst `~/.config/NoteEvolution/models/<Modell-Id>/`. Es liegt immer nur ein
  Modell dort; Reste anderer Modelle räumt die App beim Start weg. Zum Entfernen den Ordner löschen; die App bietet
  den Download danach wieder an.
- **Bedeutungsvektoren:** `<vault>/.noteevolution/vectors.db` speichert die Vektoren der Notizen (neu berechnet wird
  nur, was sich geändert hat). Die Datei darf bei geschlossener App gelöscht werden, sie wird beim nächsten Öffnen neu aufgebaut
  (das Indexieren läuft im Hintergrund, die Kopfzeile zeigt den Fortschritt). Wie alles unter `.noteevolution/` kann
  sie von der Synchronisation ausgenommen werden.

## Sprachmodell (Ollama)

Ein lokales Sprachmodell über [Ollama](https://ollama.com) verbessert „Relevant“ und „Wohin damit?“: Es schreibt im
Hintergrund für jede Überschrift des Buchs eine Themen-Zusammenfassung (von unten nach oben, einschließlich aller
Unterabschnitte) und für jede Notiz eine Kernaussage. Diese Texte werden mit dem lokalen KI-Modell (siehe „Lokale KI“)
verglichen; dadurch weiß jede Überschrift, worum es in ihren Unterabschnitten geht. Es braucht deshalb die lokale KI
(installiertes KI-Modell); ohne sie bleibt das Sprachmodell wirkungslos, die Einstellungen weisen darauf hin.

- **Einrichten:** Ollama installieren und starten. Ist Ollama erreichbar, bietet die App einmal „Sprachmodell
  einrichten?“ an; sonst in den Einstellungen unter „Sprachmodell (Ollama)“: „Sprachmodell verwenden“, Adresse
  (Vorgabe `http://127.0.0.1:11434`) und Modell (Vorgabe `qwen2.5:7b`, etwa 4,7 GB). Fehlt das Modell, lädt
  „Modell laden“ es über Ollama (Bestätigung, Fortschritt mit Restzeit, abbrechbar). Die Werte stehen in `ui.json`.
- **„Wohin damit?“** sucht zweistufig: erst die drei passendsten Themen (eine Überschrift zählt auch mit 90 % ihres
  besten Unterabschnitts), dann die Stelle darin. Die Liste zeigt „Thema › Stelle“, der Tooltip die
  Themen-Zusammenfassung. **„Genauer prüfen“** zeigt dem Sprachmodell die Unterkapitel der (bis zu) drei passendsten Kapitel
  (mit dem ersten Satz ihrer Zusammenfassung und den besten Textstellen); es wählt das Unterkapitel und die Stelle und
  begründet die Wahl. Die Antwort ist per JSON-Schema auf die angebotenen Stellen beschränkt. Auf dem Testrechner (i5,
  nur CPU) dauert das im Mittel knapp eine Minute, höchstens etwa 1½ Minuten (Anzeige „Prüfe … 0:42 von ca. 1:00“,
  abbrechbar); die Empfehlung steht oben in der Liste. Das Modell sagt dazu, wie gut die Notiz passt (gut, teilweise,
  kaum).
- **Vorberechnete Zuordnung:** Für jede noch nicht verwendete Notiz läuft „Genauer prüfen“ im Hintergrund für das
  aktuelle Buch, sobald alle Zusammenfassungen des Buchs und die Kernaussage der Notiz fertig sind. „Wohin damit?“
  zeigt die Empfehlung dann sofort; „veraltet“ heißt, Notiz oder Buch haben sich seitdem geändert (sie wird neu
  berechnet). Neu gerechnet wird nur, wenn sich die Anfrage an das Modell ändert. „Genauer prüfen“ erscheint nur noch,
  wenn eine Empfehlung fehlt oder veraltet ist.
- **„Relevant“** beschreibt den Abschnitt über seine Zusammenfassung (und zu einem Viertel die seines Oberabschnitts)
  und vergleicht die Notizen über Kernaussage und Text. Oben stehen unter „Hierher eingeordnet“ die Notizen, die das
  Modell diesem Abschnitt (oder einem Unterabschnitt) zugeordnet hat, darunter „Weitere ähnliche“.
- **Abdeckung:** Der Schalter „Abdeckung“ in der Gliederung zeigt je Überschrift einen Balken: grün übernommene,
  orange zugeordnete offene Notizen (Tooltip mit „unsicher“, wenn Embedding und Modell uneins sind); ein Klick öffnet
  „Relevant“ des Abschnitts. Eine rote 0 markiert einen untersten Abschnitt ohne Notizen, sobald alle Notizen geprüft
  sind. Der Reiter „Abdeckung“ im Notizbereich zeigt Kennzahlen (geprüft, leere Abschnitte, ohne passenden Ort,
  unsicher), alle untersten Abschnitte (Buchreihenfolge oder „Wenigste zuerst“), die Notizen ohne passenden Ort
  (Passung „kaum“) und eingeklappt die unsicheren Zuordnungen.
- **Status je Notiz:** Ein Punkt im Kopf jeder unverwendeten Notizkarte zeigt den Stand: blau pulsierend = wird
  berechnet, rot = fehlgeschlagen, grün = Zuordnung aktuell, gelb = veraltet, grau = steht noch aus (Tooltip mit allen
  Stufen).
- **Hintergrund:** immer nur eine Anfrage; zuerst das aktuelle Kapitel, dann das übrige Buch, dann die Notizen
  (neueste zuerst). Beim Tippen wartet die Berechnung 10 s Ruhe ab, im Akkubetrieb pausiert sie, der Schalter ⏸/▶ in
  der Kopfzeile hält sie an. Auf dem Testrechner braucht eine Zusammenfassung etwa 45 s, eine Kernaussage etwa 18 s je
  Eintrag, eine Zuordnung knapp eine Minute. Die Kopfzeile zeigt den Fortschritt mit Balken und Restzeit („Sprachmodell
  34/120 · noch ca. 1 h 10 min“); ein Klick darauf schlüsselt ihn nach Embedding, Zusammenfassungen, Kernaussagen und
  Zuordnungen auf (je mit Balken und Restzeit, dazu Pausengrund und übersprungene Einträge).
  Eine Anfrage darf höchstens 5 Minuten dauern; nach 3 Fehlversuchen wird ein Eintrag bis zur nächsten Änderung
  übersprungen; scheitern mehrere Einträge hintereinander am Server, pausiert die Berechnung und versucht es nach 60 s
  erneut.
- **Speicher und Datenschutz:** Ergebnisse liegen in `<vault>/.noteevolution/understanding.db` (wiederherstellbar,
  kann von der Synchronisation ausgenommen werden). Alles bleibt auf dem Rechner; eine Adresse außerhalb des eigenen
  Rechners wird nur nach einer Warnung übernommen. Das Sprachmodell schreibt keinen Buchtext und ändert keine Datei.
- **Ohne Ollama** (oder solange noch nichts berechnet ist) arbeiten alle Funktionen wie bisher.
- **Qualität messen:** `dotnet run tests/quality/measure.cs -- --model qwen2.5:7b` vergleicht auf einem Referenzbuch
  mit 40 Notizen die heutige mit der neuen Suche und misst die Antwortzeiten (braucht Ollama mit dem Modell;
  `--limit N` für einen kurzen Lauf).

**Systemanzeige:** Rechts in der Kopfzeile stehen Prozessorlast, belegter Arbeitsspeicher und freier Platz auf dem
Laufwerk des Vaults, dazu als „App“ den Anteil von NoteEvolution zusammen mit Ollama, wenn es läuft (alle Prozesse
`ollama…` und `llama-server`, das das Modell rechnet), sowie die Größe der Analysedateien des Vaults (`vectors.db` und
`understanding.db`): „CPU 63 % (App 53 %) · RAM 9,8/15,8 GB (App 5,5 GB) · Frei 112 GB · Analyse 38 MB“, alle 2 s. Über 90 % CPU oder unter 1 GB freiem Arbeitsspeicher erscheint der Wert in der Warnfarbe.
Abschaltbar in den Einstellungen.

## Manuelle Abnahme

Mit einem künstlichen Test-Vault (keine persönlichen Texte), Änderungen jeweils mit `git diff` gegen eine Kopie prüfen.

Fenster und Grundlayout
- [ ] Beim Start zeigt das Fenster sofort den grünen Startbildschirm (Logo, „Vault wird geöffnet …“, laufende Punkte),
      der nach dem Öffnen des Vaults ausblendet; Titelleiste, Taskleiste und die `.exe` zeigen das Logo.
- [ ] Das Fenster öffnet sich, nach der Ordnerauswahl erscheinen die Bücher; Darstellung hell/dunkel/System stimmt.
- [ ] Die Trenner zwischen den drei Bereichen lassen sich ziehen; die Breiten sind nach einem Neustart wieder da. Der
      Notizbereich lässt sich ohne feste Obergrenze verbreitern, bis der Editor nur noch 200 px breit ist; beim
      Verkleinern des Fensters gibt er zuerst nach.
- [ ] Im Notizbereich bleiben Kopfzeile, Reiter, Filter und das Suchfeld bzw. der Datumssprung stehen, nur die Karten
      scrollen.
- [ ] Einstellungen: Schriftgröße, Zeilenbreite und Farbschema wirken; geänderte Notizordner laden den Vault neu.

Gliederung
- [ ] Text in einem Block ändern und ca. 1 s warten: „geändert …“ im Blockkopf springt auf jetzt, der Cursor bleibt;
      der Block steht oben in „Zuletzt geändert“; in der Datei ändert sich nur dieser Block (Text und `updated::`).
- [ ] Ein Buch nur öffnen und wieder schließen: `git diff` der Buchdatei ist leer.
- [ ] Schieber zwischen „Zuletzt geändert“ und Gliederung ziehen; nach Neustart ist die Höhe dieselbe. Lange Gliederung
      scrollen: Liste und Schieber bleiben stehen.
- [ ] Zwei datierte Textblöcke zusammenfügen: klappt; der Block behält das frühere Erstellungsdatum.
- [ ] Überschrift per Doppelklick umbenennen (Fokus im Eingabefeld), „+“ legt eine Unterüberschrift an.
- [ ] Abschnitte per Ziehen umsortieren und einhängen (echtes Ziehen mit der Maus in WebView2).

Editor
- [ ] Text tippen und ca. 1 s warten: nur diese Zeile ändert sich in der Datei; der Cursor bleibt, wo er ist.
- [ ] Leerzeichen am Ende eines Absatzes tippen und warten: das Leerzeichen bleibt, der Cursor springt nicht,
      Strg+Z im Editor funktioniert weiter.
- [ ] Strg+Enter mitten in einem verknüpften Block: die Notiz hat danach zwei `used-in`-Einträge.
- [ ] Tab, Umschalt+Tab und Strg+Umschalt+N funktionieren (Strg+Umschalt+N wird nicht von WebView2 abgefangen und
      macht das Element am Cursor zur Bemerkung bzw. wieder zum Entwurf).
- [ ] Status: in der Leiste alle fünf Stati an Textblock und Detail wählen; graue und beige Schrift und die Randstriche stimmen im
      hellen und dunklen Design; die Auswahl zeigt nach einem Cursorwechsel den Status des neuen Elements;
      „Rückgängig“ in der Kopfzeile nimmt jede Änderung zurück. In Logseq erscheint `status::` als Eigenschaft.
- [ ] „Bemerkung anfügen“: ein leeres Detail mit beiger Schrift am Ende des Textblocks, der Cursor steht darin.
- [ ] Rücktaste am Blockanfang verbindet zwei Blöcke; „Rückgängig“ in der Kopfzeile stellt den Block wieder her.
- [ ] Einen verknüpften Block löschen, ca. 1 s warten, dann Strg+Z im Editor: der Block ist wieder verknüpft
      (Quellen-Chip, `source::` im Buch, `used-in::` an der Notiz). Danach „Rückgängig“ in der Kopfzeile:
      es lehnt ab („Die Aktion konnte nicht rückgängig gemacht werden.“), der Block steht nur einmal im Buch.
- [ ] Einen Block am farbigen Balken links seiner Markierung packen und unter ein anderes Kapitel ziehen; danach ist er in der
      Datei richtig eingerückt.
- [ ] Schalter „Quellen anzeigen“ und die Randmarkierung bei Blöcken mit Quellen.
- [ ] Markierung: Cursor in Überschrift, Textblock und Detail setzen; der Kasten umfasst jeweils das richtige Element,
      die Leiste sitzt am unteren Rand und bleibt beim Scrollen eines langen Abschnitts am Fensterrand sichtbar.
- [ ] Alt+Enter, Alt+Umschalt+Enter, Tab/Umschalt+Tab in einer Überschrift und Rücktaste am Titelanfang wirken im
      echten Fenster (werden nicht von WebView2 abgefangen); danach „Rückgängig“ in der Kopfzeile.
- [ ] „Abschnitt löschen“ an einer Überschrift mit verknüpften Blöcken: die `used-in::`-Einträge verschwinden,
      „Rückgängig“ bringt Buch und Notizen zurück.
- [ ] Quellen der Markierung aufklappen, eine Quelle anklicken: der Reiter Journal springt zur Notiz und hebt sie hervor.
- [ ] Reiter Journal (beim Start ausgewählt): eine Karte je Tag (Datum und „In Logseq öffnen“ oben), darin jeder
      Hauptpunkt als Kasten mit Linie, ohne Knöpfe; „Verwendete ausblenden“ blendet verwendete
      Blöcke auf jeder Ebene samt ihren Unterpunkten aus (ein Tag ohne sichtbaren Block verschwindet). Ohne den Haken
      stehen verwendete Blöcke und ihre Unterpunkte in grauer Schrift; jeder Block mit eigenem `used-in::`, auch ein
      Unterpunkt, trägt das ✓ (mit Zahl bei mehreren Verwendungen), ein Klick springt zum Ziel im Buch.
- [ ] Großes Buch (mehrere hundert Blöcke): Tippen und Scrollen bleiben flüssig.
- [ ] Bereich mit ▲/▼ und Alt+Pfeil erweitern und verkleinern; Kasten, Bereichsanzeige und Quellenanzahl folgen; ein
      Bereich mit Text nach Unterabschnitten zeigt zwei Kästen.
- [ ] „Zusammenfügen“ und „Unter neue Überschrift“ ausführen, in Logseq prüfen, danach „Rückgängig“.
- [ ] Eine nicht sicher lesbare Buchdatei (z. B. offener Codeblock) zeigt den Hinweis, dass sie schreibgeschützt ist;
      der Editor ist gesperrt.
- [ ] Fenster schließen, während der Text nicht gespeichert werden kann (z. B. Buchdatei schreibgeschützt): das
      Fenster bleibt offen und zeigt einen Hinweis; erneutes Schließen beendet das Programm.

Notizen und Übernehmen
- [ ] Eine Notiz übernehmen (anklicken und Knopf in der Leiste, oder am Griff „⠿“ an die gewünschte Stelle ziehen): die Karte zeigt ✓, der
      Buchblock einen Quellen-Chip, der Hinweis bietet 8 s lang „Rückgängig“; in der Notizdatei kam nur eine
      `id::`/`used-in::`-Zeile hinzu.
- [ ] „Verwendete ausblenden“ blendet verwendete Notizen aus.
- [ ] Die drei Arten zu übernehmen: am Cursor mitten in einem Absatz (der Rest rückt dahinter), „Danach“ und
      „Darunter“ bei Textblock, Detail und Überschrift; die Quelle steht jeweils am umgebenden Textblock, der
      neue Block bzw. das neue Detail ist grau (`status:: relevant`).
- [ ] Aktiver Block: ein Klick auf einen Unterpunkt rahmt ihn samt Unterpunkten, die Leiste steht unten am Rahmen; Text
      markieren macht nichts aktiv. Bereich ▲/▼ und Alt+Pfeil hoch/runter im Notizbereich ändern den Rahmen; bei „ohne
      Unterpunkte“ kommt beim Übernehmen nur der Block ins Buch. Gesperrte Knöpfe nennen den Grund im Tooltip; die
      Tagesmarken in der Leiste und an der Tageskarte zeigen dasselbe; ein Reiterwechsel hebt die Auswahl auf.

Logseq und Obsidian
- [ ] Den Vault in Logseq öffnen: keine Fehler, Buch und Notizen sehen wie erwartet aus.
- [ ] Maskierte Formen werden in Logseq und Obsidian richtig angezeigt: `\-` am Zeilenanfang als „-“,
      `fazit:\: gut` als „fazit:: gut“ (Text, keine Eigenschaft), `\*` als „*“.
- [ ] Echter Konflikt: einen Block im Editor ändern und innerhalb einer Sekunde denselben Block in Logseq ändern; der
      Konfliktdialog zeigt beide Fassungen; „beide nebeneinander“ ausprobieren.

Assistenten und Prüfung
- [ ] Verknüpfungsprüfung beim Start und auf Abruf (fehlende `used-in` werden ergänzt, Waisen und kaputte Quellen
      werden gelistet).
- [ ] `[handled]`-Assistent: alle Häkchen sind anfangs aus; eine Stelle außer „Stelle unbekannt“ wählen setzt das
      Häkchen; „Umwandeln“ schreibt nur die angehakten Notizen, meldet „N umgewandelt“, das Fenster bleibt offen und
      die umgewandelten Notizen verschwinden aus der Liste; Dateien prüfen.
- [ ] Entwurfs-Assistent auf einer schlichten Gliederung: Überschrift umschalten, umwandeln, Buchliste und Datei prüfen;
      alle Textblöcke und Unterpunkte haben danach `status:: relevant`, Überschriften nicht.
- [ ] `[handled]`-Assistent: der ganze Notiztext steht da; eine Stelle wählen zeigt ihren Text und Abschnitt daneben.
- [ ] Journal: „nicht relevant“ bei einem Tag setzen und wieder entfernen: die Datei ist danach unverändert; mit einem
      anderen Buch gesetzt bleibt der Eintrag des ersten Buchs erhalten; „später“ unabhängig davon. Filter blenden aus.

Lokale KI (Modell installiert, Test-Vault mit einigen Dutzend Notizen und einem Buch)
- [ ] Ohne Modell: das Angebot beim Öffnen des Vaults ablehnen; die Kopfzeile zeigt „KI: Modell fehlt“, der Reiter
      „Relevant“ bietet den Download an, die Suche arbeitet im Volltext; „Wohin damit?“ ist nicht zu sehen.
- [ ] Modell herunterladen (Bestätigung, Fortschritt); danach zeigt die Kopfzeile „KI: lokal“, anfangs mit
      Indexfortschritt.
- [ ] Der Reiter „Relevant“ folgt dem Cursor: in eine andere Überschrift oder einen anderen Block klicken, nach etwa
      1,5 s ändert sich die Liste.
- [ ] „Verwendete ausblenden“ und der Zeitraum wirken auch in „Relevant“.
- [ ] Die Suche findet sinnverwandte Notizen ohne gleiche Wörter (z. B. nach „Müdigkeit“ suchen und eine Notiz über
      „Schlaf“ finden), Treffer mit dem gesuchten Wort stehen weiter oben.
- [ ] „Wohin damit?“ in der Leiste einer Notiz oder eines Unterpunkts zeigt höchstens fünf Textblöcke mit Pfad, Auszug und
      Prozentwert; ein Klick springt zum Block, die Liste bleibt offen; dort mit „Danach“ übernehmen. Im Reiter
      „Relevant“ bleibt die Liste dabei stehen, die Notiz verschwindet nicht.
- [ ] `[handled]`-Assistent: neben jeder Notiz steht „Stelle unbekannt“ (Standard) und bis zu drei Fundstellen mit
      Textauszug und Prozent; nach dem Buchwechsel werden sie neu berechnet. Eine Fundstelle wählen, umwandeln: der
      Buchblock hat `id::` und `source::`, die Notiz `used-in:: [[Buch]] ((id))`, `[handled]` ist entfernt. Eine
      Notiz mit „Stelle unbekannt“ bekommt nur `used-in:: [[Buch]]`.
- [ ] Verhalten ohne Netz: Netzwerk trennen, App neu starten; alle KI-Funktionen arbeiten weiter, es gibt keinen
      Download-Hinweis.
- [ ] Modell wechseln (Einstellungen → „Modell wählen…“, e5-small → e5-base) bei offenem Vault: Fortschritt, danach
      öffnet sich der Vault neu, die Kopfzeile zeigt den Indexfortschritt; unter `%APPDATA%\NoteEvolution\models`
      liegt nur noch `multilingual-e5-base-int8`. Den Wechsel einmal mitten im Download abbrechen: alles bleibt beim
      alten Modell.
- [ ] Modellordner umbenennen: die App fällt auf die Volltextsuche zurück und bietet den Download an; `vectors.db`
      löschen und neu öffnen: sie wird neu aufgebaut.

Sprachmodell (Ollama, Modell `qwen2.5:7b` geladen)
- [ ] Ollama läuft, Vault öffnen: „Sprachmodell einrichten?“ erscheint einmal; einrichten; die Kopfzeile zeigt
      „Sprachmodell 0/…“ mit Balken, nach drei Antworten eine Restzeit.
- [ ] Tippen im Editor: die Kopfzeile zeigt die Tipppause; ⏸ hält an, ▶ setzt fort; beides bleibt nach einem
      Neustart erhalten.
- [ ] Ollama beenden: die Kopfzeile meldet „nicht erreichbar“, „Relevant“ und „Wohin damit?“ arbeiten weiter; Ollama
      starten: nach spätestens 60 s geht es weiter.
- [ ] „Wohin damit?“ zeigt „Thema › Stelle“ mit Zusammenfassung im Tooltip; „Genauer prüfen“ zeigt die laufende Zeit
      und die Empfehlung mit Begründung oben; „Abbrechen“ beendet die Prüfung.
- [ ] Einstellungen: eine Adresse auf einem anderen Rechner verlangt die Bestätigung der Warnung.
- [ ] Systemanzeige: Werte plausibel (Task-Manager), Warnfarbe bei Volllast, abschaltbar.
- [ ] Nach den Zusammenfassungen und Kernaussagen laufen die Zuordnungen: Statuspunkte wechseln von grau über blau
      auf grün; „Wohin damit?“ zeigt die Empfehlung sofort, nach einer Textänderung „veraltet“.
- [ ] „Relevant“ zeigt „Hierher eingeordnet“ über „Weitere ähnliche“, ohne doppelte Notizen.
- [ ] Gliederung: „Abdeckung“ an/aus bleibt nach Neustart erhalten; Balken, Tooltip und rote 0 plausibel; Klick öffnet
      „Relevant“.
- [ ] Reiter „Abdeckung“: Kennzahlen, Sortierung „Wenigste zuerst“, Notizen ohne passenden Ort.
- [ ] Klick auf den Fortschritt in der Kopfzeile öffnet die Aufschlüsselung; Escape schließt sie.
- [ ] Nach einer Berechnung sind alle Buch- und Notizdateien unverändert (`git diff`).

Querverweise und Navigation
- [ ] „Verweis einfügen“: Auswahl filtern, mit Pfeiltasten und Enter eine Überschrift wählen; im Editor steht
      „→ Titel“, in der Datei `((id))`, das Ziel hat `id::`. Klick auf den Verweis springt hin; am Ziel zeigt die
      Leiste „↩ 1 Verweis hierher“, der Eintrag springt zurück.
- [ ] Zielblock verschieben, mit einem anderen zusammenfügen und Ebene wechseln: der Verweis zeigt weiter auf ihn
      (Beschriftung aktualisiert). Ziel löschen: „Verweis fehlt“ rot; Verknüpfungsprüfung listet ihn unter
      „Querverweise ohne Ziel“ und entfernt ihn erst nach der Rückfrage.
- [ ] Ebenen-Knöpfe: Textblock mit Unterpunkten zu H2 machen (Unterpunkte bleiben geschachtelt darunter), wieder auf
      „Text“ zurück; „Rückgängig“ nimmt beides zurück. Ausgegraute Ebenen nennen im Tooltip den Grund.
- [ ] Einordnen nach Ebene: eine H3 unter H1 › H2 auf H1 stellen; sie steht danach hinter dem H1-Abschnitt, was nach
      ihr kam, gehört ihr, die Lesereihenfolge im Editor ist dieselbe. Eine in der Datei unter eine gleichrangige
      eingerückte Überschrift (Warnung in der Gliederung): der Knopf ihrer Ebene ist gestrichelt umrandet, ein Klick
      ordnet sie ein, die Warnung ist weg. „Rückgängig“ nimmt jeden Schritt zurück.
- [ ] „⇤ Ausrücken“ / „Einrücken ⇥“ und Umschalt+Tab / Tab: Tab im Text eines Textblocks macht ihn zum Unterpunkt des
      Textblocks davor; Umschalt+Tab auf einem Unterpunkt der ersten Stufe macht ihn zum eigenen Textblock (die
      folgenden Unterpunkte kommen mit, `source::` bleibt am alten Block); tiefere Unterpunkte rücken im Editor. Erster
      Textblock eines Abschnitts, erster Unterpunkt und eine Überschrift mit H6 tief darunter: Knopf ausgegraut, der
      Tooltip nennt den Grund. Ein Verweis auf einen eingerückten Textblock zeigt danach auf den aufnehmenden.
- [ ] Assistenten › „Gliederung nach Ebenen ordnen“ auf einem Buch mit Einrückungs-Warnungen: Vorschau mit Zahl und
      Titeln, „Einordnen“, danach keine Warnung mehr und gleiche Lesereihenfolge; ein „Rückgängig“ stellt die Datei
      wieder her. Auf einem geordneten Buch: „Die Gliederung ist schon nach Ebenen geordnet.“
- [ ] ◀ ▶ und Alt+Pfeil links/rechts: nach einigen Sprüngen und Klicks (je ≥ 2 s) zurück und vor, auch über einen
      Buchwechsel; bei offenem Dialog tut Alt+Pfeil nichts.
- [ ] Geteilte Ansicht: „⇅“ (und Strg+Alt+S, auch im Editor) zeigt zwei Hälften mit je Gliederung und Editor; bei
      offenem Dialog tut Strg+Alt+S nichts. Der Schieber lässt sich ziehen und mit Pfeil hoch/runter in 5-%-Schritten
      zwischen 20 und 80 % stellen. Ein Klick in die untere Hälfte macht sie aktiv (Akzentstrich, Abschnittsleiste und
      „Relevant“ wechseln); Tippen oben, dann sofort unten klicken: nichts geht verloren. Ausschalten, während unten
      aktiv ist: die obere Hälfte bleibt mit ihrer Stelle. Neustart behält Teilung und Verhältnis.
- [ ] PDF: ein Verweis steht als „→ Titel (S. n)“ mit richtiger Seitenzahl; Klick auf Titel und Seitenzahl springt
      im PDF-Betrachter zur Stelle.
- [ ] Systemanzeige: „App“ entspricht etwa NoteEvolution plus `ollama*.exe` und `llama-server.exe` im Task-Manager
      (CPU und RAM jeweils zusammengezählt); kein Tooltip.
- [ ] Gliederung: oberste Zeile mit Buchtitel und Wortzahl des ganzen Buchs. Notizbereich: „N Notizen“, mit
      „Verwendete ausblenden“ „M von N Notizen“.
- [ ] In einer langen Gliederung im Editor zu einem weit entfernten Abschnitt scrollen und hineinklicken: die
      Gliederung scrollt die markierte Überschrift in den Sichtbereich; von Hand gescrollt bleibt sie danach stehen.
- [ ] „Teilen“ mitten in einem verknüpften Textblock: zwei Blöcke, die Notiz hat zwei `used-in`-Einträge.
- [ ] „Tastenkürzel“ öffnet die Liste; jedes Kürzel darin funktioniert wie beschrieben.
- [ ] „Quellen suchen“ auf einem Textblock, dessen Notiz kein `[handled]` hatte: die Notiz steht in der Liste;
      „Als Quelle verknüpfen“ setzt `source::` im Buch und `used-in:: [[Buch]] ((id))` in der Notiz, sie verschwindet
      aus der Liste; über das × am Chip wieder entfernen, beide Dateien sind wie vorher (bis auf `id::`).
- [ ] Aufschlüsselung des Sprachmodell-Fortschritts: alle Balken liegen innerhalb des weißen Kastens.
- [ ] Zusammenfassungen in „Zuletzt geändert“ (nach der Neuberechnung) beginnen nicht mit „Der Autor erklärt“,
      „Der Abschnitt beschreibt“ o. Ä.

Anhang: Glossar, Menschen, Quellen
- [ ] Ein Buch mit Anhang (je einige Einträge, Autoren, Zitate mit Fundstelle) in Logseq öffnen: keine Fehler, die drei
      Abschnitte stehen am Ende, Zitate erscheinen als Makro, Menschen als Blockverweis.
- [ ] Im Editor tippen und ca. 1 s warten, Buch schließen: `git diff` der Buchdatei zeigt nur die getippte Stelle,
      der Anhang ist unverändert (nichts gelöscht, verschoben oder neu datiert).
- [ ] Neue Abschnitte (Gliederung „+ Abschnitt“, „Neuer Abschnitt danach“ auf dem letzten Abschnitt, „Als Überschrift“,
      Ziehen ans Ende) landen vor dem Anhang.
- [ ] Fenster „Glossar“, „Menschen“, „Quellen“ über die Anhang-Zeile: Eintrag anlegen, ändern, löschen (mit Rückfrage,
      wenn darauf verwiesen wird), „Rückgängig“; neuer Autor einer Quelle erscheint danach unter „Menschen“.
- [ ] „Mensch einfügen“ und „Quelle einfügen“ (auch mit „Neu anlegen …“ und Fundstelle): Kürzel und Nummer im Editor,
      Tooltips, Klick öffnet das Fenster; Doppelklick auf eine Nummer ändert die Fundstelle; ein Glossarbegriff im Text
      ist gepunktet unterstrichen, Strg+Klick öffnet das Glossar.
- [ ] Eintrag in Logseq ohne `id::` anlegen und im Buch etwas ändern: der Eintrag bleibt und hat danach eine `id::`;
      eine unbekannte Eigenschaft bleibt; zwei Menschen mit gleichem Kürzel landen in der Verknüpfungsprüfung unter
      „Doppelte Kürzel“, ohne dass etwas gelöscht wird.
- [ ] Verknüpfungsprüfung: einen Menschen und eine Quelle löschen, auf die verwiesen wird: „Verweise auf fehlende
      Einträge“ listet die Quellenangabe und den Autor, „Verweise entfernen“ fragt nach und entfernt sie (Datei prüfen);
      der gelöschte Mensch im Text steht unter „Querverweise ohne Ziel“.
- [ ] PDF-Anhang: nach dem Buchtext folgen Glossar, Menschen und Quellen auf neuen Seiten und im Inhaltsverzeichnis;
      hochgestellte Kürzel und Nummern, gepunktete Glossarmarkierung, Klick springt zum Eintrag; Quellen stehen nach
      Nummer, Blöcke mit Status Relevant oder Bemerkung zählen nicht.
- [ ] Teilexport („Aktueller Abschnitt“): der Anhang enthält nur, was in diesem Abschnitt vorkommt; die Quellennummern
      sind dieselben wie im ganzen Buch.

PDF
- [ ] PDF-Export für das ganze Buch in A4 und A5 sowie für „Aktueller Abschnitt“: der native Speichern-Dialog schlägt
      einen sinnvollen Dateinamen vor; das PDF hat Titelseite und Inhaltsverzeichnis, Schrift und Abstände sehen gut
      aus, Blöcke mit Status Relevant oder Bemerkung (auch `#notiz`) fehlen samt Details; Warnungen erscheinen im
      Dialog.
