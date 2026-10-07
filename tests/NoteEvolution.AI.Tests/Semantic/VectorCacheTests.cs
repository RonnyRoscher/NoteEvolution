using Microsoft.Data.Sqlite;
using NoteEvolution.AI.Embeddings;
using NoteEvolution.AI.Semantic;

namespace NoteEvolution.AI.Tests.Semantic;

public sealed class VectorCacheTests : IDisposable
{
    private const string Model = "test-model";
    private readonly string _vault = Path.Combine(Path.GetTempPath(), "ne-vectors-" + Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_vault, ".noteevolution", "vectors.db");

    public void Dispose()
    {
        if (Directory.Exists(_vault)) Directory.Delete(_vault, recursive: true);
    }

    private static string H(string text) => EmbeddingText.Hash(Model, text);

    private static float[] Vector(int seed, int dims = 384)
    {
        var random = new Random(seed);
        var v = new float[dims];
        for (var i = 0; i < dims; i++) v[i] = (float)(random.NextDouble() * 2 - 1) * (i % 7 == 0 ? 1e-7f : 1f);
        return v;
    }

    [Fact]
    public void Cache_PutGet_RoundTripsVectorsExactly()
    {
        var a = Vector(1);
        var b = Vector(2);
        using (var cache = VectorCache.Open(_vault, Model))
        {
            cache.Put([(H("a"), "pages/a.md", a), (H("b"), "pages/b.md", b)]);
            var found = cache.Get([H("a"), H("b"), H("missing")]);

            Assert.Equal(2, found.Count);
            Assert.Equal(a, found[H("a")]);
            Assert.Equal(b, found[H("b")]);
        }

        // survives a restart
        using var reopened = VectorCache.Open(_vault, Model);
        Assert.Equal(a, reopened.Get([H("a")])[H("a")]);
    }

    [Fact]
    public void Cache_Put_StoresLittleEndianFloat32()
    {
        using (var cache = VectorCache.Open(_vault, Model))
            cache.Put([(H("a"), "pages/a.md", [1f, -2f])]);

        using var db = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT vector FROM vectors";
        var blob = (byte[])command.ExecuteScalar()!;

        Assert.Equal([0x00, 0x00, 0x80, 0x3F, 0x00, 0x00, 0x00, 0xC0], blob);
    }

    [Fact]
    public void Cache_Put_ReplacesExistingHash()
    {
        using var cache = VectorCache.Open(_vault, Model);
        cache.Put([(H("a"), "pages/a.md", [1f, 0f])]);
        cache.Put([(H("a"), "pages/a.md", [0f, 1f])]);

        Assert.Equal([0f, 1f], cache.Get([H("a")])[H("a")]);
    }

    [Fact]
    public void Cache_Get_ManyHashes_IsChunked()
    {
        using var cache = VectorCache.Open(_vault, Model);
        var items = Enumerable.Range(0, 1500).Select(i => (H("n" + i), "p.md", new float[] { i, 1f })).ToList();
        cache.Put(items);

        var found = cache.Get(items.Select(i => i.Item1).ToList());

        Assert.Equal(1500, found.Count);
        Assert.Equal([1234f, 1f], found[H("n1234")]);
    }

    [Fact]
    public void Cache_Get_NoHashes_IsEmpty()
    {
        using var cache = VectorCache.Open(_vault, Model);

        Assert.Empty(cache.Get([]));
    }

    [Fact]
    public void Cache_Prune_KeepsOnlyLive()
    {
        using var cache = VectorCache.Open(_vault, Model);
        cache.Put([(H("a"), "a.md", [1f]), (H("b"), "b.md", [2f]), (H("c"), "c.md", [3f])]);

        cache.Prune(new HashSet<string> { H("a"), H("c"), H("never-stored") });

        var found = cache.Get([H("a"), H("b"), H("c")]);
        Assert.Equal(2, found.Count);
        Assert.False(found.ContainsKey(H("b")));
        Assert.True(found.ContainsKey(H("a")));
        Assert.True(found.ContainsKey(H("c")));
    }

    [Fact]
    public void Cache_OtherModel_IsDiscarded()
    {
        using (var cache = VectorCache.Open(_vault, Model))
            cache.Put([(H("a"), "a.md", [1f])]);

        using var other = VectorCache.Open(_vault, "another-model");

        Assert.Empty(other.Get([H("a")]));
        other.Put([(H("b"), "b.md", [2f])]);
        Assert.Single(other.Get([H("b")]));
    }

    [Fact]
    public void Cache_CorruptFile_IsRecreated()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        var garbage = new byte[8192];
        new Random(42).NextBytes(garbage);
        File.WriteAllBytes(DbPath, garbage);

        using var cache = VectorCache.Open(_vault, Model);

        Assert.Empty(cache.Get([H("a")]));
        cache.Put([(H("a"), "a.md", [1f, 2f])]);
        Assert.Equal([1f, 2f], cache.Get([H("a")])[H("a")]);
    }

    [Fact]
    public void Cache_AfterDispose_Throws()
    {
        var cache = VectorCache.Open(_vault, Model);
        cache.Dispose();
        cache.Dispose(); // idempotent

        Assert.Throws<ObjectDisposedException>(() => cache.Get([H("a")]));
        Assert.Throws<ObjectDisposedException>(() => cache.Put([(H("a"), "a.md", [1f])]));
        Assert.Throws<ObjectDisposedException>(() => cache.Prune(new HashSet<string>()));
    }

    [Fact]
    public void Cache_AfterDispose_FileCanBeDeleted()
    {
        using (var cache = VectorCache.Open(_vault, Model))
            cache.Put([(H("a"), "a.md", [1f])]);

        File.Delete(DbPath);

        Assert.False(File.Exists(DbPath));
    }

    [Fact]
    public async Task Cache_ConcurrentUse_DoesNotFail()
    {
        using var cache = VectorCache.Open(_vault, Model);
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++) cache.Put([(H("w" + i), "p.md", [i, 1f])]);
        });
        var reader = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++) cache.Get([H("w" + i), H("w0")]);
        });

        await Task.WhenAll(writer, reader);

        Assert.Equal(200, cache.Get(Enumerable.Range(0, 200).Select(i => H("w" + i)).ToList()).Count);
    }
}
