using NoteEvolution.Pdf.Hyphenation;

namespace NoteEvolution.Pdf.Tests;

public class HyphenatorTests
{
    private static string Show(string s) => s.Replace('­', '|');

    private static Hyphenator German => Hyphenator.German!;

    [Theory]
    [InlineData("Silbentrennung", "Sil|ben|tren|nung")]
    [InlineData("Gedächtnis", "Ge|dächt|nis")]
    [InlineData("Wiederholung", "Wie|der|ho|lung")]
    public void Hyphenate_KnownWords(string word, string expected)
    {
        Assert.Equal(expected, Show(German.Hyphenate(word)));
    }

    [Theory]
    [InlineData("Haus")]
    [InlineData("immer")]
    public void Hyphenate_ShortWordsStay(string word)
    {
        Assert.Equal(word, German.Hyphenate(word));
    }

    [Fact]
    public void Hyphenate_MinimumLettersAroundABreak()
    {
        string[] words =
        [
            "Silbentrennung", "Gedächtnis", "Wiederholung", "Donaudampfschifffahrt", "Abschnittsüberschrift",
            "Bundesverfassungsgericht", "Wahrscheinlichkeitsrechnung", "Überraschungsmoment", "Entwicklungsumgebung",
            "Aufmerksamkeitsspanne"
        ];
        foreach (var word in words)
        {
            var parts = German.Hyphenate(word).Split('­');
            Assert.True(parts.Length > 1, word);
            Assert.True(parts[0].Length >= 2, word);
            Assert.True(parts[^1].Length >= 3, word);
        }
    }

    [Theory]
    [InlineData("Abschnitt2026")]
    [InlineData("3Dimensional")]
    public void Hyphenate_WordsWithDigitsStay(string word)
    {
        Assert.Equal(word, German.Hyphenate(word));
    }

    [Fact]
    public void Hyphenate_AlreadyHyphenatedWordStays()
    {
        Assert.Equal("Sil­bentrennung", German.Hyphenate("Sil­bentrennung"));
    }

    [Fact]
    public void Hyphenate_KeepsEverythingElse()
    {
        const string input = "„Silbentrennung“, sagte er – siehe https://example.org/Silbentrennung?x=1 –\r\n"
            + "die E-Mail-Adresse  lautet (Wiederholung!)  \n\nDonaudampfschifffahrt.";
        var result = German.Hyphenate(input);
        Assert.Contains('­', result);
        Assert.Equal(input, result.Replace("­", ""));
    }

    [Theory]
    [InlineData("Überraschung")]
    [InlineData("STRASSENBAHN")]
    [InlineData("Maßnahme")]
    public void Hyphenate_UppercaseAndUmlauts(string word)
    {
        var result = German.Hyphenate(word);
        Assert.Contains('­', result);
        Assert.Equal(word, result.Replace("­", ""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("… – !")]
    public void Hyphenate_EmptyAndPunctuationOnly(string text)
    {
        Assert.Equal(text, German.Hyphenate(text));
    }

    [Fact]
    public void FromPatterns_UsesTheGivenPatterns()
    {
        var hyphenator = Hyphenator.FromPatterns(["a1b"]);
        Assert.Equal("aaa|bbb", Show(hyphenator.Hyphenate("aaabbb")));
    }
}
