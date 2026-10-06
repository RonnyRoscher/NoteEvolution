using NoteEvolution.AI.Search;

namespace NoteEvolution.AI.Tests.Search;

public class FtsQueryTests
{
    [Theory]
    [InlineData("foo \"bar", "\"foo\"* \"bar\"*")]
    [InlineData("AND OR NEAR", "\"AND\"* \"OR\"* \"NEAR\"*")]
    [InlineData("-x (y) *", "\"-x\"* \"(y)\"*")]
    [InlineData("   ", null)]
    [InlineData("", null)]
    [InlineData("\" \"\"", null)]
    [InlineData("  a \t b\nc ", "\"a\"* \"b\"* \"c\"*")]
    [InlineData("a\0b", "\"a\"* \"b\"*")]
    [InlineData("a\u0007b\u007fc", "\"a\"* \"b\"* \"c\"*")]
    [InlineData("\0", null)]
    public void Build_EscapesUserInput(string input, string? expected) => Assert.Equal(expected, FtsQuery.Build(input));
}
