using NoteEvolution.Core.Text;

namespace NoteEvolution.Core.Tests.Text;

public class NoteTagTests
{
    [Theory]
    [InlineData("x #notiz", true)]
    [InlineData("#notiz", true)]
    [InlineData("#notiz x", true)]
    [InlineData("x #notiz\ty", true)]
    [InlineData("#notizen", false)]
    [InlineData("x#notiz", false)]
    [InlineData("kein tag", false)]
    public void Has_MatchesWholeTokenOnly(string content, bool expected)
    {
        Assert.Equal(expected, NoteTag.Has(content));
    }

    [Theory]
    [InlineData("x #notiz", "x")]
    [InlineData("#notiz x", "x")]
    [InlineData("a #notiz b", "a b")]
    [InlineData("a  #notiz   b ", "a b")]
    [InlineData("#notiz", "")]
    [InlineData("#notizen", "#notizen")]
    [InlineData("a #notiz b #notiz c", "a b c")]
    [InlineData("a  b #notiz", "a  b")]
    [InlineData("a #notiz\nb", "a\nb")]
    public void Strip_RemovesTagAndCollapsesWhitespace(string content, string expected)
    {
        Assert.Equal(expected, NoteTag.Strip(content));
    }

    [Fact]
    public void Add_AppendsTag()
    {
        Assert.Equal("x #notiz", NoteTag.Add("x"));
    }
}
