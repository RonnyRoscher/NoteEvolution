using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Editor;
using NoteEvolution.UI.State;
using static NoteEvolution.UI.Tests.EditorPaneTests;

namespace NoteEvolution.UI.Tests;

/// <summary>The editor's save pipeline and its failure branches, without the component (ruling R31).</summary>
public class BookTextSaverTests : UiTestContext
{
    private const string OtherNoteBlockId = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";

    private string BookFile(TestVault tv) => Path.Combine(tv.Root, BookPath);

    /// <summary>Opens the vault and a saver that shows <paramref name="section"/> in the section view.</summary>
    private async Task<(VaultSession Session, BookTextSaver Saver)> OpenAsync(TestVault tv, string section = "Eins")
    {
        var session = await OpenSessionAsync(tv);
        var book = session.Vault.FindBook("Buch - Alpha")!;
        var node = book.Root.Children.First(n => n.Title == section);
        State.CurrentSectionKey = node.Key;
        var saver = new BookTextSaver(State, NullLogger.Instance)
        {
            Shown = new ShownText(session, book.LinkName, BookSnapshot.Create(book, session.Vault, node.Key, false)),
        };
        return (session, saver);
    }

    private static JsonNode Doc(BookTextSaver saver) => JsonNode.Parse(EditorDocMapper.ToJson(saver.Shown!.Snapshot))!;

    private static JsonArray Blocks(JsonNode doc) => doc["content"]!.AsArray();

