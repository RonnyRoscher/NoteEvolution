using NoteEvolution.AI.Embeddings;
using NoteEvolution.AI.Relevance;
using NoteEvolution.AI.Semantic;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Vaults;
using NoteEvolution.TestSupport;

namespace NoteEvolution.AI.Tests.Relevance;

public sealed class RelevanceServiceTests : IDisposable
{
    private static readonly NoteFilter NoFilter = new(false, null, null);

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    /// <summary>An index whose embeddings are looked up in dictionaries, so tests choose every vector.</summary>
    private sealed class StubIndex : ISemanticIndex
    {
        private readonly Lock _gate = new();
        private readonly List<string> _embedded = [];
        private int _nearestCalls;

        public bool Ready { get; set; } = true;
        public bool EmbedFails { get; set; }
        public Dictionary<Guid, float[]> Notes { get; } = [];
        public Dictionary<string, float[]> Queries { get; } = [];
        public int NearestCalls => Volatile.Read(ref _nearestCalls);

        /// <summary>Every text passed to <see cref="EmbedQuery"/>, in call order.</summary>
        public IReadOnlyList<string> Embedded
        {
            get
            {
                lock (_gate) return [.. _embedded];
            }
        }

        public SemanticStatus Status => new(SemanticState.Ready, 0, 0, null);
        public event Action? StatusChanged { add { } remove { } }
        public bool IsReady => Ready;

        public Task RebuildAsync(INoteRepository notes, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> UpdatePageAsync(INoteRepository notes, string pagePath, CancellationToken ct) =>
            throw new NotSupportedException();

        public IReadOnlyList<(Guid Key, float Score)> Nearest(float[] query, int k, Func<Guid, bool> include)
        {
            Interlocked.Increment(ref _nearestCalls);
            return [.. Notes.Where(n => include(n.Key))
                .Select(n => (n.Key, Score: VectorMath.Cosine(query, n.Value)))
                .OrderByDescending(n => n.Score).Take(k)];
        }

        public float[]? VectorOf(Guid noteKey) => Notes.GetValueOrDefault(noteKey);

        public float[]? EmbedQuery(string text)
        {
            lock (_gate) _embedded.Add(text);
            return EmbedFails ? null : Queries[text];
        }
    }

    private sealed class Fixture(Book book, NoteRepository notes, StubIndex index, RelevanceService service)
    {
        public Book Book { get; } = book;
        public NoteRepository Notes { get; } = notes;
        public StubIndex Index { get; } = index;
        public RelevanceService Service { get; } = service;

        public OutlineNode Section(string title) => Walk(Book.Root).Single(n => n.Title == title);

        public NoteBlock Note(string content) => Notes.All().Single(n => n.Block.Content == content);

        public TextBlock Text(string content) =>
            Walk(Book.Root).SelectMany(n => n.TextBlocks).Single(t => t.Text == content);

        public string NoteText(string content) => EmbeddingText.ForNote(Note(content));

        /// <summary>What a caller on the UI thread passes as <c>include</c>: a snapshot of the keys that pass the filter.</summary>
        public Func<Guid, bool> Allow(NoteFilter filter)
        {
            var keys = Notes.All().Where(n => Notes.Matches(n, filter)).Select(n => n.Key).ToHashSet();
            return keys.Contains;
        }

        public string TitleOf(Guid sectionKey) => Book.FindNode(sectionKey)!.Title;

        public string ContentOf(Guid noteKey) => Notes.Get(noteKey)!.Block.Content;

        /// <summary>Gives the section (heading path and own text blocks) the direction <paramref name="v"/>.</summary>
        public void SetSection(OutlineNode node, params float[] v)
        {
            Index.Queries[EmbeddingText.ForHeadingPath(Book, node)] = v;
            foreach (var tb in node.TextBlocks) Index.Queries[EmbeddingText.ForTextBlock(tb)] = v;
        }

        public void SetNote(string content, params float[] v) => Index.Notes[Note(content).Key] = v;

        public TopicRequest Topic(string section, Guid? cursor = null, bool manuscript = false) =>
            new(Book, Section(section).Key, cursor, manuscript);

        private static IEnumerable<OutlineNode> Walk(OutlineNode node) =>
            node.Children.SelectMany(Walk).Prepend(node);
    }

