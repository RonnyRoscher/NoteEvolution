using NoteEvolution.AI.Search;
using NoteEvolution.AI.Semantic;
using NoteEvolution.Core.Vaults;
using NoteEvolution.TestSupport;

namespace NoteEvolution.AI.Tests.Search;

public sealed class HybridSearchTests
{
    private static readonly NoteFilter NoFilter = new(false, null, null);
    private static readonly float[] QueryVector = [1f, 0f];

    private sealed class StubSearch : ISearchService
    {
        public List<SearchHit> Hits { get; } = [];
        public List<SearchQuery> Queries { get; } = [];
        public List<string> Forwarded { get; } = [];

        public void Rebuild(INoteRepository notes) => Forwarded.Add("rebuild");

        public void UpdatePage(INoteRepository notes, string pagePath) => Forwarded.Add("update " + pagePath);

        public IReadOnlyList<SearchHit> Search(SearchQuery query)
        {
            Queries.Add(query);
            return [.. Hits.Take(query.Limit)];
        }
    }

    /// <summary>Returns <see cref="Ranked"/> (best first) minus what <c>include</c> rejects.</summary>
    private sealed class StubIndex : ISemanticIndex
    {
        public bool Ready { get; set; } = true;
        public bool EmbedFails { get; set; }
        public List<Guid> Ranked { get; } = [];
        public List<string> Embedded { get; } = [];
        public List<(float[] Query, int K)> NearestCalls { get; } = [];

        public SemanticStatus Status => new(SemanticState.Ready, 0, 0, null);
        public event Action? StatusChanged { add { } remove { } }
        public bool IsReady => Ready;

        public Task RebuildAsync(INoteRepository notes, CancellationToken ct) => throw new NotSupportedException();

        public Task UpdatePageAsync(INoteRepository notes, string pagePath, CancellationToken ct) =>
            throw new NotSupportedException();

        public IReadOnlyList<(Guid Key, float Score)> Nearest(float[] query, int k, Func<Guid, bool> include)
        {
            NearestCalls.Add((query, k));
            return [.. Ranked.Where(include).Take(k).Select(key => (key, 1f))];
        }

        public float[]? VectorOf(Guid noteKey) => null;

        public float[]? EmbedQuery(string text)
        {
            Embedded.Add(text);
            return EmbedFails ? null : QueryVector;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestVault _vault = TestVault.Create(("pages/Notizen.md",
            "- alpha\n- beta\n- gamma\n- delta\n- used one\n  used-in:: [[Buch - X]]\n"));

        public Fixture()
        {
            Notes = new NoteRepository(_vault.Open());
            var keys = Notes.All().ToDictionary(n => n.Block.Content.Split('\n')[0], n => n.Key);
            A = keys["alpha"];
            B = keys["beta"];
            C = keys["gamma"];
            D = keys["delta"];
            Used = keys["used one"];
            Hybrid = new HybridSearchService(Text, Index, Notes);
        }

        public NoteRepository Notes { get; }
        public StubSearch Text { get; } = new();
        public StubIndex Index { get; } = new();
        public HybridSearchService Hybrid { get; }
        public Guid A { get; }
        public Guid B { get; }
        public Guid C { get; }
        public Guid D { get; }
        public Guid Used { get; }

        public void Dispose() => _vault.Dispose();
    }

    private static double Rrf(params int[] ranks) => ranks.Sum(r => 1.0 / (60 + r));

    [Fact]
    public void Rrf_CombinesBothLists_DocInBothRanksFirst()
    {
        using var f = new Fixture();
        f.Text.Hits.AddRange([new(f.A, 9.0), new(f.B, 5.0), new(f.C, 1.0)]);
        f.Index.Ranked.AddRange([f.C, f.D, f.B]);

        var hits = f.Hybrid.Search(new("wort", NoFilter));

        Assert.Equal([f.C, f.B, f.A, f.D], hits.Select(h => h.NoteBlockKey));
        Assert.Equal([Rrf(3, 1), Rrf(2, 3), Rrf(1), Rrf(2)], hits.Select(h => h.Score));
        Assert.Equal(["query: wort"], f.Index.Embedded);
        Assert.Equal(100, Assert.Single(f.Text.Queries).Limit);
        var call = Assert.Single(f.Index.NearestCalls);
        Assert.Same(QueryVector, call.Query);
        Assert.Equal(100, call.K);
    }

