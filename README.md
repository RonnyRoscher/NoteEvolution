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

„Übernehmen“ an einer Notiz oder einem Unteranstrich übernimmt mit einem Klick **am Cursor** (in den aktuellen Block);
der Pfeil daneben bietet zusätzlich **Danach** (neues Element nach dem aktuellen) und **Darunter** (als erstes
Unterelement). Die Quelle hängt immer am umgebenden Textblock.

**Bereich:** Mit „Bereich ▲“ / „Bereich ▼“ (oder Alt+Pfeil hoch/runter) wird der markierte Bereich stufenweise
erweitert (übergeordnetes Element samt allem darunter, bis zum ganzen Buch) bzw. verkleinert (bis zum Element allein,
„ohne Unterelemente“). Kasten, Quellenanzahl und „Relevant“ folgen dem Bereich; wechselt der Cursor das Element, gilt
wieder der Standard. Auf den Bereich wirken zwei weitere Knöpfe:

- **Zusammenfügen:** die Textblöcke im Bereich werden je Überschrift zu einem Block (Texte durch eine Leerzeile
  getrennt, Details wandern mit); Quellen werden vereinigt, die `used-in::`-Verweise der Notizen zeigen danach auf
  den verbleibenden Block.
- **Unter neue Überschrift:** der Bereich kommt unter eine neue, leere Überschrift eine Ebene unter seiner Überschrift
  (bei einem Textblock samt der direkt folgenden Textblöcke bis zur nächsten Unterüberschrift); der Cursor steht im
  neuen Titel.

**Wohin damit?** an einer Notiz oder einem Unteranstrich zeigt die fünf passendsten Textblöcke des Buchs mit
Überschriftenpfad, Textauszug und Prozent (✓, wenn die Notiz dort schon Quelle ist). Ein Klick springt zum Block,
die Liste bleibt offen; übernommen wird dort mit dem geteilten „Übernehmen“-Knopf.

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
nach Bedeutung (ergänzt die Volltextsuche), „Wohin damit?“ an jeder Notiz und jedem Unteranstrich (die fünf
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
- [ ] Einen Block (gepunkteter Griff links beim Überfahren) unter ein anderes Kapitel ziehen; danach ist er in der
      Datei richtig eingerückt.
- [ ] Schalter „Quellen anzeigen“ und die Randmarkierung bei Blöcken mit Quellen.
- [ ] Markierung: Cursor in Überschrift, Textblock und Detail setzen; der Kasten umfasst jeweils das richtige Element,
      die Leiste sitzt am unteren Rand und bleibt beim Scrollen eines langen Abschnitts am Fensterrand sichtbar.
- [ ] Alt+Enter, Alt+Umschalt+Enter, Tab/Umschalt+Tab in einer Überschrift und Rücktaste am Titelanfang wirken im
      echten Fenster (werden nicht von WebView2 abgefangen); danach „Rückgängig“ in der Kopfzeile.
- [ ] „Abschnitt löschen“ an einer Überschrift mit verknüpften Blöcken: die `used-in::`-Einträge verschwinden,
      „Rückgängig“ bringt Buch und Notizen zurück.
- [ ] Quellen der Markierung aufklappen, eine Quelle anklicken: der Reiter Journal springt zur Notiz und hebt sie hervor.
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
      „Darunter“ bei Textblock, Detail und Überschrift; die Quelle steht jeweils am umgebenden Textblock.

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
- [ ] „Wohin damit?“ an einer Notiz und an einem Unteranstrich zeigt höchstens fünf Textblöcke mit Pfad, Auszug und
      Prozentwert; ein Klick springt zum Block, die Liste bleibt offen; dort mit „Danach“ übernehmen.
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

PDF
- [ ] PDF-Export für das ganze Buch in A4 und A5 sowie für „Aktueller Abschnitt“: der native Speichern-Dialog schlägt
      einen sinnvollen Dateinamen vor; das PDF hat Titelseite und Inhaltsverzeichnis, Schrift und Abstände sehen gut
      aus, `#notiz`-Absätze fehlen; Warnungen erscheinen im Dialog.
