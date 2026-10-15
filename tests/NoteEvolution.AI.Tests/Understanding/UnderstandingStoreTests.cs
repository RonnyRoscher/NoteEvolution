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
    public void Open_CorruptFile_Recreated_Empty()
    {
        using var dir = new TempDir();
        var folder = Path.Combine(dir.Path, ".noteevolution");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "understanding.db"), Enumerable.Range(0, 4096).Select(i => (byte)(i * 31 + 7)).ToArray());

        using var store = UnderstandingStore.Open(dir.Path);

        Assert.Null(store.Get("anything"));
        store.Put(Entry("fp"));
        Assert.NotNull(store.Get("fp"));
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
}
