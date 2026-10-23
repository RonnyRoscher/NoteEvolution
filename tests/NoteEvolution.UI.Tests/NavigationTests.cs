using Bunit;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Editor;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>
/// Back and forward between the active blocks (spec 3): jumps and cursor dwell fill the history, back/forward switch
/// the book and reveal the block, the header buttons and Alt+←/→ drive it.
/// </summary>
public class NavigationTests : UiTestContext
{
    private const string TargetId = "eeeeeeee-0000-4000-8000-000000000001";

    private const string AlphaText =
        "title:: Alpha\ntype:: book\n\n" +
        "- # Eins\n" +
        "\t- Erster Text\n" +
        "\t- Zweiter Text\n" +
        "\t\t- Ein Detail\n" +
        "- # Zwei\n" +
        $"  id:: {TargetId}\n" +
        "\t- Dritter Text\n" +
        "\t- Vierter Text\n";

    private readonly TestVault _vault = TestVault.Create(
        ("pages/Buch - Alpha.md", AlphaText),
        ("pages/Buch - Beta.md", "title:: Beta\ntype:: book\n\n- # Anderes\n\t- Beta Text\n"));

    protected override void Dispose(bool disposing)
    {
        // The session is closed first, then the vault folder is deleted.
        base.Dispose(disposing);
        if (disposing)
        {
            _vault.Dispose();
        }
    }

    private Book Alpha => State.Session!.Vault.FindBook("Buch - Alpha")!;

    private Book Beta => State.Session!.Vault.FindBook("Buch - Beta")!;

    private static Guid KeyOf(Book book, string content) => book.Page.AllBlocks().Single(b => b.Content == content).Key;

    private Guid KeyOf(string content) => KeyOf(Alpha, content);

    /// <summary>Puts the cursor (as the editor reports it) into the element whose block has <paramref name="content"/>.</summary>
    private void MoveTo(string content)
    {
        var element = BookElements.Find(State.CurrentBook!, KeyOf(State.CurrentBook!, content))!;
        State.Cursor = new CursorInfo(element.Kind, element.Key, BookElements.TextBlockOf(State.CurrentBook!, element)?.Key, 0);
    }

    private async Task OpenAsync()
    {
        await OpenSessionAsync(_vault);
        State.CurrentBook = Alpha;
        State.CurrentSectionKey = Alpha.Root.Key;
    }

    private Guid? CursorKey => State.Cursor?.Key;

    [Fact]
    public async Task Jump_RecordsAtOnce_TheOriginAndTheTarget()
    {
        await OpenAsync();
        MoveTo("Erster Text");

        State.JumpTo(KeyOf("Dritter Text"));

        Assert.True(State.CanGoBack);
        Assert.False(State.CanGoForward);
        Assert.Equal(KeyOf("Dritter Text"), CursorKey);

        await State.GoBackAsync();

        Assert.Equal(KeyOf("Erster Text"), CursorKey);
        Assert.Equal(KeyOf("Erster Text"), State.PendingReveal);
        Assert.Equal(KeyOf("# Eins"), State.CurrentSectionKey);
        Assert.True(State.CanGoForward);

        await State.GoForwardAsync();

        Assert.Equal(KeyOf("Dritter Text"), CursorKey);
        Assert.False(State.CanGoForward);
    }

    [Fact]
    public async Task CursorDwell_RecordsAfterTwoSeconds()
    {
        await OpenAsync();
        State.JumpTo(KeyOf("Erster Text"));

        MoveTo("Zweiter Text");
        Time.Advance(NavigationHistory.DwellTime);
        MoveTo("Ein Detail");
        Time.Advance(TimeSpan.FromSeconds(1));
        State.JumpTo(KeyOf("Vierter Text"));

        // Vierter ← Ein Detail (the jump's origin) ← Zweiter (dwelled) ← Erster.
        await State.GoBackAsync();
        Assert.Equal(KeyOf("Ein Detail"), CursorKey);
        await State.GoBackAsync();
        Assert.Equal(KeyOf("Zweiter Text"), CursorKey);
        await State.GoBackAsync();
        Assert.Equal(KeyOf("Erster Text"), CursorKey);
    }

