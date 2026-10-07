using System.Collections.Concurrent;
using NoteEvolution.AI.Embeddings;
using NoteEvolution.AI.Semantic;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Vaults;
using NoteEvolution.TestSupport;

namespace NoteEvolution.AI.Tests.Semantic;

public sealed class SemanticIndexTests
{
    private const string Model = "test-model";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly FakeEmbedder _embedder = new();

    /// <summary>"- Word 1" … "- Word count", one note per line.</summary>
    private static string Lines(string word, int count) =>
        string.Concat(Enumerable.Range(1, count).Select(i => $"- {word} {i}\n"));

    /// <summary>40 notes: 30 on Apfel, 10 on Birne.</summary>
    private static TestVault TwoPages() =>
        TestVault.Create(("pages/Apfel.md", Lines("Apfel", 30)), ("pages/Birne.md", Lines("Birne", 10)));

    private SemanticIndex NewIndex(TestVault tv, out VectorCache cache)
    {
        cache = VectorCache.Open(tv.Root, Model);
        return new SemanticIndex(_embedder, cache, Model);
    }

    private static string HashOf(NoteBlock note) => EmbeddingText.Hash(Model, EmbeddingText.ForNote(note));

    private static string[] HashesOf(INoteRepository notes) => [.. notes.All().Select(HashOf)];

    /// <summary>Rewrites Birne.md with <paramref name="oldLine"/> replaced and puts the parsed page into the vault.</summary>
    private static string RewriteBirne(TestVault tv, Vault vault, string oldLine, string newLine)
    {
        var path = Path.Combine(tv.Root, "pages", "Birne.md");
        File.WriteAllText(path, Lines("Birne", 10).Replace(oldLine, newLine));
        vault.ReplacePage(LogseqParser.Parse(path, File.ReadAllBytes(path)));
        return path;
    }

    /// <summary>Blocks the embedder's <paramref name="call"/>-th call until <paramref name="release"/> is set.</summary>
    private void BlockCall(int call, ManualResetEventSlim entered, ManualResetEventSlim release) =>
        _embedder.OnEmbed = (n, _) =>
        {
            if (n != call) return;
            entered.Set();
            release.Wait(Timeout);
        };

    [Fact]
    public async Task Rebuild_EmbedsAllNotes_ProgressReachesTotal_Ready()
    {
        using var tv = TwoPages();
        var notes = new NoteRepository(tv.Open());
        using var index = NewIndex(tv, out _);
        var seen = new ConcurrentQueue<SemanticStatus>();
        index.StatusChanged += () => seen.Enqueue(index.Status);
        Assert.Equal(SemanticState.Off, index.Status.State);
        Assert.False(index.IsReady);

        await index.RebuildAsync(notes, CancellationToken.None);

        Assert.Equal(new SemanticStatus(SemanticState.Ready, 40, 40, null), index.Status);
        Assert.True(index.IsReady);
        Assert.Equal(
            [(SemanticState.Indexing, 0), (SemanticState.Indexing, 16), (SemanticState.Indexing, 32),
             (SemanticState.Indexing, 40), (SemanticState.Ready, 40)],
            seen.Select(s => (s.State, s.Done)));
        Assert.Equal([16, 16, 8], _embedder.Batches.Select(b => b.Count));
        var all = notes.All().ToList();
        Assert.Equal(all.Select(EmbeddingText.ForNote).Order(), _embedder.Texts.Order());
        Assert.Equal(_embedder.Texts.Select(t => t.Length).Order(), _embedder.Texts.Select(t => t.Length)); // sorted by length
        foreach (var note in all)
            Assert.Equal(FakeEmbedder.VectorFor(EmbeddingText.ForNote(note)), index.VectorOf(note.Key));
    }

