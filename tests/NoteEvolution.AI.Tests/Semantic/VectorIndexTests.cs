using NoteEvolution.AI.Semantic;

namespace NoteEvolution.AI.Tests.Semantic;

public class VectorIndexTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    private static readonly Guid D = Guid.Parse("00000000-0000-0000-0000-00000000000d");

    [Fact]
    public void Index_Nearest_OrdersByCosine_RespectsIncludeAndK()
    {
        var index = new VectorIndex();
        index.Set(A, "p1.md", [1f, 0f]);    // cosine 1.0
        index.Set(B, "p1.md", [1f, 1f]);    // ~0.707
        index.Set(C, "p2.md", [1f, 3f]);    // ~0.316
        index.Set(D, "p2.md", [-1f, 0f]);   // -1, dropped

        var all = index.Nearest([1f, 0f], 10, _ => true);
        Assert.Equal([A, B, C], all.Select(h => h.Key));
        Assert.Equal(1f, all[0].Score, 5);
        Assert.Equal(MathF.Sqrt(0.5f), all[1].Score, 5);

        Assert.Equal([A, B], index.Nearest([1f, 0f], 2, _ => true).Select(h => h.Key));
        Assert.Equal([B, C], index.Nearest([1f, 0f], 10, key => key != A).Select(h => h.Key));
        Assert.Empty(index.Nearest([1f, 0f], 0, _ => true));
    }

    [Fact]
    public void Index_RemovePage_RemovesItsKeys()
    {
        var index = new VectorIndex();
        index.Set(A, "Pages/One.md", [1f, 0f]);
        index.Set(B, "pages/one.md", [0f, 1f]);
        index.Set(C, "pages/two.md", [1f, 1f]);

        index.RemovePage("PAGES/ONE.MD");

        Assert.Equal(1, index.Count);
        Assert.Null(index.Get(A));
        Assert.Null(index.Get(B));
        Assert.NotNull(index.Get(C));
    }

    [Fact]
    public void Index_SetExistingKeyOnOtherPage_MovesIt()
    {
        var index = new VectorIndex();
        index.Set(A, "one.md", [1f, 0f]);
        index.Set(A, "two.md", [0f, 1f]);

        index.RemovePage("one.md");

        Assert.Equal(1, index.Count);
        Assert.Equal([0f, 1f], index.Get(A)!);
        index.RemovePage("two.md");
        Assert.Equal(0, index.Count);
    }

    [Fact]
    public void Index_Remove_RemovesKeyOnly()
    {
        var index = new VectorIndex();
        index.Set(A, "p.md", [1f, 0f]);
        index.Set(B, "p.md", [0f, 1f]);

        index.Remove(A);
        index.Remove(Guid.NewGuid());

        Assert.Equal(1, index.Count);
        Assert.Null(index.Get(A));
        Assert.Equal([B], index.Nearest([0f, 1f], 5, _ => true).Select(h => h.Key));
    }

    [Fact]
    public void Index_ZeroVector_NeverReturned()
    {
        var index = new VectorIndex();
        index.Set(A, "p.md", [0f, 0f]);
        index.Set(B, "p.md", [1f, 0f]);

        Assert.Equal([B], index.Nearest([1f, 0f], 5, _ => true).Select(h => h.Key));
        Assert.Empty(index.Nearest([0f, 0f], 5, _ => true));
    }

    [Fact]
    public void Index_Set_CopiesTheVector()
    {
        var index = new VectorIndex();
        var v = new[] { 1f, 0f };
        index.Set(A, "p.md", v);
        v[0] = -1f;

        Assert.Equal([1f, 0f], index.Get(A)!);
    }

    [Fact]
    public async Task Index_SlowInclude_DoesNotBlockWriters()
    {
        var index = new VectorIndex();
        index.Set(A, "p.md", [1f, 0f]);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var query = Task.Run(() => index.Nearest([1f, 0f], 5, _ =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return true;
        }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

        var write = Task.Run(() => index.Set(B, "p.md", [0f, 1f]));
        var finished = await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(5))) == write;
        release.Set();
        await query;

        Assert.True(finished);
        Assert.Equal(2, index.Count);
    }
}
