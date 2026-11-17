# Wiederholungen im Buch

Stand: 2026-11-16 · Status: Entwurf · Mit dem Nutzer im Chat abgestimmt.

## 1. Ziel

Die App findet Stellen eines Buchs, die dasselbe sagen, und zeigt sie an drei Orten: in einem Assistenten mit beiden
Stellen nebeneinander, in der Buchansicht (Leiste unter der Markierung, Randmarkierung am Block) und in der Gliederung.
Aus dem Assistenten heraus lassen sich Querverweise zwischen den beiden Stellen setzen, ein Paar als gewollt abhaken
und beide Stellen im Buch öffnen. Die Analyse läuft im Hintergrund mit, sodass die Anzeige ohne Zutun aktuell ist.

### Erfolgskriterien

- Eine doppelt übernommene Passage und ein in anderen Worten wiederholter Gedanke stehen nach dem Öffnen des Buchs
  ohne weiteren Klick in der Übersicht, getrennt nach „Wörtlich“ und „Inhaltlich ähnlich“.
- Von einem Block im Buch führen höchstens zwei Klicks zur Gegenstelle im Side-by-Side.
- Ein als gewollt abgehaktes Paar taucht nicht wieder auf, auch nach Neustart und nach Umschreiben der Blöcke.
- Die Analyse verändert die Buchdatei nie von sich aus.

## 2. Was verglichen wird

- **Einheit:** ein Textblock des Buchs mit seinen Unterpunkten (sein ganzer Text). Jeder wird mit jedem anderen
  verglichen, auch innerhalb desselben Abschnitts.
- **Nicht verglichen** werden: Blöcke mit Status „Relevant“ oder „Bemerkung“ (sie kommen nicht ins PDF), der Anhang,
  Überschriften, Bildblöcke und Blöcke mit weniger als 8 Wörtern.
- **Text für den Vergleich:** der sichtbare Text. Formatierungszeichen (`**`, `*`), Querverweise, Quellenangaben,
  Personen- und Glossarverweise und Bilder zählen nicht mit; Groß- und Kleinschreibung und Satzzeichen auch nicht.

## 3. Zwei Arten von Wiederholung

### 3.1 Wörtlich

- Zwei Blöcke teilen eine Folge von mindestens 8 gleichen Wörtern in Folge (im Vergleichstext aus 2).
- Zu jedem Paar werden alle gemeinsamen Folgen gemerkt, jede so lang wie möglich; sie werden im Side-by-Side
  hervorgehoben.
- Das Paar trägt die Zahl der gemeinsamen Wörter (Summe über die Folgen); sie ordnet nichts, steht aber in der Liste.

### 3.2 Inhaltlich ähnlich

- Die Embeddings zweier Blöcke (dasselbe lokale Modell wie für die Notizen, beide Seiten mit derselben Rolle) haben
  eine Kosinus-Ähnlichkeit von mindestens **0,90**.
- Je Block zählen höchstens seine **drei** ähnlichsten Gegenstellen. Ein Paar steht in der Liste, wenn es für
  mindestens einen der beiden Blöcke unter dessen drei ähnlichsten ist.
- Ein Paar, das schon wörtlich gefunden wurde, steht nur in der wörtlichen Gruppe.
- Der Wert 0,90 ist eine Schätzung. Die Analyse schreibt je Paar ab 0,85 eine Logzeile
  („Repetition candidate {Score} listed={bool}: "…" ~ "…"“, je Paar einmal pro Sitzung), damit er sich nachstellen
  lässt.
- **Grenze:** Das Modell liest etwa die ersten 500 Wortteile eines Textes. Bei sehr langen Blöcken zählt inhaltlich
  der Anfang.

## 4. Hintergrundlauf

- **Auslöser:** Öffnen oder Wechseln des Buchs, jedes Speichern, jede übernommene äußere Änderung der Buchdatei.
- **Wörtlich:** wird bei jedem Auslöser sofort für das ganze Buch neu berechnet.
- **Inhaltlich:** Nur Blöcke, deren Vergleichstext sich geändert hat, werden neu eingebettet. Die Vektoren liegen im
  bestehenden Vektor-Zwischenspeicher des Vaults (nach Textinhalt), ein unveränderter Block kostet also auch nach
  einem Neustart nichts. Das Einbetten läuft im Hintergrund, hinter dem Notiz-Index, und blockiert weder Editor noch
  Speichern.
