using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using NoteEvolution.AI.Semantic;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Repetitions;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests.State;

public sealed class RepetitionTrackerTests : IDisposable
{
    private const string Model = "test-model";

    private static readonly Guid IdA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid IdB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // A and B share a literal run (eins … neun); C and D mean the same (their fake vectors are equal); E stands alone.
    private const string TextA = "alpha eins zwei drei vier fuenf sechs sieben acht neun";
    private const string TextB = "beta eins zwei drei vier fuenf sechs sieben acht neun";
    private const string TextC = "gamma rot gruen blau gelb weiss schwarz braun";
    private const string TextD = "delta hund katze maus vogel fisch pferd kuh";
    private const string TextE = "epsilon berg tal fluss see meer insel strand";

    private readonly TestVault _vault = TestVault.Create();
    private readonly ListLogger _logger = new();
    private readonly FakeEmbed _embed = new();
    private readonly List<RepetitionTracker> _trackers = [];
    private VectorCache? _cache;

    /// <summary>What the trackers' <c>mayEmbed</c> answers: whether the note index is ready.</summary>
    private volatile bool _indexReady = true;

    /// <summary>Whether the trackers' cache function already gives the cache (null while the AI parts are still starting).</summary>
    private volatile bool _cacheAvailable = true;

    public void Dispose()
    {
        foreach (var tracker in _trackers)
        {
            tracker.Dispose();
        }

        _cache?.Dispose();
        _vault.Dispose();
    }

    private RepetitionTracker NewTracker(bool withEmbedder = true, AcceptedRepetitions? accepted = null)
    {
        var tracker = new RepetitionTracker(
            _logger,
            action =>
            {
                action();
                return Task.CompletedTask;
            },
            accepted ?? new AcceptedRepetitions(_vault.Root),
            withEmbedder ? _embed.Embed : null,
            () => _cacheAvailable ? _cache ??= VectorCache.Open(_vault.Root, Model, "book-vectors.db") : null,
            Model,
            () => _indexReady);
        _trackers.Add(tracker);
        return tracker;
    }

    private static Page Parse(string file = "Buch - Test.md") => LogseqParser.Parse(
        file,
        Encoding.UTF8.GetBytes(
            "type:: book\n\n" +
            $"- # Eins\n\t- {TextA}\n\t  id:: {IdA}\n\t- {TextB}\n\t  id:: {IdB}\n" +
            $"- # Zwei\n\t- {TextC}\n\t- {TextD}\n\t- {TextE}\n"));

    /// <summary>The page edited in place: the block that starts with <paramref name="marker"/> gets a new text, same key.</summary>
    private static Book Edit(Page page, string marker, string text)
    {
        var book = Book.Load(page);
        book.FindTextBlock(Key(book, marker))!.Block.SetContent(text);
        return Book.Load(page);
    }

    private static Guid Key(Book book, string marker) =>
        ComparisonText.Of(book).Single(block => block.Paragraphs[0].Plain.StartsWith(marker, StringComparison.Ordinal)).Key;

    private static IEnumerable<(RepetitionKind, Guid, Guid)> Pairs(RepetitionSet set) =>
        set.All.Select(r => (r.Kind, r.A, r.B));

    [Fact]
    public async Task Schedule_PublishesLiteralPairs_BeforeEmbeddingEnds()
    {
        var gate = _embed.Hold("alpha");
        var tracker = NewTracker();
        var changes = 0;
        tracker.Changed += () => changes++;
        var book = Book.Load(Parse());

        tracker.Schedule(book);
        await gate.Entered;

        var set = tracker.Of(book);
        Assert.Equal([(RepetitionKind.Literal, Key(book, "alpha"), Key(book, "beta"))], Pairs(set));
        Assert.False(set.Complete);
        Assert.True(set.Embedded < set.Total);
        Assert.Equal(5, set.Total);
        Assert.True(changes >= 1);

        gate.Release();
        await tracker.WhenIdle();
        Assert.True(tracker.Of(book).Complete);
    }

