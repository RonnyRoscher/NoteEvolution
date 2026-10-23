using System.Text.Json.Nodes;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Editor;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>The split view in the shell: two halves with outline and editor each, the divider, the header button and the settings.</summary>
public class SplitViewTests : UiTestContext
{
    private readonly TestVault _vault = TestVault.Create(
        ("pages/Buch - Alpha.md",
            "title:: Alpha\ntype:: book\n\n" +
            "- # Eins\n" +
            "\t- Erster Text\n" +
            "- # Zwei\n" +
            "\t- Dritter Text\n"));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _vault.Dispose();
        }
    }

    private Book Book => State.CurrentBook!;

    private Guid SectionKey(string title) => Book.Root.Children.Single(n => n.Title == title).Key;

    private Guid KeyOf(string content) => Book.Page.AllBlocks().Single(b => b.Content == content).Key;

    private string BookFile => File.ReadAllText(Path.Combine(_vault.Root, "pages", "Buch - Alpha.md"));

    /// <summary>Renders the shell with the vault open and every editor pane on its own fake editor (<c>Editors</c>).</summary>
    private IRenderedComponent<Shell> RenderShell()
    {
        UseEditorPerPane();
        Settings.LastVault = _vault.Root;
        var cut = Render<Shell>();
        cut.WaitForAssertion(() => Assert.NotEmpty(Editors[0].Documents), TimeSpan.FromSeconds(10));
        return cut;
    }

    private static IRenderedComponent<Shell> WaitForHalves(IRenderedComponent<Shell> cut, int halves)
    {
        cut.WaitForAssertion(() => Assert.Equal(halves, cut.FindAll(".ne-book-half").Count));
        return cut;
    }

    private static string WithFirstText(FakeEditorInterop editor, string text)
    {
        var doc = JsonNode.Parse(editor.Json)!;
        var block = doc["content"]!.AsArray().First(n => (string?)n!["type"] == "textBlock")!;
        block["content"]![0]!["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
        return doc.ToJsonString();
    }

    [Fact]
    public void Toggle_Button_SplitsIntoTwoHalves_AndSavesTheSetting()
    {
        var cut = RenderShell();
        Assert.Single(cut.FindAll(".ne-book-half"));
        Assert.Equal("false", cut.Find(".ne-split-toggle").GetAttribute("aria-pressed"));

        cut.Find(".ne-split-toggle").Click();

        WaitForHalves(cut, 2);
        var halves = cut.FindAll(".ne-book-half");
        Assert.All(halves, half =>
        {
            Assert.Single(half.QuerySelectorAll(".ne-outline-tree"));
            Assert.Single(half.QuerySelectorAll(".ne-editor-pane"));
        });
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindComponents<EditorPane>().Count));
        Assert.Equal("true", cut.Find(".ne-split-toggle").GetAttribute("aria-pressed"));
        Assert.True(State.IsSplit);
        Assert.True(Settings.SplitView);
        Assert.True(UiSettings.Load(Platform.UserDataDirectory).SplitView);

        cut.Find(".ne-split-toggle").Click();

        WaitForHalves(cut, 1);
        Assert.False(UiSettings.Load(Platform.UserDataDirectory).SplitView);
    }

    [Fact]
    public async Task Toggle_ViaInterop_ToggleSplit()
    {
        var cut = RenderShell();
        var header = cut.FindComponent<HeaderBar>();

        await cut.InvokeAsync(() => header.Instance.ToggleSplit());
        WaitForHalves(cut, 2);
        Assert.True(State.IsSplit);

        await cut.InvokeAsync(() => header.Instance.ToggleSplit());
        WaitForHalves(cut, 1);
        Assert.False(State.IsSplit);
    }

    [Fact]
    public void SavedSplit_WithoutVault_ShowsOneHalf_WithoutShareOrAccent()
    {
        Settings.SplitView = true;

        var cut = Render<Shell>();

        var half = Assert.Single(cut.FindAll(".ne-book-half"));
        Assert.Null(half.GetAttribute("style"));
        Assert.DoesNotContain("active", half.ClassList);
    }

    [Fact]
    public void Startup_WithSavedSplit_ShowsTwoHalves()
    {
        Settings.SplitView = true;

        var cut = RenderShell();

        WaitForHalves(cut, 2);
        Assert.True(State.IsSplit);
    }

    [Fact]
    public void Ratio_FromSettings_AppliedToTheHalves_ClampedOnLoad()
    {
        File.WriteAllText(Path.Combine(Platform.UserDataDirectory, UiSettings.FileName), """{ "SplitView": true, "SplitRatio": 0.95 }""");
        Assert.Equal(UiSettings.DefaultSplitRatio, UiSettings.Load(Platform.UserDataDirectory).SplitRatio);
        File.WriteAllText(Path.Combine(Platform.UserDataDirectory, UiSettings.FileName), """{ "SplitView": true, "SplitRatio": 0.3 }""");
        Assert.Equal(0.3, UiSettings.Load(Platform.UserDataDirectory).SplitRatio);

        Settings.SplitView = true;
        Settings.SplitRatio = UiSettings.Load(Platform.UserDataDirectory).SplitRatio;
        var cut = WaitForHalves(RenderShell(), 2);

        var halves = cut.FindAll(".ne-book-half");
        Assert.Contains("flex: 0.3 1 0", halves[0].GetAttribute("style"));
        Assert.Contains("flex: 0.7 1 0", halves[1].GetAttribute("style"));

        File.WriteAllText(Path.Combine(Platform.UserDataDirectory, UiSettings.FileName), """{ "SplitRatio": 0.95 }""");
        Assert.Equal(0.5, UiSettings.Load(Platform.UserDataDirectory).SplitRatio);
    }

    [Fact]
    public void Divider_ArrowKeys_StepByFiveHundredths_WithinBounds()
    {
        Settings.SplitView = true;
        var cut = WaitForHalves(RenderShell(), 2);
        var divider = () => cut.Find(".ne-split-divider");
        Assert.Equal("separator", divider().GetAttribute("role"));

        divider().KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        Assert.Equal(0.55, Settings.SplitRatio);
        Assert.Equal(0.55, UiSettings.Load(Platform.UserDataDirectory).SplitRatio);
        Assert.Contains("flex: 0.55 1 0", cut.FindAll(".ne-book-half")[0].GetAttribute("style"));

        divider().KeyDown(new KeyboardEventArgs { Key = "ArrowUp" });
        divider().KeyDown(new KeyboardEventArgs { Key = "ArrowUp" });
        Assert.Equal(0.45, Settings.SplitRatio);

        divider().KeyDown(new KeyboardEventArgs { Key = "a" });
        Assert.Equal(0.45, Settings.SplitRatio);

        for (var i = 0; i < 10; i++)
        {
            divider().KeyDown(new KeyboardEventArgs { Key = "ArrowUp" });
        }

        Assert.Equal(UiSettings.MinSplitRatio, Settings.SplitRatio);

        for (var i = 0; i < 20; i++)
        {
            divider().KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        }

        Assert.Equal(UiSettings.MaxSplitRatio, Settings.SplitRatio);
        Assert.Equal(UiSettings.MaxSplitRatio, UiSettings.Load(Platform.UserDataDirectory).SplitRatio);
    }

    [Fact]
    public void Divider_Drag_RatioFromThePointerInTheContainer_Clamped()
    {
        // The container starts 100 px from the top and is 400 px high.
        JSInterop.Setup<double>("neLayout.paneTop", _ => true).SetResult(100);
        JSInterop.Setup<double>("neLayout.paneHeight", i => i.Arguments[1] is true).SetResult(400);
        Settings.SplitView = true;
        var cut = WaitForHalves(RenderShell(), 2);

        cut.Find(".ne-split-divider").PointerDown(new PointerEventArgs { ClientY = 300 });
        cut.Find(".ne-row-splitter-overlay").PointerMove(new PointerEventArgs { ClientY = 220, Buttons = 1 });
        Assert.Equal(0.3, Settings.SplitRatio);

        cut.Find(".ne-row-splitter-overlay").PointerMove(new PointerEventArgs { ClientY = 5000, Buttons = 1 });
        cut.Find(".ne-row-splitter-overlay").PointerUp(new PointerEventArgs { ClientY = 5000 });

        Assert.Equal(UiSettings.MaxSplitRatio, Settings.SplitRatio);
        Assert.Empty(cut.FindAll(".ne-row-splitter-overlay"));
        Assert.Equal(UiSettings.MaxSplitRatio, UiSettings.Load(Platform.UserDataDirectory).SplitRatio);
    }

    [Fact]
    public void Divider_Drag_ContainerNoHigherThanTheDivider_LeavesTheRatio()
    {
        JSInterop.Setup<double>("neLayout.paneTop", _ => true).SetResult(100);
        JSInterop.Setup<double>("neLayout.paneHeight", i => i.Arguments[1] is true).SetResult(SplitDivider.Thickness);
        Settings.SplitView = true;
        Settings.SplitRatio = 0.4;
        var cut = WaitForHalves(RenderShell(), 2);

        cut.Find(".ne-split-divider").PointerDown(new PointerEventArgs { ClientY = 100 });
        cut.Find(".ne-row-splitter-overlay").PointerMove(new PointerEventArgs { ClientY = 300, Buttons = 1 });
        cut.Find(".ne-row-splitter-overlay").PointerUp(new PointerEventArgs { ClientY = 300 });

        Assert.Equal(0.4, Settings.SplitRatio);
    }

    [Fact]
    public async Task ActiveHalf_HasTheAccent_ExactlyOne_OnlyWhileSplit()
    {
        var cut = RenderShell();
        Assert.Empty(cut.FindAll(".ne-book-half.active"));

        cut.Find(".ne-split-toggle").Click();
        WaitForHalves(cut, 2);
        cut.WaitForAssertion(() => Assert.Equal(2, Editors.Count));
        Assert.Single(cut.FindAll(".ne-book-half.active"));
        Assert.Contains("active", cut.FindAll(".ne-book-half")[0].ClassList);

        await cut.InvokeAsync(() => Editors[1].Callbacks!.OnEditorFocused());

        cut.WaitForAssertion(() => Assert.Contains("active", cut.FindAll(".ne-book-half")[1].ClassList));
        Assert.Single(cut.FindAll(".ne-book-half.active"));
        Assert.Single(cut.FindAll(".ne-section-bar"));
    }

    [Fact]
    public void CollapseOutline_OnceInTheUpperHalf_CollapsesBothOutlines()
    {
        Settings.SplitView = true;
        var cut = WaitForHalves(RenderShell(), 2);
        Assert.Single(cut.FindAll(".ne-outline .ne-collapse"));

        cut.Find(".ne-outline .ne-collapse").Click();

        Assert.Equal(2, cut.FindAll(".ne-outline.collapsed").Count);
        Assert.Empty(cut.FindAll(".ne-outline-tree"));
    }

    [Fact]
    public async Task Unsplit_WhileLowerActive_UpperStays()
    {
        Settings.SplitView = true;
        var cut = WaitForHalves(RenderShell(), 2);
        cut.WaitForAssertion(() => Assert.NotEmpty(Editors[1].Documents));
        var upper = State.Views[0];
        var lower = State.Views[1];
        var upperSection = upper.CurrentSectionKey;
        await cut.InvokeAsync(() => Editors[1].Callbacks!.OnEditorFocused());
        var cursor = new CursorInfo(ElementKind.TextBlock, KeyOf("Dritter Text"), KeyOf("Dritter Text"), 0);
        await cut.InvokeAsync(() => Editors[1].Callbacks!.OnCursorChanged(cursor));
        Assert.Same(lower, State.ActiveView);
        Assert.Equal(SectionKey("Zwei"), State.CurrentSectionKey);

        cut.Find(".ne-split-toggle").Click();

        WaitForHalves(cut, 1);
        Assert.Single(State.Views);
        Assert.Same(upper, State.Views[0]);
        Assert.Same(upper, State.ActiveView);
        // What the bar and the "Relevant" tab ask for is the upper view's section.
        Assert.Equal(upperSection, State.CurrentSectionKey);
        Assert.Equal(Book.Root.Key, State.CurrentSectionKey);
        Assert.Null(State.Cursor);
        Assert.Single(cut.FindAll(".ne-section-bar"));
        Assert.Empty(cut.FindAll(".ne-book-half.active"));
    }

    [Fact]
    public async Task Unsplit_FlushesTheLowerEditorsPendingText_FirstSoItIsNotLost()
    {
        Settings.SplitView = true;
        var cut = WaitForHalves(RenderShell(), 2);
        cut.WaitForAssertion(() => Assert.NotEmpty(Editors[1].Documents));
        await cut.InvokeAsync(() => Editors[1].Callbacks!.OnEditorFocused());

        // Typed in the lower editor; the debounce has not run.
        await cut.InvokeAsync(() => Editors[1].Callbacks!.OnDocumentChanged(WithFirstText(Editors[1], "Unten getippt")));
        Assert.DoesNotContain("Unten getippt", BookFile);

        cut.Find(".ne-split-toggle").Click();

        cut.WaitForAssertion(() => Assert.Contains("Unten getippt", BookFile));
        WaitForHalves(cut, 1);
        Assert.False(State.IsSplit);
    }
}