    [Fact]
    public void Rrf_TiedScores_OrderedByKey_AndLimitApplied()
    {
        using var f = new Fixture();
        f.Text.Hits.Add(new(f.A, 1.0));
        f.Index.Ranked.Add(f.B);

        var hits = f.Hybrid.Search(new("wort", NoFilter, Limit: 1));

        var expected = new[] { f.A, f.B }.Order().First();
        Assert.Equal(new SearchHit(expected, Rrf(1)), Assert.Single(hits));
    }

    [Fact]
    public void Rrf_SemanticOnlyHitIncluded()
    {
        using var f = new Fixture();
        f.Index.Ranked.Add(f.D);

        var hit = Assert.Single(f.Hybrid.Search(new("wort", NoFilter)));

        Assert.Equal(new SearchHit(f.D, Rrf(1)), hit);
    }

    [Fact]
    public void Filters_AppliedToSemanticHits()
    {
        using var f = new Fixture();
        f.Index.Ranked.AddRange([f.Used, Guid.NewGuid(), f.A]);
        var hideUsed = new NoteFilter(true, null, null);

        var unfiltered = f.Hybrid.Search(new("wort", NoFilter));
        var filtered = f.Hybrid.Search(new("wort", hideUsed));

        // a removed (unknown) key never shows up; the used note only without the filter
        Assert.Equal([f.Used, f.A], unfiltered.Select(h => h.NoteBlockKey));
        Assert.Equal([f.A], filtered.Select(h => h.NoteBlockKey));
        Assert.Equal(hideUsed, f.Text.Queries[1].Filter);
    }

    [Fact]
    public void SemanticNotReady_EqualsFullText()
    {
        using var f = new Fixture();
        f.Index.Ready = false;
        f.Text.Hits.AddRange([new(f.B, 2.0), new(f.A, 1.0), new(f.C, 0.5)]);

        var hits = f.Hybrid.Search(new("wort", NoFilter, Limit: 2));

        Assert.Equal(f.Text.Hits.Take(2), hits);
        Assert.Empty(f.Index.Embedded);
        Assert.Empty(f.Index.NearestCalls);
    }

    [Fact]
    public void EmbedQueryFails_EqualsFullText()
    {
        using var f = new Fixture();
        f.Index.EmbedFails = true;
        f.Index.Ranked.Add(f.D);
        f.Text.Hits.AddRange([new(f.B, 2.0), new(f.A, 1.0)]);

        var hits = f.Hybrid.Search(new("wort", NoFilter));

        Assert.Equal(f.Text.Hits, hits);
        Assert.Empty(f.Index.NearestCalls);
    }

    [Fact]
    public void NoSearchableWord_Empty()
    {
        using var f = new Fixture();
        using var fullText = new FullTextSearchService();
        fullText.Rebuild(f.Notes);
        var hybrid = new HybridSearchService(fullText, f.Index, f.Notes);
        f.Index.Ranked.Add(f.D);

        Assert.Empty(hybrid.Search(new("  \" -- ", NoFilter)));
        Assert.Empty(f.Index.Embedded);
    }

    [Fact]
    public void RealFullText_FusesWithSemanticHit()
    {
        using var f = new Fixture();
        using var fullText = new FullTextSearchService();
        fullText.Rebuild(f.Notes);
        var hybrid = new HybridSearchService(fullText, f.Index, f.Notes);
        f.Index.Ranked.AddRange([f.D, f.B]);

        var hits = hybrid.Search(new("beta", NoFilter));

        Assert.Equal([f.B, f.D], hits.Select(h => h.NoteBlockKey));
        Assert.Equal([Rrf(1, 2), Rrf(1)], hits.Select(h => h.Score));
    }

    [Fact]
    public void RebuildAndUpdatePage_AreForwardedToFullTextOnly()
    {
        using var f = new Fixture();

        f.Hybrid.Rebuild(f.Notes);
        f.Hybrid.UpdatePage(f.Notes, "pages/Notizen.md");

        Assert.Equal(["rebuild", "update pages/Notizen.md"], f.Text.Forwarded);
    }
}