    [Fact]
    public async Task Rebuild_Twice_SecondUsesCacheOnly()
    {
        using var tv = TwoPages();
        using (var first = NewIndex(tv, out _))
            await first.RebuildAsync(new NoteRepository(tv.Open()), CancellationToken.None);

        // the next start of the app: fresh runtime keys, fresh embedder, same cache file
        var embedder = new FakeEmbedder();
        using var second = new SemanticIndex(embedder, VectorCache.Open(tv.Root, Model), Model);
        var notes = new NoteRepository(tv.Open());
        await second.RebuildAsync(notes, CancellationToken.None);

        Assert.Equal(0, embedder.Calls);
        Assert.Equal(new SemanticStatus(SemanticState.Ready, 40, 40, null), second.Status);
        foreach (var note in notes.All())
            Assert.Equal(FakeEmbedder.VectorFor(EmbeddingText.ForNote(note)), second.VectorOf(note.Key));
    }

    [Fact]
    public async Task Rebuild_ChangedNote_OnlyThatOneReembedded()
    {
        using var tv = TwoPages();
        using var index = NewIndex(tv, out var cache);
        var before = new NoteRepository(tv.Open());
        await index.RebuildAsync(before, CancellationToken.None);
        var old = before.All().Single(n => n.Block.Content == "Birne 3");

        var vault = tv.Open();
        RewriteBirne(tv, vault, "- Birne 3\n", "- Birne drei\n");
        var after = new NoteRepository(vault);
        await index.RebuildAsync(after, CancellationToken.None);

        Assert.Equal(["passage: Birne drei"], _embedder.Texts.Skip(40));
        Assert.Equal(new SemanticStatus(SemanticState.Ready, 40, 40, null), index.Status);
        var changed = after.All().Single(n => n.Block.Content == "Birne drei");
        Assert.Equal(FakeEmbedder.VectorFor("passage: Birne drei"), index.VectorOf(changed.Key));
        Assert.Null(index.VectorOf(old.Key)); // keys of the previous vault object are gone
        Assert.Empty(cache.Get([HashOf(old)])); // pruned
        Assert.Equal(40, cache.Get(HashesOf(after)).Count);
    }

    [Fact]
    public async Task UpdatePage_RemovedBlock_NotReturnedAnymore()
    {
        using var tv = TwoPages();
        var vault = tv.Open();
        var notes = new NoteRepository(vault);
        using var index = NewIndex(tv, out _);
        await index.RebuildAsync(notes, CancellationToken.None);
        var removed = notes.All().Single(n => n.Block.Content == "Birne 3");
        var removedVector = index.VectorOf(removed.Key)!;

        var path = RewriteBirne(tv, vault, "- Birne 3\n", "");
        await index.UpdatePageAsync(notes, path.ToUpperInvariant(), CancellationToken.None); // paths compare ignoring case

        Assert.Null(index.VectorOf(removed.Key));
        var hits = index.Nearest(removedVector, 100, _ => true);
        Assert.Equal(39, hits.Count);
        Assert.All(hits, hit => Assert.NotNull(notes.Get(hit.Key)));
        Assert.DoesNotContain(hits, hit => notes.Get(hit.Key)!.Block.Content == "Birne 3");
        Assert.Equal(3, _embedder.Calls); // the page's other blocks came from the cache
        Assert.Equal(SemanticState.Ready, index.Status.State);
    }

    [Fact]
    public async Task UpdatePage_ReportsWhetherTheIndexChanged()
    {
        using var tv = TwoPages();
        var vault = tv.Open();
        var notes = new NoteRepository(vault);
        using var index = NewIndex(tv, out _);
        await index.RebuildAsync(notes, CancellationToken.None);
        var path = Path.Combine(tv.Root, "pages", "Birne.md");

        Assert.False(await index.UpdatePageAsync(notes, path, CancellationToken.None)); // nothing changed on the page
        RewriteBirne(tv, vault, "- Birne 3\n", "- Birne drei\n");
        Assert.True(await index.UpdatePageAsync(notes, path, CancellationToken.None)); // one note re-embedded
        RewriteBirne(tv, vault, "- Birne 3\n", "");
        Assert.True(await index.UpdatePageAsync(notes, path, CancellationToken.None)); // one note removed
    }

