# NoteEvolution

NoteEvolution ist ein Schreibprogramm, mit dem aus Journal-Notizen ein Buch entsteht. Notizen und Buch liegen als
Markdown-Dateien in einem Logseq-Vault; Notizen werden per „Übernehmen“ (oder Ziehen) ins Buch kopiert und dort
ausformuliert, und beide Seiten verweisen aufeinander (`used-in::` an der Notiz, `source::` am Buchblock). Am Ende
entsteht ein PDF. Stufe 1 umfasst Editor, Notizenbereich, Verknüpfungsprüfung, Assistenten und den einfachen
PDF-Export; die Spezifikation steht in `docs/superpowers/specs/2026-10-05-noteevolution-design.md`.

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

`<Benutzerdaten>` ist der Ordner `NoteEvolution` im Anwendungsdatenordner des Systems (Windows:
`%APPDATA%\NoteEvolution`, Linux und macOS: `~/.config/NoteEvolution`).

Die Dateien im Vault sind die einzige Wahrheit. Alles unter `.noteevolution/` ist Zusatz oder wiederherstellbar und kann
von der Synchronisation (Obsidian, Syncthing usw.) ausgenommen werden; der Suchindex wird bei jedem Öffnen im Speicher
neu aufgebaut. Dasselbe Buch sollte nicht gleichzeitig in Logseq bearbeitet werden.

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

PDF
- [ ] PDF-Export für das ganze Buch in A4 und A5 sowie für „Aktueller Abschnitt“: der native Speichern-Dialog schlägt
      einen sinnvollen Dateinamen vor; das PDF hat Titelseite und Inhaltsverzeichnis, Schrift und Abstände sehen gut
      aus, `#notiz`-Absätze fehlen; Warnungen erscheinen im Dialog.
