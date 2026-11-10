# Zuordnungen nur bei wirklich betroffenen Notizen neu prüfen — Entwurf

Stand 2026-11-10, nach der Messung vom 9./10.11. mit dem Nutzer abgestimmt.

## 1. Ausgangslage

Die Messung im Vault des Nutzers (rund 3440 unverwendete Notizen, rund 160 Überschriften) zeigt:

- Eine Buchänderung ließ nur 1 bis 4 Zusammenfassungen neu entstehen.
- Eine neue oder umbenannte Überschrift machte aber über 2400 Zuordnungen fällig. Davon waren 2074 fällig, weil sich die Liste der angebotenen Abschnitte geändert hatte, 196 wegen eines anderen Zusammenfassungssatzes und 93 wegen anderer Textstellen.
- Grund: Eine Zuordnung wird neu berechnet, sobald sich ihre Anfrage an das Modell irgendwo ändert. Die Anfrage zeigt bis zu 14 Abschnitte aus den 3 besten Kapiteln, und eine neue Überschrift ändert diese Liste für jede Notiz, der ihr Kapitel angeboten wird.

Beim Lesen des Codes fiel zusätzlich auf (noch durch einen Test zu bestätigen, Abschnitt 6):

- Ein gespeichertes Ergebnis nennt Abschnitt und Textstelle über interne Schlüssel, die die App bei jedem Start neu vergibt. Nach einem Neustart findet `StoreUnderstanding.Placement` den Abschnitt nicht mehr und gibt nichts zurück. Gleichzeitig gilt die Zuordnung als aktuell und wird nicht neu berechnet. Vorberechnete Zuordnungen aus früheren Sitzungen fehlen dann in „Wohin damit?“, „Hierher eingeordnet“ und der Abdeckung.
- Notizen mit gleicher erster Zeile auf derselben Seite haben dieselbe Identität und teilen sich deshalb ihr „letztes Ergebnis“.

## 2. Ziel

- Nach einer Buchänderung werden nur die Notizen neu geprüft, für die sich die Wahl des Modells wahrscheinlich ändern könnte.
- Alle übrigen behalten ihr Ergebnis, und es gilt weiter als aktuell (grüner Punkt), nicht als „veraltet“.
- Gespeicherte Ergebnisse bleiben über einen Neustart hinweg nutzbar, auch die schon vorhandenen.

## 3. Ergebnisse dauerhaft lesbar machen

- **Neue Ergebnisse speichern zusätzlich:**
  - den Pfad des gewählten Abschnitts (Titel von oben nach unten mit Index bei gleichen Titeln, wie in der Identität der Zusammenfassungen);
  - den Anfang der gewählten Textstelle (erste Zeile, höchstens 100 Zeichen);
  - die Grundlage der Prüfung (Abschnitt 4): einen Fingerabdruck des Notiztexts, die Pfade der angebotenen Abschnitte und die Pfade der 3 besten Kapitel laut Embedding.
- **Lesen:** Der Abschnitt wird erst über seinen Schlüssel gesucht, dann über seinen Pfad. Die Textstelle wird über ihren Schlüssel gesucht, dann über den ersten Textblock im Abschnitt, der mit dem gespeicherten Anfang beginnt. Ohne Treffer gilt „neu am Ende des Abschnitts“; der Abschnitt bleibt gültig.
- **Vorhandene Ergebnisse ohne diese Angaben:** Der gewählte Abschnitt wird einmalig über die gespeicherte Zusammenfassung erkannt; sie steht im Ergebnis und in der Anfrage, die bei gleichem Fingerabdruck dieselbe ist wie damals. Ist sie eindeutig, werden Pfad und Grundlage aus der heutigen Anfrage nachgetragen. Die Textstelle lässt sich nicht mehr bestimmen, sie wird „neu am Ende des Abschnitts“. Lässt sich der Abschnitt so nicht erkennen, wird die Zuordnung einmal neu berechnet. Es wird nichts in großer Zahl neu gerechnet, nur diese Fälle.
- **Gleiche erste Zeile:** Die zweite und jede weitere Notiz mit gleicher Identität auf einer Seite bekommt `#2`, `#3` … an ihre Identität angehängt. Die erste behält sie, damit vorhandene Ergebnisse gültig bleiben.

