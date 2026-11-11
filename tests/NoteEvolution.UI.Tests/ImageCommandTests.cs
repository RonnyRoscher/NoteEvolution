using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Editor;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>Images as blocks: the insert, caption and load commands and the editor pane's handlers.</summary>
public class ImageCommandTests : UiTestContext
{
    private const string BookPath = "pages/Buch - Alpha.md";

    private const string BookText =
        "title:: Alpha\ntype:: book\n\n" +
        "- # Eins\n" +
        "\t- Erster Text\n" +
        "\t- Zweiter Text\n";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private readonly TestVault _vault = TestVault.Create((BookPath, BookText));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _vault.Dispose();
        }
    }

    private Book Book => State.CurrentBook!;

    private string Assets => Path.Combine(_vault.Root, "assets");

    private Guid KeyOf(string content) => Book.Page.AllBlocks().Single(b => b.Content == content).Key;

    private async Task<IRenderedComponent<EditorPane>> RenderAsync(string cursorIn = "Erster Text")
    {
        await OpenSessionAsync(_vault);
        var cut = Render<EditorPane>();
        cut.WaitForAssertion(() => Assert.NotEmpty(Editor.Documents));
        var element = BookElements.Find(Book, KeyOf(cursorIn))!;
        var cursor = new CursorInfo(element.Kind, element.Key, BookElements.TextBlockOf(Book, element)?.Key, 0);
        await cut.InvokeAsync(() => Editor.Callbacks!.OnCursorChanged(cursor));
        return cut;
    }

    [Fact]
    public async Task Insert_WritesFileAndBlockAfterCursorBlock_UndoKeepsFile()
    {
        var cut = await RenderAsync();

        var message = await cut.InvokeAsync(() =>
            ManuscriptCommands.InsertImageAsync(State, "foto.png", Png, "Bild einfügen", NullLogger.Instance));

        Assert.Null(message);
        Assert.Equal(Png, File.ReadAllBytes(Path.Combine(Assets, "foto.png")));
        var text = _vault.Read(BookPath);
        var lines = text.Split('\n');
        var index = Array.FindIndex(lines, l => l == "\t- Erster Text");
        Assert.Equal("\t- ![](../assets/foto.png)", lines[index + 1]);
        Assert.Contains("created::", string.Join('\n', lines[(index + 2)..(index + 4)]));

        Assert.True(State.Session!.TryUndo(out var error), error?.ToString());
        Assert.DoesNotContain("foto.png", _vault.Read(BookPath));
        Assert.True(File.Exists(Path.Combine(Assets, "foto.png")));
    }

    [Fact]
    public async Task Insert_WrongFormat_RefusesAndChangesNothing()
    {
        var cut = await RenderAsync();

        var message = await cut.InvokeAsync(() =>
            ManuscriptCommands.InsertImageAsync(State, "notiz.txt", Png, "Bild einfügen", NullLogger.Instance));

        Assert.Equal(new AdoptMessage("ImageFormatRefused", true), message);
        Assert.Equal(BookText, _vault.Read(BookPath));
        Assert.False(Directory.Exists(Assets));
        Assert.False(State.Session!.Undo.CanUndo);
    }

    [Fact]
    public async Task Insert_TooLarge_Refuses()
    {
        var cut = await RenderAsync();

        var message = await cut.InvokeAsync(() =>
            ManuscriptCommands.InsertImageAsync(State, "foto.png", [], "Bild einfügen", NullLogger.Instance));

        Assert.Equal(new AdoptMessage("ImageTooLarge", true), message);
        Assert.Equal(BookText, _vault.Read(BookPath));
    }

    [Fact]
    public async Task Paste_InsertsBlockWithDatedName()
    {
        var cut = await RenderAsync();

        await cut.InvokeAsync(() => Editor.Callbacks!.OnImagePasted(Convert.ToBase64String(Png), "image/png"));

        Assert.Matches(@"\t- !\[\]\(\.\./assets/bild-\d{8}-\d{6}\.png\)", _vault.Read(BookPath));
        Assert.Single(Directory.GetFiles(Assets, "bild-*.png"));
    }

    [Fact]
    public async Task Paste_UnknownMime_ShowsFormatRefused()
    {
        var cut = await RenderAsync();

        await cut.InvokeAsync(() => Editor.Callbacks!.OnImagePasted(Convert.ToBase64String(Png), "application/pdf"));

        Assert.Equal(BookText, _vault.Read(BookPath));
    }

    [Fact]
    public async Task Load_ReturnsDataUrl_OrMissingText()
    {
        var cut = await RenderAsync();
        Directory.CreateDirectory(Assets);
        File.WriteAllBytes(Path.Combine(Assets, "foto.png"), Png);
        File.WriteAllBytes(Path.Combine(_vault.Root, "geheim.png"), Png);

        var ok = await cut.InvokeAsync(() => Editor.Callbacks!.OnLoadImage("../assets/foto.png"));
        Assert.StartsWith("data:image/png;base64,", ok.Src);
        Assert.Null(ok.Error);

        var outside = await cut.InvokeAsync(() => Editor.Callbacks!.OnLoadImage("../../x.png"));
        Assert.Null(outside.Src);
        Assert.Equal("Bild nicht gefunden: ../../x.png", outside.Error);

        var missing = await cut.InvokeAsync(() => Editor.Callbacks!.OnLoadImage("../assets/fehlt.png"));
        Assert.Null(missing.Src);
        Assert.Equal("Bild nicht gefunden: ../assets/fehlt.png", missing.Error);
    }

    [Fact]
    public async Task Load_TooLargeToShow()
    {
        await RenderAsync();
        Directory.CreateDirectory(Assets);
        using (var stream = File.Create(Path.Combine(Assets, "gross.png")))
        {
            stream.SetLength(VaultAssetsLimit + 1);
        }

        var texts = Services.GetRequiredService<IStringLocalizer<Strings>>();
        var view = ManuscriptCommands.LoadImage(State.Session!, Book, "../assets/gross.png", texts);

        Assert.Null(view.Src);
        Assert.Equal("Bild zu groß zum Anzeigen: ../assets/gross.png", view.Error);
    }

    private const long VaultAssetsLimit = 20L * 1024 * 1024;

    [Fact]
    public async Task Caption_ChangesFileAndHasUndo_SameCaptionNothing()
    {
        var cut = await RenderAsync();
        await cut.InvokeAsync(() => ManuscriptCommands.InsertImageAsync(State, "foto.png", Png, "Bild einfügen", NullLogger.Instance));
        var key = Book.Page.AllBlocks().Single(b => b.Content == "![](../assets/foto.png)").Key;
        var undoBefore = State.Session!.Undo.CanUndo;
        Assert.True(undoBefore);

        var message = await cut.InvokeAsync(() =>
            ManuscriptCommands.SetImageCaptionAsync(State, key, 0, "Ein Bild", "Beschriftung ändern", NullLogger.Instance));

        Assert.Null(message);
        Assert.Contains("![Ein Bild](../assets/foto.png)", _vault.Read(BookPath));
        Assert.Equal("Beschriftung ändern", State.Session.Undo.NextDescription);

        Assert.True(State.Session.TryUndo(out var error), error?.ToString());
        Assert.Contains("![](../assets/foto.png)", _vault.Read(BookPath));

        // The same caption again: nothing is pushed.
        var depth = State.Session.Undo.NextDescription;
        await cut.InvokeAsync(() =>
            ManuscriptCommands.SetImageCaptionAsync(State, key, 0, "", "Beschriftung ändern", NullLogger.Instance));
        Assert.Equal(depth, State.Session.Undo.NextDescription);
    }

    [Fact]
    public async Task ImageEdit_RemembersImageAndCaption()
    {
        var cut = await RenderAsync();
        await cut.InvokeAsync(() => ManuscriptCommands.InsertImageAsync(State, "foto.png", Png, "Bild einfügen", NullLogger.Instance));
        var key = Book.Page.AllBlocks().Single(b => b.Content == "![](../assets/foto.png)").Key;

        await cut.InvokeAsync(() => Editor.Callbacks!.OnImageEdit(key, 0));

        var edit = cut.Instance.ImageEdit;
        Assert.NotNull(edit);
        Assert.Equal(key, edit!.ElementKey);
        Assert.Equal("", edit.Caption);

        await cut.InvokeAsync(() => cut.Instance.SaveImageCaptionAsync("Titel"));
        Assert.Contains("![Titel](../assets/foto.png)", _vault.Read(BookPath));
        Assert.Null(cut.Instance.ImageEdit);
    }

    [Fact]
    public void ImageView_SerializesCamelCase()
    {
        var json = JsonSerializer.Serialize(new ImageView("a", null), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("""{"src":"a","error":null}""", json);
    }
}
