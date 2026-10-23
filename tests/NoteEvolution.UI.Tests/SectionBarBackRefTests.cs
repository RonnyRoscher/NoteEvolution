using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Editor;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>"↩ N Verweise hierher" in the section bar (cross references spec 2.3).</summary>
public class SectionBarBackRefTests : UiTestContext
{
    private const string BookPath = "pages/Buch - Alpha.md";

    private const string TargetId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";

    private const string BookText =
        "title:: Alpha\ntype:: book\n\n" +
        "- # Eins\n" +
        "\t- Erster Text\n" +
        $"\t  id:: {TargetId}\n" +
        $"\t- Zweiter (({TargetId})) Text\n" +
        "- # Zwei\n" +
        "\t- Dritter Text\n" +
        "\t- Vierter Text\n";

    private readonly TestVault _vault = TestVault.Create((BookPath, BookText));

    protected override void Dispose(bool disposing)
    {
        // The session is closed first, then the vault folder is deleted.
        base.Dispose(disposing);
        if (disposing)
        {
            _vault.Dispose();
        }
    }

    private Book Book => State.CurrentBook!;

    private string Text(string key, params object[] arguments) =>
        Services.GetRequiredService<IStringLocalizer<Strings>>()[key, arguments].Value;

    private Guid KeyOf(string content) => Book.Page.AllBlocks().Single(b => b.Content == content).Key;

    private async Task<IRenderedComponent<EditorPane>> RenderAsync()
    {
        await OpenSessionAsync(_vault);
        var cut = Render<EditorPane>();
        cut.WaitForAssertion(() => Assert.NotEmpty(Editor.Documents));
        await cut.InvokeAsync(() => Editor.Callbacks!.OnSectionBoxMoved(120.5, true));
        return cut;
    }

    private Task MoveToAsync(IRenderedComponent<EditorPane> cut, string content, int offset = 0)
    {
        var element = BookElements.Find(Book, KeyOf(content))!;
        var cursor = new CursorInfo(element.Kind, element.Key, BookElements.TextBlockOf(Book, element)?.Key, offset);
        return cut.InvokeAsync(() => Editor.Callbacks!.OnCursorChanged(cursor));
    }

    [Fact]
    public async Task BackRefs_ShownOnlyForATargetWithReferences()
    {
        var cut = await RenderAsync();

        await MoveToAsync(cut, "Erster Text");
        cut.WaitForAssertion(() => Assert.Equal(Text("SectionBackRefsOne"), cut.Find(".ne-sec-backrefs").TextContent.Trim()));

        // The referring block itself, a block without id and a heading have none.
        foreach (var content in new[] { "Zweiter ((" + TargetId + ")) Text", "Dritter Text", "# Eins" })
        {
            await MoveToAsync(cut, content);
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-sec-backrefs")));
        }
    }

    [Fact]
    public async Task BackRefs_Expand_ListsSectionAndTextStart_ClickReveals()
    {
        var cut = await RenderAsync();
        await MoveToAsync(cut, "Erster Text");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ne-sec-backrefs")));
        Assert.Empty(cut.FindAll(".ne-sec-backref-list"));

        cut.Find(".ne-sec-backrefs").Click();

        Assert.Equal(Text("SectionBackRefsOneOpen"), cut.Find(".ne-sec-backrefs").TextContent.Trim());
        var entry = Assert.Single(cut.FindAll(".ne-sec-backref"));
        Assert.Equal("Eins › Zweiter → Text", entry.TextContent.Trim());

        var referrer = KeyOf("Zweiter ((" + TargetId + ")) Text");
        await cut.InvokeAsync(() => entry.Click());

        cut.WaitForAssertion(() => Assert.Equal(referrer, Assert.Single(Editor.Reveals)));
        Assert.Equal(referrer, State.Cursor!.Key);
    }

    [Fact]
    public async Task BackRefs_CountFollowsASaveThatAddsAReference_InBookOrder()
    {
        var cut = await RenderAsync();
        await MoveToAsync(cut, "Erster Text");
        cut.WaitForAssertion(() => Assert.Equal(Text("SectionBackRefsOne"), cut.Find(".ne-sec-backrefs").TextContent.Trim()));
        cut.Find(".ne-sec-backrefs").Click();

        // „Verweis einfügen“ in another block saves a new reference to the same target.
        await MoveToAsync(cut, "Vierter Text", 8);
        await cut.InvokeAsync(() => cut.Find(".ne-sec-ref").Click());
        await cut.InvokeAsync(() => cut.Find(".ne-ref-query").Input("Erster"));
        await cut.InvokeAsync(() => cut.Find(".ne-ref-query").KeyDown("Enter"));
        cut.WaitForAssertion(() => Assert.Equal(3, _vault.Read(BookPath).Split(TargetId).Length - 1));

        await MoveToAsync(cut, "Erster Text");
        cut.WaitForAssertion(() => Assert.Equal(Text("SectionBackRefs", 2), cut.Find(".ne-sec-backrefs").TextContent.Trim()));
        cut.Find(".ne-sec-backrefs").Click();
        var entries = cut.FindAll(".ne-sec-backref").Select(e => e.TextContent.Trim()).ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal("Eins › Zweiter → Text", entries[0]);
        Assert.StartsWith("Zwei › Vierter", entries[1]);
    }

    [Fact]
    public async Task BackRefs_FromASubPoint_ShowsTheSubPointsOwnText()
    {
        var text = BookText.Replace("\t- Dritter Text\n", $"\t- Dritter Text\n\t\t- Unterpunkt (({TargetId})) hier #notiz\n");
        File.WriteAllText(Path.Combine(_vault.Root, BookPath), text);
        var cut = await RenderAsync();
        await MoveToAsync(cut, "Erster Text");
        cut.WaitForAssertion(() => Assert.Equal(Text("SectionBackRefs", 2), cut.Find(".ne-sec-backrefs").TextContent.Trim()));

        cut.Find(".ne-sec-backrefs").Click();

        Assert.Equal(
            ["Eins › Zweiter → Text", "Zwei › Unterpunkt → hier"],
            cut.FindAll(".ne-sec-backref").Select(e => e.TextContent.Trim()));
    }

    [Fact]
    public async Task BackRefs_TwoReferencesInOneBlock_CountOnce_ListCollapsesWithTheElement()
    {
        var text = BookText.Replace($"Zweiter (({TargetId})) Text", $"Zweiter (({TargetId})) und (({TargetId})) Text");
        File.WriteAllText(Path.Combine(_vault.Root, BookPath), text);
        var cut = await RenderAsync();
        await MoveToAsync(cut, "Erster Text");
        cut.WaitForAssertion(() => Assert.Equal(Text("SectionBackRefsOne"), cut.Find(".ne-sec-backrefs").TextContent.Trim()));
        cut.Find(".ne-sec-backrefs").Click();
        Assert.Single(cut.FindAll(".ne-sec-backref"));

        // Another element with references starts collapsed.
        await MoveToAsync(cut, "Dritter Text");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ne-sec-backref-list")));
        await MoveToAsync(cut, "Erster Text");
        cut.WaitForAssertion(() => Assert.Equal(Text("SectionBackRefsOne"), cut.Find(".ne-sec-backrefs").TextContent.Trim()));
        Assert.Empty(cut.FindAll(".ne-sec-backref-list"));
    }
}
