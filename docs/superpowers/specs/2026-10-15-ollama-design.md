# NoteEvolution – Lokales Sprachmodell (Ollama) für Themenverständnis

Stand: 2026-10-15 · Status: Entwurf · Stufe 3 der Design-Spezifikation vom 2026-10-05 (Abschnitt 6.4), auf eine lokale Lösung beschränkt. Baut auf Stufe 2 (lokale KI: Bedeutungsvektoren, „Relevant“, „Wohin damit?“) und der Modellwahl (`2026-10-08-modellwahl-design.md`) auf.

## 1. Ziel

„Relevant“ und „Wohin damit?“ sollen Texte verstehen statt nur ähnliche Wörter und Bedeutungen zu vergleichen. Vor allem soll jede Überschrift wissen, was in ihren Unterabschnitten steht. Ein neuer Text, der einen anderen Aspekt eines vorhandenen Themas beschreibt, wird zuerst dem passenden Oberthema zugeordnet und erst danach der passenden Stelle darin.

Dazu schreibt ein lokales Sprachmodell (Ollama) im Hintergrund Zusammenfassungen und Kernaussagen. Diese Texte bekommen Bedeutungsvektoren des vorhandenen lokalen Modells, sodass alle Vergleiche sofort laufen. Auf Wunsch entscheidet das Sprachmodell bei „Wohin damit?“ zusätzlich direkt und begründet seine Wahl.

Zielrechner ist ein Notebook ohne passende Grafikkarte (Intel Core i5-10210U, 4 Kerne, 16 GB RAM). Das Sprachmodell läuft daher nur im Hintergrund und auf ausdrücklichen Knopfdruck, nie bei jeder Cursorbewegung.

### Erfolgskriterien

- **Themenverständnis:** Jede Überschrift des Buchs hat eine Themen-Zusammenfassung ihres gesamten Inhalts (eigene Textblöcke und alle Unterabschnitte). Jede Notiz (Hauptpunkt) hat eine Kernaussage.
- **„Wohin damit?“ zweistufig:** Erst werden die passendsten Oberthemen bestimmt, dann die Stelle innerhalb dieser Themen. Die Liste zeigt Thema und Stelle.
- **„Genauer prüfen“:** Das Sprachmodell wählt unter den besten Themen das passende und die Stelle und begründet die Wahl in einem Satz. Es läuft mit Fortschrittsanzeige und ist abbrechbar.
- **„Relevant“:** Der aktuelle Abschnitt wird über seine Themen-Zusammenfassung beschrieben, die Notizen über ihre Kernaussagen.
- **Qualität messbar besser:** Auf einem Referenzbuch (Abschnitt 9.2) findet das neue „Wohin damit?“ das richtige Oberthema deutlich öfter unter den ersten drei als das heutige. Zielwerte: bei mindestens 80 % aller Referenznotizen ist das richtige Oberthema unter den ersten drei, und bei den Notizen „anderer Aspekt“ liegt die Quote mindestens 20 Prozentpunkte über der heutigen. Der heutige Wert wird zu Beginn gemessen.
- **Rechner bleibt nutzbar:** Die Hintergrundarbeit läuft mit genau einer Anfrage gleichzeitig, pausiert beim Tippen und lässt sich jederzeit anhalten.
- **Ohne Ollama wie heute:** Fehlt Ollama, ist es aus oder ist noch nichts berechnet, arbeiten alle Funktionen wie bisher.
- **Nur lokal, nur lesend:** Kein Text verlässt den Rechner. Das Sprachmodell schreibt keinen Buchtext und ändert keine Notiz- oder Buchdatei.

## 2. Bausteine