    private Fixture Create(string bookBody, int cacheCapacity = 5000, params (string Path, string Content)[] notePages)
    {
        var tv = TestVault.Create([("pages/Buch.md", "title:: Buch\ntype:: book\n\n" + bookBody), .. notePages]);
        _disposables.Add(tv);
        var vault = tv.Open();
        var notes = new NoteRepository(vault);
        var index = new StubIndex();
        return new Fixture(vault.Books.Single(), notes, index, new RelevanceService(index, cacheCapacity));
    }

    /// <summary>
    /// The root text block "Prolog" (when asked for) and these sections, each with a text block named like it
    /// (except "Leer"): Liebe (with Vertrauen below), Geld, Leer, Mut. The note "Frage" points along the first axis;
    /// the sections get decreasing similarity to it in the order Liebe, Prolog, Vertrauen, Geld, Leer, Mut.
    /// </summary>
    private Fixture SixSections(bool prologue)
    {
        var body = (prologue ? "- Prolog\n" : "") +
                   "- # Liebe\n\t- Liebe eins\n\t- Liebe zwei\n\t- ## Vertrauen\n\t\t- Vertrauen Text\n" +
                   "- # Geld\n\t- Geld Text\n- # Leer\n- # Mut\n\t- Mut Text\n";
        var f = Create(body, notePages: [("journals/2026_03_01.md", "- Frage\n")]);
        f.SetNote("Frage", 1, 0, 0);
        if (prologue) f.SetSection(f.Book.Root, 2, 0, 1);
        f.SetSection(f.Section("Liebe"), 1, 0, 0);
        f.SetSection(f.Section("Vertrauen"), 1, 2, 0);
        f.SetSection(f.Section("Geld"), 1, 0, 3);
        f.SetSection(f.Section("Leer"), 1, 0, 5);
        f.SetSection(f.Section("Mut"), 1, 0, 9);
        return f;
    }

    [Fact]
    public void Relevant_Top30_ByTopicSimilarity_HonoursHideUsedAndDates()
    {
        var journal = string.Concat(Enumerable.Range(1, 34).Select(i => $"- Note {i}\n"));
        var f = Create("- # Liebe\n\t- Liebe Text\n", notePages:
        [
            ("journals/2026_03_01.md", journal + "- Benutzt\n  used-in:: [[Buch]]\n"),
            ("journals/2025_01_01.md", "- Alt\n"),
        ]);
        f.SetSection(f.Section("Liebe"), 1, 0, 0);
        for (var i = 1; i <= 34; i++) f.SetNote($"Note {i}", 1, i * 0.01f, 0);
        f.SetNote("Benutzt", 1, 0, 0);
        f.SetNote("Alt", 1, 0, 0);

        var hits = f.Service.Relevant(f.Topic("Liebe"), f.Allow(new NoteFilter(true, new DateOnly(2026, 1, 1), null)));

        Assert.Equal(30, hits.Count);
        Assert.Equal(Enumerable.Range(1, 30).Select(i => $"Note {i}"), hits.Select(h => f.ContentOf(h.NoteBlockKey)));
        Assert.Equal(hits.OrderByDescending(h => h.Score), hits);
        Assert.Equal(VectorMath.Cosine([1, 0, 0], [1, 0.01f, 0]), hits[0].Score, 1e-6);
        Assert.Equal(3, f.Service.Relevant(f.Topic("Liebe"), f.Allow(NoFilter), 3).Count);
    }

    [Fact]
    public void Relevant_SectionTopic_IsHeadingPathPlusMeanOfOwnTextBlocks()
    {
        var f = Create("- # Liebe\n\t- Eins\n\t- Zwei\n\t- ## Tief\n\t\t- Unten\n",
            notePages: [("journals/2026_03_01.md", "- Nur Pfad\n- Nur Text\n- Beides\n")]);
        f.Index.Queries[EmbeddingText.ForHeadingPath(f.Book, f.Section("Liebe"))] = [1, 0, 0];
        f.Index.Queries[EmbeddingText.ForTextBlock(f.Text("Eins"))] = [0, 1, 0];
        f.Index.Queries[EmbeddingText.ForTextBlock(f.Text("Zwei"))] = [0, 3, 0];
        f.SetNote("Nur Pfad", 1, 0, 0);
        f.SetNote("Nur Text", 0, 1, 0);
        f.SetNote("Beides", 1, 1, 0);

        var hits = f.Service.Relevant(f.Topic("Liebe"), f.Allow(NoFilter));

        // topic = normalize((1,0,0) + mean((0,1,0),(0,3,0))) = normalize((1,2,0)); the sub-section's block is not part of it
        Assert.Equal(["Beides", "Nur Text", "Nur Pfad"], hits.Select(h => f.ContentOf(h.NoteBlockKey)));
        Assert.Equal(VectorMath.Cosine([1, 2, 0], [1, 1, 0]), hits[0].Score, 1e-6);
        Assert.DoesNotContain(EmbeddingText.ForTextBlock(f.Text("Unten")), f.Index.Embedded);
    }