- **Stand:** Solange noch Blöcke fehlen, zeigt die Statuszeile „Wiederholungen: 120/340 Blöcke“, und jede Zahl in
  Leiste, Gliederung und Assistent trägt ein „…“. Paare aus schon eingebetteten Blöcken werden dabei schon gezeigt.
- **KI aus oder Modell fehlt:** Es gibt nur die wörtliche Gruppe; der Assistent zeigt den Hinweis „Inhaltlich
  ähnliche Stellen brauchen die lokale KI.“ Ein Fehler beim Einbetten wird protokolliert und lässt die betroffenen
  Blöcke aus, bis sie sich ändern.
- **Mehrere Bücher:** Analysiert wird das Buch der aktiven Hälfte. In der geteilten Ansicht mit zwei Büchern gilt
  jede Anzeige für das Buch ihrer Hälfte; das zweite Buch wird berechnet, sobald es gezeigt wird.

## 5. Assistent „Wiederholungen“

Eintrag „Wiederholungen“ im Menü „Assistenten“ (ohne offenes Buch ausgegraut). Ein Fenster mit zwei Teilen.

### 5.1 Liste (links)

- Zwei Gruppen mit Überschrift und Zahl: **„Wörtlich“** und **„Inhaltlich ähnlich“**. Innerhalb der Gruppe in
  Buchreihenfolge der ersten Stelle, dann der zweiten.
- Ein Eintrag nennt beide Stellen als „Abschnitt › Textanfang“ und rechts die Stärke: bei wörtlichen „n Wörter“, bei
  inhaltlichen die Ähnlichkeit in Prozent.
- **Filter „Kapitel“:** „Alle“ oder eine Überschrift der obersten Ebene; es bleiben die Paare, von denen mindestens
  eine Stelle in diesem Kapitel liegt.
- **Schalter „Gewollte anzeigen“:** blendet abgehakte Paare ein (grau, mit „Gewollt“ gekennzeichnet).
- Leere Liste: „Keine Wiederholungen gefunden.“ (bei laufender Analyse mit dem Stand aus 4).

### 5.2 Side-by-Side (rechts)

- Beide Blöcke vollständig nebeneinander, die im Buch frühere Stelle links. Über jedem der Abschnittspfad.
- Unterpunkte stehen eingerückt wie im Buch; Fett und Kursiv werden dargestellt. Der Text ist hier nicht änderbar.
- Bei wörtlichen Paaren sind die gemeinsamen Folgen in beiden Spalten hervorgehoben.

### 5.3 Aktionen

- **„Verweis einfügen“** unter jeder Spalte: hängt am Ende des eigenen Textes dieses Blocks (vor seinen
  Unterpunkten) einen Querverweis `((id))` auf den anderen Block an, durch ein Leerzeichen getrennt. Es gelten die
  Regeln der Querverweise: Das Ziel bekommt eine `id::`, falls es keine hat; am verweisenden Block wird `updated`
  gesetzt; der Editor speichert vorher; ein Schritt für „Rückgängig“ („Rückgängig: Verweis einfügen“).
- **„In beide Richtungen“:** beide Verweise in einem Schritt.
- Verweist ein Block schon auf den anderen, ist sein Knopf ausgegraut („Verweis besteht“).
- **„Gewollt“:** nimmt das Paar aus Liste und Zählung. Bei eingeblendeten gewollten Paaren heißt der Knopf „Nicht
  mehr gewollt“.
- **„Im Buch öffnen“:** schließt den Assistenten und zeigt die erste Stelle in der oberen, die zweite in der unteren
  Hälfte der geteilten Ansicht, jeweils markiert.
- Bei offenem Konflikt oder gesperrtem Editor sind die schreibenden Aktionen (Verweise, „Gewollt“) ausgegraut.
- Das Fenster bleibt nach einer Aktion offen und zeigt das nächste Paar der Liste.

### 5.4 Speicher für „Gewollt“

- Datei `.noteevolution/repetitions.json` im Vault: je Buch (Seitenname) die Liste der Paare als zwei Block-IDs.
- Ein Block ohne `id::` bekommt beim Abhaken eine (ein Schritt für „Rückgängig“; `updated` wird nicht gesetzt). Das
  ist die einzige Stelle, an der diese Funktion die Buchdatei ohne Verweis ändert.
