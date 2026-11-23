using System.Text;
using Bunit;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Repetitions;
using NoteEvolution.UI.Components;

namespace NoteEvolution.UI.Tests;

/// <summary>The minimap of the repetitions tab: the whole book in a strip with the places of the current hit.</summary>
public class RepetitionMinimapTests : UiTestContext
{
    private static readonly Guid IdA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid IdB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private const string RunX = "eins zwei drei vier fuenf sechs sieben acht neun";

    // The middle block is 100 characters, so the shares are easy to follow.
    private static readonly string Filler = new('x', 100);

    private static readonly string Body =
        $"- # Eins\n\t- alpha {RunX}\n\t  id:: {IdA}\n\t- {Filler}\n" +
        $"- # Zwei\n\t- beta {RunX}\n\t  id:: {IdB}\n" +
        "- # Glossar\n  anhang:: glossar\n\t- Achtsamkeit bleibt weg\n";

    private static (Book Book, Repetition Pair) Build()
    {
        var book = Book.Load(LogseqParser.Parse("Buch - Test.md", Encoding.UTF8.GetBytes("type:: book\n\n" + Body)));
        var blocks = ComparisonText.Of(book);
        var set = RepetitionSet.Create(
            book, blocks, LiteralRepetitions.Find(blocks), [], new HashSet<(Guid, Guid)>(), 0, 0);
        return (book, set.All.Single());
    }

    private IRenderedComponent<RepetitionMinimap> Render(Book book, Repetition pair, Action<Guid>? pick = null) =>
        Render<RepetitionMinimap>(p => p
            .Add(c => c.Book, book).Add(c => c.Hits, [pair]).Add(c => c.Current, pair).Add(c => c.Origin, pair.A)
            .Add(c => c.OnPick, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<Guid>(this, k => pick?.Invoke(k))));

    private static double Top(AngleSharp.Dom.IElement e) =>
        double.Parse(System.Text.RegularExpressions.Regex.Match(e.GetAttribute("style")!, @"top: ([\d.]+)%").Groups[1].Value,
            System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Minimap_PlacesAAndBAtTheirShareOfTheBook()
    {
        var (book, pair) = Build();
        var cut = Render(book, pair);

        var total = (RunX.Length + 6) + 100 + (RunX.Length + 5);
        Assert.Equal(0, Top(cut.Find(".ne-minimap-a")));
        Assert.Equal(100.0 * (RunX.Length + 6 + 100) / total, Top(cut.Find(".ne-minimap-b")), 2);
        var link = cut.Find(".ne-minimap-link");
        Assert.Equal(0, Top(link));
    }

    [Fact]
    public void Minimap_ChapterLines_And_AllHitsFaint()
    {
        var (book, pair) = Build();
        var cut = Render(book, pair);

        Assert.Equal(2, cut.FindAll(".ne-minimap-chapter").Count);
        Assert.Equal(2, cut.FindAll(".ne-minimap-hit").Count);
    }

    [Fact]
    public void Minimap_Click_PicksTheBlockAtThatShare()
    {
        var (book, pair) = Build();
        Guid? picked = null;
        var cut = Render(book, pair, k => picked = k);

        cut.Find($".ne-minimap-zone[data-key='{pair.B}']").Click();

        Assert.Equal(pair.B, picked);
    }

    [Fact]
    public async Task Minimap_RegistersTheViewportTracking_AndRemovesItOnDispose()
    {
        var (book, pair) = Build();
        var cut = Render(book, pair);

        Assert.Single(JSInterop.Invocations, i => i.Identifier == "neRepetitions.trackViewports");
        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "neRepetitions.untrack");

        await cut.Instance.DisposeAsync();

        Assert.Single(JSInterop.Invocations, i => i.Identifier == "neRepetitions.untrack");
    }
}
