using NoteEvolution.AI.Understanding;

namespace NoteEvolution.AI.Tests.Understanding;

public class UnderstandingPromptsTests
{
    private static UnderstandingItem Heading(string title, string ownText) =>
        new(UnderstandingKind.Heading, "fp", "id", title, ownText, [], Guid.NewGuid());

    [Fact]
    public void Summary_ContainsTitleChildSummariesAndText_OmitsEmptyParts()
    {
        var (system, user) = UnderstandingPrompts.Summary(Heading("Kapitel A", "Eigener Text."),
            [("Unter A", "Zusammenfassung A."), ("Unter B", "Zusammenfassung B.")]);

        Assert.StartsWith("Du fasst Abschnitte eines deutschsprachigen Buchs zusammen.", system);
        Assert.EndsWith("Keine Einleitung, keine Aufzählung, kein Markdown.", system);
        Assert.Equal("Überschrift: Kapitel A\n\nUnterabschnitte:\n- Unter A: Zusammenfassung A.\n- Unter B: Zusammenfassung B.\n\n" +
                     "Text des Abschnitts:\nEigener Text.", user);

        var (_, noChildren) = UnderstandingPrompts.Summary(Heading("Kapitel A", "Eigener Text."), []);
        Assert.Equal("Überschrift: Kapitel A\n\nText des Abschnitts:\nEigener Text.", noChildren);

        var (_, noText) = UnderstandingPrompts.Summary(Heading("Kapitel A", "  "), [("Unter A", "Zusammenfassung A.")]);
        Assert.Equal("Überschrift: Kapitel A\n\nUnterabschnitte:\n- Unter A: Zusammenfassung A.", noText);
    }

    [Fact]
    public void Core_ContainsNoteText()
    {
        var note = new UnderstandingItem(UnderstandingKind.Note, "fp", "id", "Titel", "Der Notiztext.", [], Guid.NewGuid());

        var (system, user) = UnderstandingPrompts.Core(note);

        Assert.StartsWith("Du bestimmst die Kernaussage einer persönlichen Notiz.", system);
        Assert.Equal("Notiz:\nDer Notiztext.", user);
    }

    [Fact]
    public void Clean_StripsPreambleAndMarkdown_AndTrims()
    {
        Assert.Equal("Das Kapitel behandelt X. Es zeigt Y.",
            UnderstandingPrompts.Clean("Hier ist die Zusammenfassung:\n\n**Das Kapitel** behandelt X.\n  Es zeigt Y.  ", 600));
        Assert.Equal("Erster Punkt. Zweiter Punkt.",
            UnderstandingPrompts.Clean("## Zusammenfassung:\n- Erster Punkt.\n* Zweiter Punkt.", 600));
        Assert.Equal("Ein Satz.", UnderstandingPrompts.Clean("\"Ein Satz.\"", 600));
        Assert.Equal("Ein Satz.", UnderstandingPrompts.Clean("„Ein Satz.“", 600));
    }

    [Fact]
    public void Clean_CutsAtSentenceEndWithinLimit()
    {
        var answer = "Erster Satz ist hier. Zweiter Satz ist auch hier. Dritter Satz wird abgeschnitten werden.";

        Assert.Equal("Erster Satz ist hier. Zweiter Satz ist auch hier.", UnderstandingPrompts.Clean(answer, 60));
        Assert.Equal("Ohne Satzende wird hart", UnderstandingPrompts.Clean("Ohne Satzende wird hart abgeschnitten", 23));
        Assert.Equal(answer, UnderstandingPrompts.Clean(answer, 600));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    [InlineData("Zusammenfassung:")]
    public void Clean_EmptyOrWhitespace_ReturnsEmpty(string answer)
    {
        Assert.Equal("", UnderstandingPrompts.Clean(answer, 600));
    }
}
