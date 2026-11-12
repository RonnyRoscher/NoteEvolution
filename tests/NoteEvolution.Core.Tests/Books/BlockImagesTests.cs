using NoteEvolution.Core.Books;

namespace NoteEvolution.Core.Tests.Books;

public class BlockImagesTests
{
    [Fact]
    public void Find_ImageWithLogseqSuffix_ReturnsPartsAndSpan()
    {
        var text = "Text ![a_b](../assets/x_y z.png){:height 1, :width 2} mehr";

        var found = BlockImages.Find(text);

        var image = Assert.Single(found);
        Assert.Equal("a_b", image.Alt);
        Assert.Equal("../assets/x_y z.png", image.Path);
        Assert.Equal("{:height 1, :width 2}", image.Suffix);
        Assert.Equal(5, image.Start);
        Assert.Equal("![a_b](../assets/x_y z.png){:height 1, :width 2}".Length, image.Length);
    }

    [Fact]
    public void Find_TwoImages_InTextOrder()
    {
        var found = BlockImages.Find("![a](p.png) und ![b](q.png)");

        Assert.Equal(2, found.Count);
        Assert.Equal("p.png", found[0].Path);
        Assert.Equal("q.png", found[1].Path);
        Assert.Equal("", found[0].Suffix);
    }

    [Fact]
    public void Find_SecondImage_HasItsStart() => Assert.Equal(16, BlockImages.Find("![a](p.png) und ![b](q.png)")[1].Start);

    [Fact]
    public void Find_Unclosed_ReturnsNothing() => Assert.Empty(BlockImages.Find("![a](b"));

    [Fact]
    public void Find_TrimsPath() => Assert.Equal("p.png", BlockImages.Find("![a]( p.png )")[0].Path);

    [Fact]
    public void LengthAt_OnlyAtExactStart()
    {
        var text = "x ![a](p.png){:w 1} y";

        Assert.Equal("![a](p.png){:w 1}".Length, BlockImages.LengthAt(text, 2));
        Assert.Equal(0, BlockImages.LengthAt(text, 1));
        Assert.Equal(0, BlockImages.LengthAt(text, 3));
        Assert.Equal(0, BlockImages.LengthAt(text, text.Length));
    }

    [Fact]
    public void Format_WithAndWithoutSuffix()
    {
        Assert.Equal("![a](p.png)", BlockImages.Format("a", "p.png"));
        Assert.Equal("![a](p.png){:w 1}", BlockImages.Format("a", "p.png", "{:w 1}"));
    }

    [Fact]
    public void CleanCaption_RemovesBracketAndLineBreaks() => Assert.Equal("abc", BlockImages.CleanCaption(" a]b\nc "));

    [Theory]
    [InlineData("Quelle:: Wikipedia", "Quelle: Wikipedia")]
    [InlineData("a::::b", "a:b")]
    [InlineData("a:::b", "a:b")]
    public void CleanCaption_ReplacesDoubleColons(string caption, string expected) =>
        Assert.Equal(expected, BlockImages.CleanCaption(caption));

    [Theory]
    [InlineData("Mein Bild", "Mein Bild")]
    [InlineData(" Mein Bild ", "Mein Bild")]
    [InlineData("image.png", "")]
    [InlineData(" Foto.JPG ", "")]
    [InlineData("", "")]
    public void ShownCaption_FileNameIsNoCaption(string alt, string expected) =>
        Assert.Equal(expected, BlockImages.ShownCaption(alt));

    [Fact]
    public void WithCaption_ChangesOnlyTheNthImage()
    {
        var result = BlockImages.WithCaption("x ![a](p.png){:w 1} ![b](q.png)", 1, "neu]");

        Assert.Equal("x ![a](p.png){:w 1} ![neu](q.png)", result);
    }

    [Fact]
    public void WithCaption_KeepsSuffix() =>
        Assert.Equal("![neu](p.png){:w 1}", BlockImages.WithCaption("![a](p.png){:w 1}", 0, "neu"));

    [Fact]
    public void WithCaption_KeepsRawPathAndSuffix() =>
        Assert.Equal("![neu]( p.png ){:w 1}", BlockImages.WithCaption("![a]( p.png ){:w 1}", 0, "neu"));

    [Fact]
    public void WithCaption_NoSuchImage_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BlockImages.WithCaption("![a](p.png)", 1, "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => BlockImages.WithCaption("![a](p.png)", -1, "x"));
    }

    [Fact]
    public void IsRemote_DetectsSchemes()
    {
        Assert.True(BlockImages.IsRemote("https://x/y.png"));
        Assert.False(BlockImages.IsRemote("../assets/y.png"));
        Assert.False(BlockImages.IsRemote("C:\\x\\y.png"));
    }
}
