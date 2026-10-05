using NoteEvolution.Core.Text;

namespace NoteEvolution.Core.Tests.Text;

public class BlockTextEscapeTests
{
    [Theory]
    [InlineData("- Liste", "\\- Liste")]
    [InlineData("* Stern", "\\* Stern")]
    [InlineData("-", "\\-")]
    [InlineData("*", "\\*")]
    [InlineData("  - eingerückt", "\\  - eingerückt")]
    [InlineData("\t* Tab", "\\\t* Tab")]
    [InlineData("```", "\\```")]
    [InlineData("```js", "\\```js")]
    [InlineData("  ```", "\\  ```")]
    [InlineData("# Titel", "\\# Titel")]
    [InlineData("###### Titel", "\\###### Titel")]
    [InlineData("\\- schon maskiert", "\\\\- schon maskiert")]
    [InlineData("\\\\# zweifach", "\\\\\\# zweifach")]
    [InlineData("a\n- b\n# c", "a\n\\- b\n\\# c")]
    public void Escape_LinesThatWouldChangeTheTree_GetBackslash_UnescapeReverts(string editor, string file)
    {
        Assert.Equal(file, BlockTextEscape.Escape(editor));
        Assert.Equal(editor, BlockTextEscape.Unescape(file));
    }

    [Theory]
    [InlineData("Text")]
    [InlineData("-x")]
    [InlineData("*fett*")]
    [InlineData("**fett**")]
    [InlineData("a - b")]
    [InlineData("#tag")]
    [InlineData("####### sieben")]
    [InlineData("``inline``")]
    [InlineData("\\x")]
    [InlineData("\\")]
    [InlineData("")]
    [InlineData("  # eingerückt")]
    public void Escape_OrdinaryLines_Unchanged(string text)
    {
        Assert.Equal(text, BlockTextEscape.Escape(text));
        Assert.Equal(text, BlockTextEscape.Unescape(text));
    }

    [Theory]
    [InlineData("- x")]
    [InlineData("# x")]
    [InlineData("```")]
    public void Unescape_UnescapedSyntaxInFile_KeptAsIs(string file)
    {
        Assert.Equal(file, BlockTextEscape.Unescape(file));
    }
}
