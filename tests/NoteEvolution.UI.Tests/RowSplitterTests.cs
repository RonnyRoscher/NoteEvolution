using Bunit;
using Microsoft.AspNetCore.Components.Web;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>The splitter between the "Zuletzt geändert" list and the outline (spec "Blockdaten" 4.3).</summary>
public class RowSplitterTests : UiTestContext
{
    private readonly List<TestVault> _vaults = [];

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _vaults.ForEach(v => v.Dispose());
        }
    }

    /// <summary>The list shows <paramref name="list"/> px of a pane that is <paramref name="pane"/> px tall.</summary>
    private void ShownHeights(double list, double pane)
    {
        JSInterop.Setup<double>("neLayout.paneHeight", i => i.Arguments[1] is false).SetResult(list);
        JSInterop.Setup<double>("neLayout.paneHeight", i => i.Arguments[1] is true).SetResult(pane);
    }

    private async Task<IRenderedComponent<OutlinePane>> RenderPaneAsync()
    {
        var vault = TestVault.Create(("pages/Buch - Alpha.md", "title:: Alpha\ntype:: book\n\n- # Eins\n"));
        _vaults.Add(vault);
        await OpenSessionAsync(vault);
        return Render<OutlinePane>();
    }

    private double SavedHeight => UiSettings.Load(Platform.UserDataDirectory).RecentListHeight;

    private static void Drag(IRenderedComponent<OutlinePane> cut, double from, params double[] to)
    {
        cut.Find(".ne-row-splitter").PointerDown(new PointerEventArgs { ClientY = from });
        foreach (var y in to)
        {
            cut.Find(".ne-row-splitter-overlay").PointerMove(new PointerEventArgs { ClientY = y });
        }
    }

    [Fact]
    public async Task Drag_ChangesHeight_WithinMinAndPaneMinusOutlineMinimum_SavedOnDragEnd()
    {
        ShownHeights(list: 180, pane: 600);
        var cut = await RenderPaneAsync();

        Drag(cut, 200, 260);
        Assert.Contains("height: 240px", cut.Find(".ne-recent").GetAttribute("style"));
        Assert.Equal(180, SavedHeight);

        cut.Find(".ne-row-splitter-overlay").PointerMove(new PointerEventArgs { ClientY = 5000 });
        Assert.Contains("height: 480px", cut.Find(".ne-recent").GetAttribute("style"));

        cut.Find(".ne-row-splitter-overlay").PointerUp(new PointerEventArgs { ClientY = -5000 });
        Assert.Contains("height: 60px", cut.Find(".ne-recent").GetAttribute("style"));
        Assert.Empty(cut.FindAll(".ne-row-splitter-overlay"));
        Assert.Equal(60, SavedHeight);
    }

    [Fact]
    public async Task Separator_IsFocusableAndHorizontal()
    {
        var cut = await RenderPaneAsync();

        var splitter = cut.Find(".ne-row-splitter");

        Assert.Equal("separator", splitter.GetAttribute("role"));
        Assert.Equal("horizontal", splitter.GetAttribute("aria-orientation"));
        Assert.Equal("0", splitter.GetAttribute("tabindex"));
    }

    [Fact]
    public async Task Arrows_ChangeHeightBy20_AndSaveEachTime()
    {
        ShownHeights(list: 180, pane: 600);
        var cut = await RenderPaneAsync();

        cut.Find(".ne-row-splitter").KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        Assert.Contains("height: 200px", cut.Find(".ne-recent").GetAttribute("style"));
        Assert.Equal(200, SavedHeight);

        ShownHeights(list: 200, pane: 600);
        cut.Find(".ne-row-splitter").KeyDown(new KeyboardEventArgs { Key = "ArrowUp" });
        cut.Find(".ne-row-splitter").KeyDown(new KeyboardEventArgs { Key = "a" });
        Assert.Contains("height: 180px", cut.Find(".ne-recent").GetAttribute("style"));
        Assert.Equal(180, SavedHeight);
    }

    [Fact]
    public async Task Arrows_StopAtTheLimits()
    {
        ShownHeights(list: 70, pane: 600);
        var cut = await RenderPaneAsync();

        cut.Find(".ne-row-splitter").KeyDown(new KeyboardEventArgs { Key = "ArrowUp" });

        Assert.Equal(60, Settings.RecentListHeight);

        ShownHeights(list: 470, pane: 600);
        cut.Find(".ne-row-splitter").KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });

        Assert.Equal(480, Settings.RecentListHeight);
    }

    [Fact]
    public async Task NewRender_RestoresStoredHeight()
    {
        Settings.RecentListHeight = 250;

        var cut = await RenderPaneAsync();

        Assert.Contains("height: 250px", cut.Find(".ne-recent").GetAttribute("style"));
    }

    [Fact]
    public async Task StoredHeightTallerThanPane_StaysStored_ArrowUsesWhatIsShown()
    {
        // The pane is 600 px tall and shows at most 480 px of the stored 900 (the CSS cuts it back).
        Settings.RecentListHeight = 900;
        ShownHeights(list: 480, pane: 600);
        var cut = await RenderPaneAsync();

        Assert.Equal(900, Settings.RecentListHeight);
        Assert.Contains("height: 900px", cut.Find(".ne-recent").GetAttribute("style"));

        cut.Find(".ne-row-splitter").KeyDown(new KeyboardEventArgs { Key = "ArrowUp" });

        Assert.Equal(460, Settings.RecentListHeight);
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(0)]
    [InlineData(59)]
    [InlineData(100000)]
    public void Load_NegativeOrAbsurdHeight_GivesDefault(double stored)
    {
        using var dir = new TempDir();
        dir.Write("ui.json", $"{{ \"RecentListHeight\": {stored} }}");

        Assert.Equal(180, UiSettings.Load(dir.Path).RecentListHeight);
    }

    [Fact]
    public void Load_ValidHeight_IsKept()
    {
        using var dir = new TempDir();
        new UiSettings { RecentListHeight = 321.5 }.Save(dir.Path);

        Assert.Equal(321.5, UiSettings.Load(dir.Path).RecentListHeight);
    }
}
