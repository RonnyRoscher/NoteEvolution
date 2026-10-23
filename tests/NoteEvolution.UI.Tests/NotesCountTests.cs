using Bunit;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;

namespace NoteEvolution.UI.Tests;

/// <summary>The number of notes at the start of the notes pane's filter row: all of them, or how many the filter lets through.</summary>
public class NotesCountTests : UiTestContext
{
    private static TestVault Create() => TestVault.Create(
        ("pages/Buch - Alpha.md", "title:: Alpha\ntype:: book\n\n- # Eins\n"),
        ("journals/2026_10_01.md", "- Erste Idee\n\t- Ein Unterpunkt\n- Zweite Idee\n  used-in:: [[Buch - Alpha]]\n"),
        ("journals/2026_10_02.md", "- Dritte Idee\n"));

    [Fact]
    public async Task Count_MainPoints_AllOrThoseTheFilterLetsThrough()
    {
        using var tv = Create();
        await OpenSessionAsync(tv);
        var cut = Render<NotesPane>();

        // Sub-points are part of their note: three notes.
        Assert.Equal("3 Notizen", cut.Find(".ne-notes-count").TextContent);

        cut.Find(".ne-hide-used").Change(true);

        Assert.Equal("2 von 3 Notizen", cut.Find(".ne-notes-count").TextContent);
    }
}