| Baustein | Aufgabe | Projekt |
|---|---|---|
| `IChatModel` / `OllamaChatModel` | Anfragen an Ollama (`/api/chat`, `/api/tags`, `/api/pull`, `/api/version`), Abbruch, Zeitlimit | AI |
| `UnderstandingStore` | Zusammenfassungen und Kernaussagen mit Fingerabdruck in `<vault>/.noteevolution/understanding.db` (SQLite) | AI |
| `UnderstandingWorker` | Warteschlange und Hintergrundberechnung, Pause, Fortschritt | AI |
| `UnderstandingPrompts` | Die festen Anweisungen an das Modell und das Auswerten der Antworten | AI |
| `RelevanceService` (erweitert) | Zweistufiges „Wohin damit?“, „Relevant“ mit Zusammenfassungen, „Genauer prüfen“ | AI |
| Einstellungen, Kopfzeile, „Wohin damit?“-Liste | Oberfläche | UI |

Die vorhandenen Bausteine bleiben: Bedeutungsvektoren (e5/bge-m3 über ONNX), `vectors.db`, Volltextsuche.

## 3. Was berechnet wird

### 3.1 Themen-Zusammenfassung je Überschrift

- **Inhalt:** 2–4 Sätze darüber, worum es in diesem Abschnitt geht, einschließlich aller Unterabschnitte. Sachlich, deutsch, ohne Einleitung („In diesem Abschnitt …“ ist erlaubt, aber nicht nötig).
- **Von unten nach oben:** Das Modell bekommt die eigenen Textblöcke der Überschrift (gekürzt, siehe 3.4) und die Zusammenfassungen ihrer direkten Unterüberschriften. Eine Überschrift wird erst berechnet, wenn alle ihre Unterüberschriften eine aktuelle Zusammenfassung haben.
- **Was nicht hineinkommt:** Blöcke mit Status Bemerkung (wie im PDF und bei „Relevant“ heute). Blöcke mit Status Relevant (übernommene Notizen) zählen mit, sie gehören inhaltlich zum Thema.
- **Die Wurzel:** Das Buch als Ganzes bekommt keine Zusammenfassung; Textblöcke vor der ersten Überschrift (Prolog) werden wie heute einzeln behandelt.

### 3.2 Kernaussage je Notiz

- **Inhalt:** 1–2 Sätze darüber, was die Notiz (ein Hauptpunkt mit allen Unterpunkten) im Kern sagt.
- **Für alle Notizen** im Hintergrund, nach den Überschriften des aktuellen Buchs, neueste zuerst.
- Sehr kurze Notizen (unter 120 Zeichen samt Unterpunkten) bekommen keine Kernaussage; ihr Text ist schon kernig genug. Sie werden über ihren Text verglichen.

### 3.3 Bedeutungsvektoren der neuen Texte

Jede Zusammenfassung und jede Kernaussage bekommt einen Bedeutungsvektor des eingestellten lokalen Modells (e5/bge-m3). Er liegt mit im `understanding.db`, zusammen mit der Modell-Id des Einbettungsmodells. Wechselt das Einbettungsmodell, werden nur die Vektoren neu berechnet, nicht die Texte.

### 3.4 Begrenzungen

- Eigene Textblöcke einer Überschrift gehen höchstens mit 6.000 Zeichen ein (die ersten Absätze jedes Blocks, gleichmäßig gekürzt). So bleibt jede Anfrage kurz genug für die CPU.
- Eine Notiz geht höchstens mit 4.000 Zeichen ein.
- Antworten werden auf 600 Zeichen (Zusammenfassung) bzw. 300 Zeichen (Kernaussage) begrenzt (`num_predict` und Kürzen).

### 3.5 Fingerabdruck und Neuberechnung

