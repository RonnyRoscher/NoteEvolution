using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components.Dialogs;
using NoteEvolution.UI.Resources;

namespace NoteEvolution.UI.Tests;

/// <summary>
/// The assistant „Gliederung nach Ebenen ordnen“ (Einordnen spec 3): the preview names the misplaced headings, running it
/// places them all as one undo step, and an ordered outline has nothing to do.
/// </summary>
public class NormalizeOutlineTests : UiTestContext
{
    private const string BookPath = "pages/Buch - Alpha.md";

    // "# Zwei" stands under "# Eins", "## Fünf" under "## Vier": both are warned about.
    private const string BookText =
        "title:: Alpha\ntype:: book\n\n" +
        "- # Eins\n" +
        "\t- Text Eins\n" +
        "\t- # Zwei\n" +
        "\t\t- Text Zwei\n" +
        "- # Drei\n" +
        "\t- ## Vier\n" +
        "\t\t- ## Fünf\n" +
        "\t\t\t- Text Fünf\n";

    private const string Ordered =
        "title:: Alpha\ntype:: book\n\n" +
        "- # Eins\n" +
        "\t- Text Eins\n" +
        "- # Zwei\n" +
        "\t- Text Zwei\n" +
        "- # Drei\n" +
        "\t- ## Vier\n" +
        "\t- ## Fünf\n" +
        "\t\t- Text Fünf\n";

    private string Text(string key, params object[] arguments) =>
        Services.GetRequiredService<IStringLocalizer<Strings>>()[key, arguments].Value;

    [Fact]
    public async Task Normalize_Preview_Run_Undo()
    {
        using var vault = TestVault.Create((BookPath, BookText));
        var session = await OpenSessionAsync(vault);
        var path = Path.Combine(vault.Root, BookPath);
        var before = File.ReadAllBytes(path);
        var cut = Render<NormalizeOutlineDialog>();

        // The preview: how many and which, with their level marks; nothing is changed by it.
        Assert.Equal(Text("NormalizeCount", 2), cut.Find(".ne-normalize-count").TextContent);
        Assert.Equal(["# Zwei", "## Fünf"], cut.FindAll(".ne-normalize-list .ne-normalize-item").Select(item => item.TextContent));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Null(session.Undo.NextDescription);

        await cut.InvokeAsync(() => cut.Find(".ne-normalize-run").Click());

        // Every heading has its place by its level; the order of the text is the same.
        Assert.Equal(Ordered, vault.Read(BookPath));
        Assert.Empty(State.CurrentBook!.Warnings);
        Assert.Equal(Text("NormalizeDone", 2), cut.Find(".ne-normalize-done").TextContent);
        Assert.Equal(Text("NormalizeNone"), cut.Find(".ne-normalize-none").TextContent);
        Assert.Empty(cut.FindAll(".ne-normalize-run"));
        Assert.Equal(Text("UndoNormalize"), session.Undo.NextDescription);

        // One undo step takes it all back.
        Assert.True(session.TryUndo(out var error), error?.ToString());
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Null(session.Undo.NextDescription);
    }

    [Fact]
    public async Task Normalize_OrderedOutline_NothingToDo()
    {
        using var vault = TestVault.Create((BookPath, Ordered));
        await OpenSessionAsync(vault);

        var cut = Render<NormalizeOutlineDialog>();

        Assert.Equal(Text("NormalizeNone"), cut.Find(".ne-normalize-none").TextContent);
        Assert.Empty(cut.FindAll(".ne-normalize-run"));
        Assert.Empty(cut.FindAll(".ne-normalize-list"));
    }

    [Fact]
    public async Task Normalize_OneHeading_UnplaceableListedApart()
    {
        // A heading in a text block whose title holds a reference cannot become a heading: it stays and is named apart.
        const string id = "11111111-1111-4111-8111-111111111111";
        const string text =
            "title:: Alpha\ntype:: book\n\n" +
            "- # Eins\n" +
            "\t- Ziel\n" +
            $"\t  id:: {id}\n" +
            "\t\t- ## Mit ((" + id + "))\n" +
            "\t- # Zwei\n";
        using var vault = TestVault.Create((BookPath, text));
        await OpenSessionAsync(vault);

        var cut = Render<NormalizeOutlineDialog>();

        Assert.Equal(Text("NormalizeCountOne"), cut.Find(".ne-normalize-count").TextContent);
        Assert.Equal(Text("NormalizeUnplaceable"), cut.Find(".ne-normalize-unplaceable").TextContent);
        Assert.Equal(
            ["# Zwei", $"## Mit (({id}))"],
            cut.FindAll(".ne-normalize-item").Select(item => item.TextContent));

        await cut.InvokeAsync(() => cut.Find(".ne-normalize-run").Click());

        Assert.Equal(text.Replace("\t- # Zwei\n", "- # Zwei\n"), vault.Read(BookPath));
        Assert.Equal(Text("NormalizeDoneOne"), cut.Find(".ne-normalize-done").TextContent);
    }
}
