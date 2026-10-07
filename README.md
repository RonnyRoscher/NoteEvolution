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

## Wo was liegt

| Was | Ort |
| --- | --- |
| Protokolle, solange kein Vault offen ist | `<Benutzerdaten>/logs` |
| Protokolle eines geöffneten Vaults (täglich neue Datei) | `<vault>/.noteevolution/logs` |
| Fenster- und Anzeigeeinstellungen (`ui.json`, u. a. zuletzt geöffneter Vault) | `<Benutzerdaten>/ui.json` |
| Vault-Einstellungen (Notizordner) | `<vault>/.noteevolution/settings.json` |
| Tägliche Sicherungen vor dem ersten Schreiben einer Datei (30 Tage) | `<vault>/.noteevolution/backups/<datum>/` |
| Noch nicht geschriebene Notiz-Änderungen (z. B. schreibgeschützte Notiz) | `<vault>/.noteevolution/pending.json` |
| KI-Modell (nach dem einmaligen Download) | `<Benutzerdaten>/models/multilingual-e5-small-int8/` |
| Gespeicherte Bedeutungsvektoren der Notizen | `<vault>/.noteevolution/vectors.db` |

`<Benutzerdaten>` ist der Ordner `NoteEvolution` im Anwendungsdatenordner des Systems (Windows:
`%APPDATA%\NoteEvolution`, Linux und macOS: `~/.config/NoteEvolution`).

Die Dateien im Vault sind die einzige Wahrheit. Alles unter `.noteevolution/` ist Zusatz oder wiederherstellbar und kann
von der Synchronisation (Obsidian, Syncthing usw.) ausgenommen werden; der Suchindex wird bei jedem Öffnen im Speicher
neu aufgebaut. Dasselbe Buch sollte nicht gleichzeitig in Logseq bearbeitet werden.

## Lokale KI

Die KI-Funktionen sind ein Zusatz: „Relevant“ (die Notizen, die zum Abschnitt oder zur Textstelle passen), die Suche
nach Bedeutung (ergänzt die Volltextsuche), „Wohin damit?“ an jeder Notizkarte (die fünf passendsten Abschnitte des
Buchs) und Fundstellen-Vorschläge im `[handled]`-Assistenten. Sie lesen und schreiben keinen Buchtext. Einzige
Schreibaktion sind die Verknüpfungszeilen (`id::`, `source::`, `used-in::`) für Fundstellen, die du im Assistenten
selbst gewählt und bestätigt hast. Ohne Modell arbeitet die App unverändert mit der Volltextsuche.

- **Modell:** `multilingual-e5-small`, quantisiert (int8), etwa 120 MB, läuft mit ONNX Runtime auf der CPU. Quelle ist
  Hugging Face, Repository `Xenova/multilingual-e5-small` (`onnx/model_quantized.onnx` und `sentencepiece.bpe.model`),
  auf eine feste Revision gepinnt und per SHA-256 geprüft. Das ursprüngliche Modell `intfloat/multilingual-e5-small`
  steht unter der MIT-Lizenz.
- **Download:** nur nach deiner Bestätigung (Angebot beim Öffnen eines Vaults, Knopf im Reiter „Relevant“ oder in den
  Einstellungen unter „KI-Modell“). Danach arbeitet alles offline, die App greift nicht mehr auf das Netz zu.
