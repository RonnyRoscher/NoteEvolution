using Microsoft.Extensions.Logging;
using NoteEvolution.AI.Understanding;
using NoteEvolution.TestSupport;

namespace NoteEvolution.AI.Tests.Understanding;

public sealed class UnderstandingStoreTests
{
    private static UnderstandingEntry Entry(string fingerprint, string identity = "id", string text = "text",
        UnderstandingKind kind = UnderstandingKind.Heading, string? embedModel = null, float[]? vector = null) =>
        new(kind, fingerprint, identity, text, "chat-1", embedModel, vector);

    [Fact]
    public void PutGet_RoundTrips_WithVector()
    {
        using var dir = new TempDir();
        using var store = UnderstandingStore.Open(dir.Path);
        float[] vector = [0.25f, -1.5f, 1e-7f, 3f];
        var entry = Entry("fp1", "h1", "Zusammenfassung", UnderstandingKind.Note, "embed-1", vector);

        store.Put(entry);

        var read = store.Get("fp1");
        Assert.NotNull(read);
        Assert.Equal(UnderstandingKind.Note, read.Kind);
        Assert.Equal("h1", read.Identity);
        Assert.Equal("Zusammenfassung", read.Text);
        Assert.Equal("chat-1", read.ChatModel);
        Assert.Equal("embed-1", read.EmbedModel);
        Assert.Equal(vector, read.Vector);
        Assert.Null(store.Get("missing"));
    }

    [Fact]
    public void LatestByIdentity_ReturnsNewestOfThatKind()
    {
        using var dir = new TempDir();
        using var store = UnderstandingStore.Open(dir.Path);
        store.Put(Entry("a", "id", "old"));
        store.Put(Entry("b", "id", "new"));
        store.Put(Entry("c", "id", "note text", UnderstandingKind.Note));

        Assert.Equal("new", store.LatestByIdentity(UnderstandingKind.Heading, "id")!.Text);
        Assert.Equal("note text", store.LatestByIdentity(UnderstandingKind.Note, "id")!.Text);
        Assert.Null(store.LatestByIdentity(UnderstandingKind.Heading, "other"));

        store.Put(Entry("a", "id", "old again")); // re-put counts as newest
        Assert.Equal("old again", store.LatestByIdentity(UnderstandingKind.Heading, "id")!.Text);
    }

    [Fact]
    public void SetVector_OtherEmbedModel_ListedInWithoutVector()
    {
        using var dir = new TempDir();
        using var store = UnderstandingStore.Open(dir.Path);
        store.Put(Entry("none"));
        store.Put(Entry("m1", embedModel: "embed-1", vector: [1f]));
        store.Put(Entry("m2"));
        store.SetVector("m2", "embed-2", [2f, 3f]);

        Assert.Equal(["m1", "none"], store.WithoutVector("embed-2").Order());
        Assert.Equal(["m2", "none"], store.WithoutVector("embed-1").Order());
        Assert.Equal([2f, 3f], store.Get("m2")!.Vector!);
        Assert.Equal("embed-2", store.Get("m2")!.EmbedModel);
    }

    [Fact]
    public void WithoutVector_IgnoresPlacements()
    {
        using var dir = new TempDir();
        using var store = UnderstandingStore.Open(dir.Path);
        store.Put(Entry("note", kind: UnderstandingKind.Note));
        store.Put(Entry("place", kind: UnderstandingKind.Placement));

        Assert.Equal(["note"], store.WithoutVector("embed-1"));
        Assert.Equal(2, (int)UnderstandingKind.Placement);
    }

    [Fact]
    public void RecordFailure_CountsUp_PutResets()
    {
        using var dir = new TempDir();
        using var store = UnderstandingStore.Open(dir.Path);
        Assert.Equal(0, store.Attempts("fp"));

        store.RecordFailure("fp");
        store.RecordFailure("fp");
        Assert.Equal(2, store.Attempts("fp"));

        store.Put(Entry("fp"));
        Assert.Equal(0, store.Attempts("fp"));
    }

    [Fact]
    public void ForgetFailure_CountsDown_NotBelowZero()
    {
        using var dir = new TempDir();
        using var store = UnderstandingStore.Open(dir.Path);
        store.RecordFailure("fp");
        store.RecordFailure("fp");

        store.ForgetFailure("fp");
        Assert.Equal(1, store.Attempts("fp"));
        store.ForgetFailure("fp");
        store.ForgetFailure("fp");
        store.ForgetFailure("unknown");
        Assert.Equal(0, store.Attempts("fp"));
        store.RecordFailure("fp");
        Assert.Equal(1, store.Attempts("fp"));
    }

    [Fact]
    public void Prune_KeepsLiveFingerprintsAndLatestOfLiveIdentities()
    {
        using var dir = new TempDir();
        using var store = UnderstandingStore.Open(dir.Path);
        store.Put(Entry("live", "x"));
        store.Put(Entry("old-of-live-id", "y"));
        store.Put(Entry("new-of-live-id", "y"));
        store.Put(Entry("dead", "z"));
        store.RecordFailure("dead");
        store.RecordFailure("old-of-live-id");

        store.Prune(new HashSet<string> { "live" }, new HashSet<string> { "y" });

        Assert.NotNull(store.Get("live"));
        Assert.NotNull(store.Get("new-of-live-id"));
        Assert.Null(store.Get("old-of-live-id"));
        Assert.Null(store.Get("dead"));
        Assert.Equal(0, store.Attempts("dead"));
        Assert.Equal(0, store.Attempts("old-of-live-id"));
    }

