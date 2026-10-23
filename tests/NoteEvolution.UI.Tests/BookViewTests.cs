using Microsoft.Extensions.Time.Testing;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Vaults;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Editor;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>The book views behind <see cref="AppState"/>: one without a split, two with it, the position members forwarding to the active one.</summary>
public class BookViewTests : IDisposable
{
    private const string BookText =
        "title:: Alpha\ntype:: book\n\n" +
        "- # Eins\n" +
        "\t- Text eins\n" +
        "- # Zwei\n" +
        "\t- Text zwei\n";

    private readonly TestVault _vault = TestVault.Create(
        ("pages/Buch - Alpha.md", BookText),
        ("pages/Buch - Beta.md", "title:: Beta\ntype:: book\n\n- # Anderes\n\t- Beta Text\n"));

    private readonly Vault _opened;

    private readonly FakeTimeProvider _time = new();

    public BookViewTests()
    {
        _opened = _vault.Open();
        State = new AppState(_time) { CurrentBook = _opened.FindBook("Buch - Alpha") };
    }

    private AppState State { get; }

    private Book Book => State.CurrentBook!;

    public void Dispose() => _vault.Dispose();

    private Guid KeyOf(string content) => Book.Page.AllBlocks().Single(b => b.Content == content).Key;

    private CursorInfo CursorOf(string content, int offset = 0)
    {
        var element = BookElements.Find(Book, KeyOf(content))!;
        return new CursorInfo(element.Kind, element.Key, BookElements.TextBlockOf(Book, element)?.Key, offset);
    }

    [Fact]
    public void Unsplit_OneView_ForwardsAsBefore()
    {
        var cursor = CursorOf("# Eins");
        State.Cursor = cursor;
        State.CurrentSectionKey = cursor.Key;

        Assert.Single(State.Views);
        Assert.False(State.IsSplit);
        Assert.Same(State.Views[0], State.ActiveView);
        Assert.Equal(cursor, State.Views[0].Cursor);
        Assert.Equal(State.ActiveView.CurrentSectionKey, State.CurrentSectionKey);
        Assert.Equal(cursor.Key, State.Views[0].CurrentSectionKey);
    }