    [Fact]
    public async Task Schedule_Finishes_WithSemanticPairs_AndComplete()
    {
        var tracker = NewTracker();
        var book = Book.Load(Parse());

        tracker.Schedule(book);
        await tracker.WhenIdle();

        var set = tracker.Of(book);
        Assert.True(set.Complete);
        Assert.Equal(5, set.Embedded);
        Assert.Equal(5, set.Total);
        Assert.Equal(
            [
                (RepetitionKind.Literal, Key(book, "alpha"), Key(book, "beta")),
                (RepetitionKind.Semantic, Key(book, "gamma"), Key(book, "delta")),
            ],
            Pairs(set));
        Assert.Equal(5, _embed.Calls);
        Assert.All(_embed.Texts, text => Assert.StartsWith("query: ", text));
    }

    [Fact]
    public async Task NearPair_IsLoggedOncePerSession()
    {
        var tracker = NewTracker();
        var page = Parse();

        tracker.Schedule(Book.Load(page));
        await tracker.WhenIdle();
        tracker.Schedule(Book.Load(page));
        await tracker.WhenIdle();

        var line = Assert.Single(_logger.Messages, m => m.StartsWith("Repetition candidate", StringComparison.Ordinal));
        Assert.Equal(
            "Repetition candidate 1.000 listed=True: \"gamma rot gruen blau gelb weiss schwarz…\" ~ \"delta hund katze maus vogel fisch pferd…\"",
            line);
    }

    [Fact]
    public async Task SecondRun_EmbedsOnlyChangedBlocks()
    {
        var tracker = NewTracker();
        var page = Parse();
        tracker.Schedule(Book.Load(page));
        await tracker.WhenIdle();
        Assert.Equal(5, _embed.Calls);

        var edited = Edit(page, "epsilon", "epsilon wolke regen schnee wind sturm nebel blitz");
        tracker.Schedule(edited);
        await tracker.WhenIdle();

        Assert.Equal(6, _embed.Calls);
        Assert.True(tracker.Of(edited).Complete);

        // After a restart the cached vectors are used again: nothing is embedded.
        var restarted = NewTracker();
        restarted.Schedule(Book.Load(page));
        await restarted.WhenIdle();
        Assert.Equal(6, _embed.Calls);
    }

    [Fact]
    public async Task BlockEditedWhileEmbedding_OldVectorIsNotUsedForNewText()
    {
        var gate = _embed.Hold("delta");
        var tracker = NewTracker();
        var page = Parse();
        var before = Book.Load(page);

        tracker.Schedule(before);
        await gate.Entered;
        var after = Edit(page, "delta", "zeta hund katze maus vogel fisch pferd kuh");
        Assert.Equal(Key(before, "delta"), Key(after, "zeta"));
        tracker.Schedule(after);
        gate.Release();
        await tracker.WhenIdle();

        var set = tracker.Of(after);
        Assert.True(set.Complete);
        Assert.DoesNotContain(set.All, r => r.Kind == RepetitionKind.Semantic);
        Assert.Contains(_embed.Texts, text => text.StartsWith("query: zeta", StringComparison.Ordinal));
        Assert.Same(RepetitionSet.Empty, tracker.Of(before));
    }

    [Fact]
    public async Task BlockEditedAfterItsVectorWasStored_CountsAsMissing_UntilEmbeddedAgain()
    {
        var tracker = NewTracker();
        var page = Parse();
        var before = Book.Load(page);
        tracker.Schedule(before);
        await tracker.WhenIdle();
        Assert.Contains((RepetitionKind.Semantic, Key(before, "gamma"), Key(before, "delta")), Pairs(tracker.Of(before)));

        // The old text's vector is stored under the old text's hash: the new text has none until it is embedded.
        _indexReady = false;
        var after = Edit(page, "delta", "zeta hund katze maus vogel fisch pferd kuh");
        tracker.Schedule(after);
        await tracker.WhenIdle();

        var waiting = tracker.Of(after);
        Assert.Equal((4, 5, false), (waiting.Embedded, waiting.Total, waiting.Complete));
        Assert.DoesNotContain(waiting.All, r => r.Kind == RepetitionKind.Semantic);

        _indexReady = true;
        tracker.Schedule(after);
        await tracker.WhenIdle();

        var done = tracker.Of(after);
        Assert.True(done.Complete);
        Assert.DoesNotContain(done.All, r => r.Kind == RepetitionKind.Semantic);
        Assert.Equal(1, _embed.CallsFor("zeta"));
    }

