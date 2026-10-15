using NoteEvolution.AI.Embeddings;
using NoteEvolution.AI.Relevance;
using NoteEvolution.AI.Semantic;
using NoteEvolution.AI.Understanding;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Vaults;
using NoteEvolution.TestSupport;

namespace NoteEvolution.AI.Tests.Relevance;

/// <summary>The two-stage relevance with the language model's understanding (summaries and core statements), with fixed vectors.</summary>
public sealed class RelevanceUnderstandingTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    /// <summary>An index whose embeddings are looked up in dictionaries, so tests choose every vector.</summary>
    private sealed class StubIndex : ISemanticIndex
    {
        public Dictionary<Guid, float[]> Notes { get; } = [];
        public Dictionary<string, float[]> Queries { get; } = [];

        public SemanticStatus Status => new(SemanticState.Ready, 0, 0, null);
        public event Action? StatusChanged { add { } remove { } }
        public bool IsReady => true;

        public Task RebuildAsync(INoteRepository notes, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> UpdatePageAsync(INoteRepository notes, string pagePath, CancellationToken ct) =>
            throw new NotSupportedException();

        public IReadOnlyList<(Guid Key, float Score)> Nearest(float[] query, int k, Func<Guid, bool> include) =>
            [.. Notes.Where(n => include(n.Key))
                .Select(n => (n.Key, Score: VectorMath.Cosine(query, n.Value)))
                .OrderByDescending(n => n.Score).Take(k)];

        public float[]? VectorOf(Guid noteKey) => Notes.GetValueOrDefault(noteKey);

        public float[]? EmbedQuery(string text) => Queries[text];
    }

    /// <summary>Summaries per heading key and core-statement vectors per note key, chosen by the test.</summary>
    private sealed class FakeUnderstanding : IUnderstanding
    {
        public Dictionary<Guid, (string Text, float[] Vector)> Headings { get; } = [];
        public Dictionary<Guid, float[]> Cores { get; } = [];

        public (string Text, float[] Vector)? Heading(Book book, OutlineNode heading) =>
            Headings.TryGetValue(heading.Key, out var summary) ? summary : null;

        public float[]? NoteCore(Guid noteKey) => Cores.GetValueOrDefault(noteKey);

        public IReadOnlyList<(Guid Key, float[] Vector)> NoteCores() => [.. Cores.Select(c => (c.Key, c.Value))];
    }

    private sealed class Fixture(Book book, NoteRepository notes, StubIndex index, FakeUnderstanding understanding)
    {
        public Book Book { get; } = book;
        public NoteRepository Notes { get; } = notes;
        public StubIndex Index { get; } = index;
        public FakeUnderstanding Understanding { get; } = understanding;
        public RelevanceService Service { get; } = new(index, understanding);
        public RelevanceService Plain { get; } = new(index, understanding: null);

        public OutlineNode Section(string title) => Walk(Book.Root).Single(n => n.Title == title);

        public NoteBlock Note(string content) => Notes.All().Single(n => n.Block.Content == content);

        public TextBlock Text(string content) =>
            Walk(Book.Root).SelectMany(n => n.TextBlocks).Single(t => t.Text == content);

        public string NoteText(string content) => EmbeddingText.ForNote(Note(content));

        public Func<Guid, bool> AllowAll() => Notes.All().Select(n => n.Key).ToHashSet().Contains;

        public string TitleOf(Guid sectionKey) => Book.FindNode(sectionKey)!.Title;

        public string TextOf(Guid textBlockKey) => Book.FindTextBlock(textBlockKey)!.Text;

        public string ContentOf(Guid noteKey) => Notes.Get(noteKey)!.Block.Content;

        /// <summary>Gives the section's heading path and its own text blocks the direction <paramref name="v"/> (today's section vector).</summary>
        public void SetSection(string title, params float[] v)
        {
            var node = Section(title);
            Index.Queries[EmbeddingText.ForHeadingPath(Book, node)] = v;
            foreach (var tb in node.TextBlocks) Index.Queries[EmbeddingText.ForTextBlock(tb)] = v;
        }

        public void SetSummary(string title, params float[] v) => Understanding.Headings[Section(title).Key] = ("Sum " + title, v);

        public void SetText(string content, params float[] v) => Index.Queries[EmbeddingText.ForTextBlock(Text(content))] = v;

        public void SetNote(string content, params float[] v) => Index.Notes[Note(content).Key] = v;

        public void SetCore(string content, params float[] v) => Understanding.Cores[Note(content).Key] = v;

        public IReadOnlyList<PlacementHit> Placements(RelevanceService service, int limit = 5) =>
            service.Placements(Note("Frage").Key, NoteText("Frage"), Book, limit);

        public IReadOnlyList<TopicHit> Topics(int limit = 3) =>
            Service.Topics(Note("Frage").Key, NoteText("Frage"), Book, limit);

        private static IEnumerable<OutlineNode> Walk(OutlineNode node) =>
            node.Children.SelectMany(Walk).Prepend(node);
    }

    private Fixture Create(string bookBody, string journal = "- Frage\n")
    {
        var tv = TestVault.Create(("pages/Buch.md", "title:: Buch\ntype:: book\n\n" + bookBody), ("journals/2026_03_01.md", journal));
        _disposables.Add(tv);
        var vault = tv.Open();
        return new Fixture(vault.Books.Single(), new NoteRepository(vault), new StubIndex(), new FakeUnderstanding());
    }

    [Fact]
    public void WithoutUnderstanding_SameResultsAsToday()
    {
        // Placements_Top3TextBlocks: every text block of the book is a candidate
        var f = Create("- Prolog\n- # Liebe\n\t- Liebe eins\n\t- Liebe zwei\n\t- ## Vertrauen\n\t\t- Vertrauen Text\n" +
                       "- # Geld\n\t- Geld Text\n- # Mut\n\t- Mut Text\n",
            "- Frage\n");
        f.SetNote("Frage", 1, 0, 0);
        f.Index.Queries[EmbeddingText.ForHeadingPath(f.Book, f.Book.Root)] = [2, 0, 1];
        f.Index.Queries[EmbeddingText.ForTextBlock(f.Text("Prolog"))] = [2, 0, 1];
        f.SetSection("Liebe", 1, 0, 0);
        f.SetText("Liebe zwei", 1, 1, 0);
        f.SetSection("Vertrauen", 1, 2, 0);
        f.SetSection("Geld", 1, 0, 3);
        f.SetSection("Mut", 1, 0, 9);

        var placements = f.Placements(f.Plain, 3);

        Assert.Equal(["Liebe eins", "Prolog", "Liebe zwei"], placements.Select(h => f.TextOf(h.TextBlockKey)));
        Assert.Equal(VectorMath.Cosine([1, 0, 0], [1, 1, 0]), placements[2].Score, 1e-6);
        Assert.All(placements, h => Assert.Equal(Guid.Empty, h.SectionKey));

        // Relevant_SectionTopic_IsHeadingPathPlusMeanOfOwnTextBlocks
        var g = Create("- # Liebe\n\t- Eins\n\t- Zwei\n\t- ## Tief\n\t\t- Unten\n", "- Nur Pfad\n- Nur Text\n- Beides\n");
        g.Index.Queries[EmbeddingText.ForHeadingPath(g.Book, g.Section("Liebe"))] = [1, 0, 0];
        g.Index.Queries[EmbeddingText.ForTextBlock(g.Text("Eins"))] = [0, 1, 0];
        g.Index.Queries[EmbeddingText.ForTextBlock(g.Text("Zwei"))] = [0, 3, 0];
        g.SetNote("Nur Pfad", 1, 0, 0);
        g.SetNote("Nur Text", 0, 1, 0);
        g.SetNote("Beides", 1, 1, 0);

        var hits = g.Plain.Relevant(new TopicRequest(g.Book, g.Section("Liebe").Key, null, false), g.AllowAll());

        Assert.Equal(["Beides", "Nur Text", "Nur Pfad"], hits.Select(h => g.ContentOf(h.NoteBlockKey)));
        Assert.Equal(VectorMath.Cosine([1, 2, 0], [1, 1, 0]), hits[0].Score, 1e-6);
    }

    [Fact]
    public void Topics_ParentWinsThroughChild_0_9Rule()
    {
        var f = Create("- # A\n\t- A Text\n\t- ## A1\n\t\t- A1 Text\n\t\t- ### A1x\n\t\t\t- A1x Text\n" +
                       "\t- ## A2\n\t\t- A2 Text\n- # B\n\t- B Text\n");
        f.SetNote("Frage", 1, 0, 0);
        f.SetSummary("A", 0, 1, 0);
        f.SetSummary("A1", 1, 1, 0);
        f.SetSummary("A1x", 1, 0, 0);
        f.SetSummary("A2", 0, 0, 1);
        f.SetSection("B", 0.6f, 0.8f, 0); // no summary: today's section vector

        var top3 = f.Topics();
        var all = f.Topics(10);

        // A1 = max(0.71, 0.9 × 1); A = max(0, 0.9 × 0.9): the parent beats B (0.6) only through its descendants
        Assert.Equal(["A1x", "A1", "A"], top3.Select(h => f.TitleOf(h.SectionKey)));
        Assert.Equal(1.0, top3[0].Score, 1e-6);
        Assert.Equal(0.9, top3[1].Score, 1e-6);
        Assert.Equal(0.81, top3[2].Score, 1e-6);
        Assert.Equal("Sum A1", top3[1].Summary);
        Assert.Equal(["A1x", "A1", "A", "B", "A2"], all.Select(h => f.TitleOf(h.SectionKey))); // the root has no text: no topic
        Assert.Equal(0.6, all[3].Score, 1e-6);
        Assert.Null(all[3].Summary);
    }

    /// <summary>
    /// Schlaf (with Träume below), Ruhe and Sport; the note fits the topics Schlaf, Träume, Ruhe, but the single most
    /// similar text block is in Sport.
    /// </summary>
    private Fixture SleepBook()
    {
        var f = Create("- # Schlaf\n\t- Schlaf Text\n\t- ## Träume\n\t\t- Traum Text\n- # Ruhe\n\t- Ruhe Text\n" +
                       "- # Sport\n\t- Lauf Text\n");
        f.SetNote("Frage", 1, 0, 0);
        f.SetSummary("Schlaf", 1, 0, 0);
        f.SetSummary("Träume", 0.9f, 0.43589f, 0);
        f.SetSummary("Ruhe", 0.8f, 0.6f, 0);
        f.SetSummary("Sport", 0, 1, 0);
        f.SetText("Schlaf Text", 0.6f, 0.8f, 0);
        f.SetText("Traum Text", 0.7f, 0.71414f, 0);
        f.SetText("Ruhe Text", 0.5f, 0, 0.86603f);
        f.SetText("Lauf Text", 0.95f, 0.31225f, 0);
        return f;
    }

    [Fact]
    public void Placements_OtherAspect_LandsInRightTopic_ThoughAnotherBlockIsMoreSimilar()
    {
        var f = SleepBook();

        var hits = f.Placements(f.Service);
        var today = f.Placements(f.Plain);

        Assert.Equal(["Traum Text", "Schlaf Text", "Ruhe Text"], hits.Select(h => f.TextOf(h.TextBlockKey)));
        Assert.Equal(VectorMath.Cosine([1, 0, 0], [0.7f, 0.71414f, 0]), hits[0].Score, 1e-6);
        Assert.Equal("Lauf Text", f.TextOf(today[0].TextBlockKey)); // one stage: the single most similar block wins
    }

    [Fact]
    public void Placements_SectionKeyIsTheTopic()
    {
        var sleep = SleepBook();

        var nested = sleep.Placements(sleep.Service);

        // a block in two chosen topics belongs to the innermost one
        Assert.Equal(["Träume", "Schlaf", "Ruhe"], nested.Select(h => sleep.TitleOf(h.SectionKey)));

        var f = Create("- # Schlaf\n\t- Schlaf Text\n\t- ## Träume\n\t\t- Traum Text\n- # Geld\n\t- Geld Text\n" +
                       "- # Mut\n\t- Mut Text\n- # Ruhe\n\t- Ruhe Text\n");
        f.SetNote("Frage", 1, 0, 0);
        f.SetSummary("Schlaf", 1, 0, 0);
        f.SetSummary("Träume", 0, 1, 0); // not among the 3 topics itself
        f.SetSummary("Geld", 0.5f, 0.86603f, 0);
        f.SetSummary("Mut", 0.4f, 0.91652f, 0);
        f.SetSummary("Ruhe", 0.3f, 0.95394f, 0);
        f.SetText("Schlaf Text", 0.5f, 0.5f, 0);
        f.SetText("Traum Text", 1, 0, 0);
        f.SetText("Geld Text", 0.1f, 1, 0);
        f.SetText("Mut Text", 0.2f, 1, 0);
        f.SetText("Ruhe Text", 1, 0, 0);

        var hits = f.Placements(f.Service);

        // the sub-section's block counts for its chosen parent topic; Ruhe is the fourth topic and stays out
        Assert.Equal(["Traum Text", "Schlaf Text", "Mut Text", "Geld Text"], hits.Select(h => f.TextOf(h.TextBlockKey)));
        Assert.Equal(["Schlaf", "Schlaf", "Mut", "Geld"], hits.Select(h => f.TitleOf(h.SectionKey)));
    }

    [Fact]
    public void Placements_AtMostLimit()
    {
        var blocks = string.Concat(Enumerable.Range(1, 8).Select(i => $"\t- Block {i}\n"));
        var f = Create("- # Alles\n" + blocks + "- # Sonst\n\t- Sonst Text\n");
        f.SetNote("Frage", 1, 0, 0);
        f.SetSummary("Alles", 1, 0, 0);
        f.SetSummary("Sonst", 0, 1, 0);
        for (var i = 1; i <= 8; i++) f.SetText($"Block {i}", 1, i * 0.1f, 0);
        f.SetText("Sonst Text", 1, 0, 0);

        var five = f.Placements(f.Service);
        var two = f.Placements(f.Service, 2);

        Assert.Equal(5, five.Count);
        Assert.Equal(five.OrderByDescending(h => h.Score), five);
        Assert.Equal(["Sonst Text", "Block 1", "Block 2", "Block 3", "Block 4"], five.Select(h => f.TextOf(h.TextBlockKey)));
        Assert.Equal(["Sonst Text", "Block 1"], two.Select(h => f.TextOf(h.TextBlockKey)));
    }

    [Fact]
    public void NoteVector_MeanOfCoreAndText()
    {
        var f = Create("- # A\n\t- A Text\n- # B\n\t- B Text\n");
        f.SetNote("Frage", 1, 0, 0);
        f.SetSummary("A", 1, 1, 0);
        f.SetSummary("B", 1, 0, 0);

        var textOnly = f.Topics();
        f.SetCore("Frage", 0, 1, 0);
        var withCore = f.Topics();

        Assert.Equal(["B", "A"], textOnly.Select(h => f.TitleOf(h.SectionKey)));
        Assert.Equal(1.0, textOnly[0].Score, 1e-6);
        // note vector = normalize(mean((1,0,0), (0,1,0))) = (1,1,0)/√2
        Assert.Equal(["A", "B"], withCore.Select(h => f.TitleOf(h.SectionKey)));
        Assert.Equal(1.0, withCore[0].Score, 1e-6);
        Assert.Equal(Math.Sqrt(0.5), withCore[1].Score, 1e-6);
    }

    [Fact]
    public void Relevant_UsesSectionSummaryAndQuarterOfParent()
    {
        var f = Create("- # Liebe\n\t- Liebe Text\n\t- ## Vertrauen\n\t\t- Vertrauen Text\n", "- Genau\n- Heute\n");
        f.SetSection("Liebe", 1, 0, 0);
        f.SetSection("Vertrauen", 1, 0, 0);
        f.SetSummary("Liebe", 0, 0, 1);
        f.SetSummary("Vertrauen", 0, 1, 0);
        // normalize(normalize(mean(summary (0,1,0), today (1,0,0))) + 0.25 × parent summary (0,0,1))
        var h = (float)Math.Sqrt(0.5);
        float[] expected = [h, h, 0.25f];
        f.SetNote("Genau", expected);
        f.SetNote("Heute", 1, 0, 0);

        var vertrauen = f.Service.Relevant(new TopicRequest(f.Book, f.Section("Vertrauen").Key, null, false), f.AllowAll());
        var liebe = f.Service.Relevant(new TopicRequest(f.Book, f.Section("Liebe").Key, null, false), f.AllowAll());

        Assert.Equal(["Genau", "Heute"], vertrauen.Select(x => f.ContentOf(x.NoteBlockKey)));
        Assert.Equal(1.0, vertrauen[0].Score, 1e-6);
        Assert.Equal(VectorMath.Cosine(expected, [1, 0, 0]), vertrauen[1].Score, 1e-6);
        // Liebe has no parent summary (the root has none): mean of its summary and today's topic only
        Assert.Equal(Math.Sqrt(0.5), liebe.Single(x => f.ContentOf(x.NoteBlockKey) == "Heute").Score, 1e-6);
    }

    [Fact]
    public void Relevant_ComparesNotesThroughCoreAndText()
    {
        var f = Create("- # Liebe\n\t- Liebe Text\n", "- N1\n- N2\n- N3\n- N4\n- Kern\n- Versteckt\n");
        f.SetSection("Liebe", 1, 0, 0);
        foreach (var n in new[] { "N1", "N2", "N3", "N4" }) f.SetNote(n, 0.6f, 0.8f, 0);
        f.SetCore("N1", 0, 1, 0); // its core pulls it away from the topic
        f.SetNote("Kern", 0, 0, 1);
        f.SetCore("Kern", 1, 0, 0); // found only through its core statement
        f.SetNote("Versteckt", 0, 0, 1);
        f.SetCore("Versteckt", 1, 0, 0);
        var versteckt = f.Note("Versteckt").Key;
        bool Include(Guid key) => key != versteckt;

        var one = f.Service.Relevant(new TopicRequest(f.Book, f.Section("Liebe").Key, null, false), Include, 1);
        var three = f.Service.Relevant(new TopicRequest(f.Book, f.Section("Liebe").Key, null, false), Include, 3);

        Assert.Equal("Kern", f.ContentOf(Assert.Single(one).NoteBlockKey));
        Assert.Equal(Math.Sqrt(0.5), one[0].Score, 1e-6);
        Assert.Equal(3, three.Count);
        Assert.Equal("Kern", f.ContentOf(three[0].NoteBlockKey));
        Assert.All(three.Skip(1), x => Assert.Equal(0.6, x.Score, 1e-6));
        Assert.DoesNotContain(three, x => f.ContentOf(x.NoteBlockKey) is "N1" or "Versteckt");
    }
}
