using System.Text.RegularExpressions;
using NoteEvolution.Core.Repetitions;
using NoteEvolution.Core.Text;

namespace NoteEvolution.Core.Tests.Repetitions;

public class SentencesTests
{
    /// <summary>A block of plain paragraphs (depth 0 for the first, 1 for the others) with its words.</summary>
    private static ComparedBlock Block(params string[] paragraphs)
    {
        var compared = paragraphs
            .Select((text, index) => new ComparedParagraph(
                Guid.NewGuid(), index == 0 ? 0 : 1, [new InlineRun(text, false, false)], text, [.. Enumerable.Range(0, text.Length + 1)]))
            .ToList();
        var words = new List<ComparedWord>();
        for (var index = 0; index < paragraphs.Length; index++)
        {
            foreach (Match match in Regex.Matches(paragraphs[index], @"[\p{L}\p{N}]+"))
            {
                words.Add(new ComparedWord(index, match.Index, match.Length, match.Value.ToLowerInvariant()));
            }
        }

        return new ComparedBlock(Guid.NewGuid(), compared, words);
    }

    private static string[] Texts(ComparedBlock block) => [.. Sentences.Of(block).Select(s => Sentences.Text(block, s))];

    [Fact]
    public void Of_SplitsAtSentenceEnds_WithinEachParagraph()
    {
        var block = Block(
            "Schlaf festigt das Gedächtnis sehr. Das Gehirn sortiert nachts Erlebtes neu!",
            "Am Morgen ist vieles klarer als am Abend");

        var sentences = Sentences.Of(block);

        Assert.Equal(
            ["Schlaf festigt das Gedächtnis sehr.", "Das Gehirn sortiert nachts Erlebtes neu!", "Am Morgen ist vieles klarer als am Abend"],
            Texts(block));
        Assert.Equal([0, 0, 1], sentences.Select(s => s.Paragraph));
        Assert.Equal([5, 6, 8], sentences.Select(s => s.Words));
    }

    [Fact]
    public void Of_NoSplit_WhenNoCapitalFollows()
    {
        var block = Block("z. B. ein Beispiel mit vielen Worten hier");

        Assert.Equal(["z. B. ein Beispiel mit vielen Worten hier"], Texts(block));
    }

    [Theory]
    [InlineData("Das ist ein langer Satz zum Ende. 2024 war ein gutes Jahr für uns")]
    [InlineData("Das ist ein langer Satz zum Ende. „Zitat folgt hier gleich und dann mehr")]
    [InlineData("Das ist ein langer Satz zum Ende. \"Zitat folgt hier gleich und dann mehr")]
    [InlineData("Das ist ein langer Satz zum Ende… »Zitat folgt hier gleich und dann mehr")]
    [InlineData("Ist das ein langer Satz zum Ende? “Zitat folgt hier gleich und dann mehr")]
    public void Of_SplitsBeforeDigitAndQuote(string text)
    {
        var block = Block(text);

        var texts = Texts(block);

        Assert.Equal(2, texts.Length);
        Assert.Contains("Satz zum Ende", texts[0]);
        Assert.DoesNotContain("2024", texts[0]);
        Assert.DoesNotContain("Zitat", texts[0]);
    }

    [Fact]
    public void Of_ShortSentences_AreLeftOut()
    {
        var block = Block("Ja. Das ist ein Satz mit sechs Worten.");

        Assert.Equal(["Das ist ein Satz mit sechs Worten."], Texts(block));
    }

    [Fact]
    public void Of_SpanExcludesTrailingWhitespace()
    {
        var block = Block("Schlaf festigt das Gedächtnis sehr.   \tDas Gehirn sortiert nachts Erlebtes neu!  ");

        var sentences = Sentences.Of(block);

        Assert.Equal(2, sentences.Count);
        Assert.Equal(0, sentences[0].Start);
        Assert.Equal("Schlaf festigt das Gedächtnis sehr.".Length, sentences[0].Length);
        Assert.Equal("Das Gehirn sortiert nachts Erlebtes neu!", Sentences.Text(block, sentences[1]));
    }
}