    [Fact]
    public async Task FullyCachedBook_PublishesOnce_Complete()
    {
        var page = Parse();
        var first = NewTracker();
        first.Schedule(Book.Load(page));
        await first.WhenIdle();

        var restarted = NewTracker();
        var changes = 0;
        restarted.Changed += () => changes++;
        var book = Book.Load(page);
        restarted.Schedule(book);
        await restarted.WhenIdle();

        Assert.Equal(1, changes);
        Assert.True(restarted.Of(book).Complete);
        Assert.Equal(5, _embed.Calls);
    }

    [Theory]
    [InlineData(SemanticState.Ready, true)]
    [InlineData(SemanticState.Failed, true)]
    [InlineData(SemanticState.Indexing, false)]
    [InlineData(SemanticState.Off, false)]
    [InlineData(null, false)]
    public void Session_MayEmbedRepetitions_OnceTheNoteIndexIsSettled(SemanticState? state, bool may)
    {
        // A failed note index does not block the repetitions: each block embeds or is left out, so the set completes.
        Assert.Equal(may, VaultSession.MayEmbedRepetitions(state));
    }

    [Fact]
    public async Task NoteIndexFailed_EmbedderFailing_CompletesWithBlocksLeftOut()
    {
        foreach (var word in new[] { "alpha", "beta", "gamma", "delta", "epsilon" })
        {
            _embed.Failing.Add(word);
        }

        var tracker = NewTracker(); // mayEmbed answers yes, as the session does for a failed note index
        var book = Book.Load(Parse());

        tracker.Schedule(book);
        await tracker.WhenIdle();

        var set = tracker.Of(book);
        Assert.Equal(5, _embed.Calls);
        Assert.Equal((0, 0, true), (set.Embedded, set.Total, set.Complete));
        Assert.Contains(set.All, r => r.Kind == RepetitionKind.Literal);
    }

    [Fact]
    public async Task NoteIndexNotReady_EmbedsNothing_UntilScheduledWhenReady()
    {
        _indexReady = false;
        var tracker = NewTracker();
        var book = Book.Load(Parse());

        tracker.Schedule(book);
        await tracker.WhenIdle();

        var waiting = tracker.Of(book);
        Assert.Equal(0, _embed.Calls);
        Assert.Equal([(RepetitionKind.Literal, Key(book, "alpha"), Key(book, "beta"))], Pairs(waiting));
        Assert.Equal((0, 5, false), (waiting.Embedded, waiting.Total, waiting.Complete));

        _indexReady = true;
        tracker.Schedule(book);
        await tracker.WhenIdle();

        Assert.Equal(5, _embed.Calls);
        Assert.True(tracker.Of(book).Complete);
        Assert.Contains(tracker.Of(book).All, r => r.Kind == RepetitionKind.Semantic);
    }

    [Fact]
    public async Task NoteIndexBusyAgain_StopsEmbedding_UntilScheduledWhenReady()
    {
        _embed.OnEmbed = word =>
        {
            if (word == "beta")
            {
                _indexReady = false; // e.g. a saved page is embedded again
            }
        };
        var tracker = NewTracker();
        var book = Book.Load(Parse());

        tracker.Schedule(book);
        await tracker.WhenIdle();

        Assert.Equal(2, _embed.Calls);
        Assert.Equal((2, 5, false), (tracker.Of(book).Embedded, tracker.Of(book).Total, tracker.Of(book).Complete));

        _indexReady = true;
        tracker.Schedule(book);
        await tracker.WhenIdle();

        Assert.Equal(5, _embed.Calls);
        Assert.True(tracker.Of(book).Complete);
    }

