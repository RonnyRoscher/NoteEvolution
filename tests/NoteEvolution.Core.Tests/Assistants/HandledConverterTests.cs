using NoteEvolution.Core.Assistants;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Links;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Storage;
using NoteEvolution.Core.Vaults;
using NoteEvolution.TestSupport;

namespace NoteEvolution.Core.Tests.Assistants;

public class HandledConverterTests
{
    private const string BookFile = "pages/Buch - Test.md";
    private const string JournalFile = "journals/2026_03_01.md";
    private const string OtherJournalFile = "journals/2026_03_02.md";

    private const string BookText =
        "title:: Buch: Test\ntype:: book\n\n- Vorspann\n- # Kapitel\n\t- Absatz\n\t- Zweiter Absatz\n";

    private sealed class Setup : IDisposable
    {
        private static readonly DateTimeOffset Now = new(2026, 3, 15, 14, 30, 5, TimeSpan.Zero);

        public Setup(string journal, string? other = null)
        {
            Tv = other is null
                ? TestVault.Create((BookFile, BookText), (JournalFile, journal))
                : TestVault.Create((BookFile, BookText), (JournalFile, journal), (OtherJournalFile, other));
            var vault = Tv.Open();
            Book = vault.FindBook("Buch - Test")!;
            var notes = new NoteRepository(vault);
            var writer = new PageWriter(vault, new BackupService(vault.Root, new FakeClock(Now)), new SelfWriteRegistry())
            {
                Sleep = _ => { },
            };
            Pending = new PendingStore(vault.Root);
            Converter = new HandledConverter(notes, writer, new LinkService(vault, writer, new UndoManager(), Pending));
        }

        public TestVault Tv { get; }

        public Book Book { get; }

        public PendingStore Pending { get; }

        public Guid TextKey(string text) =>
            Book.Page.AllBlocks().Select(b => Book.FindTextBlock(b.Key)).OfType<TextBlock>().Single(t => t.Text == text).Key;

        public Guid? BookBlockId(string content) => Book.Page.AllBlocks().Single(b => b.Content == content).Id;

        public string ReadBook() => Tv.Read(BookFile);

        public HandledConverter Converter { get; }

        public string ReadJournal() => Tv.Read(JournalFile);

        public void Dispose() => Tv.Dispose();
    }

    [Fact]
    public void Find_ListsHandledBlocksIncludingChildren()
    {
        using var s = new Setup(
            "- [handled] Eins\n" +
            "\t- [handled] Kind\n" +
            "\t- Normal\n" +
            "- [Handled] Falsche Schreibweise\n" +
            "- Text [handled] mittendrin\n" +
            "- [handled]\n");

        var found = s.Converter.Find();

        Assert.Equal(["[handled] Eins", "[handled] Kind", "[handled]"], found.Select(n => n.Block.Content));
    }

    [Fact]
    public void Apply_StripsPrefix_AddsUsedInWithoutBlockRef()
    {
        using var s = new Setup("- [handled] Eins\n- Zwei\n");

        var result = s.Converter.Apply(s.Converter.Find(), s.Book);

        Assert.Equal(1, result.Converted);
        Assert.Empty(result.Skipped);
        Assert.Equal("- Eins\n  used-in:: [[Buch - Test]]\n- Zwei\n", s.ReadJournal());
        Assert.Empty(s.Converter.Find());
    }

    [Fact]
    public void Apply_PrefixOnlyBlock_BecomesEmptyFirstLine()
    {
        using var s = new Setup("- [handled]\n");

        s.Converter.Apply(s.Converter.Find(), s.Book);

        Assert.Equal("-\n  used-in:: [[Buch - Test]]\n", s.ReadJournal());
    }

    [Fact]
    public void Apply_OnlySelected_OthersUntouched()
    {
        using var s = new Setup("- [handled] Eins\n- [handled] Zwei\n", other: "- [handled] Drei\n");
        var found = s.Converter.Find();

        var result = s.Converter.Apply(found.Where(n => n.Block.Content.EndsWith("Zwei")), s.Book);

        Assert.Equal(1, result.Converted);
        Assert.Equal("- [handled] Eins\n- Zwei\n  used-in:: [[Buch - Test]]\n", s.ReadJournal());
        Assert.Equal("- [handled] Drei\n", s.Tv.Read(OtherJournalFile));
    }

    [Fact]
    public void Apply_ExistingUsedIn_AppendsEntry()
    {
        using var s = new Setup(
            "- [handled] Eins\n  used-in:: [[Anderes Buch]]\n" +
            "- [handled] Zwei\n  used-in:: [[buch - test]] ((6f1c2a3b-0000-4000-8000-000000000001))\n");

        s.Converter.Apply(s.Converter.Find(), s.Book);

        Assert.Equal(
            "- Eins\n  used-in:: [[Anderes Buch]], [[Buch - Test]]\n" +
            "- Zwei\n  used-in:: [[buch - test]] ((6f1c2a3b-0000-4000-8000-000000000001))\n",
            s.ReadJournal());
    }