    [Fact]
    public void Split_SecondViewStartsAtTheActiveSection_WithoutCursor()
    {
        State.CurrentSectionKey = KeyOf("# Zwei");
        State.Cursor = CursorOf("# Zwei");
        var changed = 0;
        State.Changed += () => changed++;

        State.SetSplit(true);

        Assert.True(State.IsSplit);
        Assert.Equal(2, State.Views.Count);
        Assert.Equal(KeyOf("# Zwei"), State.Views[1].CurrentSectionKey);
        Assert.Null(State.Views[1].Cursor);
        Assert.NotNull(State.Views[0].Cursor);
        Assert.Same(State.Views[0], State.ActiveView);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task Views_KeepTheirOwnCursorSectionAndRange()
    {
        State.CurrentSectionKey = KeyOf("# Eins");
        State.Cursor = CursorOf("# Eins");
        State.RangeLevel = 1;
        State.SetSplit(true);

        await State.ActivateAsync(State.Views[1]);
        State.CurrentSectionKey = KeyOf("# Zwei");
        State.Cursor = CursorOf("# Zwei");

        Assert.Equal(KeyOf("# Eins"), State.Views[0].Cursor!.Key);
        Assert.Equal(1, State.Views[0].RangeLevel);
        Assert.Equal(KeyOf("# Eins"), State.Views[0].CurrentSectionKey);
        Assert.Equal(KeyOf("# Zwei"), State.CurrentElement!.Key);
        Assert.Equal(0, State.RangeLevel);
    }

    [Fact]
    public async Task Activate_FlushesThePreviousViewFirst()
    {
        var log = new List<string>();
        State.SetSplit(true);
        State.Views[0].FlushEditor = () =>
        {
            log.Add("flush0");
            return Task.CompletedTask;
        };
        State.Changed += () => log.Add("notify");

        await State.ActivateAsync(State.Views[1]);
        await State.ActivateAsync(State.Views[1]);

        Assert.Equal(["flush0", "notify"], log);
        Assert.Same(State.Views[1], State.ActiveView);
    }

    [Fact]
    public async Task RevealElement_GoesToTheActiveViewOnly()
    {
        State.SetSplit(true);
        await State.ActivateAsync(State.Views[1]);
        var first = new List<Guid>();
        var second = new List<Guid>();
        var state = new List<Guid>();
        State.Views[0].RevealRequested += first.Add;
        State.Views[1].RevealRequested += second.Add;
        State.RevealRequested += state.Add;

        State.RevealElement(KeyOf("Text zwei"));

        Assert.Empty(first);
        Assert.Equal([KeyOf("Text zwei")], second);
        Assert.Equal([KeyOf("Text zwei")], state);
        Assert.Null(State.Views[0].PendingReveal);
        Assert.Equal(KeyOf("Text zwei"), State.Views[1].PendingReveal);
        Assert.Equal(KeyOf("Text zwei"), State.TakePendingReveal());
        Assert.Null(State.Views[1].PendingReveal);
    }

    [Fact]
    public void OtherBook_ResetsTheInactiveView()
    {
        State.CurrentSectionKey = KeyOf("# Eins");
        State.Cursor = CursorOf("# Eins");
        State.SetSplit(true);
        State.Views[1].Cursor = CursorOf("Text zwei");
        State.Views[1].CurrentSectionKey = KeyOf("# Zwei");
        State.Views[1].RangeLevel = 1;

        var beta = _opened.FindBook("Buch - Beta")!;
        State.CurrentBook = beta;

        Assert.Equal(beta.Root.Key, State.Views[1].CurrentSectionKey);
        Assert.Null(State.Views[1].Cursor);
        Assert.Equal(0, State.Views[1].RangeLevel);

        // The same book taken from the vault again does not reset anything.
        State.Views[1].CurrentSectionKey = beta.Root.Key;
        State.Views[1].Cursor = new CursorInfo(ElementKind.TextBlock, Guid.NewGuid(), null, 0);
        State.CurrentBook = _opened.FindBook("Buch - Beta");
        Assert.NotNull(State.Views[1].Cursor);

        // Refresh checks the inactive view's section as well.
        State.Views[1].CurrentSectionKey = Guid.NewGuid();
        State.RefreshBook();
        Assert.Equal(beta.Root.Key, State.Views[1].CurrentSectionKey);
        Assert.Null(State.Views[1].Cursor);
    }

    [Fact]
    public async Task Unsplit_WhileSecondActive_FirstBecomesActive_KeepsItsPlace()
    {
        State.CurrentSectionKey = KeyOf("# Eins");
        State.Cursor = CursorOf("# Eins");
        State.SetSplit(true);
        await State.ActivateAsync(State.Views[1]);
        State.CurrentSectionKey = KeyOf("# Zwei");
        State.Cursor = CursorOf("# Zwei");

        State.SetSplit(false);

        Assert.False(State.IsSplit);
        Assert.Single(State.Views);
        Assert.Same(State.Views[0], State.ActiveView);
        Assert.Equal(KeyOf("# Eins"), State.CurrentSectionKey);
        Assert.Equal(KeyOf("# Eins"), State.CurrentElement!.Key);
    }

    [Fact]
    public async Task Flush_And_Warn_AskEveryView()
    {
        Assert.Null(State.FlushEditor);
        Assert.Null(State.WarnUnsavedText);
        Assert.Null(State.HasUnsavedEditorText);

        State.SetSplit(true);
        var log = new List<string>();
        State.Views[0].FlushEditor = () =>
        {
            log.Add("flush0");
            return Task.CompletedTask;
        };
        State.Views[1].FlushEditor = () =>
        {
            log.Add("flush1");
            return Task.CompletedTask;
        };
        State.Views[0].WarnUnsavedText = () =>
        {
            log.Add("warn0");
            return false;
        };
        State.Views[1].WarnUnsavedText = () =>
        {
            log.Add("warn1");
            return true;
        };
        State.Views[1].HasUnsavedEditorText = () => true;

        Assert.True(State.WarnUnsavedText!());
        Assert.True(State.HasUnsavedEditorText!());
        await State.FlushEditor!();

        Assert.Equal(["warn0", "warn1", "flush0", "flush1"], log);

        State.Views[1].WarnUnsavedText = () => true;
        State.Views[0].WarnUnsavedText = () =>
        {
            log.Add("asked");
            return true;
        };
        Assert.True(State.WarnUnsavedText!());
        Assert.Contains("asked", log);
    }

    [Fact]
    public void FlushEditor_Setter_SetsTheFirstView()
    {
        Func<Task> flush = () => Task.CompletedTask;
        State.FlushEditor = flush;

        Assert.Same(flush, State.Views[0].FlushEditor);
        Assert.Same(flush, State.FlushEditor);
        State.FlushEditor = null;
        Assert.Null(State.FlushEditor);
    }

    [Fact]
    public async Task Activate_WhenTheSplitIsSwitchedOffDuringTheFlush_DoesNotActivateTheRemovedView()
    {
        State.SetSplit(true);
        var second = State.Views[1];
        State.Views[0].FlushEditor = () =>
        {
            State.SetSplit(false);
            return Task.CompletedTask;
        };

        await State.ActivateAsync(second);

        Assert.Single(State.Views);
        Assert.Same(State.Views[0], State.ActiveView);
    }

    [Fact]
    public async Task Activate_ThrowingFlush_LeavesTheActiveViewUnchanged()
    {
        State.SetSplit(true);
        State.Views[0].FlushEditor = () => throw new InvalidOperationException("save failed");

        await Assert.ThrowsAsync<InvalidOperationException>(() => State.ActivateAsync(State.Views[1]));

        Assert.Same(State.Views[0], State.ActiveView);
    }

    [Fact]
    public async Task Activate_RestartsTheDwell_ForTheNewViewsCursor()
    {
        State.Cursor = CursorOf("# Eins");
        State.SetSplit(true);
        State.Views[1].Cursor = CursorOf("# Zwei");

        await State.ActivateAsync(State.Views[1]);
        _time.Advance(NavigationHistory.DwellTime);

        // The dwell recorded "Zwei" (the active block), not "Eins" of the old half.
        Assert.False(State.CanGoBack);
    }
}

/// <summary>What the session's focus feed gets from the active view.</summary>
public class BookViewFocusTests : UiTestContext
{
    private readonly TestVault _vault = TestVault.Create(
        ("pages/Buch - Alpha.md", "title:: Alpha\ntype:: book\n\n- # Eins\n\t- Text eins\n- # Zwei\n\t- Text zwei\n"));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _vault.Dispose();
        }
    }

    [Fact]
    public async Task SetFocus_GetsTheActiveViewsSection_AfterActivate()
    {
        var session = await OpenSessionAsync(_vault);
        var book = State.CurrentBook!;
        var zwei = book.Page.AllBlocks().Single(b => b.Content == "# Zwei").Key;
        State.SetSplit(true);
        State.Views[1].CurrentSectionKey = zwei;

        await State.ActivateAsync(State.Views[1]);

        var focus = typeof(VaultSession).GetField("_focusSection", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Equal(zwei, (Guid)focus.GetValue(session)!);
    }
}