    [Fact]
    public async Task CacheGetThrows_KeepsLiteralPairs_LogsOnce()
    {
        _cache = VectorCache.Open(_vault.Root, Model, "book-vectors.db");
        _cache.Dispose();
        var tracker = NewTracker();
        var page = Parse();

        tracker.Schedule(Book.Load(page));
        await tracker.WhenIdle();
        var book = Book.Load(page);
        tracker.Schedule(book);
        await tracker.WhenIdle();

        var set = tracker.Of(book);
        Assert.Equal([(RepetitionKind.Literal, Key(book, "alpha"), Key(book, "beta"))], Pairs(set));
        Assert.True(set.Complete);
        Assert.Equal(0, _embed.Calls);
        Assert.Single(_logger.Warnings);
    }

    [Fact]
    public async Task CachePutThrows_BlockLeftOut_RunGoesOn()
    {
        _embed.OnEmbed = _ => _cache!.Dispose();
        var tracker = NewTracker();
        var book = Book.Load(Parse());

        tracker.Schedule(book);
        await tracker.WhenIdle();

        var set = tracker.Of(book);
        Assert.Equal(5, _embed.Calls);
        Assert.True(set.IsFor(book));
        Assert.True(set.Complete);
        Assert.Contains(set.All, r => r.Kind == RepetitionKind.Literal);
        Assert.Single(_logger.Warnings);
    }

    [Fact]
    public async Task ThrowingChangedSubscriber_DoesNotAbortTheAnalysis()
    {
        var tracker = NewTracker();
        tracker.Changed += () => throw new InvalidOperationException("Kaputt.");
        var book = Book.Load(Parse());

        tracker.Schedule(book);
        await tracker.WhenIdle();

        Assert.True(tracker.Of(book).Complete);
        Assert.Equal(5, tracker.Of(book).Embedded);
        Assert.NotEmpty(_logger.Warnings);
    }

    [Fact]
    public async Task CancelExcept_StopsAnalysesOfPagesNoLongerShown()
    {
        var gate = _embed.Hold("alpha");
        var tracker = NewTracker();
        var first = Book.Load(Parse("Buch - Eins.md"));
        var second = Book.Load(Parse("Buch - Zwei.md"));

        tracker.Schedule(first);
        await gate.Entered;
        tracker.CancelExcept([second]);
        tracker.Schedule(second);
        gate.Release();
        await tracker.WhenIdle();

        Assert.Equal(6, _embed.Calls); // the first book's run ended after its blocked call
        Assert.Same(RepetitionSet.Empty, tracker.Of(first)); // forgotten with the page no longer shown
        Assert.True(tracker.Of(second).Complete);
    }

    [Fact]
    public async Task CacheNotYetAvailable_PublishesLiteralPairs_AsPendingWithTotal_ThenCompletesOnceAvailable()
    {
        _cacheAvailable = false;
        var tracker = NewTracker();
        var book = Book.Load(Parse());

        tracker.Schedule(book);
        await tracker.WhenIdle();

        var waiting = tracker.Of(book);
        Assert.Equal([(RepetitionKind.Literal, Key(book, "alpha"), Key(book, "beta"))], Pairs(waiting));
        Assert.Equal((0, 5, false), (waiting.Embedded, waiting.Total, waiting.Complete));
        Assert.Equal(0, _embed.Calls);

        _cacheAvailable = true;
        tracker.Schedule(book);
        await tracker.WhenIdle();

        Assert.True(tracker.Of(book).Complete);
        Assert.Equal(5, tracker.Of(book).Total);
    }

