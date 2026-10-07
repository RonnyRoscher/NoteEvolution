using NoteEvolution.Core.Vaults;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>Artificial vault contents and helpers shared by the notes pane tests.</summary>
internal static class NotesTestData
{
    public const string AlphaPath = "pages/Buch - Alpha.md";

    public const string NotesPath = "pages/Ideen.md";

    public const string AlphaFirstId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

    public const string BetaBlockId = "cccccccc-cccc-cccc-cccc-cccccccccccc";

    public const string UsedNoteId = "11111111-1111-4111-8111-111111111111";

    public const string BetaBook = "Buch - Beta";

    /// <summary>
    /// Two books, a page with a used note (used in Beta) and an unused note with a sub-bullet, and two journal days
    /// with one used and one unused entry each.
    /// </summary>
    public static TestVault Create() => TestVault.Create(
        (AlphaPath,
            "title:: Alpha\ntype:: book\n\n" +
            "- # Eins\n" +
            "\t- Erster Text\n" +
            "\t  id:: " + AlphaFirstId + "\n" +
            "\t- Zweiter Text\n" +
            "\t- ## Eins-A\n" +
            "\t\t- Dritter Text\n" +
            "- # Zwei\n"),
        ("pages/Buch - Beta.md",
            "title:: Beta\ntype:: book\n\n" +
            "- # Beta Eins\n" +
            "\t- ## Beta Unter\n" +
            "\t\t- Beta Text\n" +
            "\t\t  id:: " + BetaBlockId + "\n"),
        (NotesPath,
            "- Gedächtnis wird durch Wiederholung stabil\n" +
            "  id:: " + UsedNoteId + "\n" +
            "  used-in:: [[" + BetaBook + "]] ((" + BetaBlockId + "))\n" +
            "- Gedächtnis braucht Schlaf\n" +
            "\t- Unterpunkt Träume\n"),
        ("journals/2026_10_01.md",
            "- Gedächtnis im Alltag beobachten\n" +
            "- Eine ganz andere Idee\n"),
        ("journals/2026_09_15.md",
            "- Gedächtnis und Musik\n" +
            "  used-in:: [[" + BetaBook + "]]\n" +
            "- Septemberidee\n"));

    /// <summary>The note block (any depth) whose content starts with <paramref name="startsWith"/>.</summary>
    public static NoteBlock Note(VaultSession session, string startsWith) =>
        session.Notes.All().First(n => n.Block.Content.StartsWith(startsWith, StringComparison.Ordinal));
}