    [Fact]
    public async Task UpdatePage_EmbedderThrows_StaysReady_OthersFound_LaterUpdateAddsTheNote()
    {
        using var tv = TwoPages();
        var vault = tv.Open();
        var notes = new NoteRepository(vault);
        using var index = NewIndex(tv, out _);
        await index.RebuildAsync(notes, CancellationToken.None);
        var ready = index.Status;
        var path = RewriteBirne(tv, vault, "- Birne 3\n", "- Birne drei\n");
        var added = notes.All().Single(n => n.Block.Content == "Birne drei");
        var other = notes.All().Single(n => n.Block.Content == "Birne 4");
        _embedder.Throws = true;

        var changed = await index.UpdatePageAsync(notes, path, CancellationToken.None);

        Assert.True(changed); // the old note of the page is gone from the index
        Assert.Equal(ready, index.Status);
        Assert.True(index.IsReady);
        Assert.Null(index.VectorOf(added.Key)); // left out until the page changes again
        Assert.Equal(FakeEmbedder.VectorFor("passage: Birne 4"), index.VectorOf(other.Key)); // from the cache
        Assert.NotNull(index.VectorOf(notes.All().Single(n => n.Block.Content == "Apfel 1").Key));

        _embedder.Throws = false;
        Assert.True(await index.UpdatePageAsync(notes, path, CancellationToken.None));

        Assert.Equal(FakeEmbedder.VectorFor("passage: Birne drei"), index.VectorOf(added.Key));
        Assert.Equal(ready, index.Status);
    }

    [Fact]
    public async Task UpdatePage_DuringRebuild_WaitsAndKeepsItsResult()
    {
        using var tv = TwoPages();
        var vault = tv.Open();
        var notes = new NoteRepository(vault);
        using var index = NewIndex(tv, out _);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        BlockCall(2, entered, release);

        var rebuild = index.RebuildAsync(notes, CancellationToken.None);
        Assert.True(entered.Wait(Timeout));
        var path = RewriteBirne(tv, vault, "- Birne 3\n", "- Birne drei\n");
        var update = index.UpdatePageAsync(notes, path, CancellationToken.None);
        Assert.False(update.IsCompleted);
        release.Set();
        await Task.WhenAll(rebuild, update);

        Assert.Equal(4, _embedder.Calls);
        Assert.Equal(["passage: Birne drei"], _embedder.Batches[^1]);
        var changed = notes.All().Single(n => n.Block.Content == "Birne drei");
        Assert.Equal(FakeEmbedder.VectorFor("passage: Birne drei"), index.VectorOf(changed.Key));
        Assert.All(index.Nearest(FakeEmbedder.VectorFor("Birne"), 100, _ => true), hit => Assert.NotNull(notes.Get(hit.Key)));
        Assert.Equal(new SemanticStatus(SemanticState.Ready, 40, 40, null), index.Status);
    }

    [Fact]
    public async Task Rebuild_EmbedderThrows_Failed_NearestEmpty()
    {
        using var tv = TwoPages();
        using var index = NewIndex(tv, out _);
        _embedder.Throws = true;

        await index.RebuildAsync(new NoteRepository(tv.Open()), CancellationToken.None);

        Assert.Equal(SemanticState.Failed, index.Status.State);
        Assert.Equal(FakeEmbedder.FailureMessage, index.Status.Error);
        Assert.False(index.IsReady);
        Assert.Empty(index.Nearest(FakeEmbedder.VectorFor("Apfel 1"), 10, _ => true));
    }

    [Fact]
    public async Task Rebuild_Cancelled_StopsBetweenBatches_NoPutAfterCancel()
    {
        using var tv = TwoPages();
        var notes = new NoteRepository(tv.Open());
        using var index = NewIndex(tv, out var cache);
        using var cts = new CancellationTokenSource();
        _embedder.OnEmbed = (call, _) =>
        {
            if (call == 2) cts.Cancel(); // the fake still returns this batch's vectors
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => index.RebuildAsync(notes, cts.Token));

        Assert.Equal(2, _embedder.Calls);
        Assert.Equal(new SemanticStatus(SemanticState.Indexing, 16, 40, null), index.Status);
        Assert.Equal(16, cache.Get(HashesOf(notes)).Count);
        Assert.True(index.IsReady); // the first batch stays usable
    }

