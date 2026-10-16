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

`node_modules` wird nicht eingecheckt.

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

Am unteren Rand der Markierung stehen die Abschnittsknöpfe und die Quellen:

| Befehl | Tastenkürzel |
| --- | --- |
| Neuer Abschnitt danach (gleiche Ebene) | Alt+Enter |
| Neuer Unterabschnitt (als erstes Element) | Alt+Umschalt+Enter |
| Überschrift ein-/ausrücken | Tab / Umschalt+Tab in der Überschrift |
| Überschrift entfernen (Inhalt bleibt) | Rücktaste am Anfang des Titels |
| Abschnitt löschen | nur Knopf |

Jeder Befehl lässt sich mit „Rückgängig“ in der Kopfzeile zurücknehmen, solange danach nichts an der Buchdatei
geändert wurde. „N Quellen ▸“ klappt die Quellen des Abschnitts auf; ein Klick springt im Reiter Journal zur Notiz.
Ist „Verwendete ausblenden“ eingeschaltet, erscheint die Notiz stattdessen als Karte oben im Notizbereich.

Der Reiter **Journal** ist beim Start ausgewählt und zeigt jede Journal-Datei als eine Karte (ein Tag, mit Datum
und „In Logseq öffnen“); ihre Hauptpunkte stehen darin als Kästen mit Linie, jeder mit seinen Knöpfen untereinander
rechts neben dem Text (darüber ✓, wenn verwendet) und seinen Unterpunkten darunter. „Suche“ und „Relevant“ zeigen
weiter einzelne Hauptpunkte als Treffer.

Zum Übernehmen hat jede Notiz einen geteilten Knopf, ebenso jeder Unteranstrich mit mehr als einer Zeile (mehr als
80 Zeichen, ein Zeilenumbruch oder eigene Unteranstriche), der kein Punkt einer Aufzählung ist („1.“, „2)“, „a)“, „–“,
„•“ an mindestens zwei Geschwistern); kurze Einträge und Listenpunkte werden mit ihrer Notiz übernommen. An einem
Unteranstrich stehen die Knöpfe untereinander rechts neben dem Text; sie nehmen ihn mit allen seinen Unterpunkten (die
Linie links umfasst sie, beim Zeigen auf die Knöpfe ist der Bereich hinterlegt). Der Knopf selbst übernimmt auf die
zuletzt gewählte Art und trägt deren Namen (anfangs **Am Cursor**), der Pfeil daneben bietet alle drei Arten:
**Am Cursor** (in den aktuellen Block an der Cursorstelle), **Danach** (neues Element nach dem aktuellen) und
**Darunter** (als erstes Unterelement). Die gewählte Art gilt für alle Karten und bleibt nach einem Neustart
erhalten (`ui.json`). Daneben steht **Wohin damit?** (nur mit lokaler KI). Die Quelle hängt immer am umgebenden
Textblock. Neue Blöcke und Details aus „Danach“ und „Darunter“ bekommen den Status Relevant; am Cursor behält der
Block seinen Status, nur die dabei entstehenden Details werden Relevant.

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

**Wohin damit?** an einer Notiz oder einem Unteranstrich mit eigenen Knöpfen (siehe oben) zeigt die fünf passendsten Textblöcke des Buchs mit
Überschriftenpfad, Textauszug und Prozent (✓, wenn die Notiz dort schon Quelle ist). Ein Klick springt zum Block,
die Liste bleibt offen; übernommen wird dort mit „Danach“ oder „Darunter“. Solange im Reiter „Relevant“ eine solche
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
Laufwerk des Vaults („CPU 63 % · RAM 9,8/15,8 GB · Frei 112 GB“, alle 2 s; der Tooltip zeigt den Anteil von Ollama
und NoteEvolution). Über 90 % CPU oder unter 1 GB freiem Arbeitsspeicher erscheint der Wert in der Warnfarbe.
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
      Hauptpunkt als Kasten mit Linie und Knöpfen untereinander rechts; „Verwendete ausblenden“ blendet verwendete
      Hauptpunkte in ihrer Tageskarte aus.
- [ ] Großes Buch (mehrere hundert Blöcke): Tippen und Scrollen bleiben flüssig.
- [ ] Bereich mit ▲/▼ und Alt+Pfeil erweitern und verkleinern; Kasten, Bereichsanzeige und Quellenanzahl folgen; ein
      Bereich mit Text nach Unterabschnitten zeigt zwei Kästen.
- [ ] „Zusammenfügen“ und „Unter neue Überschrift“ ausführen, in Logseq prüfen, danach „Rückgängig“.
- [ ] Eine nicht sicher lesbare Buchdatei (z. B. offener Codeblock) zeigt den Hinweis, dass sie schreibgeschützt ist;
      der Editor ist gesperrt.
- [ ] Fenster schließen, während der Text nicht gespeichert werden kann (z. B. Buchdatei schreibgeschützt): das
      Fenster bleibt offen und zeigt einen Hinweis; erneutes Schließen beendet das Programm.

Notizen und Übernehmen
- [ ] Eine Notiz mit einem Handgriff übernehmen (Knopf oder Ziehen an die gewünschte Stelle): die Karte zeigt ✓, der
      Buchblock einen Quellen-Chip, der Hinweis bietet 8 s lang „Rückgängig“; in der Notizdatei kam nur eine
      `id::`/`used-in::`-Zeile hinzu.
- [ ] „Verwendete ausblenden“ blendet verwendete Notizen aus.
- [ ] Die drei Arten zu übernehmen: am Cursor mitten in einem Absatz (der Rest rückt dahinter), „Danach“ und
      „Darunter“ bei Textblock, Detail und Überschrift; die Quelle steht jeweils am umgebenden Textblock, der
      neue Block bzw. das neue Detail ist grau (`status:: relevant`). Danach trägt der Übernehmen-Knopf den Namen der
      zuletzt gewählten Art, auch nach einem Neustart.

Logseq und Obsidian
- [ ] Den Vault in Logseq öffnen: keine Fehler, Buch und Notizen sehen wie erwartet aus.
- [ ] Maskierte Formen werden in Logseq und Obsidian richtig angezeigt: `\-` am Zeilenanfang als „-“,
      `fazit:\: gut` als „fazit:: gut“ (Text, keine Eigenschaft), `\*` als „*“.
- [ ] Echter Konflikt: einen Block im Editor ändern und innerhalb einer Sekunde denselben Block in Logseq ändern; der
      Konfliktdialog zeigt beide Fassungen; „beide nebeneinander“ ausprobieren.

Assistenten und Prüfung
- [ ] Verknüpfungsprüfung beim Start und auf Abruf (fehlende `used-in` werden ergänzt, Waisen und kaputte Quellen
      werden gelistet).
- [ ] `[handled]`-Assistent: einen Eintrag abwählen, umwandeln, Datei prüfen.
- [ ] Entwurfs-Assistent auf einer schlichten Gliederung: Überschrift umschalten, umwandeln, Buchliste und Datei prüfen.

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
- [ ] „Wohin damit?“ an einer Notiz und an einem längeren Unteranstrich zeigt höchstens fünf Textblöcke mit Pfad, Auszug und
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

PDF
- [ ] PDF-Export für das ganze Buch in A4 und A5 sowie für „Aktueller Abschnitt“: der native Speichern-Dialog schlägt
      einen sinnvollen Dateinamen vor; das PDF hat Titelseite und Inhaltsverzeichnis, Schrift und Abstände sehen gut
      aus, Blöcke mit Status Relevant oder Bemerkung (auch `#notiz`) fehlen samt Details; Warnungen erscheinen im
      Dialog.
