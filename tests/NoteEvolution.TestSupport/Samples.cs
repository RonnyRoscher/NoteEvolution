using System.Text;

namespace NoteEvolution.TestSupport;

/// <summary>Artificial Logseq sample texts shared by the test projects.</summary>
public static class Samples
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>The book example from spec section 3.2, indented with tabs.</summary>
    public const string SpecBook =
        "title:: Buch: LoveMagic\n" +
        "type:: book\n" +
        "dedication:: Für alle, die weiterfragen.\n" +
        "\n" +
        "- # Liebe und Wahrheit\n" +
        "  collapsed:: true\n" +
        "\t- ## Vertrauen\n" +
        "\t\t- Angst baut Widerstand auf, Vertrauen baut Schwung auf.\n" +
        "\t\t  id:: 6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70\n" +
        "\t\t  source:: ((7f3a91c2-4b1d-4e8a-9c3f-1a2b3c4d5e6f)), ((81bb02aa-5c2e-4f9b-8d4a-2b3c4d5e6f71))\n" +
        "\t\t\t- Gute Interpretationsvarianten zu sehen ist trainierbar.\n" +
        "\t\t\t- noch ein Beispiel ergänzen #notiz\n";

    /// <summary>Files that must survive parse + serialize byte for byte: <c>(string name, byte[] bytes)</c>.</summary>
    public static IEnumerable<object[]> RoundTrip
    {
        get
        {
            yield return Text("leer.md", "");
            yield return Bytes("nur-bom.md", [0xEF, 0xBB, 0xBF]);
            yield return Text("nur-seiten-eigenschaften.md", "title:: Nur Eigenschaften\ntype:: page\n");
            yield return Text("seiten-eigenschaften-ohne-umbruch.md", "title:: Ohne Umbruch\ntags:: a, b");
            yield return Text("vorspann-text.md", "\nEinfacher Text ohne Anstrich\n\nalias:: vorne\n\n- erster Block\n");
            yield return Text("leerzeilen-zwischen-bloecken.md", "- eins\n\n- zwei\n\n\n- drei\n\t- vier\n\n");
            yield return Text("leerzeichen-am-zeilenende.md", "- Zeile mit Leerzeichen   \n  zweite Zeile\t\n  collapsed:: true \n- ende \n  \n");
            yield return Text("crlf.md", "title:: CRLF\r\n\r\n- a\r\n\t- b\r\n  fortsetzung\r\n- c\r\n");
            yield return Text("gemischte-zeilenenden.md", "- a\r\n- b\n\t- c\r\n- d");
            yield return Bytes("bom.md", [0xEF, 0xBB, 0xBF, .. Utf8NoBom.GetBytes("title:: Mit BOM\n\n- Größe\r\n")]);
            yield return Text("anstriche-gemischt.md", "* a\n- b\n\t* c\n\t- d\n* e");
            yield return Text("tabs-und-leerzeichen.md", "- a\n\t- b\n    - c\n  - d\n\t  - e\n        - f\n- g\n");
            yield return Text("collapsed.md", "- eltern\n  collapsed:: true\n\t- kind\n\t  collapsed:: false\n\t\t- enkel\n");
            yield return Text("tiefe-6.md", "- 1\n\t- 2\n\t\t- 3\n\t\t\t- 4\n\t\t\t\t- 5\n\t\t\t\t\t- 6\n\t\t\t\t\t  id:: 6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70\n- wieder oben\n");
            yield return Text("handled.md", "- [handled] erledigt\n- [handled]\n\t- darunter [handled] mitten im Satz\n");
            yield return Text("fett.md", "- **fett** und *kursiv*\n  **fett** am Zeilenanfang\n\t* Sternchen-Unteranstrich mit **fett**\n- ***\n");
            yield return Text("deutsch-englisch.md", "- Über die Größe der Bäume\n- The quick brown fox jumps over the lazy dog\n\t- Grüße aus Köln – „Zitat“ ß ÄÖÜ\n\t- Mixed: Vertrauen means trust\n");
            yield return Text("codezaun.md", "- Code:\n  ```csharp\n  - kein Block\n  key:: kein Wert\n  ```\n- danach\n- ```js\n  - auch kein Block\n  ```\n");
            yield return Text("ohne-umbruch-am-ende.md", "- a\n- b");
            yield return Text("leere-anstriche.md", "-\n- \n*\n\t-\n");
            yield return Text("blockverweise.md", "- Siehe ((6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70)) und [[Buch - LoveMagic]] #tag\n  used-in:: [[Buch - LoveMagic]] ((6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70))\n  ![bild](../assets/bild.png)\n");
            yield return Text("spec-buch.md", SpecBook);
            yield return Text("spec-buch-crlf.md", SpecBook.Replace("\n", "\r\n"));
        }
    }

    private static object[] Text(string name, string text) => [name, Utf8NoBom.GetBytes(text)];

    private static object[] Bytes(string name, byte[] bytes) => [name, bytes];
}