    [Fact]
    public async Task CancelExcept_ForgetsThePagesNoLongerShown_KeepsTheShownOnes()
    {
        var tracker = NewTracker();
        var first = Book.Load(Parse("Buch - Eins.md"));
        var second = Book.Load(Parse("Buch - Zwei.md"));
        tracker.Schedule(first);
        tracker.Schedule(second);
        await tracker.WhenIdle();
        Assert.True(tracker.Shown(first).Set.IsFor(first));

        tracker.CancelExcept([second]);

        Assert.Equal((RepetitionSet.Empty, false), tracker.Shown(first));
        Assert.Same(RepetitionSet.Empty, tracker.Of(first));
        Assert.True(tracker.Of(second).IsFor(second));
        Assert.True(tracker.Shown(second).Set.IsFor(second));
    }

    [Fact]
    public async Task PruneCache_RemovesVectorsOfTextsNoBookHasAnymore_KeepsLiveOnes_AndRunsOncePerSession()
    {
        var tracker = NewTracker();
        var page = Parse();
        tracker.Schedule(Book.Load(page));
        await tracker.WhenIdle();
        var stale = Hash(Book.Load(page), "epsilon"); // the text before the edit
        var edited = Edit(page, "epsilon", "epsilon wolke regen schnee wind sturm nebel blitz");
        var live = Hash(edited, "alpha");
        Assert.Equal(2, _cache!.Get([stale, live]).Count);

        tracker.PruneCache([edited]);
        await tracker.WhenIdle();

        var left = _cache.Get([stale, live]);
        Assert.False(left.ContainsKey(stale));
        Assert.True(left.ContainsKey(live));
        Assert.Equal(3, _cache.Get([Hash(edited, "beta"), Hash(edited, "gamma"), Hash(edited, "delta")]).Count);

        // Once per session: a vector stored afterwards survives a second call.
        _cache.Put([("later", "x", new float[] { 1f })]);
        tracker.PruneCache([edited]);
        await tracker.WhenIdle();
        Assert.True(_cache.Get(["later"]).ContainsKey("later"));
    }

    [Fact]
    public async Task PruneCache_WithoutCacheYet_RunsAgainLater()
    {
        _cacheAvailable = false;
        var tracker = NewTracker();
        var book = Book.Load(Parse());

        tracker.PruneCache([book]);
        await tracker.WhenIdle();
        _cacheAvailable = true;
        _cache = VectorCache.Open(_vault.Root, Model, "book-vectors.db");
        _cache.Put([("stale", "x", new float[] { 1f })]);
        tracker.PruneCache([book]);
        await tracker.WhenIdle();

        Assert.Empty(_cache.Get(["stale"]));
    }

    [Fact]
    public async Task PruneCache_CacheThrows_IsLoggedNotThrown()
    {
        _cache = VectorCache.Open(_vault.Root, Model, "book-vectors.db");
        _cache.Dispose();
        var tracker = NewTracker();

        tracker.PruneCache([Book.Load(Parse())]);
        await tracker.WhenIdle();

        Assert.Single(_logger.Warnings);
    }

    private static string Hash(Book book, string marker) =>
        NoteEvolution.AI.Embeddings.EmbeddingText.Hash(
            Model, NoteEvolution.AI.Embeddings.EmbeddingText.ForRepetition(
                ComparisonText.Of(book).Single(b => b.Paragraphs[0].Plain.StartsWith(marker, StringComparison.Ordinal))));

    [Fact]
    public async Task EmbedNull_LeavesBlockOut_AndLogsOnce()
    {
        _embed.Failing.Add("epsilon");
        var tracker = NewTracker();
        var page = Parse();

        tracker.Schedule(Book.Load(page));
        await tracker.WhenIdle();
        var book = Book.Load(page);
        tracker.Schedule(book);
        await tracker.WhenIdle();

        var set = tracker.Of(book);
        Assert.True(set.Complete);
        Assert.Equal(4, set.Embedded);
        Assert.Equal(1, _embed.CallsFor("epsilon"));
        Assert.Single(_logger.Warnings);
        Assert.Contains(set.All, r => r.Kind == RepetitionKind.Semantic);
    }