    [Fact]
    public void Relevant_EmptySection_OnlyHeadingPath()
    {
        var f = SixSections(prologue: false);
        var path = EmbeddingText.ForHeadingPath(f.Book, f.Section("Leer"));
        f.Index.Queries[path] = [0, 0, 1];
        f.SetNote("Frage", 0, 0, 1);

        var hits = f.Service.Relevant(f.Topic("Leer"), f.Allow(NoFilter));

        Assert.Equal([path], f.Index.Embedded);
        Assert.Equal(1.0, Assert.Single(hits).Score, 1e-6);
    }

    [Fact]
    public void Relevant_ManuscriptCursor_UsesNeighbours()
    {
        var f = Create("- # Liebe\n\t- Eins\n\t- Zwei\n\t- Drei\n",
            notePages: [("journals/2026_03_01.md", "- Um Zwei\n- Sonst\n")]);
        var zwei = f.Text("Zwei");
        f.Index.Queries[EmbeddingText.ForCursor(f.Book, zwei)] = [0, 1, 0];
        f.SetSection(f.Section("Liebe"), 1, 0, 0);
        f.SetNote("Um Zwei", 0, 1, 0);
        f.SetNote("Sonst", 1, 0, 0);

        var manuscript = f.Service.Relevant(f.Topic("Liebe", zwei.Key, manuscript: true), f.Allow(NoFilter));
        var section = f.Service.Relevant(f.Topic("Liebe", zwei.Key, manuscript: false), f.Allow(NoFilter));
        var vanished = f.Service.Relevant(f.Topic("Liebe", Guid.NewGuid(), manuscript: true), f.Allow(NoFilter));

        Assert.Equal("Um Zwei", f.ContentOf(manuscript[0].NoteBlockKey));
        Assert.Equal(1.0, manuscript[0].Score, 1e-6);
        Assert.Equal("Sonst", f.ContentOf(section[0].NoteBlockKey)); // section view ignores the cursor
        Assert.Equal("Sonst", f.ContentOf(vanished[0].NoteBlockKey)); // a cursor block that is gone: section topic
    }

    [Fact]
    public void Relevant_OnlyConsultsTheIncludeDelegate()
    {
        var f = Create("- # Liebe\n\t- Liebe Text\n",
            notePages: [("journals/2026_03_01.md", "- Eins\n- Zwei\n- Drei\n")]);
        f.SetSection(f.Section("Liebe"), 1, 0, 0);
        f.SetNote("Eins", 1, 0, 0);
        f.SetNote("Zwei", 1, 1, 0);
        f.SetNote("Drei", 1, 2, 0);
        var asked = new List<Guid>();
        var zwei = f.Note("Zwei").Key;

        // the service has no repository: whatever the delegate says is the whole filter
        var hits = f.Service.Relevant(f.Topic("Liebe"), key =>
        {
            asked.Add(key);
            return key == zwei;
        });

        Assert.Equal(zwei, Assert.Single(hits).NoteBlockKey);
        Assert.Equal(f.Index.Notes.Keys.Order(), asked.Order());
    }

    [Fact]
    public void Relevant_UnknownSection_Empty()
    {
        var f = SixSections(prologue: false);

        Assert.Empty(f.Service.Relevant(new TopicRequest(f.Book, Guid.NewGuid(), null, false), f.Allow(NoFilter)));
    }

    [Fact]
    public void Relevant_NotAvailable_Empty()
    {
        var f = SixSections(prologue: false);
        f.Index.Ready = false;

        Assert.False(f.Service.IsAvailable);
        Assert.Empty(f.Service.Relevant(f.Topic("Liebe"), f.Allow(NoFilter)));
        Assert.Empty(f.Service.WhereTo(f.Note("Frage").Key, f.NoteText("Frage"), f.Book));
        Assert.Empty(f.Service.Placements(f.Note("Frage").Key, f.NoteText("Frage"), f.Book));
        Assert.Empty(f.Index.Embedded);
        Assert.Equal(0, f.Index.NearestCalls);

        f.Index.Ready = true;
        Assert.True(f.Service.IsAvailable);
        Assert.NotEmpty(f.Service.Relevant(f.Topic("Liebe"), f.Allow(NoFilter)));
    }

