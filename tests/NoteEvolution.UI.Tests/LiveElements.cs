using System.Runtime.CompilerServices;
using AngleSharp.Dom;
using Bunit;
using Bunit.Web.AngleSharp;
using Microsoft.AspNetCore.Components;

namespace NoteEvolution.UI.Tests;

/// <summary>
/// <c>Find</c> and <c>FindAll</c> whose elements follow the component's renders, and <see cref="Find(IElement, string)"/>
/// for an element inside one of them.
/// </summary>
/// <remarks>
/// The app's background work (opening the vault, indexing, the repetition analysis) re-renders components at any time.
/// A render between finding an element and triggering an event on it gives the element's handler a new id, and the
/// event fails with "There is no event handler with ID …". bUnit's <c>Find</c> returns an element that is read again
/// after a render, its <c>FindAll</c> and AngleSharp's <c>QuerySelector</c> do not. The extensions here are declared in
/// the tests' own namespace, so the compiler picks them over bUnit's for every <c>cut.Find(…)</c> and
/// <c>cut.FindAll(…)</c> in the suite. Each element they return is located again after a render: the selector's match
/// at the same position, or the match inside the element it was found in. The event then reads the current handler id
/// in the same dispatch that triggers it.
/// </remarks>
internal static class LiveElements
{
    private static readonly ConditionalWeakTable<IElement, Locator> Locators = new();

    public static IElement Find<TComponent>(this IRenderedComponent<TComponent> cut, string cssSelector)
        where TComponent : IComponent =>
        cut.Find<TComponent, IElement>(cssSelector);

    public static TElement Find<TComponent, TElement>(this IRenderedComponent<TComponent> cut, string cssSelector)
        where TComponent : IComponent
        where TElement : class, IElement
    {
        var target = (IRenderedComponent<IComponent>)cut;
        return Live<TElement>(target, () => target.Nodes.QuerySelectorAll(cssSelector).OfType<TElement>().FirstOrDefault(), cssSelector);
    }

    public static IReadOnlyList<IElement> FindAll<TComponent>(this IRenderedComponent<TComponent> cut, string cssSelector)
        where TComponent : IComponent =>
        cut.FindAll<TComponent, IElement>(cssSelector);

    public static IReadOnlyList<TElement> FindAll<TComponent, TElement>(this IRenderedComponent<TComponent> cut, string cssSelector)
        where TComponent : IComponent
        where TElement : class, IElement
    {
        var target = (IRenderedComponent<IComponent>)cut;
        IEnumerable<TElement> Matches() => target.Nodes.QuerySelectorAll(cssSelector).OfType<TElement>();
        return Enumerable.Range(0, OnDispatcher(target, () => Matches().Count()))
            .Select(index => Live<TElement>(target, () => Matches().ElementAtOrDefault(index), $"{cssSelector} (match {index + 1})"))
            .ToList();
    }

    /// <summary>
    /// The first element inside <paramref name="found"/> that matches <paramref name="cssSelector"/>, located again
    /// after a render like <paramref name="found"/> itself; for an element that did not come from these extensions, the
    /// match as it is now. Throws <see cref="ElementNotFoundException"/> if there is none.
    /// </summary>
    public static IElement Find(this IElement found, string cssSelector)
    {
        if (!Locators.TryGetValue(found, out var outer))
        {
            return found.QuerySelector(cssSelector) ?? throw new ElementNotFoundException(cssSelector);
        }

        return Live<IElement>(outer.Target, () => outer.Locate()?.QuerySelector(cssSelector), $"{outer.Description} {cssSelector}");
    }

    private static TElement Live<TElement>(IRenderedComponent<IComponent> target, Func<IElement?> locate, string description)
        where TElement : class, IElement
    {
        var locator = new Locator(target, locate, description);
        var (found, renders) = OnDispatcher(target, () => (locate(), locator.Renders));
        if (found is not TElement element)
        {
            throw new ElementNotFoundException(description);
        }

        var wrapped = (TElement)element.WrapUsing(locator);
        Locators.Add(wrapped, locator);
        locator.Attach(wrapped, renders);
        return wrapped;
    }

    /// <summary>
    /// Runs <paramref name="read"/> on the renderer's dispatcher (at once if this already is it). bUnit's
    /// <c>Nodes</c> is not safe to read beside a render: read on the test thread while the background work renders, it
    /// can parse the old markup after the render cleared it and keep those nodes, with their old handler ids, until
    /// the next render.
    /// </summary>
    private static T OnDispatcher<T>(IRenderedComponent<IComponent> target, Func<T> read) =>
        target.InvokeAsync(read).GetAwaiter().GetResult();

    /// <summary>
    /// Finds the element again after each render of <see cref="Target"/>, right there on the dispatcher. bUnit's wrapper
    /// keeps the element it got; if the test thread fetched it, a render could fall between the fetch and the wrapper
    /// keeping it, and the wrapper would keep the element of the render before. Fetched in the render instead, the
    /// wrapper always holds the element of the latest render.
    /// </summary>
    private sealed class Locator : IElementWrapperFactory
    {
        private readonly Func<IElement?> _locate;
        private WeakReference<IElement>? _wrapper;

        public Locator(IRenderedComponent<IComponent> target, Func<IElement?> locate, string description)
        {
            Target = target;
            Description = description;
            _locate = locate;
            target.OnMarkupUpdated += (_, _) =>
            {
                Renders++;
                Refresh();
            };
        }

        /// <summary>The renders of <see cref="Target"/> since the locator was made; changed on the dispatcher only.</summary>
        public int Renders { get; private set; }

        public IRenderedComponent<IComponent> Target { get; }

        public string Description { get; }

        public Action? OnElementReplaced { get; set; }

        /// <summary>The element as it is now; call on the dispatcher (from another locator's location).</summary>
        public IElement? Locate() => _locate();

        public TElement GetElement<TElement>() where TElement : class, IElement =>
            OnDispatcher(Target, _locate) as TElement ?? throw new ElementNotFoundException(Description);

        /// <summary>
        /// Starts refreshing <paramref name="wrapper"/>, which holds the element of the render
        /// <paramref name="renders"/>; a render since then is caught up at once.
        /// </summary>
        public void Attach(IElement wrapper, int renders)
        {
            _wrapper = new WeakReference<IElement>(wrapper);
            OnDispatcher(Target, () =>
            {
                if (Renders != renders)
                {
                    Refresh();
                }

                return true;
            });
        }

        /// <summary>On the dispatcher: lets the wrapper drop its element and fetch the current one.</summary>
        private void Refresh()
        {
            OnElementReplaced?.Invoke();
            if (_wrapper?.TryGetTarget(out var wrapper) == true)
            {
                try
                {
                    _ = wrapper.LocalName;
                }
                catch (ElementNotFoundException)
                {
                    // The element is gone after this render: the wrapper stays empty, and its next use says so.
                }
            }
        }
    }
}