- **Überschrift:** Fingerabdruck über ihren Titel, den eingehenden Text ihrer eigenen Blöcke und die Fingerabdrücke ihrer Unterüberschriften. Ändert sich ein Absatz, sind seine Überschrift und alle darüber veraltet.
- **Notiz:** Fingerabdruck über ihren Text samt Unterpunkten.
- **Modell:** Zum Fingerabdruck gehört der Name des Sprachmodells und eine Versionsnummer der Anweisungen (`PromptVersion`). Ein Modell- oder Anweisungswechsel macht alles veraltet.
- **Veraltet heißt nicht unbrauchbar:** Bis die neue Fassung da ist, wird die alte weiter genutzt. Ohne jede Fassung wird der Text selbst genutzt (wie heute).

## 4. Hintergrundberechnung

- **Eine Anfrage gleichzeitig.** Ollama bekommt nie mehr als eine Anfrage von NoteEvolution.
- **Reihenfolge der Warteschlange:**
  1. Überschriften des aktuellen Buchs, von unten nach oben, zuerst die des aktuellen Abschnitts und seiner Oberabschnitte;
  2. übrige Überschriften des aktuellen Buchs;
  3. Notizen, neueste zuerst;
  4. Überschriften anderer Bücher.
- **Geänderter Text** setzt seine Überschrift (und die darüber) bzw. die Notiz an den Anfang ihrer Gruppe.
- **Pause beim Tippen:** Nach jeder Eingabe im Editor wartet die Berechnung, bis 10 s lang nichts getippt wurde. Eine laufende Anfrage wird dafür nicht abgebrochen.
- **Pausenschalter:** In der Kopfzeile neben der KI-Anzeige; der Zustand wird in `ui.json` gespeichert.
- **Akku:** Läuft der Rechner erkennbar im Akkubetrieb, pausiert die Berechnung von selbst und zeigt das an. Nicht erkennbar heißt: läuft.
- **Fortsetzen:** Berechnete Ergebnisse werden sofort gespeichert. Beim nächsten Start geht es mit dem Rest weiter.
- **Zeitlimit:** Eine Anfrage, die länger als 5 Minuten braucht, wird abgebrochen und später erneut versucht.
- **Fortschritt:** Die Kopfzeile zeigt z. B. „KI: lokal · Sprachmodell 34/120“ (aktuelles Buch und Notizen zusammen), danach „KI: lokal · Sprachmodell bereit“.

## 5. „Wohin damit?“

### 5.1 Zweistufige Suche (sofort)

- **Stufe 1, Thema:** Der Vektor der Notiz (aus Kernaussage und Text, 5.3) wird mit den Vektoren der Themen-Zusammenfassungen aller Überschriften des Buchs verglichen. Überschriften ohne Zusammenfassung werden wie heute über Pfad und Text verglichen. Die besten 3 Themen werden gewählt.
- **Auch Oberthemen zählen:** Der Wert einer Überschrift ist das Maximum aus ihrem eigenen Wert und 90 % des besten Werts ihrer Unterüberschriften. So setzt sich ein passendes Oberthema durch, auch wenn kein einzelner Unterabschnitt stark passt.
- **Stufe 2, Stelle:** Innerhalb der 3 Themen (samt Unterabschnitten) wird wie heute der passendste Textblock gesucht.
- **Liste:** Höchstens 5 Einträge, je Eintrag Thema und Stelle, z. B. „Schlaf › Träume – nach ‚Wer zu wenig …‘“, mit Prozentwert. Die Themen-Zusammenfassung steht im Tooltip. Ein Klick springt wie heute zur Stelle.

### 5.2 „Genauer prüfen“

