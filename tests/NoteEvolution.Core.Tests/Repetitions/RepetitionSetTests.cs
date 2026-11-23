using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Repetitions;

namespace NoteEvolution.Core.Tests.Repetitions;

public class RepetitionSetTests
{
    private static readonly Guid IdA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid IdB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static string Text(string name) =>
        $"{name} eins zwei drei vier fuenf sechs sieben acht neun";

    /// <summary>
    /// # Eins: a (id IdA), b (id IdB); ## Zwei: c, d; # Drei: e. Block c has no id.
    /// </summary>
    private static Book Load() => Book.Load(LogseqParser.Parse(
        "Buch - Test.md",
        Encoding.UTF8.GetBytes(
            "type:: book\n\n" +
            $"- # Eins\n\t- {Text("alpha")}\n\t  id:: {IdA}\n\t- {Text("beta")}\n\t  id:: {IdB}\n" +
            $"\t- ## Zwei\n\t\t- {Text("gamma")}\n\t\t- {Text("delta")}\n" +
            $"- # Drei\n\t- {Text("epsilon")}\n")));

    private sealed record Fixture(Book Book, IReadOnlyList<ComparedBlock> Blocks)
    {
        public Guid A => Blocks[0].Key;
        public Guid B => Blocks[1].Key;
        public Guid C => Blocks[2].Key;
        public Guid D => Blocks[3].Key;
        public Guid E => Blocks[4].Key;

        public OutlineNode Eins => Book.ContentChildren.First();
        public OutlineNode Zwei => Eins.Children.Single();
        public OutlineNode Drei => Book.ContentChildren.Last();

        public RepetitionSet Create(
            IReadOnlyList<LiteralPair>? literal = null, IReadOnlyList<SemanticPair>? semantic = null,
            IReadOnlySet<(Guid, Guid)>? accepted = null, int embedded = 5) =>
            RepetitionSet.Create(Book, Blocks, literal ?? [], semantic ?? [], accepted ?? new HashSet<(Guid, Guid)>(), embedded, Blocks.Count);
    }

    private static Fixture Build()
    {
        var book = Load();
        return new Fixture(book, ComparisonText.Of(book));
    }

    private static LiteralPair Literal(Guid a, Guid b) => new(a, b, [new SharedRun(0, 0, 8)]);

    private static IReadOnlySet<Guid> Keys(params Guid[] keys) => new HashSet<Guid>(keys);

    [Fact]
    public void Fixture_HasFiveComparedBlocks()
    {
        Assert.Equal(5, Build().Blocks.Count);
    }

    [Fact]
    public void All_LiteralFirst_ThenSemantic_InBookOrder()
    {
        var f = Build();

        var set = f.Create(
            literal: [Literal(f.B, f.C), Literal(f.A, f.D), Literal(f.A, f.B)],
            semantic: [new SemanticPair(f.D, f.E, 0.91f, []), new SemanticPair(f.A, f.E, 0.95f, []), new SemanticPair(f.A, f.C, 0.92f, [])]);

        Assert.Equal(
            [
                (RepetitionKind.Literal, f.A, f.B), (RepetitionKind.Literal, f.A, f.D), (RepetitionKind.Literal, f.B, f.C),
                (RepetitionKind.Semantic, f.A, f.C), (RepetitionKind.Semantic, f.A, f.E), (RepetitionKind.Semantic, f.D, f.E),
            ],
            set.All.Select(r => (r.Kind, r.A, r.B)));
    }

    [Fact]
    public void Repetition_CarriesRunsWordsAndScore()
    {
        var f = Build();
        var runs = new[] { new SharedRun(1, 2, 8), new SharedRun(20, 30, 9) };

        var set = f.Create(
            literal: [new LiteralPair(f.A, f.B, runs)],
            semantic: [new SemanticPair(f.C, f.D, 0.93f, [])]);

        var literal = set.All[0];
        Assert.Equal(runs, literal.Runs);
        Assert.Equal(17, literal.Words);
        var semantic = set.All[1];
        Assert.Empty(semantic.Runs);
        Assert.Equal(0, semantic.Words);
        Assert.Equal(0.93f, semantic.Score);
    }

    [Fact]
    public void Of_Block_GivesItsPairs_NotAccepted()
    {
        var f = Build();
        var set = f.Create(
            literal: [Literal(f.A, f.B), Literal(f.C, f.D)],
            semantic: [new SemanticPair(f.A, f.E, 0.95f, []), new SemanticPair(f.B, f.E, 0.91f, [])],
            accepted: new HashSet<(Guid, Guid)> { (IdA, IdB) });

        var ofA = set.Of(f.A);

        var only = Assert.Single(ofA);
        Assert.Equal((RepetitionKind.Semantic, f.A, f.E), (only.Kind, only.A, only.B));
        Assert.Equal(2, set.Of(f.E).Count);
        Assert.Empty(set.Of(Guid.NewGuid()));
    }

    [Fact]
    public void Count_PairInsideRange_CountsOnce()
    {
        var f = Build();
        var set = f.Create(literal: [Literal(f.A, f.B)]);

        Assert.Equal(1, set.Count(Keys(f.A, f.B)));
        Assert.Equal(1, set.Count(Keys(f.A)));
        Assert.Equal(0, set.Count(Keys(f.C)));
    }