- **Ablage:** das Modell liegt im Benutzerprofil, nicht im Vault: unter Windows
  `%APPDATA%\NoteEvolution\models\multilingual-e5-small-int8\`, sonst `~/.config/NoteEvolution/models/multilingual-e5-small-int8/`.
  Zum Entfernen den Ordner löschen; die App bietet den Download danach wieder an.
- **Bedeutungsvektoren:** `<vault>/.noteevolution/vectors.db` speichert die Vektoren der Notizen (neu berechnet wird
  nur, was sich geändert hat). Die Datei darf jederzeit gelöscht werden, sie wird beim nächsten Öffnen neu aufgebaut
  (das Indexieren läuft im Hintergrund, die Kopfzeile zeigt den Fortschritt). Wie alles unter `.noteevolution/` kann
  sie von der Synchronisation ausgenommen werden.

## Manuelle Abnahme

Mit einem künstlichen Test-Vault (keine persönlichen Texte), Änderungen jeweils mit `git diff` gegen eine Kopie prüfen.

Fenster und Grundlayout
- [ ] Das Fenster öffnet sich, nach der Ordnerauswahl erscheinen die Bücher; Darstellung hell/dunkel/System stimmt.
- [ ] Die Trenner zwischen den drei Bereichen lassen sich ziehen; die Breiten sind nach einem Neustart wieder da.
- [ ] Einstellungen: Schriftgröße, Zeilenbreite und Farbschema wirken; geänderte Notizordner laden den Vault neu.

Gliederung
- [ ] Überschrift per Doppelklick umbenennen (Fokus im Eingabefeld), „+“ legt eine Unterüberschrift an.
- [ ] Abschnitte per Ziehen umsortieren und einhängen (echtes Ziehen mit der Maus in WebView2).

Editor
- [ ] Text tippen und ca. 1 s warten: nur diese Zeile ändert sich in der Datei; der Cursor bleibt, wo er ist.
- [ ] Leerzeichen am Ende eines Absatzes tippen und warten: das Leerzeichen bleibt, der Cursor springt nicht,
      Strg+Z im Editor funktioniert weiter.
- [ ] Strg+Enter mitten in einem verknüpften Block: die Notiz hat danach zwei `used-in`-Einträge.
- [ ] Tab, Umschalt+Tab und Strg+Umschalt+N funktionieren (Strg+Umschalt+N wird nicht von WebView2 abgefangen).
- [ ] Rücktaste am Blockanfang verbindet zwei Blöcke; „Rückgängig“ in der Kopfzeile stellt den Block wieder her.
- [ ] Einen verknüpften Block löschen, ca. 1 s warten, dann Strg+Z im Editor: der Block ist wieder verknüpft
      (Quellen-Chip, `source::` im Buch, `used-in::` an der Notiz). Danach „Rückgängig“ in der Kopfzeile:
      es lehnt ab („Die Aktion konnte nicht rückgängig gemacht werden.“), der Block steht nur einmal im Buch.
- [ ] Manuskriptansicht: einen Block (gepunkteter Griff links beim Überfahren) unter ein anderes Kapitel ziehen;
      danach ist er in der Datei richtig eingerückt.
- [ ] Manuskriptansicht: Schalter „Quellen anzeigen“ und die Randmarkierung bei Blöcken mit Quellen.
- [ ] Eine nicht sicher lesbare Buchdatei (z. B. offener Codeblock) zeigt den Hinweis, dass sie schreibgeschützt ist;
      der Editor ist gesperrt.
- [ ] Fenster schließen, während der Text nicht gespeichert werden kann (z. B. Buchdatei schreibgeschützt): das
      Fenster bleibt offen und zeigt einen Hinweis; erneutes Schließen beendet das Programm.

Notizen und Übernehmen
- [ ] Eine Notiz mit einem Handgriff übernehmen (Knopf oder Ziehen an die gewünschte Stelle): die Karte zeigt ✓, der
      Buchblock einen Quellen-Chip, der Hinweis bietet 8 s lang „Rückgängig“; in der Notizdatei kam nur eine
      `id::`/`used-in::`-Zeile hinzu.
- [ ] „Verwendete ausblenden“ blendet verwendete Notizen aus.

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
- [ ] Der Reiter „Relevant“ folgt dem Abschnitt: einen anderen Abschnitt wählen, nach etwa 1,5 s ändert sich die
      Liste; in der Manuskriptansicht folgt sie der Textstelle am Cursor.
- [ ] „Verwendete ausblenden“ und der Zeitraum wirken auch in „Relevant“.
- [ ] Die Suche findet sinnverwandte Notizen ohne gleiche Wörter (z. B. nach „Müdigkeit“ suchen und eine Notiz über
      „Schlaf“ finden), Treffer mit dem gesuchten Wort stehen weiter oben.
- [ ] „Wohin damit?“ an einer Notizkarte zeigt höchstens fünf Abschnitte mit Prozentwert; ein Klick springt zum
      Abschnitt (der Editor speichert vorher).
- [ ] `[handled]`-Assistent: neben jeder Notiz steht „Stelle unbekannt“ (Standard) und bis zu drei Fundstellen mit
      Textauszug und Prozent; nach dem Buchwechsel werden sie neu berechnet. Eine Fundstelle wählen, umwandeln: der
      Buchblock hat `id::` und `source::`, die Notiz `used-in:: [[Buch]] ((id))`, `[handled]` ist entfernt. Eine
      Notiz mit „Stelle unbekannt“ bekommt nur `used-in:: [[Buch]]`.
- [ ] Verhalten ohne Netz: Netzwerk trennen, App neu starten; alle KI-Funktionen arbeiten weiter, es gibt keinen
      Download-Hinweis.
- [ ] Modellordner umbenennen: die App fällt auf die Volltextsuche zurück und bietet den Download an; `vectors.db`
      löschen und neu öffnen: sie wird neu aufgebaut.

PDF
- [ ] PDF-Export für das ganze Buch in A4 und A5 sowie für „Aktueller Abschnitt“: der native Speichern-Dialog schlägt
      einen sinnvollen Dateinamen vor; das PDF hat Titelseite und Inhaltsverzeichnis, Schrift und Abstände sehen gut
      aus, `#notiz`-Absätze fehlen; Warnungen erscheinen im Dialog.