- **Knopf** unter der „Wohin damit?“-Liste, nur wenn das Sprachmodell verbunden ist.
- **Anfrage:** Das Modell bekommt die Notiz (Text, gekürzt wie 3.4), die 3 besten Themen mit Pfad und Zusammenfassung und je Thema die 3 besten Stellen (Anfang des Textblocks). Es antwortet in festem JSON: gewähltes Thema, gewählte Stelle (oder „neue Stelle am Ende des Themas“), ein Satz Begründung.
- **Anzeige:** Während der Anfrage „Prüfe …“ mit Laufanzeige und „Abbrechen“. Danach steht oben in der Liste der gewählte Eintrag mit Begründung, markiert als „Empfehlung“. Ein Klick springt dorthin; übernommen wird wie heute mit dem Übernehmen-Knopf.
- **Vorrang:** Eine laufende Hintergrundanfrage wird nicht abgebrochen; „Genauer prüfen“ kommt als nächste Anfrage dran. Die Anzeige sagt dann „Wartet auf das Sprachmodell …“.
- **Unbrauchbare Antwort** (kein gültiges JSON, Thema nicht aus der Liste): Hinweis „Das Sprachmodell hat keine verwertbare Antwort gegeben“; die Liste aus 5.1 bleibt.
- **Ergebnis merken:** Die Empfehlung gilt für die Notiz und den Stand des Buchs; sie bleibt, solange die Liste offen ist.

### 5.3 Vektor der Notiz

Mittel aus dem Vektor ihrer Kernaussage und dem Vektor ihres Texts (wie heute), normalisiert. Ohne Kernaussage nur der Text. Gilt auch für Unterpunkte mit eigenen Knöpfen: sie werden wie heute über ihren eigenen Text verglichen, bekommen aber keine eigene Kernaussage.

## 6. „Relevant“

- **Thema des aktuellen Abschnitts:** Mittel aus dem Vektor seiner Themen-Zusammenfassung und dem heutigen Themenvektor (Pfad und Text bzw. Cursor, Bereich, eigener Text). Ohne Zusammenfassung wie heute.
- **Oberthema:** Die Zusammenfassung des übergeordneten Abschnitts geht mit einem Viertel Gewicht ein, damit Notizen zum größeren Zusammenhang nicht verloren gehen.
- **Notizen:** Verglichen wird mit dem Vektor aus 5.3 (Kernaussage und Text).
- Kein Nachsortieren durch das Sprachmodell (zu langsam auf der CPU).

## 7. Einrichtung und Einstellungen

- **Abschnitt „Sprachmodell (Ollama)“ in den Einstellungen:**
  - Schalter „Sprachmodell verwenden“ (aus bis zur ersten Einrichtung).
  - Adresse, Vorgabe `http://127.0.0.1:11434`.
  - Modell, Vorgabe `qwen2.5:7b`; Auswahl aus den in Ollama installierten Modellen (`/api/tags`).
  - Zustand: „verbunden (Ollama 0.x)“, „Ollama nicht erreichbar“, „Modell nicht installiert“.
  - „Modell laden“, wenn das Modell fehlt: Bestätigungsdialog mit Name und Größe (bei `qwen2.5:7b` etwa 4,7 GB), Fortschritt über `/api/pull`, abbrechbar.
  - Speicherort aller Werte: `ui.json` (gilt für alle Vaults).
- **Erste Einrichtung:** Ist Ollama erreichbar und das Sprachmodell noch nicht eingerichtet, bietet die App einmalig (wie beim KI-Modell) „Sprachmodell einrichten?“ an.
- **Modellwechsel:** Neue Zusammenfassungen werden im Hintergrund berechnet, die alten bis dahin weiter genutzt.
- **Nur auf diesem Rechner:** Eine Adresse, die nicht auf den eigenen Rechner zeigt (nicht `localhost`, `127.0.0.1`, `::1`), wird nur nach einer deutlichen Warnung übernommen („Notiz- und Buchtexte werden an diesen Rechner gesendet“).

## 8. Fehlerbehandlung

