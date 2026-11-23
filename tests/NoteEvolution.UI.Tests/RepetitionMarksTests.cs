using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Repetitions;
using NoteEvolution.UI.Editor;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>
/// What the editor underlines (repetitions tab spec 3): literal runs always, semantic sentences and the current hit only
/// with the tab, nothing of accepted pairs, at the offsets the editor counts.
/// </summary>
public class RepetitionMarksTests : UiTestContext
{
    private static readonly Guid IdA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid IdB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private const string RunX = "eins zwei drei vier fuenf sechs sieben acht neun";

    // The same letters in another order: no shared run, one sentence each.
    private const string SemA = "sonne mond sterne himmel wolken regen wind schnee";
    private const string SemB = "schnee wind regen wolken himmel sterne mond sonne";

    private IStringLocalizer<Strings> L => Services.GetRequiredService<IStringLocalizer<Strings>>();

    private static Book Load(string body) =>
        Book.Load(LogseqParser.Parse("Buch - Test.md", Encoding.UTF8.GetBytes("type:: book\n\n" + body)));

    private static readonly string Body =
        $"- # Eins\n\t- alpha {RunX}\n\t  id:: {IdA}\n\t- beta {RunX}\n\t  id:: {IdB}\n" +
        $"- # Zwei\n\t- {SemA}\n\t- {SemB}\n";

    private sealed record Fixture(Book Book, IReadOnlyList<ComparedBlock> Blocks, RepetitionSet Set)
    {
        public Guid Key(string start) =>
            Blocks.Single(b => b.Paragraphs[0].Plain.StartsWith(start, StringComparison.Ordinal)).Key;

        /// <summary>"Ort " and the first word of the block.</summary>
        public string PlaceOf(Guid key) => "Ort " + Blocks.Single(b => b.Key == key).Words[0].Normal;
    }

    /// <summary>The literal pairs of the book and, if asked, the one semantic pair of the blocks starting with "sonne" and "schnee".</summary>
    private static Fixture Build(string body, bool semantic = false, IReadOnlySet<(Guid, Guid)>? accepted = null)
    {
        var book = Load(body);
        var blocks = ComparisonText.Of(book);
        List<SemanticPair> pairs = [];
        if (semantic)
        {
            var a = blocks.Single(b => b.Paragraphs[0].Plain == SemA);
            var b = blocks.Single(b => b.Paragraphs[0].Plain == SemB);
            pairs.Add(new SemanticPair(a.Key, b.Key, 0.95f, [new SentencePair(Sentences.Of(a)[0], Sentences.Of(b)[0], 0.95f)]));
        }

        var set = RepetitionSet.Create(
            book, blocks, LiteralRepetitions.Find(blocks), pairs, accepted ?? new HashSet<(Guid, Guid)>(), 0, 0);
        return new Fixture(book, blocks, set);
    }

    private IReadOnlyList<RepetitionTextMark> For(
        Fixture f, bool tabOpen = false, RepetitionFocus? focus = null, bool showAccepted = false) =>
        RepetitionMarks.For(f.Set, f.Book, tabOpen, focus, showAccepted, f.PlaceOf, L);

    private string AlsoIn(string places) => L["RepetitionAlsoIn", places].Value;

    [Fact]
    public void Literal_IsMarkedOnBothSides_WithTheOtherPlaceAsTitle()
    {
        var f = Build(Body);

        var marks = For(f);

        Assert.Equal(
            [
                new RepetitionTextMark(f.Key("alpha"), 6, RunX.Length, RepetitionMarkKind.Literal, AlsoIn("Ort beta")),
                new RepetitionTextMark(f.Key("beta"), 5, RunX.Length, RepetitionMarkKind.Literal, AlsoIn("Ort alpha")),
            ],
            marks);
        Assert.Equal("Auch in: Ort beta", marks[0].Title);
    }

    [Fact]
    public void SameSpan_InSeveralPairs_IsOneMark_WithAllPlaces()
    {
        var f = Build($"- # Eins\n\t- alpha {RunX}\n\t- beta {RunX}\n\t- gamma {RunX}\n");

        var alpha = Assert.Single(For(f), m => m.ParagraphKey == f.Key("alpha"));

        Assert.Equal(AlsoIn("Ort beta; Ort gamma"), alpha.Title);
    }

    [Fact]
    public void Semantic_OnlyWhileTheTabIsOpen()
    {
        var f = Build(Body, semantic: true);

        Assert.DoesNotContain(For(f), m => m.Kind == RepetitionMarkKind.Semantic);

        var semantic = For(f, tabOpen: true).Where(m => m.Kind == RepetitionMarkKind.Semantic).ToList();
        Assert.Equal(
            [
                new RepetitionTextMark(f.Key("sonne"), 0, SemA.Length, RepetitionMarkKind.Semantic, AlsoIn("Ort schnee")),
                new RepetitionTextMark(f.Key("schnee"), 0, SemB.Length, RepetitionMarkKind.Semantic, AlsoIn("Ort sonne")),
            ],
            semantic);
        // The literal marks stay as they are.
        Assert.Equal(2, For(f, tabOpen: true).Count(m => m.Kind == RepetitionMarkKind.Literal));
    }