    [Fact]
    public async Task CursorPassingQuicklyThroughBlocks_RecordsNothing()
    {
        await OpenAsync();
        State.JumpTo(KeyOf("Erster Text"));

        MoveTo("Zweiter Text");
        Time.Advance(TimeSpan.FromSeconds(1.9));
        MoveTo("Ein Detail");
        Time.Advance(TimeSpan.FromSeconds(1.9));
        MoveTo("Dritter Text");
        Time.Advance(TimeSpan.FromSeconds(1));
        State.JumpTo(KeyOf("Vierter Text"));

        // Vierter ← Dritter (the jump's origin) ← Erster: the blocks passed on the way are not in the history.
        await State.GoBackAsync();
        Assert.Equal(KeyOf("Dritter Text"), CursorKey);
        await State.GoBackAsync();
        Assert.Equal(KeyOf("Erster Text"), CursorKey);
    }

    [Fact]
    public async Task Back_FromABlockNotDwelledIn_ComesBackToTheLastEntry_AndForwardReturns()
    {
        await OpenAsync();
        MoveTo("Erster Text");
        State.JumpTo(KeyOf("Dritter Text"));
        MoveTo("Vierter Text");

        Assert.True(State.CanGoBack);
        await State.GoBackAsync();
        Assert.Equal(KeyOf("Dritter Text"), CursorKey);

        await State.GoForwardAsync();
        Assert.Equal(KeyOf("Vierter Text"), CursorKey);
    }

    [Fact]
    public async Task Back_WithoutAnEarlierEntry_RecordsTheBlock_AndNotifies()
    {
        await OpenAsync();
        MoveTo("Erster Text");
        var notified = 0;
        State.Changed += () => notified++;

        await State.GoBackAsync();

        // Nothing to go back to, but the active block is recorded now: the header shows the history as it is.
        Assert.Equal(1, notified);
        Assert.Equal(KeyOf("Erster Text"), CursorKey);
        MoveTo("Zweiter Text");
        Assert.True(State.CanGoBack);
    }

    [Fact]
    public async Task Dwell_NotifiesOnlyWhenBackOrForwardChanges()
    {
        await OpenAsync();
        MoveTo("Erster Text");
        State.JumpTo(KeyOf("Dritter Text"));
        var notified = 0;
        State.Changed += () => notified++;

        // Back was possible before and still is: no notification.
        MoveTo("Vierter Text");
        Time.Advance(NavigationHistory.DwellTime);
        Assert.Equal(0, notified);

        // After going back, a dwell elsewhere drops the forward entries: forward changes, the header is told.
        await State.GoBackAsync();
        Assert.True(State.CanGoForward);
        notified = 0;
        MoveTo("Zweiter Text");
        Time.Advance(NavigationHistory.DwellTime);
        Assert.False(State.CanGoForward);
        Assert.Equal(1, notified);
    }

    [Fact]
    public async Task NewJumpAfterBack_DropsTheForwardEntries()
    {
        await OpenAsync();
        MoveTo("Erster Text");
        State.JumpTo(KeyOf("Dritter Text"));
        await State.GoBackAsync();

        State.JumpTo(KeyOf("Vierter Text"));

        Assert.False(State.CanGoForward);
        await State.GoBackAsync();
        Assert.Equal(KeyOf("Erster Text"), CursorKey);
    }

    [Fact]
    public async Task Back_SwitchesTheBook_AndRaisesBookSelected()
    {
        await OpenAsync();
        MoveTo("Zweiter Text");
        var selected = new List<string>();
        State.BookSelected += book => selected.Add(book.LinkName);

        // A jump into another book selects it like the book selection does (so it is remembered).
        State.JumpTo(KeyOf(Beta, "Beta Text"), Beta);
        Assert.Equal("Buch - Beta", State.CurrentBook!.LinkName);
        Assert.Equal(["Buch - Beta"], selected);

        // A jump within the book selects nothing.
        State.JumpTo(KeyOf(Beta, "Beta Text"), Beta);
        Assert.Equal(["Buch - Beta"], selected);

        await State.GoBackAsync();

        Assert.Equal("Buch - Alpha", State.CurrentBook!.LinkName);
        Assert.Equal(KeyOf("Zweiter Text"), CursorKey);
        Assert.Equal(KeyOf("Zweiter Text"), State.PendingReveal);
        Assert.Equal(["Buch - Beta", "Buch - Alpha"], selected);

        await State.GoForwardAsync();

        Assert.Equal("Buch - Beta", State.CurrentBook!.LinkName);
        Assert.Equal(KeyOf(Beta, "Beta Text"), CursorKey);
        Assert.Equal(["Buch - Beta", "Buch - Alpha", "Buch - Beta"], selected);
    }

