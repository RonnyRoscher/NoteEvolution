using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Tests.Links;

namespace NoteEvolution.Core.Tests.Books;

public class BookSyncDatesTests
{
    private const string Old = "2026-01-02T03:04";

    private const string TestBook =
        "title:: Buch: Test\n" +
        "type:: book\n" +
        "\n" +
        "- # Kapitel\n" +
        "\t- ## Abschnitt\n" +
        "\t\t- Erster Textblock\n" +
        $"\t\t  created:: {Old}\n" +
        $"\t\t  updated:: {Old}\n" +
        "\t\t\t- Absatz eins\n" +
        "\t\t\t- Absatz zwei\n" +
        "\t\t- Zweiter Textblock\n" +
        "\t\t- Dritter Textblock\n" +
        "\t\t  updated:: gestern irgendwann\n" +
        "\t- ## Zweiter Abschnitt\n" +
        "\t\t- Anderswo\n";

    // A fixed local time with the local offset, so the tests hold in any time zone.
    private static readonly DateTimeOffset Now = LocalNow();
    private const string Stamp = "2026-10-19T14:32";

    private static DateTimeOffset LocalNow()
    {
        var local = new DateTime(2026, 10, 19, 14, 32, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    [Fact]
    public void Apply_NewTextBlock_GetsBothDates()
    {
        using var s = new LinkSetup(TestBook);
        var snapshot = Section(s, "Abschnitt");
        var nodes = snapshot.Nodes.ToList();
        var newKey = Guid.NewGuid();
        nodes.Insert(1, new SnapshotTextBlock(newKey, null, "Neuer Block", [new SnapshotParagraph(Guid.NewGuid(), "Neuer Absatz", 1, false)], []));

        var result = BookSync.Apply(s.Book, snapshot with { Nodes = nodes }, now: Now);

        Assert.True(result.Changed);
        Assert.Equal(
            TestBook.Replace(
                "\t\t- Zweiter Textblock\n",
                $"\t\t- Neuer Block\n\t\t  created:: {Stamp}\n\t\t  updated:: {Stamp}\n\t\t\t- Neuer Absatz\n\t\t- Zweiter Textblock\n"),
            Serialize(s.Book));
    }

    [Fact]
    public void Apply_TextChanged_UpdatesOnly()
    {
        using var s = new LinkSetup(TestBook);
        var snapshot = Section(s, "Abschnitt");
        var block = TextNode(snapshot, "Erster Textblock");

        BookSync.Apply(s.Book, Replace(snapshot, block, block with { Text = "Erster Textblock, neu" }), now: Now);

        Assert.Equal(
            TestBook.Replace(
                $"\t\t- Erster Textblock\n\t\t  created:: {Old}\n\t\t  updated:: {Old}\n",
                $"\t\t- Erster Textblock, neu\n\t\t  created:: {Old}\n\t\t  updated:: {Stamp}\n"),
            Serialize(s.Book));
    }

    [Fact]
    public void Apply_ParagraphChanged_UpdatesItsTextBlock()
    {
        using var s = new LinkSetup(TestBook);
        var snapshot = Section(s, "Abschnitt");
        var block = TextNode(snapshot, "Erster Textblock");
        var changed = block with { Paragraphs = [block.Paragraphs[0] with { Text = "Absatz eins, neu" }, block.Paragraphs[1]] };

        BookSync.Apply(s.Book, Replace(snapshot, block, changed), now: Now);

        Assert.Equal(
            TestBook
                .Replace($"\t\t  updated:: {Old}\n", $"\t\t  updated:: {Stamp}\n")
                .Replace("\t\t\t- Absatz eins\n", "\t\t\t- Absatz eins, neu\n"),
            Serialize(s.Book));
    }

    [Fact]
    public void Apply_ParagraphRemoved_UpdatesItsTextBlock()
    {
        using var s = new LinkSetup(TestBook);
        var snapshot = Section(s, "Abschnitt");
        var block = TextNode(snapshot, "Erster Textblock");

        BookSync.Apply(s.Book, Replace(snapshot, block, block with { Paragraphs = [block.Paragraphs[0]] }), now: Now);

        Assert.Equal(
            TestBook
                .Replace($"\t\t  updated:: {Old}\n", $"\t\t  updated:: {Stamp}\n")
                .Replace("\t\t\t- Absatz zwei\n", ""),
            Serialize(s.Book));
    }

    /// <summary>
    /// The sync writes no <c>status::</c> (the snapshot's status is ignored); the status it can change is a
    /// paragraph's remark tag, which counts as a change of the text block.
    /// </summary>
    [Fact]
    public void Apply_StatusChanged_Updates()
    {
        using var s = new LinkSetup(TestBook);
        var snapshot = Section(s, "Abschnitt");
        var block = TextNode(snapshot, "Erster Textblock");
        var toggled = block with { Paragraphs = [block.Paragraphs[0] with { IsNote = true }, block.Paragraphs[1]] };

        BookSync.Apply(s.Book, Replace(snapshot, block, toggled), now: Now);

        Assert.Equal(
            TestBook
                .Replace($"\t\t  updated:: {Old}\n", $"\t\t  updated:: {Stamp}\n")
                .Replace("\t\t\t- Absatz eins\n", "\t\t\t- Absatz eins #notiz\n"),
            Serialize(s.Book));
    }

    [Fact]
    public void Apply_SnapshotStatusDiffers_NotWritten_NoDates()
    {
        using var s = new LinkSetup(TestBook);
        var snapshot = Section(s, "Abschnitt");
        var block = TextNode(snapshot, "Zweiter Textblock");

        var result = BookSync.Apply(s.Book, Replace(snapshot, block, block with { Status = BlockStatus.Done }), now: Now);

        Assert.False(result.Changed);
        Assert.Equal(TestBook, Serialize(s.Book));
    }

    [Fact]
    public void Apply_Split_NewPartGetsBothDates()
    {
        using var s = new LinkSetup(TestBook);
        var snapshot = Section(s, "Abschnitt");
        var original = TextNode(snapshot, "Erster Textblock");
        var first = original with { Text = "Erster", Paragraphs = [original.Paragraphs[0]] };
        var second = new SnapshotTextBlock(Guid.NewGuid(), original.Key, "Textblock", [original.Paragraphs[1]], []);

        BookSync.Apply(s.Book, Replace(snapshot, original, first, second), now: Now);

        Assert.Equal(
            TestBook.Replace(
                $"\t\t- Erster Textblock\n\t\t  created:: {Old}\n\t\t  updated:: {Old}\n\t\t\t- Absatz eins\n\t\t\t- Absatz zwei\n",
                $"\t\t- Erster\n\t\t  created:: {Old}\n\t\t  updated:: {Stamp}\n\t\t\t- Absatz eins\n" +
                $"\t\t- Textblock\n\t\t  created:: {Stamp}\n\t\t  updated:: {Stamp}\n\t\t\t- Absatz zwei\n"),
            Serialize(s.Book));
    }

    [Fact]
    public void Apply_Merge_RemainingUpdated()
    {
        using var s = new LinkSetup(TestBook);
        var snapshot = Section(s, "Abschnitt");
        var first = TextNode(snapshot, "Erster Textblock");
        var second = TextNode(snapshot, "Zweiter Textblock");
        var merged = first with { Text = "Erster TextblockZweiter Textblock" };

        BookSync.Apply(s.Book, Replace(Replace(snapshot, second), first, merged), now: Now);

        Assert.Equal(
            TestBook
                .Replace(
                    $"\t\t- Erster Textblock\n\t\t  created:: {Old}\n\t\t  updated:: {Old}\n",
                    $"\t\t- Erster TextblockZweiter Textblock\n\t\t  created:: {Old}\n\t\t  updated:: {Stamp}\n")
                .Replace("\t\t- Zweiter Textblock\n", ""),
            Serialize(s.Book));
    }

    [Fact]
    public void Apply_MovedOnly_NoDates()
    {
        using var s = new LinkSetup(TestBook);
        var snapshot = Manuscript(s, "Kapitel");
        var moved = TextNode(snapshot, "Erster Textblock");
        var nodes = snapshot.Nodes.Where(n => n != moved).ToList();
        nodes.Add(moved);

        var result = BookSync.Apply(s.Book, snapshot with { Nodes = nodes }, now: Now);

        Assert.True(result.Changed);
        var after = Serialize(s.Book);
        Assert.DoesNotContain(Stamp, after);
        Assert.EndsWith(
            $"\t\t- Anderswo\n\t\t- Erster Textblock\n\t\t  created:: {Old}\n\t\t  updated:: {Old}\n\t\t\t- Absatz eins\n\t\t\t- Absatz zwei\n",
            after);
    }

    [Fact]
    public void Apply_HeadingRenamed_NoDates()
    {
        using var s = new LinkSetup(TestBook);
        var snapshot = Manuscript(s, "Kapitel");
        var heading = snapshot.Nodes.OfType<SnapshotHeading>().Single(h => h.Text == "Abschnitt");

        var result = BookSync.Apply(s.Book, Replace(snapshot, heading, heading with { Text = "Umbenannt" }), now: Now);

        Assert.True(result.Changed);
        Assert.Equal(TestBook.Replace("\t- ## Abschnitt\n", "\t- ## Umbenannt\n"), Serialize(s.Book));
    }

    /// <summary>Review focus 1: opening and saving an unchanged book changes no byte and stamps nothing.</summary>
    [Fact]
    public void Apply_NoChange_ByteIdentical_NoDates()
    {
        using var s = new LinkSetup(TestBook);
        var book = s.Book;
        var scopes = book.Page.AllBlocks().Select(b => book.FindNode(b.Key)).OfType<OutlineNode>().Select(n => n.Key)
            .Prepend(Guid.Empty).ToList();

        foreach (var scope in scopes)
        {
            foreach (var manuscript in new[] { false, true })
            {
                var result = BookSync.Apply(book, BookSnapshot.Create(book, s.Vault, scope, manuscript), s.Vault, Now);

                Assert.False(result.Changed);
                Assert.Empty(result.Effects);
                Assert.Equal(TestBook, Serialize(book));
                Assert.False(book.Page.IsDirty);
            }
        }
    }

    [Fact]
    public void Apply_ExistingBlockWithoutDates_ChangedGetsUpdatedOnly()
    {
        using var s = new LinkSetup(TestBook);
        var snapshot = Section(s, "Abschnitt");
        var block = TextNode(snapshot, "Zweiter Textblock");

        BookSync.Apply(s.Book, Replace(snapshot, block, block with { Text = "Zweiter, neu" }), now: Now);

        Assert.Equal(
            TestBook.Replace("\t\t- Zweiter Textblock\n", $"\t\t- Zweiter, neu\n\t\t  updated:: {Stamp}\n"),
            Serialize(s.Book));
    }

    /// <summary>Review focus 2: an unreadable <c>updated::</c> stays on an unrelated save and is replaced on a change.</summary>
    [Fact]
    public void Apply_GarbageUpdated_UnrelatedSaveKeepsIt()
    {
        using var s = new LinkSetup(TestBook);
        var snapshot = Section(s, "Abschnitt");
        var other = TextNode(snapshot, "Zweiter Textblock");

        BookSync.Apply(s.Book, Replace(snapshot, other, other with { Text = "Zweiter, neu" }), now: Now);

        var afterUnrelated = Serialize(s.Book);
        Assert.Contains("\t\t- Dritter Textblock\n\t\t  updated:: gestern irgendwann\n", afterUnrelated);

        // The book is stale after Apply; the next save starts from the page as it is now.
        var book = Book.Load(s.Book.Page);
        snapshot = BookSnapshot.Create(book, s.Vault, s.SectionKey("Abschnitt"), includeSubsections: false);
        var garbage = TextNode(snapshot, "Dritter Textblock");
        Assert.Null(book.FindTextBlock(garbage.Key)!.Updated);
        BookSync.Apply(book, Replace(snapshot, garbage, garbage with { Text = "Dritter, neu" }), now: Now);

        Assert.Equal(
            afterUnrelated.Replace(
                "\t\t- Dritter Textblock\n\t\t  updated:: gestern irgendwann\n",
                $"\t\t- Dritter, neu\n\t\t  updated:: {Stamp}\n"),
            Serialize(book));
    }

    [Fact]
    public void Apply_WithoutNow_StampsNothing()
    {
        using var s = new LinkSetup(TestBook);
        var snapshot = Section(s, "Abschnitt");
        var block = TextNode(snapshot, "Zweiter Textblock");
        var nodes = Replace(snapshot, block, block with { Text = "Zweiter, neu" }).Nodes.ToList();
        nodes.Add(new SnapshotTextBlock(Guid.NewGuid(), null, "Neuer Block", [], []));

        BookSync.Apply(s.Book, snapshot with { Nodes = nodes });

        Assert.Equal(
            TestBook.Replace("\t\t- Zweiter Textblock\n", "\t\t- Zweiter, neu\n")
                .Replace("\t\t  updated:: gestern irgendwann\n", "\t\t  updated:: gestern irgendwann\n\t\t- Neuer Block\n"),
            Serialize(s.Book));
    }

    private static string Serialize(Book book) => Encoding.UTF8.GetString(PageSerializer.Serialize(book.Page));

    private static SectionSnapshot Section(LinkSetup s, string title) =>
        BookSnapshot.Create(s.Book, s.Vault, s.SectionKey(title), includeSubsections: false);

    private static SectionSnapshot Manuscript(LinkSetup s, string title) =>
        BookSnapshot.Create(s.Book, s.Vault, s.SectionKey(title), includeSubsections: true);

    private static SnapshotTextBlock TextNode(SectionSnapshot snapshot, string text) =>
        snapshot.Nodes.OfType<SnapshotTextBlock>().Single(t => t.Text == text);

    /// <summary>The snapshot with <paramref name="old"/> replaced by <paramref name="replacements"/> (none = removed).</summary>
    private static SectionSnapshot Replace(SectionSnapshot snapshot, SnapshotNode old, params SnapshotNode[] replacements) =>
        snapshot with { Nodes = [.. snapshot.Nodes.SelectMany(n => n == old ? replacements : [n])] };
}
