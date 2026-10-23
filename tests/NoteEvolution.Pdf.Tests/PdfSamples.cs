using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;

namespace NoteEvolution.Pdf.Tests;

/// <summary>Artificial book pages for the PDF tests.</summary>
internal static class PdfSamples
{
    public const string ChapterTwoTitle = "Zweites Kapitel";

    /// <summary>Two chapters; the first has a long body, so the second one starts on a later page.</summary>
    public static string SpecBookWithTwoChapters
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append("title:: Buch: LoveMagic\n");
            sb.Append("type:: book\n");
            sb.Append("dedication:: Für alle, die weiterfragen.\n");
            sb.Append('\n');
            sb.Append("- # Liebe und Wahrheit\n");
            sb.Append("  collapsed:: true\n");
            sb.Append("\t- ## Vertrauen\n");
            sb.Append("\t\t- Angst baut Widerstand auf, Vertrauen baut Schwung auf. **Fett** und *kursiv*.\n");
            sb.Append("\t\t  id:: 6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70\n");
            sb.Append("\t\t  source:: ((7f3a91c2-4b1d-4e8a-9c3f-1a2b3c4d5e6f)), ((81bb02aa-5c2e-4f9b-8d4a-2b3c4d5e6f71))\n");
            sb.Append("\t\t\t- Gute Interpretationsvarianten zu sehen ist trainierbar.\n");
            sb.Append("\t\t\t- noch ein Beispiel ergänzen #notiz\n");
            for (var i = 1; i <= 60; i++)
            {
                sb.Append($"\t\t- Füllabsatz {i}: Wer fragt, der lernt, und wer lernt, der fragt weiter, bis aus Neugier Gewissheit wird.\n");
            }

            sb.Append($"- # {ChapterTwoTitle}\n");
            sb.Append("\t- Ein Satz mit [[Seitenlink]] und #tag sowie ((6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70)).\n");
            sb.Append("\t- ### Unterabschnitt\n");
            sb.Append("\t\t- Text im Unterabschnitt.\n");
            return sb.ToString();
        }
    }

    /// <summary>A book whose single text block is <paramref name="blockContent"/> (continuation lines indented by two spaces).</summary>
    public static string BookWithBlock(string blockContent) =>
        "title:: Bildbuch\ntype:: book\n\n- # Kapitel\n\t- " + blockContent + "\n";

    /// <summary>A text block with a note bullet that has a child and a grandchild, followed by a normal sibling.</summary>
    public const string BookWithNoteSubtree =
        "title:: Notizbuch\ntype:: book\n\n" +
        "- # Kapitel\n" +
        "\t- Hauptabsatz\n" +
        "\t\t- Vorheriger Absatz\n" +
        "\t\t- Arbeitsnotiz #notiz\n" +
        "\t\t\t- Kind der Notiz\n" +
        "\t\t\t\t- Enkel der Notiz\n" +
        "\t\t- Folgender Absatz\n";

    public const string HeadingTargetId = "11111111-1111-4111-8111-111111111111";
    public const string TextTargetId = "22222222-2222-4222-8222-222222222222";
    public const string ExcludedTargetId = "33333333-3333-4333-8333-333333333333";
    public const string MissingTargetId = "44444444-4444-4444-8444-444444444444";

    /// <summary>
    /// Three chapters, each on its own page (3, 4, 5): a referenced heading, a referenced text block, and a chapter
    /// that refers to both, to a relevant (excluded) block and to an id without a target.
    /// </summary>
    public static string BookWithRefs =>
        "title:: Verweisbuch\ntype:: book\n\n" +
        "- # Abendroutine\n" +
        $"  id:: {HeadingTargetId}\n" +
        "\t- Am Abend zur Ruhe kommen.\n" +
        "- # Morgenroutine\n" +
        "\t- Am Morgen langsam beginnen.\n" +
        $"\t  id:: {TextTargetId}\n" +
        "- # Verweise\n" +
        $"\t- Siehe (({HeadingTargetId})) und (({TextTargetId})).\n" +
        $"\t- Auch (({ExcludedTargetId})) und (({MissingTargetId})) zählen.\n" +
        "\t- Ausgelassener Block\n" +
        "\t  status:: relevant\n" +
        $"\t  id:: {ExcludedTargetId}\n";

    public static Book Load(string text, string path = "Buch - Test.md") =>
        Book.Load(LogseqParser.Parse(path, new UTF8Encoding(false).GetBytes(text)));

    /// <summary>A valid 1x1 PNG.</summary>
    public static byte[] Png { get; } = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==");
}
