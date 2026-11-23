using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Repetitions;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>
/// The hits of the repetitions tab (repetitions tab spec 4): their order and filters, where the tab stays after a change,
/// and which side is the origin.
/// </summary>
public class RepetitionHitsTests
{
    private static readonly Guid IdA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid IdB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private const string RunX = "eins zwei drei vier fuenf sechs sieben acht neun";

    private const string RunY = "rot gruen blau gelb weiss schwarz braun lila";

    private const string SemA = "sonne mond sterne himmel wolken regen wind schnee";
    private const string SemB = "schnee wind regen wolken himmel sterne mond sonne";

    /// <summary>
    /// "Eins": alpha, beta (sharing RunX), gamma; "Zwei": delta (sharing RunY with gamma), then the semantic pair. The
    /// semantic pair comes first in the book's text after the literal ones only in "Zwei".
    /// </summary>
    private static readonly string Body =
        $"- # Eins\n\t- alpha {RunX}\n\t  id:: {IdA}\n\t- beta {RunX}\n\t  id:: {IdB}\n\t- gamma {RunY}\n" +
        $"- # Zwei\n\t- {SemA}\n\t- delta {RunY}\n\t- {SemB}\n";

    private sealed record Fixture(Book Book, IReadOnlyList<ComparedBlock> Blocks, RepetitionSet Set)
    {
        public Guid Key(string start) =>
            Blocks.Single(b => b.Paragraphs[0].Plain.StartsWith(start, StringComparison.Ordinal)).Key;

        public OutlineNode Chapter(string title) => Book.Root.Children.Single(c => c.Title == title);
    }

    private static Fixture Build(IReadOnlySet<(Guid, Guid)>? accepted = null)
    {
        var book = Book.Load(LogseqParser.Parse("Buch - Test.md", Encoding.UTF8.GetBytes("type:: book\n\n" + Body)));
        var blocks = ComparisonText.Of(book);
        var a = blocks.Single(b => b.Paragraphs[0].Plain == SemA);
        var b = blocks.Single(b => b.Paragraphs[0].Plain == SemB);
        List<SemanticPair> semantic = [new(a.Key, b.Key, 0.95f, [new SentencePair(Sentences.Of(a)[0], Sentences.Of(b)[0], 0.95f)])];
        var set = RepetitionSet.Create(
            book, blocks, LiteralRepetitions.Find(blocks), semantic, accepted ?? new HashSet<(Guid, Guid)>(), 0, 0);
        return new Fixture(book, blocks, set);
    }

    private static string Name(Fixture f, Repetition r) =>
        $"{r.Kind}:{f.Blocks.Single(b => b.Key == r.A).Words[0].Normal}~{f.Blocks.Single(b => b.Key == r.B).Words[0].Normal}";

    [Fact]
    public void Ordered_LiteralFirst_ThenSemantic_ByPosition()
    {
        var f = Build();

        var hits = RepetitionHits.Ordered(f.Set, f.Book, showAccepted: false, chapter: null);

        // The semantic pair starts before delta in the book, but literal ones come first.
        Assert.Equal(["Literal:alpha~beta", "Literal:gamma~delta", "Semantic:sonne~schnee"], hits.Select(h => Name(f, h)));
    }

    [Fact]
    public void Ordered_ChapterFilter_And_ShowAccepted()
    {
        var f = Build(new HashSet<(Guid, Guid)> { (IdA, IdB) });

        Assert.Equal(
            ["Literal:gamma~delta", "Semantic:sonne~schnee"],
            RepetitionHits.Ordered(f.Set, f.Book, showAccepted: false, chapter: null).Select(h => Name(f, h)));
        Assert.Equal(
            ["Literal:alpha~beta", "Literal:gamma~delta", "Semantic:sonne~schnee"],
            RepetitionHits.Ordered(f.Set, f.Book, showAccepted: true, chapter: null).Select(h => Name(f, h)));

        // A hit counts for a chapter if one of its sides is in it: gamma~delta for both.
        Assert.Equal(
            ["Literal:alpha~beta", "Literal:gamma~delta"],
            RepetitionHits.Ordered(f.Set, f.Book, showAccepted: true, f.Chapter("Eins")).Select(h => Name(f, h)));
        Assert.Equal(
            ["Literal:gamma~delta", "Semantic:sonne~schnee"],
            RepetitionHits.Ordered(f.Set, f.Book, showAccepted: true, f.Chapter("Zwei")).Select(h => Name(f, h)));
    }

    [Fact]
    public void IndexAfterChange_SamePair_StaysOnIt()
    {
        var f = Build();
        var hits = RepetitionHits.Ordered(f.Set, f.Book, true, null);
        var focus = new RepetitionFocus(RepetitionKind.Literal, f.Key("gamma"), f.Key("delta"), Swapped: true);

        Assert.Equal(1, RepetitionHits.IndexAfterChange(hits, focus, f.Book));
        // The same blocks as another kind are another pair.
        var semantic = focus with { Kind = RepetitionKind.Semantic };
        Assert.NotEqual(1, RepetitionHits.IndexAfterChange(hits, semantic, f.Book));
        Assert.Equal(-1, RepetitionHits.IndexAfterChange([], focus, f.Book));
    }

    [Fact]
    public void IndexAfterChange_PairGone_TakesTheNextAfterTheSamePlace()
    {
        var f = Build();
        var all = RepetitionHits.Ordered(f.Set, f.Book, true, null);

        // alpha~beta is gone (from beta): the next literal hit from beta's place on is gamma~delta.
        var gone = new RepetitionFocus(RepetitionKind.Literal, f.Key("alpha"), f.Key("beta"), Swapped: true);
        Assert.Equal(0, RepetitionHits.IndexAfterChange([all[1], all[2]], gone, f.Book));

        // gamma~delta is gone: no literal hit is after gamma, the next is the first semantic one.
        var last = new RepetitionFocus(RepetitionKind.Literal, f.Key("gamma"), f.Key("delta"), Swapped: false);
        Assert.Equal(1, RepetitionHits.IndexAfterChange([all[0], all[2]], last, f.Book));

        // The semantic pair is gone and nothing comes after it: the last hit.
        var semantic = new RepetitionFocus(RepetitionKind.Semantic, f.Key("sonne"), f.Key("schnee"), Swapped: false);
        Assert.Equal(1, RepetitionHits.IndexAfterChange([all[0], all[1]], semantic, f.Book));

        // A focus of another book: the first hit.
        var elsewhere = new RepetitionFocus(RepetitionKind.Literal, Guid.NewGuid(), Guid.NewGuid(), Swapped: false);
        Assert.Equal(0, RepetitionHits.IndexAfterChange(all, elsewhere, f.Book));
    }

    [Fact]
    public void FocusOn_OriginIsB_Swaps()
    {
        var f = Build();
        var hit = RepetitionHits.Ordered(f.Set, f.Book, true, null)[0];

        Assert.Equal(new RepetitionFocus(RepetitionKind.Literal, hit.A, hit.B, Swapped: true), RepetitionHits.FocusOn(hit, hit.B));
        Assert.False(RepetitionHits.FocusOn(hit, hit.A).Swapped);
        Assert.False(RepetitionHits.FocusOn(hit, null).Swapped);
        Assert.False(RepetitionHits.FocusOn(hit, Guid.NewGuid()).Swapped);
    }
}