- Ein Paar bleibt gewollt, solange beide Blöcke existieren, auch wenn ihr Text sich ändert. Einträge, deren Block es
  nicht mehr gibt, werden beim nächsten Speichern der Datei entfernt.
- Fehlende oder beschädigte Datei: gilt als leer.

## 6. Anzeige im Buch

- **Leiste unter der Markierung:** neben den Quellen „Wiederholungen: n“ für den markierten Bereich (Block,
  Abschnitt, Kapitel, ganzes Buch); ohne Wiederholung steht nichts. Ein Klick klappt die Liste der Gegenstellen auf:
  Art (wörtlich / Prozent), „Abschnitt › Textanfang“ beider Stellen. Ein Klick auf einen Eintrag öffnet den
  Assistenten bei diesem Paar.
- **Randmarkierung:** Ein Block mit mindestens einer Wiederholung trägt am rechten Rand das Zeichen „≈“ mit der
  Zahl; Tooltip „n Wiederholungen“. Ein Klick markiert den Block und klappt die Liste der Leiste auf.
- **Gliederung:** je Überschrift die Zahl für den Abschnitt samt Unterabschnitten („≈ n“), ohne Wiederholung nichts.
- **Schalter „Wiederholungen anzeigen“** im Kopf der Gliederung, neben „Abdeckung anzeigen“: steuert Randmarkierung
  und Gliederungszahlen gemeinsam; die Einstellung wird gemerkt, Standard an. Der Zähler in der Leiste ist immer da.
- **Zählweise:** Paare mit mindestens einem Block im Bereich; liegen beide im Bereich, zählt das Paar einmal.
  Gewollte Paare zählen nicht. Wörtliche und inhaltliche zählen zusammen.

## 7. Fehler

- Schlägt das Schreiben eines Verweises oder von `repetitions.json` fehl, bleibt alles unverändert und der Assistent
  zeigt die Meldung.
- Gibt es einen Block eines gezeigten Paares nicht mehr (äußere Änderung), verschwindet das Paar mit dem nächsten
  Lauf; eine Aktion darauf zeigt „Diese Stelle gibt es nicht mehr.“ und ändert nichts.

## 8. Nicht enthalten

- Vergleich zwischen Büchern oder mit Notizen (dafür gibt es den Dubletten-Hinweis an der Notiz).
- Bearbeiten, Löschen oder Zusammenführen im Assistenten.
- Vergleich einzelner Sätze oder Unterpunkte nach Inhalt.
- Einstellbarer Schwellenwert in der Oberfläche.
- Wiederholungen im PDF.

## 9. Tests

- **Kern, wörtlich:** Grenze bei 8 Wörtern (7 kein Fund, 8 Fund); Normalisierung (Groß/Klein, Satzzeichen,
  Formatierung, Verweise); mehrere Folgen in einem Paar mit ihren Stellen; Unterpunkte zählen zum Block; Ausschlüsse
  (Status, Anhang, kurze Blöcke, Bilder); ein Block gegen sich selbst kein Fund.
- **Kern, inhaltlich:** Schwelle 0,90; höchstens drei je Block und die Regel „unter den dreien eines der beiden“;
  wörtliche Paare nicht doppelt; fehlende Vektoren lassen den Block aus.
- **Kern, Zählung:** je Block, Abschnitt samt Unterabschnitten, Buch; Paar innerhalb des Bereichs einmal; gewollte
  nicht.
- **Kern, Speicher:** Schreiben und Lesen von `repetitions.json`; beschädigte Datei; Aufräumen verschwundener Blöcke.
- **Hintergrund:** nur geänderte Blöcke werden eingebettet; Stand und „…“ während des Laufs; KI aus.
- **Oberfläche:** Assistent mit Gruppen, Filter, Schalter und Hinweis; Side-by-Side mit Hervorhebung; jede Aktion
  (Verweis je Seite, beide Richtungen, „Verweis besteht“, Gewollt und zurück, Im Buch öffnen); Rückgängig-Texte;
  Leiste, Randmarkierung, Gliederung, Schalter.
- **Im Programmfenster:** ein Buch mit einer wörtlichen und einer umformulierten Wiederholung: Anzeige nach dem
  Öffnen, Assistent, Verweis setzen, Gewollt, Neustart.