## 4. Wann eine Zuordnung neu geprüft wird

Hat eine Notiz ein gespeichertes Ergebnis und ändert sich ihre Anfrage, wird sie nur neu geprüft, wenn mindestens einer dieser Punkte zutrifft:

1. Ihr Text hat sich geändert (Fingerabdruck des Notiztexts).
2. Der gewählte Abschnitt ist nicht mehr im Buch (weder über Schlüssel noch über Pfad zu finden).
3. Unter ihren 3 besten Kapiteln laut Embedding ist eines, das in der Grundlage nicht vorkam. Oder ein Abschnitt, der heute angeboten wird und in der Grundlage fehlte, steht in der Embedding-Rangfolge der Notiz vor dem gewählten Abschnitt.
4. Von den Abschnitten der Grundlage wird weniger als die Hälfte noch angeboten (großer Umbau).
5. Der Nutzer klickt „Genauer prüfen“; das ist unverändert.

Sonst bleibt das Ergebnis und gilt als aktuell. Nicht mehr allein ausschlaggebend sind:
- andere Zusammenfassungssätze, auch die des gewählten Abschnitts,
- andere oder verschwundene Textstellen (eine verschwundene gewählte Stelle wird „neu am Ende des Abschnitts“),
- umbenannte Abschnitte, die über ihren Schlüssel gefunden werden,
- eine geänderte Reihenfolge der angebotenen Abschnitte.

Verglichen wird immer mit der Grundlage der letzten echten Prüfung, nicht mit der vorigen Anfrage. Viele kleine Änderungen summieren sich so und lösen über Punkt 3 oder 4 irgendwann eine Prüfung aus.

## 5. Anzeige und Messung

- **Statuspunkt:** Ein behaltenes Ergebnis ist grün; „veraltet“ (gelb) steht nur noch für Notizen, die wirklich neu geprüft werden.
- **Fortschrittsanzeige** in der Kopfzeile: Sie zählt nur noch diese Notizen.
- **Messung:**
  - Die Messzeilen im Log zählen zusätzlich, wie viele Zuordnungen behalten wurden und nach welchem Punkt aus Abschnitt 4 die übrigen neu geprüft werden.
  - „Placement recheck“ vergleicht künftig über den Abschnittspfad, damit der Vergleich auch nach einem Neustart klappt.
  - Die Messung bleibt im Code, bis wir sie gemeinsam ausgewertet haben.

## 6. Prüfung

- **Zuerst ein Test zum Neustart-Fehler aus Abschnitt 1:** Ergebnis speichern, Sitzung schließen, neu öffnen; heute erwartet: keine Zuordnung sichtbar. Danach zeigt derselbe Test die Zuordnung samt Abschnitt.
- **Tests je Punkt aus Abschnitt 4,** jeweils behalten und neu prüfen: neue Überschrift in einem angebotenen Kapitel ohne bessere Rangfolge, dieselbe mit besserer Rangfolge, umbenannter Abschnitt, geänderte Zusammenfassung, verschwundene Textstelle, gelöschter gewählter Abschnitt, großer Umbau, geänderter Notiztext.
- **Übernahme vorhandener Ergebnisse:** über die Zusammenfassung erkannt, nicht erkannt, Zusammenfassung mehrdeutig.

## 7. Nicht enthalten

- Eine Teilprüfung, bei der das Modell nur zwischen altem und neuem Abschnitt wählt.
- Eine Änderung an den Zusammenfassungen selbst; die Messung zeigt, dass sie nicht teuer sind.
- Ein Umbau von „Relevant“ und „Wohin damit?“ außer dem Lesen der Ergebnisse (Abschnitt 3).