    [Fact]
    public async Task NoEmbedder_OnlyLiteral_AndComplete()
    {
        var tracker = NewTracker(withEmbedder: false);
        var book = Book.Load(Parse());

        tracker.Schedule(book);
        await tracker.WhenIdle();

        var set = tracker.Of(book);
        Assert.Equal([(RepetitionKind.Literal, Key(book, "alpha"), Key(book, "beta"))], Pairs(set));
        Assert.Equal(0, set.Embedded);
        Assert.Equal(0, set.Total);
        Assert.True(set.Complete);
        Assert.Null(_cache);
    }

    [Fact]
    public async Task Of_OtherSnapshot_IsEmpty_UntilComputed()
    {
        var tracker = NewTracker();
        var page = Parse();
        var first = Book.Load(page);
        tracker.Schedule(first);
        await tracker.WhenIdle();

        var second = Book.Load(page);
        Assert.Same(RepetitionSet.Empty, tracker.Of(second));
        Assert.Same(RepetitionSet.Empty, tracker.Of(null));
        Assert.True(tracker.Of(first).IsFor(first));

        tracker.Schedule(second);
        await tracker.WhenIdle();

        Assert.True(tracker.Of(second).IsFor(second));
        Assert.Same(RepetitionSet.Empty, tracker.Of(first));
    }

    [Fact]
    public async Task Refresh_AfterAccept_MarksPairAccepted_WithoutEmbedding()
    {
        var accepted = new AcceptedRepetitions(_vault.Root);
        var tracker = NewTracker(accepted: accepted);
        var book = Book.Load(Parse());
        tracker.Schedule(book);
        await tracker.WhenIdle();
        Assert.False(tracker.Of(book).All.Single(r => r.Kind == RepetitionKind.Literal).Accepted);
        var changes = 0;
        tracker.Changed += () => changes++;

        accepted.Set(book.LinkName, IdA, IdB, accepted: true, new HashSet<Guid> { IdA, IdB });
        tracker.Refresh();

        var set = tracker.Of(book);
        Assert.True(set.IsFor(book));
        Assert.True(set.All.Single(r => r.Kind == RepetitionKind.Literal).Accepted);
        Assert.Empty(set.Of(Key(book, "alpha")));
        Assert.Equal(5, _embed.Calls);
        Assert.Equal(1, changes);
    }

    /// <summary>
    /// Embeds by the first word of the text (after the role prefix): "gamma" and "delta" give the same vector, every other
    /// word its own one. Can hold a call until released and fail for given words.
    /// </summary>
    private sealed class FakeEmbed
    {
        private readonly ConcurrentDictionary<string, Gate> _gates = new();
        private int _calls;

        public ConcurrentQueue<string> Texts { get; } = new();

        public ConcurrentBag<string> Failing { get; } = [];

        public int Calls => Volatile.Read(ref _calls);

        public int CallsFor(string word) => Texts.Count(text => Word(text) == word);

        /// <summary>Called with the first word on every call.</summary>
        public Action<string>? OnEmbed { get; set; }

        public Gate Hold(string word) => _gates[word] = new Gate();

        public float[]? Embed(string text)
        {
            Interlocked.Increment(ref _calls);
            Texts.Enqueue(text);
            var word = Word(text);
            OnEmbed?.Invoke(word);
            if (_gates.TryRemove(word, out var gate))
            {
                gate.Wait();
            }

            if (Failing.Contains(word))
            {
                return null;
            }

            var vector = new float[Words.Length];
            vector[Array.IndexOf(Words, word == "delta" ? "gamma" : word)] = 1f;
            return vector;
        }

        private static readonly string[] Words = ["alpha", "beta", "gamma", "epsilon", "zeta"];

        private static string Word(string text) => text["query: ".Length..].Split(' ', '\n')[0];
    }

    private sealed class Gate
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Release() => _released.SetResult();

        public void Wait()
        {
            _entered.SetResult();
            _released.Task.Wait();
        }
    }

    private sealed class ListLogger : ILogger
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ConcurrentQueue<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            Messages.Enqueue(message);
            if (logLevel >= LogLevel.Warning)
            {
                Warnings.Enqueue(message);
            }
        }
    }
}