    [Fact]
    public async Task SelectBook_IsAJump_AndRaisesBookSelected()
    {
        await OpenAsync();
        MoveTo("Zweiter Text");
        var selected = new List<string>();
        State.BookSelected += book => selected.Add(book.LinkName);

        State.SelectBook(Beta);

        Assert.Equal("Buch - Beta", State.CurrentBook!.LinkName);
        Assert.Null(State.Cursor);
        Assert.Equal(Beta.Root.Key, State.CurrentSectionKey);
        Assert.Equal(["Buch - Beta"], selected);

        await State.GoBackAsync();
        Assert.Equal("Buch - Alpha", State.CurrentBook!.LinkName);
        Assert.Equal(KeyOf("Zweiter Text"), CursorKey);
    }

    [Fact]
    public async Task Back_FlushesTheEditorFirst()
    {
        await OpenAsync();
        MoveTo("Erster Text");
        State.JumpTo(KeyOf("Dritter Text"));
        var flushes = 0;
        State.FlushEditor = () =>
        {
            flushes++;
            return Task.CompletedTask;
        };

        await State.GoBackAsync();

        Assert.Equal(1, flushes);
    }

    [Fact]
    public async Task Back_ToABlockThatIsGone_SkipsIt()
    {
        await OpenAsync();
        MoveTo("Erster Text");
        State.JumpTo(KeyOf("Dritter Text"));
        State.RecordJump(new NavEntry("Buch - Alpha", Guid.NewGuid()));
        State.RecordJump(new NavEntry("Buch - Gamma", Alpha.Root.Key));
        State.JumpTo(KeyOf(Beta, "Beta Text"), Beta);

        // A block that is gone, and a book that is gone, are skipped; Dritter Text is found.
        await State.GoBackAsync();
        Assert.Equal("Buch - Alpha", State.CurrentBook!.LinkName);
        Assert.Equal(KeyOf("Dritter Text"), CursorKey);

        await State.GoBackAsync();

        Assert.Equal("Buch - Alpha", State.CurrentBook!.LinkName);
        Assert.Equal(KeyOf("Erster Text"), CursorKey);
    }

    [Fact]
    public async Task VaultSwitch_ClearsTheHistory()
    {
        await OpenAsync();
        MoveTo("Erster Text");
        State.JumpTo(KeyOf("Dritter Text"));
        Assert.True(State.CanGoBack);

        var old = State.Session!;
        State.Session = null;
        old.Dispose();
        await OpenAsync();

        Assert.False(State.CanGoBack);
        Assert.False(State.CanGoForward);
    }

    [Fact]
    public async Task VaultSwitch_StopsAPendingDwell()
    {
        await OpenAsync();
        MoveTo("Erster Text");

        var old = State.Session!;
        State.Session = null;
        old.Dispose();
        await OpenAsync();
        Time.Advance(NavigationHistory.DwellTime);
        MoveTo("Dritter Text");
        Time.Advance(NavigationHistory.DwellTime);

        // Only Dritter Text is in the history: nothing to go back to.
        Assert.False(State.CanGoBack);
    }

    [Fact]
    public async Task HeaderButtons_DisabledWithoutEntries_ClickGoesBackAndForward()
    {
        await OpenAsync();
        var header = Render<HeaderBar>();

        Assert.True(header.Find(".ne-nav-back").HasAttribute("disabled"));
        Assert.True(header.Find(".ne-nav-forward").HasAttribute("disabled"));
        Assert.Equal("Zurück (Alt+←)", header.Find(".ne-nav-back").GetAttribute("title"));
        Assert.Equal("Vor (Alt+→)", header.Find(".ne-nav-forward").GetAttribute("title"));

        MoveTo("Erster Text");
        State.JumpTo(KeyOf("Dritter Text"));
        State.Notify();
        header.WaitForAssertion(() =>
        {
            Assert.False(header.Find(".ne-nav-back").HasAttribute("disabled"));
            Assert.True(header.Find(".ne-nav-forward").HasAttribute("disabled"));
        });

        await header.Find(".ne-nav-back").ClickAsync(new());
        header.WaitForAssertion(() =>
        {
            Assert.Equal(KeyOf("Erster Text"), CursorKey);
            Assert.True(header.Find(".ne-nav-back").HasAttribute("disabled"));
            Assert.False(header.Find(".ne-nav-forward").HasAttribute("disabled"));
        });

        await header.Find(".ne-nav-forward").ClickAsync(new());
        header.WaitForAssertion(() =>
        {
            Assert.Equal(KeyOf("Dritter Text"), CursorKey);
            Assert.True(header.Find(".ne-nav-forward").HasAttribute("disabled"));
        });
    }

