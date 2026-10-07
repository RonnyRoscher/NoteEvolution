namespace NoteEvolution.Pdf.Tests;

public class ExportTextTests
{
    [Theory]
    [InlineData("Siehe [[Seite]] hier", "Siehe Seite hier")]
    [InlineData("Ein #tag und #[[langer tag]] enden", "Ein und enden")]
    [InlineData("Quelle ((6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70)) weg", "Quelle weg")]
    [InlineData("zu   viele    Leerzeichen", "zu viele Leerzeichen")]
    [InlineData("Satz [[a]]. Und C# bleibt, a#b auch", "Satz a. Und C# bleibt, a#b auch")]
    [InlineData("# Keine Marke", "# Keine Marke")]
    [InlineData("**fett [[x]]** und *[[y]]*", "**fett x** und *y*")]
    [InlineData("  Rand  ", "Rand")]
    public void Clean_RemovesLinksTagsRefs(string input, string expected) =>
        Assert.Equal(expected, ExportText.Clean(input));

    [Fact]
    public void Clean_KeepsLineBreaksAndTrimsEachLine()
    {
        Assert.Equal("eins\nzwei drei\nvier", ExportText.Clean("eins  \n zwei   drei\n#tag vier"));
    }
}