    [Fact]
    public void Apply_OtherLinesByteIdentical()
    {
        using var s = new Setup(
            "- Davor\r\n" +
            "\t- [handled] Eins\r\n" +
            "\t  collapsed:: true\r\n" +
            "\t  zweite Zeile\r\n" +
            "\r\n" +
            "\t\t- Kind\r\n" +
            "- Danach\r\n");

        s.Converter.Apply(s.Converter.Find(), s.Book);

        Assert.Equal(
            "- Davor\r\n" +
            "\t- Eins\r\n" +
            "\t  collapsed:: true\r\n" +
            "\t  used-in:: [[Buch - Test]]\r\n" +
            "\t  zweite Zeile\r\n" +
            "\r\n" +
            "\t\t- Kind\r\n" +
            "- Danach\r\n",
            s.ReadJournal());
    }

    [Fact]
    public void Apply_ReadOnlyPage_IsSkippedAndNotWritten()
    {
        const string broken = "- [handled] Kaputt\n  ```\n  nie geschlossen\n";
        using var s = new Setup("- [handled] Eins\n", other: broken);
        var found = s.Converter.Find();
        Assert.Equal(2, found.Count);

        var result = s.Converter.Apply(found, s.Book);

        Assert.Equal(1, result.Converted);
        Assert.Equal([found.Single(n => n.Page.IsReadOnly)], result.Skipped);
        Assert.Equal(broken, s.Tv.Read(OtherJournalFile));
        Assert.Equal("- Eins\n  used-in:: [[Buch - Test]]\n", s.ReadJournal());
    }

    [Fact]
    public void Handled_WithPlacement_StripsPrefixAndLinksFully()
    {
        using var s = new Setup("- [handled] Eins\n  collapsed:: true\n- Zwei\n");
        var note = s.Converter.Find().Single();
        var placements = new Dictionary<Guid, Guid> { [note.Block.Key] = s.TextKey("Absatz") };

        var result = s.Converter.Apply([note], s.Book, placements);

        var noteId = note.Block.Id;
        var textId = s.BookBlockId("Absatz");
        Assert.Equal(1, result.Converted);
        Assert.Empty(result.Skipped);
        Assert.NotNull(noteId);
        Assert.NotNull(textId);
        Assert.Equal(
            $"- Eins\n  collapsed:: true\n  id:: {noteId}\n  used-in:: [[Buch - Test]] (({textId}))\n- Zwei\n", s.ReadJournal());
        Assert.Equal(
            $"title:: Buch: Test\ntype:: book\n\n- Vorspann\n- # Kapitel\n\t- Absatz\n\t  id:: {textId}\n\t  source:: (({noteId}))\n" +
            "\t- Zweiter Absatz\n",
            s.ReadBook());
        Assert.Empty(s.Converter.Find());
        Assert.Empty(s.Pending.Load());
    }

    [Fact]
    public void Handled_MixedPlacedAndUnplaced()
    {
        using var s = new Setup("- [handled] Eins\n- [handled] Zwei\n- [handled] Drei\n");
        var found = s.Converter.Find();
        var placements = new Dictionary<Guid, Guid>
        {
            [found[0].Block.Key] = s.TextKey("Absatz"),
            [found[2].Block.Key] = s.TextKey("Zweiter Absatz"),
        };

        var result = s.Converter.Apply(found, s.Book, placements);

        var ids = found.Select(n => n.Block.Id).ToList();
        Assert.Equal(3, result.Converted);
        Assert.Equal(
            $"- Eins\n  id:: {ids[0]}\n  used-in:: [[Buch - Test]] (({s.BookBlockId("Absatz")}))\n" +
            "- Zwei\n  used-in:: [[Buch - Test]]\n" +
            $"- Drei\n  id:: {ids[2]}\n  used-in:: [[Buch - Test]] (({s.BookBlockId("Zweiter Absatz")}))\n",
            s.ReadJournal());
        Assert.Null(ids[1]);
        Assert.Contains($"\t- Absatz\n\t  id:: {s.BookBlockId("Absatz")}\n\t  source:: (({ids[0]}))\n", s.ReadBook());
        Assert.Contains($"\t- Zweiter Absatz\n\t  id:: {s.BookBlockId("Zweiter Absatz")}\n\t  source:: (({ids[2]}))\n", s.ReadBook());
        Assert.Empty(s.Converter.Find());
    }

    [Fact]
    public void Handled_PlacementReplacesIdlessEntryOfSameBook()
    {
        using var s = new Setup("- [handled] Eins\n  used-in:: [[Anderes]], [[buch - test]]\n");
        var note = s.Converter.Find().Single();

        s.Converter.Apply([note], s.Book, new Dictionary<Guid, Guid> { [note.Block.Key] = s.TextKey("Absatz") });

        Assert.Contains($"used-in:: [[Anderes]], [[Buch - Test]] (({s.BookBlockId("Absatz")}))\n", s.ReadJournal());
    }

    [Fact]
    public void Handled_UnknownPlacement_RevertsPrefixRemoval_AndPropagates()
    {
        const string journal = "- [handled] Eins\n- [handled] Zwei\n";
        using var s = new Setup(journal);
        var found = s.Converter.Find();
        var placements = new Dictionary<Guid, Guid> { [found[0].Block.Key] = Guid.NewGuid() };

        Assert.Throws<ArgumentException>(() => s.Converter.Apply(found, s.Book, placements));

        Assert.Equal(journal, s.ReadJournal());
        Assert.Equal("[handled] Eins", found[0].Block.Content);
        Assert.False(found[0].Page.IsDirty);
        Assert.Equal(BookText, s.ReadBook());
    }
}