    [Fact]
    public async Task HeaderButtons_StandLeftOfUndo()
    {
        await OpenAsync();
        var header = Render<HeaderBar>();

        var classes = header.FindAll(".ne-header > button").Select(b => b.ClassName).ToList();
        Assert.Equal(classes.IndexOf("ne-nav-back") + 1, classes.IndexOf("ne-nav-forward"));
        Assert.Equal(classes.IndexOf("ne-nav-forward") + 1, classes.IndexOf("ne-undo"));
    }

    [Fact]
    public async Task HeaderOrder_NavigationUndoCommandsThenSpacerThenIndicators()
    {
        await OpenAsync();
        var header = Render<HeaderBar>(p => p
            .Add(c => c.OnHandledWizard, Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, () => { }))
            .Add(c => c.OnPdfExport, Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, () => { }))
            .Add(c => c.OnLinkCheck, Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, () => { }))
            .Add(c => c.OnSettings, Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, () => { })));

        var classes = header.FindAll(".ne-header > *").Select(e => e.ClassList.First()).ToList();
        string[] expected = ["ne-nav-back", "ne-nav-forward", "ne-undo", "ne-assistants", "ne-pdf", "ne-linkcheck", "ne-settings", "ne-header-spacer", "ne-ai"];
        var start = classes.IndexOf("ne-nav-back");
        Assert.Equal(expected, classes.Skip(start).Take(expected.Length));
    }

    [Fact]
    public async Task Shortcut_AltArrows_ThroughTheInteropEvent()
    {
        await OpenAsync();
        var header = Render<HeaderBar>();
        header.WaitForAssertion(() => JSInterop.VerifyInvoke("neNav.register"));
        MoveTo("Erster Text");
        State.JumpTo(KeyOf("Dritter Text"));

        await header.InvokeAsync(() => header.Instance.Navigate("back"));
        Assert.Equal(KeyOf("Erster Text"), CursorKey);

        await header.InvokeAsync(() => header.Instance.Navigate("forward"));
        Assert.Equal(KeyOf("Dritter Text"), CursorKey);

        // Anything else is ignored.
        await header.InvokeAsync(() => header.Instance.Navigate("up"));
        Assert.Equal(KeyOf("Dritter Text"), CursorKey);
    }

    [Fact]
    public async Task HeaderBookSelection_IsAJump()
    {
        await OpenAsync();
        var header = Render<HeaderBar>();
        MoveTo("Zweiter Text");

        await header.Find(".ne-book-select").ChangeAsync(new() { Value = "Buch - Beta" });

        Assert.Equal("Buch - Beta", State.CurrentBook!.LinkName);
        await State.GoBackAsync();
        Assert.Equal("Buch - Alpha", State.CurrentBook!.LinkName);
        Assert.Equal(KeyOf("Zweiter Text"), CursorKey);
    }

    [Fact]
    public async Task OutlineClick_IsAJump()
    {
        await OpenAsync();
        var outline = Render<OutlinePane>();
        MoveTo("Erster Text");

        await outline.InvokeAsync(() => outline.Instance.Select(Alpha.FindNode(KeyOf("# Zwei"))!));

        Assert.Equal(KeyOf("# Zwei"), CursorKey);
        await State.GoBackAsync();
        Assert.Equal(KeyOf("Erster Text"), CursorKey);
    }

    [Fact]
    public async Task ReferenceClick_IsAJump()
    {
        await OpenAsync();
        var pane = Render<EditorPane>();
        pane.WaitForAssertion(() => Assert.NotEmpty(Editor.Documents));
        MoveTo("Erster Text");

        await pane.InvokeAsync(() => Editor.Callbacks!.OnReferenceClicked(Guid.Parse(TargetId)));

        Assert.Equal(KeyOf("# Zwei"), CursorKey);

        // Back saves the shown editor first, on the renderer's thread as the header calls it.
        await pane.InvokeAsync(State.GoBackAsync);
        Assert.Equal(KeyOf("Erster Text"), CursorKey);
        pane.WaitForAssertion(() => Assert.Equal(KeyOf("Erster Text"), Editor.Reveals.LastOrDefault()));
    }
}
