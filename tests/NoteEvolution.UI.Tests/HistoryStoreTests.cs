using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.History;

namespace NoteEvolution.UI.Tests;

/// <summary>The store of text block versions and the stands it keeps.</summary>
public class HistoryStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private HistoryStore Open() => HistoryStore.Open(_dir.Path, NullLogger.Instance);

    private static BlockStand Stand(string text, params StandParagraph[] paragraphs) =>
        new(text, paragraphs, BlockStatus.Draft, []);

    [Fact]
    public void Add_ThenVersions_NewestFirst_WithAllFields()
    {
        using var store = Open();
        var id = Guid.NewGuid();
        store.Add(id, "Buch", T0, HistoryReason.First, Stand("eins"));
        store.Add(id, "Buch", T0.AddMinutes(5), HistoryReason.Pause, Stand("zwei"));
        store.Add(Guid.NewGuid(), "Buch", T0, HistoryReason.OnOpen, Stand("fremd"));

        var versions = store.Versions(id);

        Assert.Equal(2, versions.Count);
        Assert.Equal(["zwei", "eins"], versions.Select(v => v.Stand.Text));
        Assert.Equal(HistoryReason.Pause, versions[0].Reason);
        Assert.Equal(T0.AddMinutes(5), versions[0].At);
        Assert.Equal(id, versions[0].BlockId);
        Assert.True(versions[0].Id > versions[1].Id);
        Assert.Equal(versions[0].Id, store.Latest(id)!.Id);
        Assert.Contains(id, store.BlocksWithHistory());
        Assert.Equal(2, store.BlocksWithHistory().Count);
    }

    [Fact]
    public void HasVersions_TrueOnlyForABlockWithVersions()
    {
        using var store = Open();
        var id = Guid.NewGuid();
        Assert.False(store.HasVersions(id));

        store.Add(id, "Buch", T0, HistoryReason.First, Stand("eins"));

        Assert.True(store.HasVersions(id));
        Assert.False(store.HasVersions(Guid.NewGuid()));
    }

    [Fact]
    public void Stand_RoundTrip_KeepsParagraphsStatusAndSources()
    {
        using var store = Open();
        var id = Guid.NewGuid();
        var source = Guid.NewGuid();
        var stand = new BlockStand("Text",
            [new StandParagraph("Punkt", 1, BlockStatus.Done), new StandParagraph("Unterpunkt", 2, BlockStatus.Remark)],
            BlockStatus.Wording, [source]);

        store.Add(id, "Buch", T0, HistoryReason.OnClose, stand);

        var back = store.Latest(id)!.Stand;
        Assert.Equal("Text", back.Text);
        Assert.Equal(BlockStatus.Wording, back.Status);
        Assert.Equal([source], back.Sources);
        Assert.Equal(stand.Paragraphs, back.Paragraphs);
    }

    [Fact]
    public void SameText_IgnoresStatusAndSources()
    {
        var a = new BlockStand("Text", [new StandParagraph("Punkt", 1, BlockStatus.Draft)], BlockStatus.Draft, []);
        var b = new BlockStand("Text", [new StandParagraph("Punkt", 1, BlockStatus.Done)], BlockStatus.Done, [Guid.NewGuid()]);
        var otherDepth = new BlockStand("Text", [new StandParagraph("Punkt", 2, BlockStatus.Draft)], BlockStatus.Draft, []);
        var otherText = new BlockStand("Text", [new StandParagraph("Anders", 1, BlockStatus.Draft)], BlockStatus.Draft, []);
        var fewer = new BlockStand("Text", [], BlockStatus.Draft, []);

        Assert.True(a.SameText(b));
        Assert.False(a.SameText(otherDepth));
        Assert.False(a.SameText(otherText));
        Assert.False(a.SameText(fewer));
        Assert.False(a.SameText(new BlockStand("Anderer Text", a.Paragraphs, BlockStatus.Draft, [])));
    }

    [Fact]
    public void Length_CountsParagraphs()
    {
        var stand = Stand("abc", new StandParagraph("de", 1, BlockStatus.Draft), new StandParagraph("fghi", 2, BlockStatus.Draft));

        Assert.Equal(9, stand.Length);
    }

    [Fact]
    public void Of_ReadsTextParagraphsStatusAndSources()
    {
        var source = Guid.NewGuid();
        using var vault = TestVault.Create(("pages/Buch - Alpha.md",
            "title:: Alpha\ntype:: book\n\n- # Eins\n\t- Haupttext\n\t  status:: fertig\n\t  source:: ((" + source + "))\n\t\t- Unterpunkt\n"));
        var book = vault.Open().FindBook("Buch - Alpha")!;
        var block = book.Page.AllBlocks().Select(b => book.FindTextBlock(b.Key)).OfType<TextBlock>().Single();

        var stand = BlockStand.Of(block);

        Assert.Equal("Haupttext", stand.Text);
        Assert.Equal(BlockStatus.Done, stand.Status);
        Assert.Equal([source], stand.Sources);
        Assert.Equal("Unterpunkt", Assert.Single(stand.Paragraphs).Text);
    }

    [Fact]
    public void Latest_Unknown_IsNull()
    {
        using var store = Open();

        Assert.Null(store.Latest(Guid.NewGuid()));
        Assert.Empty(store.Versions(Guid.NewGuid()));
        Assert.Empty(store.BlocksWithHistory());
    }

    [Fact]
    public void Deleted_ListedPerBook_NewestFirst()
    {
        using var store = Open();
        var older = new DeletedBlock(Guid.NewGuid(), "Alpha", T0, "Eins/Zwei", Guid.NewGuid(), Stand("alt"));
        var newer = new DeletedBlock(Guid.NewGuid(), "Alpha", T0.AddHours(1), "Eins", null, Stand("neu"));
        var other = new DeletedBlock(Guid.NewGuid(), "Beta", T0.AddHours(2), "Drei", null, Stand("fremd"));
        store.MarkDeleted(older);
        store.MarkDeleted(newer);
        store.MarkDeleted(other);

        var listed = store.Deleted("Alpha");

        Assert.Equal([newer, older], listed, DeletedComparer.Instance);
        Assert.Equal(older.AfterBlockId, listed[1].AfterBlockId);
        Assert.Null(listed[0].AfterBlockId);
        Assert.Equal("Eins/Zwei", listed[1].SectionPath);
        Assert.Equal(["fremd"], store.Deleted("Beta").Select(d => d.Stand.Text));
    }

    [Fact]
    public void ClearDeleted_RemovesFromList_KeepsVersions()
    {
        using var store = Open();
        var id = Guid.NewGuid();
        store.Add(id, "Alpha", T0, HistoryReason.BeforeDelete, Stand("weg"));
        store.MarkDeleted(new DeletedBlock(id, "Alpha", T0, "Eins", null, Stand("weg")));

        store.ClearDeleted(id);

        Assert.Empty(store.Deleted("Alpha"));
        Assert.Single(store.Versions(id));
    }

    [Fact]
    public void Open_UnusableFile_MovedAside_NewStoreWorks()
    {
        var folder = Path.Combine(_dir.Path, ".noteevolution");
        Directory.CreateDirectory(folder);
        var garbage = Encoding.UTF8.GetBytes("Das ist keine Datenbank, nur Müll. " + new string('x', 200));
        File.WriteAllBytes(Path.Combine(folder, "history.db"), garbage);

        using (var store = Open())
        {
            var id = Guid.NewGuid();
            store.Add(id, "Alpha", T0, HistoryReason.First, Stand("neu"));
            Assert.Single(store.Versions(id));
        }

        var aside = Assert.Single(Directory.GetFiles(folder, "history.db.defekt-*"));
        Assert.Equal(garbage, File.ReadAllBytes(aside));
    }

    [Fact]
    public void Reopen_KeepsEverything()
    {
        var id = Guid.NewGuid();
        var deleted = new DeletedBlock(Guid.NewGuid(), "Alpha", T0, "Eins", id, Stand("weg"));
        using (var store = Open())
        {
            store.Add(id, "Alpha", T0, HistoryReason.First, Stand("eins"));
            store.MarkDeleted(deleted);
        }

        using var again = Open();

        Assert.Equal(["eins"], again.Versions(id).Select(v => v.Stand.Text));
        Assert.Equal([deleted], again.Deleted("Alpha"), DeletedComparer.Instance);
        Assert.Contains(id, again.BlocksWithHistory());
    }

    /// <summary>Compares deleted blocks field by field (the stand holds lists, which records compare by reference).</summary>
    private sealed class DeletedComparer : IEqualityComparer<DeletedBlock>
    {
        public static readonly DeletedComparer Instance = new();

        public bool Equals(DeletedBlock? x, DeletedBlock? y) =>
            x is not null && y is not null
            && (x.BlockId, x.Book, x.At, x.SectionPath, x.AfterBlockId) == (y.BlockId, y.Book, y.At, y.SectionPath, y.AfterBlockId)
            && x.Stand.Text == y.Stand.Text;

        public int GetHashCode(DeletedBlock obj) => obj.BlockId.GetHashCode();
    }
}