    [Fact]
    public void Current_OnlyOnTheOriginSide_AndOnlyWithTheTab()
    {
        var f = Build(Body, semantic: true);
        var literal = new RepetitionFocus(RepetitionKind.Literal, f.Key("alpha"), f.Key("beta"), Swapped: true);

        Assert.DoesNotContain(For(f, focus: literal), m => m.Kind == RepetitionMarkKind.Current);

        var current = Assert.Single(For(f, tabOpen: true, focus: literal), m => m.Kind == RepetitionMarkKind.Current);
        Assert.Equal(new RepetitionTextMark(f.Key("beta"), 5, RunX.Length, RepetitionMarkKind.Current, AlsoIn("Ort alpha")), current);

        var semantic = new RepetitionFocus(RepetitionKind.Semantic, f.Key("sonne"), f.Key("schnee"), Swapped: false);
        current = Assert.Single(For(f, tabOpen: true, focus: semantic), m => m.Kind == RepetitionMarkKind.Current);
        Assert.Equal(new RepetitionTextMark(f.Key("sonne"), 0, SemA.Length, RepetitionMarkKind.Current, AlsoIn("Ort schnee")), current);

        // A focus on a pair the set does not have (any more) marks nothing.
        var gone = new RepetitionFocus(RepetitionKind.Semantic, f.Key("alpha"), f.Key("beta"), Swapped: false);
        Assert.DoesNotContain(For(f, tabOpen: true, focus: gone), m => m.Kind == RepetitionMarkKind.Current);
    }

    [Fact]
    public void Accepted_IsNotMarked()
    {
        var f = Build(Body, accepted: new HashSet<(Guid, Guid)> { (IdB, IdA) });
        var focus = new RepetitionFocus(RepetitionKind.Literal, f.Key("alpha"), f.Key("beta"), Swapped: false);

        Assert.Empty(For(f, tabOpen: true));
        Assert.Empty(For(f, tabOpen: true, focus: focus));

        // Shown in the tab with "Gewollte anzeigen", the accepted pair is the current hit and highlighted as such.
        var current = Assert.Single(For(f, tabOpen: true, focus: focus, showAccepted: true));
        Assert.Equal(new RepetitionTextMark(f.Key("alpha"), 6, RunX.Length, RepetitionMarkKind.Current, AlsoIn("Ort beta")), current);
    }

    [Fact]
    public void Spans_LiteralRunOverTwoParagraphs_GivesOnePiecePerParagraph()
    {
        const string head = "eins zwei drei vier";
        const string tail = "fuenf sechs sieben acht neun";
        var f = Build($"- # Eins\n\t- vorne {head}\n\t\t- {tail} hinten\n\t- anders {RunX}\n");
        var repetition = Assert.Single(f.Set.All);
        var split = f.Key("vorne");

        Assert.Equal([(0, 6, head.Length), (1, 0, tail.Length)], RepetitionMarks.Spans(repetition, split, f.Set));
        Assert.Equal([(0, 7, RunX.Length)], RepetitionMarks.Spans(repetition, f.Key("anders"), f.Set));

        // In the editor, the second piece is in the sub-point's paragraph.
        var subPoint = f.Set.Block(split)!.Paragraphs[1].Key;
        Assert.Equal(
            [(split, 6, head.Length), (subPoint, 0, tail.Length)],
            For(f).Where(m => m.Title == AlsoIn("Ort anders")).Select(m => (m.ParagraphKey, m.Start, m.Length)));
    }

    [Fact]
    public void Spans_Semantic_AreTheSentencesOfThatSide()
    {
        var f = Build(Body, semantic: true);
        var repetition = f.Set.All.Single(r => r.Kind == RepetitionKind.Semantic);

        Assert.Equal([(0, 0, SemB.Length)], RepetitionMarks.Spans(repetition, f.Key("schnee"), f.Set));
    }

    [Fact]
    public void Marks_UseTheEditorOffsets_AfterAReference()
    {
        // The reference is one node in the editor, so the run starts after "alpha", a space, the node and a space.
        var f = Build($"- # Eins\n\t- alpha (({IdB})) {RunX}\n\t- beta {RunX}\n\t  id:: {IdB}\n");

        var alpha = Assert.Single(For(f), m => m.ParagraphKey == f.Key("alpha"));

        Assert.Equal((8, RunX.Length), (alpha.Start, alpha.Length));
    }

    [Fact]
    public void Marks_UseTheEditorOffsets_AfterAnImage_AndBold()
    {
        // The image is one inline node in the editor (as a reference), the stars of the bold text are none.
        var f = Build($"- # Eins\n\t- **alpha** ![x](../assets/a.png) {RunX}\n\t- beta {RunX}\n");

        var alpha = Assert.Single(For(f), m => m.ParagraphKey == f.Key("alpha"));

        Assert.Equal((8, RunX.Length), (alpha.Start, alpha.Length));
    }
}