    [Fact]
    public void Prune_KeepsNewestPlacementOfEveryIdentity_LiveOrNot()
    {
        using var dir = new TempDir();
        using var store = UnderstandingStore.Open(dir.Path);
        store.Put(Entry("other-book", "placement|Beta|n1", kind: UnderstandingKind.Placement));
        store.Put(Entry("old", "placement|Alpha|n1", kind: UnderstandingKind.Placement));

        // No placement is live (another book is current, or this one waits for its summaries): the newest of each stays.
        store.Prune(new HashSet<string>(), new HashSet<string>());
        Assert.NotNull(store.Get("other-book"));
        Assert.NotNull(store.Get("old"));

        // A newer result of the same identity replaces the older one at the next prune; the other book's stays.
        store.Put(Entry("new", "placement|Alpha|n1", kind: UnderstandingKind.Placement));
        store.Prune(new HashSet<string>(), new HashSet<string>());
        Assert.NotNull(store.Get("new"));
        Assert.Null(store.Get("old"));
        Assert.NotNull(store.Get("other-book"));

        // Other kinds still need a live identity.
        store.Put(Entry("heading", "book:Gone/X#0"));
        store.Prune(new HashSet<string>(), new HashSet<string>());
        Assert.Null(store.Get("heading"));
    }

    [Fact]
    public void Prune_KeepsFailedAttemptsOfLiveFingerprints()
    {
        using var dir = new TempDir();
        using var store = UnderstandingStore.Open(dir.Path);
        for (var i = 0; i < 3; i++) store.RecordFailure("live-failing");
        store.RecordFailure("dead-failing");

        store.Prune(new HashSet<string> { "live-failing" }, new HashSet<string>());

        // A live item that failed too often stays skipped; attempts of things gone are forgotten.
        Assert.Equal(3, store.Attempts("live-failing"));
        Assert.Equal(0, store.Attempts("dead-failing"));
    }

    [Fact]
    public void PutAndSetVector_RaiseEntryChanged()
    {
        using var dir = new TempDir();
        using var store = UnderstandingStore.Open(dir.Path);
        var changed = new List<string>();
        store.EntryChanged += changed.Add;

        store.Put(Entry("a"));
        store.SetVector("a", "embed-1", [1f]);
        store.RecordFailure("b");

        Assert.Equal(["a", "a"], changed);
    }

    [Fact]
    public void Open_CorruptFile_Recreated_Empty()
    {
        using var dir = new TempDir();
        var folder = Path.Combine(dir.Path, ".noteevolution");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "understanding.db"), Enumerable.Range(0, 4096).Select(i => (byte)(i * 31 + 7)).ToArray());

        var logger = new ListLogger();

        using var store = UnderstandingStore.Open(dir.Path, logger);

        // Spec 8: recreated, with a note in the log.
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("understanding.db", StringComparison.Ordinal));
        Assert.Null(store.Get("anything"));
        store.Put(Entry("fp"));
        Assert.NotNull(store.Get("fp"));
    }

    [Fact]
    public void BulkReads_MatchTheSingleReads()
    {
        using var dir = new TempDir();
        using var store = UnderstandingStore.Open(dir.Path);
        store.Put(Entry("a", "h1"));
        store.Put(Entry("b", "h1"));
        store.Put(Entry("c", "n1", kind: UnderstandingKind.Note));
        store.RecordFailure("x");
        store.RecordFailure("x");
        store.RecordFailure("c");
        store.Put(Entry("c", "n1", kind: UnderstandingKind.Note)); // clears its attempts

        Assert.Equal(new HashSet<string> { "a", "b", "c" }, store.Fingerprints());
        Assert.Equal(new Dictionary<string, int> { ["x"] = 2 }, store.AllAttempts());
        Assert.Equal(new HashSet<(UnderstandingKind, string)> { (UnderstandingKind.Heading, "h1"), (UnderstandingKind.Note, "n1") },
            store.Identities());
        Assert.Equal(UnderstandingKind.Note, store.KindOf("c"));
        Assert.Null(store.KindOf("x"));
    }

    [Fact]
    public void Open_UsesWriteAheadLog_GoneAfterDispose()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, ".noteevolution", "understanding.db");
        using (var store = UnderstandingStore.Open(dir.Path))
        {
            store.Put(Entry("a"));
            Assert.True(File.Exists(path + "-wal"));
        }

        // The last connection closed: the log was written back and removed, so the folder can be deleted.
        Assert.False(File.Exists(path + "-wal"));
        using var reopened = UnderstandingStore.Open(dir.Path);
        Assert.NotNull(reopened.Get("a"));
    }

    [Fact]
    public void Survives_Reopen()
    {
        using var dir = new TempDir();
        using (var store = UnderstandingStore.Open(dir.Path))
        {
            store.Put(Entry("a", "id", "first", embedModel: "e", vector: [1f, 2f]));
            store.Put(Entry("b", "id", "second"));
            store.RecordFailure("b");
        }

        using var reopened = UnderstandingStore.Open(dir.Path);
        Assert.Equal([1f, 2f], reopened.Get("a")!.Vector!);
        Assert.Equal(1, reopened.Attempts("b"));
        reopened.Put(Entry("c", "id", "third"));
        Assert.Equal("third", reopened.LatestByIdentity(UnderstandingKind.Heading, "id")!.Text);
        reopened.Put(Entry("d", "id", "fourth"));
        Assert.Equal("fourth", reopened.LatestByIdentity(UnderstandingKind.Heading, "id")!.Text);
    }

    [Fact]
    public void Open_IntactFile_LogsNothing()
    {
        using var dir = new TempDir();
        UnderstandingStore.Open(dir.Path).Dispose();
        var logger = new ListLogger();

        using var store = UnderstandingStore.Open(dir.Path, logger);

        Assert.Empty(logger.Entries);
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
