using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>The session history of the active blocks (spec 3): duplicates, forward entries, capacity, missing blocks.</summary>
public class NavigationHistoryTests
{
    private static readonly NavEntry A = new("alpha", Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));
    private static readonly NavEntry B = new("alpha", Guid.Parse("bbbbbbbb-0000-4000-8000-000000000001"));
    private static readonly NavEntry C = new("alpha", Guid.Parse("cccccccc-0000-4000-8000-000000000001"));
    private static readonly NavEntry D = new("beta", Guid.Parse("dddddddd-0000-4000-8000-000000000001"));

    private static bool All(NavEntry _) => true;

    [Fact]
    public void Empty_CannotGoAnywhere()
    {
        var history = new NavigationHistory();

        Assert.False(history.CanGoBack);
        Assert.False(history.CanGoForward);
        Assert.Null(history.Back(All));
        Assert.Null(history.Forward(All));
        Assert.Null(history.Current);
    }

    [Fact]
    public void Record_DuplicateOfTheCurrentEntry_NotRecordedAgain()
    {
        var history = new NavigationHistory();

        history.Record(A);
        history.Record(B);
        history.Record(B);

        Assert.Equal(A, history.Back(All));
        Assert.False(history.CanGoBack);
        Assert.Equal(B, history.Forward(All));
        Assert.False(history.CanGoForward);
    }

    [Fact]
    public void Record_AfterBack_DropsTheForwardEntries()
    {
        var history = new NavigationHistory();
        history.Record(A);
        history.Record(B);
        history.Record(C);

        Assert.Equal(B, history.Back(All));
        Assert.Equal(A, history.Back(All));
        history.Record(D);

        Assert.False(history.CanGoForward);
        Assert.Equal(A, history.Back(All));
        Assert.Equal(D, history.Forward(All));
        Assert.False(history.CanGoForward);
    }

    [Fact]
    public void Record_AfterBack_SameAsTheCurrentEntry_KeepsTheForwardEntries()
    {
        var history = new NavigationHistory();
        history.Record(A);
        history.Record(B);
        history.Back(All);

        history.Record(A);

        Assert.True(history.CanGoForward);
        Assert.Equal(B, history.Forward(All));
    }

    [Fact]
    public void Capacity_DropsTheOldestEntries()
    {
        var history = new NavigationHistory();
        var entries = Enumerable.Range(0, 60).Select(i => new NavEntry("alpha", Guid.NewGuid())).ToList();
        foreach (var entry in entries)
        {
            history.Record(entry);
        }

        var back = new List<NavEntry>();
        while (history.Back(All) is { } entry)
        {
            back.Add(entry);
        }

        // 50 entries: the current one and 49 behind it; the 10 oldest are gone.
        Assert.Equal(49, back.Count);
        Assert.Equal(entries[10], back[^1]);
        Assert.Equal(entries[58], back[0]);
    }

    [Fact]
    public void Capacity_IsAParameter()
    {
        var history = new NavigationHistory(capacity: 2);
        history.Record(A);
        history.Record(B);
        history.Record(C);

        Assert.Equal(B, history.Back(All));
        Assert.Null(history.Back(All));
    }

    [Fact]
    public void Back_EntryThatNoLongerExists_SkippedAndRemoved()
    {
        var history = new NavigationHistory();
        history.Record(A);
        history.Record(B);
        history.Record(C);

        Assert.Equal(A, history.Back(e => e != B));

        // B is gone for good: forward from A comes to C, even when B exists again.
        Assert.Equal(C, history.Forward(All));
        Assert.Equal(A, history.Back(All));
    }

    [Fact]
    public void Forward_EntryThatNoLongerExists_SkippedAndRemoved()
    {
        var history = new NavigationHistory();
        history.Record(A);
        history.Record(B);
        history.Record(C);
        history.Back(All);
        history.Back(All);

        Assert.Equal(C, history.Forward(e => e != B));
        Assert.Equal(A, history.Back(All));
    }

    [Fact]
    public void Back_NothingThatExists_StaysAndRemovesTheMissing()
    {
        var history = new NavigationHistory();
        history.Record(A);
        history.Record(B);

        Assert.Null(history.Back(e => e == B));

        Assert.False(history.CanGoBack);
        Assert.Equal(B, history.Current);
    }

    [Fact]
    public void Back_RemovingAMissingEntry_DoesNotLeaveTwoEqualEntriesInARow()
    {
        var history = new NavigationHistory();
        history.Record(A);
        history.Record(B);
        history.Record(A);

        // B is gone: A, A would follow each other; going back from A lands nowhere new.
        Assert.Null(history.Back(e => e != B));
        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void Clear_EmptiesTheHistory()
    {
        var history = new NavigationHistory();
        history.Record(A);
        history.Record(B);

        history.Clear();

        Assert.False(history.CanGoBack);
        Assert.Null(history.Current);
    }
}
