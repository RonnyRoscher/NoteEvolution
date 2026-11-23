using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace NoteEvolution.UI.Tests;

/// <summary>
/// An element found before a render still reaches its handler: the app's background work re-renders components
/// between a test's find and its click (<see cref="LiveElements"/>).
/// </summary>
public sealed class StaleElementTests : BunitContext
{
    [Fact]
    public void Find_RenderBeforeTheClick_ReachesTheHandler()
    {
        var cut = Render<Clicker>();
        var button = cut.Find("button");
        cut.Render();

        button.Click();

        Assert.Equal(1, cut.Instance.Clicks);
    }

    [Fact]
    public void FindAll_RenderBeforeTheClick_ReachesTheHandler()
    {
        var cut = Render<Clicker>();
        var button = cut.FindAll("button")[1];
        cut.Render();

        button.Click();

        Assert.Equal(2, cut.Instance.Clicks);
    }

    [Fact]
    public void FindAllTyped_RenderBeforeTheClick_ReachesTheHandler()
    {
        var cut = Render<Clicker>();
        var button = cut.FindAll<Clicker, AngleSharp.Html.Dom.IHtmlButtonElement>("button")[0];
        cut.Render();

        button.Click();

        Assert.Equal(1, cut.Instance.Clicks);
    }

    [Fact]
    public void FindInsideAFoundElement_RenderBeforeTheClick_ReachesTheHandler()
    {
        var cut = Render<Clicker>();
        var button = cut.FindAll("div")[1].Find("button");
        cut.Render();

        button.Click();

        Assert.Equal(2, cut.Instance.Clicks);
    }

    [Fact]
    public async Task Find_OnlyAChildRendersBeforeTheClick_ReachesTheHandler()
    {
        var cut = Render<Host>();
        var button = cut.Find("button");
        var child = cut.FindComponent<Clicker>().Instance;
        await cut.InvokeAsync(child.Refresh);

        button.Click();

        Assert.Equal(1, child.Clicks);
    }

    [Fact]
    public async Task FindAll_OnlyAChildRendersBeforeTheClick_ReachesTheHandler()
    {
        var cut = Render<Host>();
        var button = cut.FindAll("button")[1];
        var child = cut.FindComponent<Clicker>().Instance;
        await cut.InvokeAsync(child.Refresh);

        button.Click();

        Assert.Equal(2, child.Clicks);
    }

    [Fact]
    public async Task FindAndClick_WhileTheBackgroundKeepsRendering_EveryClickArrives()
    {
        const int clicks = 300;
        var cut = Render<Host>();
        var child = cut.FindComponent<Clicker>().Instance;
        using var stop = new CancellationTokenSource();
        var renders = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await cut.InvokeAsync(child.Refresh);
            }
        });

        try
        {
            for (var i = 0; i < clicks; i++)
            {
                // Each way to find the first button, which adds 1.
                var button = (i % 3) switch
                {
                    0 => cut.Find("button"),
                    1 => cut.FindAll("button")[0],
                    _ => cut.FindAll("div")[0].Find("button"),
                };
                button.Click();
            }
        }
        finally
        {
            await stop.CancelAsync();
            await renders;
        }

        Assert.Equal(clicks, child.Clicks);
    }

    [Fact]
    public void FindInsideAFoundElement_NoMatch_Throws()
    {
        var cut = Render<Clicker>();

        var error = Assert.Throws<ElementNotFoundException>(() => cut.FindAll("div")[0].Find("input"));

        Assert.Contains("div (match 1) input", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FindAll_MatchGoneAfterARender_SaysWhichOne()
    {
        var cut = Render<Clicker>();
        var button = cut.FindAll("button")[1];
        cut.Instance.Steps = [1];
        cut.Render();

        var error = Assert.Throws<ElementNotFoundException>(() => button.Click());

        Assert.Contains("button (match 2)", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A parent that does not render again when its child does.</summary>
    private sealed class Host : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "section");
            builder.OpenComponent<Clicker>(1);
            builder.CloseComponent();
            builder.CloseElement();
        }
    }

    private sealed class Clicker : ComponentBase
    {
        public int Clicks { get; private set; }

        public void Refresh() => StateHasChanged();

        public int[] Steps { get; set; } = [1, 2];

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            foreach (var step in Steps)
            {
                // A handler that captures a loop variable is a new delegate on every render, so it gets a new id.
                builder.OpenElement(0, "div");
                builder.OpenElement(1, "button");
                builder.AddAttribute(2, "onclick", EventCallback.Factory.Create(this, () => Clicks += step));
                builder.CloseElement();
                builder.CloseElement();
            }
        }
    }
}
