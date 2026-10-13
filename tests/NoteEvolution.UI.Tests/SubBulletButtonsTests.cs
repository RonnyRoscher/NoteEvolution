using System.Text;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>Which sub-bullets of a note get their own buttons: more than one line, and not part of an enumeration.</summary>
public class SubBulletButtonsTests
{
    private static readonly string Long = "Wer zu wenig schläft, vergisst mehr, weil das Gehirn nachts das Gelernte ordnet und festigt.";

    /// <summary>The sub-bullets of the single note in <paramref name="text"/>, depth first.</summary>
    private static List<Block> SubBullets(string text)
    {
        var note = LogseqParser.Parse("journals/2026_10_01.md", Encoding.UTF8.GetBytes(text)).Roots.Single();
        return [.. Descendants(note)];

        static IEnumerable<Block> Descendants(Block block) => block.Children.SelectMany(c => Descendants(c).Prepend(c));
    }

    [Fact]
    public void ShortSingleLine_NoButtons()
    {
        var subBullets = SubBullets("- Einkauf\n\t- Milch\n\t- Brot\n");

        Assert.All(subBullets, b => Assert.False(SubBulletButtons.Offer(b)));
    }

    [Fact]
    public void LongerThanALine_GetsButtons()
    {
        Assert.True(Long.Length > SubBulletButtons.LineLength);
        var subBullets = SubBullets($"- Schlaf\n\t- {Long}\n\t- kurz\n");

        Assert.True(SubBulletButtons.Offer(subBullets[0]));
        Assert.False(SubBulletButtons.Offer(subBullets[1]));
    }

    [Fact]
    public void ExactlyALine_NoButtons()
    {
        var subBullets = SubBullets($"- Notiz\n\t- {new string('x', SubBulletButtons.LineLength)}\n");

        Assert.False(SubBulletButtons.Offer(subBullets[0]));
    }

    [Fact]
    public void SecondLine_GetsButtons()
    {
        var subBullets = SubBullets("- Notiz\n\t- Erste Zeile\n\t  zweite Zeile\n");

        Assert.Equal("Erste Zeile\nzweite Zeile", subBullets[0].Content);
        Assert.True(SubBulletButtons.Offer(subBullets[0]));
    }

    [Fact]
    public void WithOwnSubBullets_GetsButtons_TheyAreJudgedOnTheirOwn()
    {
        var subBullets = SubBullets("- Notiz\n\t- Abschnitt\n\t\t- a\n\t\t- b\n");

        Assert.True(SubBulletButtons.Offer(subBullets[0]));
        Assert.False(SubBulletButtons.Offer(subBullets[1]));
        Assert.False(SubBulletButtons.Offer(subBullets[2]));
    }

    [Theory]
    [InlineData("1.", "2.")]
    [InlineData("1)", "2)")]
    [InlineData("a)", "b)")]
    [InlineData("–", "–")]
    [InlineData("•", "•")]
    public void LongItemsOfAnEnumeration_NoButtons(string first, string second)
    {
        var subBullets = SubBullets($"- Liste\n\t- {first} {Long}\n\t- {second} {Long}\n\t- {Long}\n");

        Assert.False(SubBulletButtons.Offer(subBullets[0]));
        Assert.False(SubBulletButtons.Offer(subBullets[1]));

        // A long sibling without a mark is no item of the enumeration.
        Assert.True(SubBulletButtons.Offer(subBullets[2]));
    }

    [Fact]
    public void SingleMarkedItem_IsNoEnumeration()
    {
        var subBullets = SubBullets($"- Notiz\n\t- 1. {Long}\n\t- {Long}\n");

        Assert.True(SubBulletButtons.Offer(subBullets[0]));
    }
}
