using System.Text;
using Bunit;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Repetitions;
using NoteEvolution.UI.Components;

namespace NoteEvolution.UI.Tests;

/// <summary>The book reader of the repetitions tab: the whole book read-only, marked like the editor, scrolled to the hit.</summary>
public class BookReaderTests : UiTestContext
{
    private static readonly Guid IdA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid IdB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private const string RunX = "eins zwei drei vier fuenf sechs sieben acht neun";

    private static readonly string Body =
        $"- # Eins\n\t- alpha {RunX}\n\t  id:: {IdA}\n\t- kurz\n" +
        $"- ## Zwei\n\t- beta {RunX}\n\t  id:: {IdB}\n" +
        "- # Glossar\n  anhang:: glossar\n\t- Achtsamkeit bleibt weg\n";

    private sealed record Fixture(Book Book, RepetitionSet Set)
    {
        public Repetition Pair => Set.All.Single();
    }

    private static Fixture Build()
    {
        var book = Book.Load(LogseqParser.Parse("Buch - Test.md", Encoding.UTF8.GetBytes("type:: book\n\n" + Body)));
        var blocks = ComparisonText.Of(book);
        var set = RepetitionSet.Create(
            book, blocks, LiteralRepetitions.Find(blocks), [], new HashSet<(Guid, Guid)>(), 0, 0);
        return new Fixture(book, set);
    }

    private IRenderedComponent<BookReader> Render(
        Fixture f, Repetition? current = null, Guid? side = null, int revision = 0) =>
        Render<BookReader>(p => p
            .Add(c => c.Book, f.Book).Add(c => c.Set, f.Set).Add(c => c.Current, current).Add(c => c.Side, side)
            .Add(c => c.Revision, revision));

    private int Scrolls => JSInterop.Invocations.Count(i => i.Identifier == "neLayout.scrollReaderTo");

    [Fact]
    public void Reader_ShowsHeadingsAndBlocksInBookOrder_WithoutTheAppendix()
    {
        var f = Build();
        var cut = Render(f);

        Assert.Equal(["Eins", "Zwei"], cut.FindAll(".ne-reader-heading").Select(h => h.TextContent));
        Assert.Contains("ne-reader-h1", cut.FindAll(".ne-reader-heading")[0].ClassList);
        Assert.Contains("ne-reader-h2", cut.FindAll(".ne-reader-heading")[1].ClassList);
        Assert.Equal(3, cut.FindAll(".ne-reader-block").Count);
        Assert.Equal("kurz", cut.FindAll(".ne-reader-block")[1].TextContent.Trim());
        Assert.DoesNotContain("Achtsamkeit", cut.Markup);
        Assert.DoesNotContain("Glossar", cut.Markup);
        Assert.NotNull(cut.Find($"#ne-reader-{f.Pair.A}"));
    }

    [Fact]
    public void Reader_UnderlinesLiteralRepetitions()
    {
        var cut = Render(Build());

        Assert.Equal([RunX, RunX], cut.FindAll(".ne-rep-literal").Select(m => m.TextContent));
        Assert.Empty(cut.FindAll(".ne-rep-current"));
        Assert.Empty(cut.FindAll(".ne-rep-current-block"));
    }

    [Fact]
    public void Reader_HighlightsTheSideOfTheCurrentHit()
    {
        var f = Build();

        var cut = Render(f, f.Pair, f.Pair.B);

        Assert.Equal(RunX, Assert.Single(cut.FindAll(".ne-rep-current")).TextContent);
        var block = Assert.Single(cut.FindAll(".ne-rep-current-block"));
        Assert.Equal($"ne-reader-{f.Pair.B}", block.Id);
        Assert.Contains(RunX, block.TextContent);
    }

    [Fact]
    public void Reader_ScrollsToTheSide_OnlyWhenTheHitChanges()
    {
        var f = Build();
        var cut = Render(f, f.Pair, f.Pair.A, revision: 1);
        Assert.Equal(1, Scrolls);
        Assert.Equal($"ne-reader-{f.Pair.A}", JSInterop.Invocations.Last(i => i.Identifier == "neLayout.scrollReaderTo").Arguments[1]);

        cut.Render(p => p.Add(c => c.Revision, 2));
        Assert.Equal(1, Scrolls);

        cut.Render(p => p.Add(c => c.Side, f.Pair.B));
        Assert.Equal(2, Scrolls);
        Assert.Equal($"ne-reader-{f.Pair.B}", JSInterop.Invocations.Last(i => i.Identifier == "neLayout.scrollReaderTo").Arguments[1]);
    }

    [Fact]
    public void Reader_SameRevision_DoesNotRenderAgain()
    {
        var f = Build();
        var cut = Render(f, revision: 1);
        var renders = cut.RenderCount;

        cut.Render(p => p.Add(c => c.Set, RepetitionSet.Empty).Add(c => c.Revision, 1));

        Assert.Equal(renders, cut.RenderCount);

        cut.Render(p => p.Add(c => c.Revision, 2));

        Assert.Equal(renders + 1, cut.RenderCount);
    }
}