| Situation | Verhalten |
|---|---|
| Ollama nicht erreichbar | Berechnung pausiert, Kopfzeile „Sprachmodell nicht erreichbar“; neuer Versuch alle 60 s; Funktionen wie heute |
| Modell nicht installiert | Hinweis in Kopfzeile und Einstellungen mit „Modell laden“ |
| Anfrage schlägt fehl oder läuft in das Zeitlimit | später erneut, höchstens 3 Versuche je Eintrag und Fassung; danach übersprungen bis zur nächsten Änderung |
| Leere oder zu lange Antwort | gekürzt bzw. als Fehlversuch gewertet |
| `understanding.db` nicht lesbar | neu angelegt (Inhalt ist wiederherstellbar), Hinweis im Protokoll |
| Vault gewechselt oder App beendet während einer Anfrage | Anfrage abgebrochen, nichts gespeichert |

## 9. Tests und Qualitätsnachweis

### 9.1 Automatische Tests

Mit einem Ersatz für Ollama (feste Antworten, steuerbare Verzögerung, Fehler):
- Zusammenfassungen von unten nach oben; Oberabschnitt erst nach allen Unterabschnitten.
- Fingerabdruck: Änderung eines Absatzes macht seine Überschrift und alle darüber veraltet, sonst nichts; Modell- und Anweisungswechsel macht alles veraltet.
- Warteschlange: Reihenfolge, Vorrang geänderter Texte, Pause beim Tippen, Pausenschalter, Fortsetzen nach Neustart.
- Bemerkungen gehen nicht in Zusammenfassungen ein.
- Zweistufiges „Wohin damit?“ mit festen Vektoren: ein Oberthema setzt sich gegen einen einzeln ähnlicheren Textblock in einem anderen Thema durch.
- „Genauer prüfen“: gültige Antwort, ungültiges JSON, Thema nicht aus der Liste, Abbruch, Warten hinter einer Hintergrundanfrage.
- Fehlerfälle aus Abschnitt 8.
- Nichts schreibt in Buch- oder Notizdateien (Dateiinhalte vor und nach unverändert).

### 9.2 Qualitätsvergleich

- **Referenzbuch:** Ein künstliches Buch mit 6–8 Kapiteln und Unterkapiteln und dazu 40 Notizen, alle selbst geschrieben (keine persönlichen Texte). Für jede Notiz ist festgelegt, welches Kapitel bzw. Unterkapitel richtig ist.
- **Darunter mindestens 15 Notizen „anderer Aspekt“:** Sie gehören zu einem Thema, teilen aber mit keinem einzelnen Textblock dort viele Wörter oder Bedeutungen.
- **Messung:** Anteil der Notizen, deren richtiges Kapitel unter den ersten 3 Themen ist, heute gegen neu (mit dem echten Sprachmodell, einmal vorberechnet). Zusätzlich die Trefferquote von „Genauer prüfen“ (richtiges Unterkapitel an erster Stelle).
- **Ablage:** Referenzbuch und Messprogramm liegen unter `tests/quality/`. Es läuft nicht mit der normalen Testsuite (braucht Ollama), sondern auf Aufruf. Das Ergebnis kommt in den Bericht zur Umsetzung.

### 9.3 Messung der Geschwindigkeit

Zu Beginn der Umsetzung: reale Antwortzeiten von `qwen2.5:7b` auf dem Zielrechner für eine Zusammenfassung, eine Kernaussage und „Genauer prüfen“. Ist „Genauer prüfen“ langsamer als 3 Minuten, wird `qwen2.5:3b` als Vorgabe vorgeschlagen.

## 10. Nicht enthalten

- Cloud-Anbieter (Claude-API); die Spezifikation vom 2026-10-05 sieht sie in Stufe 3 vor, sie werden bei Bedarf nachgereicht.
- Nachsortieren der Relevant-Liste durch das Sprachmodell.
- Prüfen der Fundstellen-Vorschläge im `[handled]`-Assistenten durch das Sprachmodell (nutzt aber die zweistufige Suche aus 5.1 mit).
- Schreiben oder Umformulieren von Buchtext (bleibt bewusst ausgeschlossen, Spezifikation vom 2026-10-05, 12).
- Einstellen der Ollama-Laufzeit (Threads, Kontextlänge) aus der App heraus.