    [Fact]
    public void Count_AcceptedPairs_DoNotCount()
    {
        var f = Build();
        var set = f.Create(
            literal: [Literal(f.A, f.B)],
            semantic: [new SemanticPair(f.A, f.E, 0.95f, [])],
            accepted: new HashSet<(Guid, Guid)> { (IdB, IdA) });

        Assert.Equal(1, set.Count(Keys(f.A, f.B, f.E)));
        Assert.Equal(2, set.All.Count);
        Assert.True(set.All.Single(r => r.Kind == RepetitionKind.Literal).Accepted);
        Assert.Empty(set.In(Keys(f.B)));
    }

    [Fact]
    public void CountIn_IncludesSubSections()
    {
        var f = Build();
        var set = f.Create(
            literal: [Literal(f.A, f.B), Literal(f.C, f.D)],
            semantic: [new SemanticPair(f.D, f.E, 0.91f, []), new SemanticPair(f.A, f.E, 0.92f, [])]);

        Assert.Equal(2, set.CountIn(f.Zwei));   // c-d, d-e
        Assert.Equal(4, set.CountIn(f.Eins));   // a-b, c-d (sub-section), d-e, a-e: each once
        Assert.Equal(2, set.CountIn(f.Drei));   // d-e, a-e
        Assert.Equal(4, set.CountIn(f.Book.Root));
    }

    [Fact]
    public void Count_WholeBook()
    {
        var f = Build();
        var set = f.Create(
            literal: [Literal(f.A, f.B), Literal(f.C, f.D)],
            semantic: [new SemanticPair(f.A, f.E, 0.95f, [])],
            accepted: new HashSet<(Guid, Guid)> { (IdA, IdB) });

        Assert.Equal(2, set.Count(new HashSet<Guid>(f.Blocks.Select(b => b.Key))));
        Assert.Equal(2, set.CountIn(f.Book.Root));
    }

    [Fact]
    public void In_GivesPairsOnceEach_NotAccepted()
    {
        var f = Build();
        var set = f.Create(literal: [Literal(f.A, f.B), Literal(f.B, f.C)]);

        Assert.Equal(2, set.In(Keys(f.A, f.B, f.C)).Count);
    }

    [Fact]
    public void Accepted_NeedsBothIds_EitherOrder()
    {
        var f = Build();
        var set = f.Create(
            literal: [Literal(f.A, f.B), Literal(f.A, f.C)],
            semantic: [new SemanticPair(f.B, f.A, 0.95f, [])],
            accepted: new HashSet<(Guid, Guid)> { (IdB, IdA) });

        Assert.True(set.All[0].Accepted);   // a-b, ids given in the other order
        Assert.False(set.All[1].Accepted);  // c has no id
        Assert.True(set.All[2].Accepted);
    }

    [Fact]
    public void Create_SemanticRepetitions_CarryTheirSentencePairs()
    {
        var f = Build();
        var pair = new SentencePair(new ComparedSentence(0, 0, 10, 5), new ComparedSentence(0, 3, 12, 6), 0.95f);
        var set = f.Create(literal: [Literal(f.A, f.B)], semantic: [new SemanticPair(f.A, f.E, 0.95f, [pair])]);

        Assert.Empty(set.All[0].Sentences);
        Assert.Equal(pair, Assert.Single(set.All[1].Sentences));
    }

    [Fact]
    public void Accepted_BlockWithoutId_NeverMatches()
    {
        var f = Build();
        var set = f.Create(
            literal: [Literal(f.A, f.C)],
            accepted: new HashSet<(Guid, Guid)> { (IdA, Guid.Empty), (Guid.Empty, IdA) });

        Assert.False(Assert.Single(set.All).Accepted);
    }

    [Fact]
    public void Complete_WhenAllEmbedded()
    {
        var f = Build();

        var partial = f.Create(embedded: 3);
        var full = f.Create(embedded: 5);

        Assert.False(partial.Complete);
        Assert.Equal((3, 5), (partial.Embedded, partial.Total));
        Assert.True(full.Complete);
    }

    [Fact]
    public void Block_FindsComparedBlockByKey()
    {
        var f = Build();
        var set = f.Create();

        Assert.Same(f.Blocks[2], set.Block(f.C));
        Assert.Null(set.Block(Guid.NewGuid()));
    }

    [Fact]
    public void IsFor_IsReferenceEquality()
    {
        var f = Build();
        var set = f.Create();

        Assert.True(set.IsFor(f.Book));
        Assert.False(set.IsFor(Load()));
        Assert.False(set.IsFor(null));
        Assert.False(RepetitionSet.Empty.IsFor(f.Book));
    }

    [Fact]
    public void Empty_HasNothing()
    {
        var empty = RepetitionSet.Empty;

        Assert.Empty(empty.All);
        Assert.Equal(0, empty.Count(Keys(Guid.NewGuid())));
        Assert.Equal(0, empty.CountIn(Build().Book.Root));
        Assert.True(empty.Complete);
    }
}
