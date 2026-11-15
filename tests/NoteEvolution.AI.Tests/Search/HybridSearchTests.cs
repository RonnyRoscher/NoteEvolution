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

        /// <summary>The similarity of a ranked note; 1 if none is given.</summary>
        public Dictionary<Guid, float> Scores { get; } = [];
        public List<string> Embedded { get; } = [];
        public List<(float[] Query, int K)> NearestCalls { get; } = [];

        public SemanticStatus Status => new(SemanticState.Ready, 0, 0, null);
        public event Action? StatusChanged { add { } remove { } }
        public bool IsReady => Ready;

        public Task RebuildAsync(INoteRepository notes, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> UpdatePageAsync(INoteRepository notes, string pagePath, CancellationToken ct) =>
            throw new NotSupportedException();

        public IReadOnlyList<(Guid Key, float Score)> Nearest(float[] query, int k, Func<Guid, bool> include)
        {
            NearestCalls.Add((query, k));
            return [.. Ranked.Where(include).Take(k).Select(key => (key, Scores.GetValueOrDefault(key, 1f)))];
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

    [Fact]
    public void NotesWithTheWordsComeFirst_ThenTheSimilarOnes()
    {
        using var f = new Fixture();
        f.Text.Hits.AddRange([new(f.A, 9.0), new(f.B, 5.0)]);
        f.Index.Ranked.AddRange([f.C, f.B, f.D]);

        var hits = f.Hybrid.Search(new("wort", NoFilter));

        // B contains the words: it is not listed again as similar.
        Assert.Equal(
            [new SearchHit(f.A, 9.0), new SearchHit(f.B, 5.0), new SearchHit(f.C, 1f, Similar: true), new SearchHit(f.D, 1f, Similar: true)],
            hits);
        Assert.Equal(["query: wort"], f.Index.Embedded);
        Assert.Same(QueryVector, Assert.Single(f.Index.NearestCalls).Query);
    }

    [Fact]
    public void SimilarNotes_NeedTheThreshold()
    {
        using var f = new Fixture();
        f.Index.Ranked.AddRange([f.A, f.B, f.C]);
        f.Index.Scores[f.A] = 0.9f;
        f.Index.Scores[f.B] = HybridSearchService.SimilarThreshold;
        f.Index.Scores[f.C] = HybridSearchService.SimilarThreshold - 0.01f;

        var hits = f.Hybrid.Search(new("wort", NoFilter));

        Assert.Equal([f.A, f.B], hits.Select(h => h.NoteBlockKey));
        Assert.All(hits, h => Assert.True(h.Similar));
    }

    [Fact]
    public void SimilarNotes_AtMostTwenty()
    {
        var lines = string.Concat(Enumerable.Range(0, 30).Select(i => $"- notiz {i}\n"));
        using var vault = TestVault.Create(("pages/Viele.md", lines));
        var notes = new NoteRepository(vault.Open());
        var index = new StubIndex();
        index.Ranked.AddRange(notes.All().Select(n => n.Key));

        var hits = new HybridSearchService(new StubSearch(), index, notes).Search(new("wort", NoFilter));

        Assert.Equal(HybridSearchService.MaxSimilar, hits.Count);
        Assert.Equal(index.Ranked.Take(20), hits.Select(h => h.NoteBlockKey));
    }

    [Fact]
    public void NewestFirst_OrdersTheSimilarNotesByDate_UndatedLast()
    {
        using var vault = TestVault.Create(
            ("journals/2026_01_05.md", "- januar\n"),
            ("journals/2026_03_09.md", "- maerz\n"),
            ("pages/Seite.md", "- ohne datum\n"));
        var notes = new NoteRepository(vault.Open());
        Guid Key(string text) => notes.All().Single(n => n.Block.Content == text).Key;
        var index = new StubIndex();
        index.Ranked.AddRange([Key("ohne datum"), Key("januar"), Key("maerz")]);
        var text = new StubSearch();
        var hybrid = new HybridSearchService(text, index, notes);

        var newest = hybrid.Search(new("wort", NoFilter, NewestFirst: true));
        var best = hybrid.Search(new("wort", NoFilter));

        Assert.Equal([Key("maerz"), Key("januar"), Key("ohne datum")], newest.Select(h => h.NoteBlockKey));
        Assert.Equal(index.Ranked, best.Select(h => h.NoteBlockKey));
        // The order is asked of the full-text search too.
        Assert.True(text.Queries[0].NewestFirst);
    }

    [Fact]
    public void NoteWithTheWordsBeyondTheLimit_IsNotListedAsSimilar()
    {
        using var f = new Fixture();
        f.Text.Hits.AddRange([new(f.A, 9.0), new(f.B, 5.0)]);
        f.Index.Ranked.AddRange([f.B, f.C]);

        var hits = f.Hybrid.Search(new("wort", NoFilter, Limit: 1));

        Assert.Equal([new SearchHit(f.A, 9.0), new SearchHit(f.C, 1f, Similar: true)], hits);
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
    public void RealFullText_ThenTheSimilarNote()
    {
        using var f = new Fixture();
        using var fullText = new FullTextSearchService();
        fullText.Rebuild(f.Notes);
        var hybrid = new HybridSearchService(fullText, f.Index, f.Notes);
        f.Index.Ranked.AddRange([f.D, f.B]);

        var hits = hybrid.Search(new("beta", NoFilter));

        Assert.Equal([(f.B, false), (f.D, true)], hits.Select(h => (h.NoteBlockKey, h.Similar)));
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