    private static void SetText(JsonNode block, string text) =>
        block["content"]![0]!["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });

    /// <summary>The first block split after "Erster": a new linked block "Text" (gives a BlockSplit effect).</summary>
    private static string SplitFirst(BookTextSaver saver)
    {
        var doc = Doc(saver);
        var first = Blocks(doc)[0]!;
        var split = first.DeepClone();
        split["attrs"]!["key"] = Guid.CreateVersion7().ToString("D");
        split["attrs"]!["splitFrom"] = first["attrs"]!["key"]!.GetValue<string>();
        SetText(first, "Erster");
        SetText(split, "Text");
        Blocks(doc).Insert(1, split);
        return doc.ToJsonString();
    }

    private static int Usages(TestVault tv) => tv.Read(NotesPath).Split("[[Buch - Alpha]]").Length - 1;

    [Fact]
    public async Task Save_FileLockedForWriting_KeepsTextAndSavesItOnTheNextTry()
    {
        using var tv = Alpha();
        var (_, saver) = await OpenAsync(tv);
        var before = tv.Read(BookPath);
        var doc = Doc(saver);
        SetText(Blocks(doc)[1]!, "Zweiter Text, gesperrt");
        var json = doc.ToJsonString();

        using (new FileStream(BookFile(tv), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            saver.Pending = json;
            saver.Save();

            Assert.Equal(json, saver.Pending);
            Assert.Equal("EditorSaveFailed", saver.Message?.Key);
            Assert.False(State.CurrentBook!.Page.IsDirty); // the page is the file's version again
        }

        Assert.Equal(before, tv.Read(BookPath));
        saver.Save();

        Assert.Null(saver.Pending);
        Assert.Null(saver.Message);
        Assert.Contains("\t- Zweiter Text, gesperrt\n", tv.Read(BookPath));
    }

    [Fact]
    public async Task Save_FileUnreadable_PageKeepsTextAndTheLinksFollowOnceSaved()
    {
        using var tv = Alpha();
        var (session, saver) = await OpenAsync(tv);
        var before = tv.Read(BookPath);

        using (new FileStream(BookFile(tv), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            saver.Pending = SplitFirst(saver);
            saver.Save();
            saver.SaveDirtyPage();
            saver.ApplyUnsavedEffects();

            Assert.Null(saver.Pending);
            Assert.Equal("EditorSaveFailed", saver.Message?.Key);
            Assert.True(session.Vault.FindBook("Buch - Alpha")!.Page.IsDirty);
        }

        Assert.Equal(before, tv.Read(BookPath));
        Assert.Equal(1, Usages(tv));

        saver.SaveDirtyPage();
        saver.ApplyUnsavedEffects();

        Assert.Contains("\t- Erster\n\t  id:: " + FirstId, tv.Read(BookPath));
        Assert.Contains("\t- Text\n", tv.Read(BookPath));
        Assert.Equal(2, Usages(tv));
        Assert.Null(saver.Message);
    }

    [Fact]
    public async Task Save_ConflictOpen_IsRefusedAndTheDeletedFileIsNotRecreated()
    {
        using var tv = Alpha();
        var (session, saver) = await OpenAsync(tv);
        var page = session.Vault.FindBook("Buch - Alpha")!.Page;
        page.Roots[0].Children[0].SetContent("Lokal geändert");
        File.Delete(BookFile(tv));
        session.HandleExternalChange(BookFile(tv));
        Assert.True(session.HasOpenConflict(BookFile(tv)));
        var doc = Doc(saver);
        SetText(Blocks(doc)[1]!, "Zweiter Text, neu");

        saver.Pending = doc.ToJsonString();
        saver.Save();
        saver.SaveDirtyPage();

        Assert.Equal("EditorConflict", saver.Message?.Key);
        Assert.True(saver.ReloadNeeded);
        Assert.False(File.Exists(BookFile(tv)));
    }

    [Fact]
    public async Task SaveDirtyPage_SavesThePageWithUnsavedChanges()
    {
        using var tv = Alpha();
        var (session, saver) = await OpenAsync(tv);
        session.Vault.FindBook("Buch - Alpha")!.Page.Roots[0].Children[1].SetContent("Zweiter Text, gemischt");

        saver.SaveDirtyPage();

        Assert.Contains("\t- Zweiter Text, gemischt\n", tv.Read(BookPath));
        Assert.False(State.CurrentBook!.Page.IsDirty);
    }

    [Fact]
    public async Task ApplyUnsavedEffects_OnlyEffectsThatStillFitThePage()
    {
        using var tv = TestVault.Create(
            (BookPath, "title:: Alpha\ntype:: book\n\n- # Eins\n\t- Erster Text\n\t  id:: " + FirstId + "\n\t  source:: ((" + NoteId + "))\n"),
            (NotesPath,
                "- Gedächtnis\n  id:: " + NoteId + "\n" +
                "  used-in:: [[Buch - Alpha]] ((" + FirstId + ")), [[Buch - Alpha]] ((" + OtherNoteBlockId + "))\n"));
        var (session, saver) = await OpenAsync(tv);
        var note = Guid.Parse(NoteId);
        var notInPage = Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee");

        saver.QueueEffects(session, "Buch - Alpha",
        [
            new BlockDeleted(Guid.Parse(FirstId), [note]),        // the block is still there (e.g. the merge kept it)
            new BlockSplit(notInPage, [note]),                    // the split-off block is gone
            new BlockDeleted(Guid.Parse(OtherNoteBlockId), [note]), // really gone: its usage goes
        ]);
        saver.ApplyUnsavedEffects();

        var notes = tv.Read(NotesPath);
        Assert.Contains("((" + FirstId + "))", notes);
        Assert.DoesNotContain(OtherNoteBlockId, notes);
        Assert.DoesNotContain(notInPage.ToString("D"), notes);
    }

    [Fact]
    public async Task Save_PastedCopyFromAnotherSection_IsASplitCopy_TheOriginalStays()
    {
        using var tv = Alpha();
        var (session, saver) = await OpenAsync(tv, "Zwei");
        var doc = Doc(saver);

        // What the editor sends after "Erster Text" was copied in section Eins and pasted here: no key (R31), splitFrom.
        var pasted = Blocks(doc)[0]!.DeepClone();
        pasted["attrs"]!["key"] = Guid.CreateVersion7().ToString("D");
        pasted["attrs"]!["splitFrom"] = session.Vault.FindBook("Buch - Alpha")!.Root.Children.First().TextBlocks.First().Key.ToString("D");
        pasted["content"]![0]!["attrs"]!["md"] = null;
        SetText(pasted, "Erster Text");
        Blocks(doc).Add(pasted);
        saver.Pending = doc.ToJsonString();

        saver.Save();

        var book = session.Vault.FindBook("Buch - Alpha")!;
        var eins = book.Root.Children.First(n => n.Title == "Eins");
        var zwei = book.Root.Children.First(n => n.Title == "Zwei");
        Assert.Equal(["Erster Text", "Zweiter Text"], eins.TextBlocks.Select(t => t.Text));
        Assert.Equal(Guid.Parse(FirstId), eins.TextBlocks.First().Block.Id);
        Assert.Equal(["Dritter Text", "Erster Text"], zwei.TextBlocks.Select(t => t.Text));
        var copy = zwei.TextBlocks.Last();
        Assert.NotEqual(Guid.Parse(FirstId), copy.Block.Id);
        Assert.Equal([Guid.Parse(NoteId)], copy.Sources);
        Assert.Equal(2, Usages(tv));
    }
}
