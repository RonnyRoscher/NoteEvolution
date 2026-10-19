using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>The left pane: the collapsible "Zuletzt geändert" list above the outline with its label.</summary>
public class RecentListCollapseTests : UiTestContext
{
    private readonly TestVault _vault = TestVault.Create(
        ("pages/Buch - Alpha.md", "title:: Alpha\ntype:: book\n\n- # Eins\n\t- Text\n\t  updated:: 2026-10-01T10:00\n"));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _vault.Dispose();
        }
    }

    private string Text(string key) => Services.GetRequiredService<IStringLocalizer<Strings>>()[key].Value;

    private async Task<IRenderedComponent<OutlinePane>> RenderPaneAsync()
    {
        await OpenSessionAsync(_vault);
        return Render<OutlinePane>();
    }

    private static void AssertExpanded(IRenderedComponent<OutlinePane> cut, bool expanded)
    {
        var toggle = cut.Find(".ne-recent-toggle");
        Assert.Equal(expanded ? "true" : "false", toggle.GetAttribute("aria-expanded"));
        Assert.StartsWith(expanded ? "▾" : "▸", toggle.TextContent);
        Assert.Equal(expanded ? 1 : 0, cut.FindAll(".ne-recent-list").Count);
        Assert.Equal(expanded ? 1 : 0, cut.FindAll(".ne-row-splitter").Count);
        Assert.Single(cut.FindAll(".ne-outline-tree"));
    }

    [Fact]
    public async Task Order_ListHeader_List_Splitter_OutlineLabel_CoverageToggle_Tree()
    {
        var cut = await RenderPaneAsync();

        var markup = cut.Markup;
        int At(string cssClass) => markup.IndexOf($"class=\"{cssClass}", StringComparison.Ordinal);
        string[] order = ["ne-recent-toggle", "ne-recent-list", "ne-row-splitter", "ne-outline-label", "ne-outline-coverage-toggle", "ne-outline-tree"];
        Assert.All(order, cssClass => Assert.True(At(cssClass) >= 0, cssClass));
        Assert.Equal(order, order.OrderBy(At));
        Assert.Equal(Text("Outline"), cut.Find(".ne-outline-label").TextContent);
        Assert.Contains(Text("RecentTitle"), cut.Find(".ne-recent-toggle").TextContent);
    }

    [Fact]
    public async Task Toggle_HidesAndShowsListAndSplitter_SavesTheSetting_KeepsTheHeight()
    {
        Settings.RecentListHeight = 250;
        var cut = await RenderPaneAsync();
        AssertExpanded(cut, true);
        Assert.Equal(Text("Collapse"), cut.Find(".ne-recent-toggle").GetAttribute("title"));

        cut.Find(".ne-recent-toggle").Click();

        AssertExpanded(cut, false);
        Assert.Equal(Text("Expand"), cut.Find(".ne-recent-toggle").GetAttribute("title"));
        Assert.Null(cut.Find(".ne-recent").GetAttribute("style"));
        Assert.True(Settings.RecentListCollapsed);
        Assert.True(UiSettings.Load(Platform.UserDataDirectory).RecentListCollapsed);
        Assert.Equal(250, Settings.RecentListHeight);

        cut.Find(".ne-recent-toggle").Click();

        AssertExpanded(cut, true);
        Assert.Contains("height: 250px", cut.Find(".ne-recent").GetAttribute("style"));
        Assert.False(UiSettings.Load(Platform.UserDataDirectory).RecentListCollapsed);
        Assert.Equal(250, UiSettings.Load(Platform.UserDataDirectory).RecentListHeight);
    }

    [Fact]
    public async Task Collapsed_RestoredOnANewRender()
    {
        var cut = await RenderPaneAsync();
        cut.Find(".ne-recent-toggle").Click();
        Settings.RecentListCollapsed = UiSettings.Load(Platform.UserDataDirectory).RecentListCollapsed;

        var again = Render<OutlinePane>();

        AssertExpanded(again, false);
    }
}
