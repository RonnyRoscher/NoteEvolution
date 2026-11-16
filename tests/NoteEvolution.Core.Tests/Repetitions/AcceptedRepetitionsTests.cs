using NoteEvolution.Core.Repetitions;
using NoteEvolution.TestSupport;

namespace NoteEvolution.Core.Tests.Repetitions;

public class AcceptedRepetitionsTests
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static readonly Guid C = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");

    private static IReadOnlySet<Guid> Live(params Guid[] ids) => ids.ToHashSet();

    [Fact]
    public void MissingFile_IsEmpty()
    {
        using var dir = new TempDir();

        var store = new AcceptedRepetitions(dir.Path);

        Assert.Empty(store.Of("Buch"));
        Assert.False(store.Contains("Buch", A, B));
    }

    [Fact]
    public void DamagedFile_IsEmpty_AndNextSetOverwrites()
    {
        using var dir = new TempDir();
        dir.Write(".noteevolution/repetitions.json", "{ not json");
        var store = new AcceptedRepetitions(dir.Path);
        Assert.Empty(store.Of("Buch"));

        store.Set("Buch", A, B, true, Live(A, B));

        Assert.True(new AcceptedRepetitions(dir.Path).Contains("Buch", A, B));
    }

    [Fact]
    public void Set_ThenNewInstance_ReadsThePair()
    {
        using var dir = new TempDir();

        new AcceptedRepetitions(dir.Path).Set("Buch", A, B, true, Live(A, B));

        var again = new AcceptedRepetitions(dir.Path);
        Assert.True(again.Contains("Buch", A, B));
        Assert.Single(again.Of("Buch"));
    }

    [Fact]
    public void PairInBothOrders_IsOnePair()
    {
        using var dir = new TempDir();
        var store = new AcceptedRepetitions(dir.Path);

        store.Set("Buch", B, A, true, Live(A, B));
        store.Set("Buch", A, B, true, Live(A, B));

        Assert.Single(store.Of("Buch"));
        Assert.True(store.Contains("Buch", A, B));
        Assert.True(store.Contains("Buch", B, A));
        Assert.Contains((A, B), store.Of("Buch"));
    }

    [Fact]
    public void Set_False_Removes()
    {
        using var dir = new TempDir();
        var store = new AcceptedRepetitions(dir.Path);
        store.Set("Buch", A, B, true, Live(A, B));

        store.Set("Buch", B, A, false, Live(A, B));

        Assert.False(store.Contains("Buch", A, B));
        Assert.Empty(new AcceptedRepetitions(dir.Path).Of("Buch"));
    }

    [Fact]
    public void Set_DropsPairsWithDeadIds_OfThatBookOnly()
    {
        using var dir = new TempDir();
        var store = new AcceptedRepetitions(dir.Path);
        store.Set("Buch", A, B, true, Live(A, B));
        store.Set("Anderes", A, B, true, Live(A, B));

        store.Set("Buch", A, C, true, Live(A, C));

        Assert.False(store.Contains("Buch", A, B));
        Assert.True(store.Contains("Buch", A, C));
        Assert.True(store.Contains("Anderes", A, B));
    }

    [Fact]
    public void TwoBooks_AreSeparate()
    {
        using var dir = new TempDir();
        var store = new AcceptedRepetitions(dir.Path);

        store.Set("Eins", A, B, true, Live(A, B));

        Assert.True(store.Contains("Eins", A, B));
        Assert.False(store.Contains("Zwei", A, B));
        Assert.Empty(store.Of("Zwei"));
    }
}
