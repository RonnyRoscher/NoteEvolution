using NoteEvolution.Core.Assistants;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Storage;
using NoteEvolution.Core.Vaults;
using NoteEvolution.TestSupport;

namespace NoteEvolution.Core.Tests.Assistants;

public class HandledConverterTests
{
    private const string BookFile = "pages/Buch - Test.md";
    private const string JournalFile = "journals/2026_03_01.md";
    private const string OtherJournalFile = "journals/2026_03_02.md";

    private const string BookText = "title:: Buch: Test\ntype:: book\n\n- Vorspann\n";

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
            Converter = new HandledConverter(notes, writer);
        }

        public TestVault Tv { get; }

        public Book Book { get; }

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
}