    [Fact]
    public void EmbedderFailure_EmptyForThatCall_NothingCached()
    {
        var f = SixSections(prologue: false);
        f.Index.EmbedFails = true;

        Assert.Empty(f.Service.Relevant(f.Topic("Liebe"), f.Allow(NoFilter)));
        Assert.Empty(f.Service.WhereTo(f.Note("Frage").Key, f.NoteText("Frage"), f.Book));
        Assert.Empty(f.Service.Placements(f.Note("Frage").Key, f.NoteText("Frage"), f.Book));

        f.Index.EmbedFails = false;
        Assert.NotEmpty(f.Service.Relevant(f.Topic("Liebe"), f.Allow(NoFilter)));
        Assert.NotEmpty(f.Service.WhereTo(f.Note("Frage").Key, f.NoteText("Frage"), f.Book));
        Assert.NotEmpty(f.Service.Placements(f.Note("Frage").Key, f.NoteText("Frage"), f.Book));
    }

    [Fact]
    public void WhereTo_Top5Sections_RootOnlyWithText()
    {
        var without = SixSections(prologue: false);
        var with = SixSections(prologue: true);

        var withoutHits = without.Service.WhereTo(without.Note("Frage").Key, without.NoteText("Frage"), without.Book);
        var withHits = with.Service.WhereTo(with.Note("Frage").Key, with.NoteText("Frage"), with.Book);

        // no prologue: the root is no candidate, the five real sections are listed
        Assert.Equal(["Liebe", "Vertrauen", "Geld", "Leer", "Mut"], withoutHits.Select(h => without.TitleOf(h.SectionKey)));
        // prologue: the root is a candidate (second place); the least similar section is cut off
        Assert.Equal(["Liebe", "Buch", "Vertrauen", "Geld", "Leer"], withHits.Select(h => with.TitleOf(h.SectionKey)));
        Assert.Equal(Guid.Empty, withHits[1].SectionKey);
        Assert.Equal(VectorMath.Cosine([1, 0, 0], [2, 0, 1]), withHits[1].Score, 1e-6);
        Assert.Equal(1.0, withHits[0].Score, 1e-6);
        Assert.Equal(2, with.Service.WhereTo(with.Note("Frage").Key, with.NoteText("Frage"), with.Book, 2).Count);
    }

    [Fact]
    public void WhereTo_NoteNotIndexed_EmbedsItsText()
    {
        var f = SixSections(prologue: false);
        var frage = f.Note("Frage");
        f.Index.Notes.Clear();
        f.Index.Queries[EmbeddingText.ForNote(frage)] = [1, 0, 0];

        var hits = f.Service.WhereTo(frage.Key, EmbeddingText.ForNote(frage), f.Book);

        Assert.Equal("Liebe", f.TitleOf(hits[0].SectionKey));
        Assert.Contains(EmbeddingText.ForNote(frage), f.Index.Embedded);
    }

    [Fact]
    public void Placements_Top3TextBlocks()
    {
        var f = SixSections(prologue: true);
        f.SetSection(f.Section("Liebe"), 1, 0, 0);
        f.Index.Queries[EmbeddingText.ForTextBlock(f.Text("Liebe zwei"))] = [1, 1, 0];

        var hits = f.Service.Placements(f.Note("Frage").Key, f.NoteText("Frage"), f.Book);

        Assert.Equal(["Liebe eins", "Prolog", "Liebe zwei"], hits.Select(h => f.Book.FindTextBlock(h.TextBlockKey)!.Text));
        Assert.Equal(hits.OrderByDescending(h => h.Score), hits);
        Assert.Equal(VectorMath.Cosine([1, 0, 0], [1, 1, 0]), hits[2].Score, 1e-6);
        Assert.Single(f.Service.Placements(f.Note("Frage").Key, f.NoteText("Frage"), f.Book, 1));
    }

    [Fact]
    public void ZeroTopicVector_Empty_NoNaN()
    {
        var f = SixSections(prologue: false);
        f.Index.Queries[EmbeddingText.ForHeadingPath(f.Book, f.Section("Leer"))] = [0, 0, 0];

        Assert.Empty(f.Service.Relevant(f.Topic("Leer"), f.Allow(NoFilter)));
        Assert.Equal(0, f.Index.NearestCalls);
    }