    [Fact]
    public async Task Dispose_DuringRebuild_NoExceptionNoWrites()
    {
        using var tv = TwoPages();
        var notes = new NoteRepository(tv.Open());
        var index = NewIndex(tv, out _);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        BlockCall(2, entered, release); // the fake ignores the token: the batch finishes after Dispose

        var rebuild = index.RebuildAsync(notes, CancellationToken.None);
        Assert.True(entered.Wait(Timeout));
        index.Dispose();
        var eventsAfterDispose = 0;
        index.StatusChanged += () => Interlocked.Increment(ref eventsAfterDispose);
        release.Set();
        await rebuild;

        Assert.Equal(0, eventsAfterDispose);
        await index.RebuildAsync(notes, CancellationToken.None);
        await index.UpdatePageAsync(notes, Path.Combine(tv.Root, "pages", "Birne.md"), CancellationToken.None);
        Assert.Null(index.EmbedQuery("query: Apfel"));
        Assert.Equal(2, _embedder.Calls);
        using var reopened = VectorCache.Open(tv.Root, Model);
        Assert.Equal(16, reopened.Get(HashesOf(notes)).Count); // only the batch finished before Dispose
    }

    [Fact]
    public async Task Dispose_WhileEmbedderRunsAndUpdateWaits_BothEndQuietly()
    {
        using var tv = TwoPages();
        var notes = new NoteRepository(tv.Open());
        var index = NewIndex(tv, out _);
        using var entered = new ManualResetEventSlim();
        _embedder.OnEmbed = (call, ct) =>
        {
            if (call != 2) return;
            entered.Set();
            ct.WaitHandle.WaitOne(Timeout); // like the ONNX embedder: Dispose cancels the running batch
            ct.ThrowIfCancellationRequested();
        };

        var rebuild = index.RebuildAsync(notes, CancellationToken.None);
        Assert.True(entered.Wait(Timeout));
        var update = index.UpdatePageAsync(notes, Path.Combine(tv.Root, "pages", "Birne.md"), CancellationToken.None);
        index.Dispose();
        await Task.WhenAll(rebuild, update).WaitAsync(Timeout);

        Assert.Equal(2, _embedder.Calls);
        Assert.Equal(SemanticState.Indexing, index.Status.State);
    }

    [Fact]
    public async Task Nearest_PartialIndexWhileIndexing_IsReady()
    {
        using var tv = TwoPages();
        using var index = NewIndex(tv, out _);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        BlockCall(2, entered, release);

        var rebuild = index.RebuildAsync(new NoteRepository(tv.Open()), CancellationToken.None);
        Assert.True(entered.Wait(Timeout));

        Assert.True(index.IsReady);
        Assert.Equal(new SemanticStatus(SemanticState.Indexing, 16, 40, null), index.Status);
        Assert.Equal(16, index.Nearest(FakeEmbedder.VectorFor(_embedder.Batches[0][0]), 100, _ => true).Count);
        Assert.Equal(FakeEmbedder.VectorFor("query: Apfel 1"), index.EmbedQuery("query: Apfel 1")); // does not wait
        release.Set();
        await rebuild;
        Assert.Equal(SemanticState.Ready, index.Status.State);
    }

    [Fact]
    public async Task EmbedQuery_EmbedderThrows_NullStatusUnchanged()
    {
        using var tv = TwoPages();
        using var index = NewIndex(tv, out _);
        await index.RebuildAsync(new NoteRepository(tv.Open()), CancellationToken.None);
        _embedder.Throws = true;

        Assert.Null(index.EmbedQuery("query: Apfel"));
        Assert.Equal(new SemanticStatus(SemanticState.Ready, 40, 40, null), index.Status);
    }
}