    [Fact]
    public void ZeroVectors_NeverReachTheResults_NoNaN()
    {
        var f = SixSections(prologue: false);
        f.SetSection(f.Section("Geld"), 0, 0, 0); // a section and a text block without meaning
        f.SetSection(f.Section("Mut"), 0, 0, 0);

        var sections = f.Service.WhereTo(f.Note("Frage").Key, f.NoteText("Frage"), f.Book);
        var placements = f.Service.Placements(f.Note("Frage").Key, f.NoteText("Frage"), f.Book);

        Assert.Equal(["Liebe", "Vertrauen", "Leer"], sections.Select(h => f.TitleOf(h.SectionKey)));
        Assert.Equal(["Liebe eins", "Liebe zwei", "Vertrauen Text"],
            placements.Select(h => f.Book.FindTextBlock(h.TextBlockKey)!.Text));
        Assert.All(sections.Select(h => h.Score).Concat(placements.Select(h => h.Score)), s => Assert.True(double.IsFinite(s)));

        f.Index.Notes[f.Note("Frage").Key] = [0, 0, 0]; // a note without meaning
        Assert.Empty(f.Service.WhereTo(f.Note("Frage").Key, f.NoteText("Frage"), f.Book));
        Assert.Empty(f.Service.Placements(f.Note("Frage").Key, f.NoteText("Frage"), f.Book));
    }

    [Fact]
    public void BookTexts_AreCachedPerText_OldestEvictedFirst()
    {
        var f = Create("- # A\n\t- A Text\n- # B\n\t- B Text\n", cacheCapacity: 2);
        f.SetSection(f.Section("A"), 1, 0, 0);
        f.SetSection(f.Section("B"), 0, 1, 0);

        f.Service.Relevant(f.Topic("A"), f.Allow(NoFilter));
        Assert.Equal(2, f.Index.Embedded.Count);
        f.Service.Relevant(f.Topic("B"), f.Allow(NoFilter)); // evicts both texts of A
        Assert.Equal(4, f.Index.Embedded.Count);
        f.Service.Relevant(f.Topic("B"), f.Allow(NoFilter));
        Assert.Equal(4, f.Index.Embedded.Count); // served from the cache
        f.Service.Relevant(f.Topic("A"), f.Allow(NoFilter));
        Assert.Equal(6, f.Index.Embedded.Count); // embedded again
    }

    [Fact]
    public void BookTexts_DefaultCapacity_KeepsFiveThousandTexts()
    {
        var blocks = string.Concat(Enumerable.Range(1, 5000).Select(i => $"\t- Block {i}\n"));
        var f = Create("- # Alles\n" + blocks, notePages: [("journals/2026_03_01.md", "- Frage\n")]);
        f.SetSection(f.Section("Alles"), 1, 0, 0);
        f.SetNote("Frage", 1, 0, 0);

        f.Service.Placements(f.Note("Frage").Key, f.NoteText("Frage"), f.Book);
        var first = f.Index.Embedded.Count;
        f.Service.Placements(f.Note("Frage").Key, f.NoteText("Frage"), f.Book);

        Assert.Equal(5000, first); // one text per block; the note's own vector is indexed
        Assert.Equal(5000, f.Index.Embedded.Count); // the second call used the cache only
    }

    [Fact]
    public void ConcurrentCalls_ShareTheCacheSafely()
    {
        var f = Create("- # A\n\t- A Text\n- # B\n\t- B Text\n- # C\n\t- C Text\n", cacheCapacity: 3,
            notePages: [("journals/2026_03_01.md", "- Frage\n")]);
        foreach (var title in new[] { "A", "B", "C" }) f.SetSection(f.Section(title), 1, 0, 0);
        f.SetNote("Frage", 1, 0, 0);
        var frage = f.Note("Frage");
        var topics = new[] { f.Topic("A"), f.Topic("B"), f.Topic("C") };
        var text = f.NoteText("Frage"); // everything from the vault is captured before going parallel
        var include = f.Allow(NoFilter);

        Parallel.For(0, 200, i =>
        {
            Assert.Single(f.Service.Relevant(topics[i % 3], include));
            Assert.Equal(3, f.Service.WhereTo(frage.Key, text, f.Book).Count);
        });
    }
}
