using System.Text.RegularExpressions;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Links;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Vaults;

namespace NoteEvolution.Core.Tests.Links;

/// <summary>An adoption links every copied block to its own note block (usages per block, spec 3).</summary>
public class AdoptPerBlockTests
{
    private const string Journal = "- Kern\n\t- Unter A\n\t\t- Tief A1\n\t- Unter B\n- Zweite Notiz\n";

    private static readonly string[] NoteBlocks = ["Kern", "Unter A", "Tief A1", "Unter B"];

    private static readonly DateTimeOffset Now = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Adopt_LinksEveryCopiedBlock()
    {
        using var s = new LinkSetup(journal: Journal);

        var r = s.Links.Adopt(s.Book, s.Note("Kern").Key, new InsertPosition.SectionEnd(s.SectionKey("Abschnitt")));

        var text = BookBlock(s, "Kern");
        Assert.Equal(r.TextBlockId, text.Id);
        Assert.Equal(SourceValue.Format([s.Note("Kern").Id!.Value]), text.GetProperty("source"));
        foreach (var content in NoteBlocks.Skip(1))
        {
            var detail = BookBlock(s, content);
            Assert.NotNull(detail.Id);
            Assert.Equal(SourceValue.Format([s.Note(content).Id!.Value]), detail.GetProperty("source"));
        }

        foreach (var content in NoteBlocks)
        {
            Assert.Equal($"[[Buch - Test]] (({BookBlock(s, content).Id}))", s.Note(content).GetProperty(UsedInValue.Key));
        }

        Assert.Equal("true", s.Note("Kern").GetProperty(UsedInValue.WithoutChildrenKey));
        Assert.Equal("true", s.Note("Unter A").GetProperty(UsedInValue.WithoutChildrenKey));
        Assert.Null(s.Note("Tief A1").GetProperty(UsedInValue.WithoutChildrenKey));
        Assert.Null(s.Note("Unter B").GetProperty(UsedInValue.WithoutChildrenKey));
        Assert.Equal("relevant", text.GetProperty("status"));
        Assert.All(NoteBlocks.Skip(1), content => Assert.Null(BookBlock(s, content).GetProperty("status")));

        // The files hold the same: four entries on the note page, four sources in the book.
        Assert.Equal(4, CountOf(s.ReadJournal(), "used-in:: [[Buch - Test]]"));
        Assert.Equal(4, CountOf(s.ReadBook(), "source:: (("));
        Assert.False(r.NoteUpdatePending);
        Assert.Empty(s.Pending.Load());
    }

    [Fact]
    public void Adopt_CopiedDetailsGetDates()
    {
        using var s = new LinkSetup(journal: Journal, clock: new NoteEvolution.TestSupport.FakeClock(Now));

        s.Links.Adopt(s.Book, s.Note("Kern").Key, new InsertPosition.SectionEnd(s.SectionKey("Abschnitt")));

        Assert.All(NoteBlocks, content =>
        {
            Assert.NotNull(BookBlock(s, content).GetProperty(BlockDates.CreatedKey));
            Assert.NotNull(BookBlock(s, content).GetProperty(BlockDates.UpdatedKey));
        });
    }

    [Fact]
    public void Adopt_WritesTheNotePageOnce()
    {
        using var s = new LinkSetup(journal: Journal);

        s.Links.Adopt(s.Book, s.Note("Kern").Key, new InsertPosition.SectionEnd(s.SectionKey("Abschnitt")));

        Assert.Equal(1, s.Writer.SavesOf(s.JournalPath));
    }

    [Fact]
    public void Adopt_WithoutChildren_LinksOnlyTheHead()
    {
        using var s = new LinkSetup(journal: Journal);

        s.Links.Adopt(s.Book, s.Note("Kern").Key, new InsertPosition.SectionEnd(s.SectionKey("Abschnitt")), includeChildren: false);

        Assert.Equal(1, CountOf(s.ReadJournal(), "used-in::"));
        Assert.NotNull(s.Note("Kern").GetProperty(UsedInValue.Key));
        Assert.All(NoteBlocks.Skip(1), content => Assert.False(NoteBlock.IsUsedBlock(s.Note(content))));
        Assert.DoesNotContain("Unter A", s.ReadBook());
    }

    [Fact]
    public void Adopt_HideUsed_LeavesOutUsedSubPoint()
    {
        var journal = Journal.Replace("\t- Unter B\n", "\t- Unter B\n\t  used-in:: [[Andere]]\n");
        using var s = new LinkSetup(journal: journal);

        s.Links.Adopt(s.Book, s.Note("Kern").Key, new InsertPosition.SectionEnd(s.SectionKey("Abschnitt")), hideUsed: true);

        Assert.DoesNotContain("Unter B", s.ReadBook());
        Assert.Equal("[[Andere]]", s.Note("Unter B").GetProperty(UsedInValue.Key));
        Assert.Equal(3, CountOf(s.ReadJournal(), "[[Buch - Test]]"));
        Assert.Equal("true", s.Note("Kern").GetProperty(UsedInValue.WithoutChildrenKey));
    }

    [Fact]
    public void DeleteParentTextBlock_SubPointsStayUsed()
    {
        using var s = new LinkSetup(journal: Journal);
        s.Links.Adopt(s.Book, s.Note("Kern").Key, new InsertPosition.SectionEnd(s.SectionKey("Abschnitt")));

        ManuscriptEditor.OutdentDetail(Book.Load(s.Book.Page), BookBlock(s, "Unter A").Key, Now);
        ManuscriptEditor.OutdentDetail(Book.Load(s.Book.Page), BookBlock(s, "Unter B").Key, Now);
        s.Writer.Save(s.Book.Page);
        var book = Book.Load(s.Book.Page);
        s.Links.DeleteTextBlock(book, book.FindTextBlock(BookBlock(s, "Kern").Key)!.Key);

        Assert.False(NoteBlock.IsUsedBlock(s.Note("Kern")));
        Assert.All(NoteBlocks.Skip(1), content => Assert.True(NoteBlock.IsUsedBlock(s.Note(content))));
        Assert.Equal(3, CountOf(s.ReadJournal(), "used-in:: [[Buch - Test]]"));
    }

    [Fact]
    public void Undo_RemovesEveryEntryAndMark()
    {
        using var s = new LinkSetup(journal: Journal);
        s.Links.Adopt(s.Book, s.Note("Kern").Key, new InsertPosition.SectionEnd(s.SectionKey("Abschnitt")));

        s.Undo.Undo();

        // The notes keep their id:: lines (as an undone adoption always did); everything else is as before.
        Assert.Equal(LinkSetup.DefaultBook, s.ReadBook());
        Assert.Equal(Journal, WithoutIdLines(s.ReadJournal()));
        Assert.Equal(4, CountOf(s.ReadJournal(), "id:: "));
        Assert.Empty(s.Pending.Load());
    }

    [Fact]
    public void NotePageReadOnly_QueuesEverySubPoint()
    {
        using var s = new LinkSetup(journal: Journal);
        s.Writer.FailNext(s.JournalPath);

        var r = s.Links.Adopt(s.Book, s.Note("Kern").Key, new InsertPosition.SectionEnd(s.SectionKey("Abschnitt")));

        Assert.True(r.NoteUpdatePending);
        Assert.Equal(Journal, s.ReadJournal());
        Assert.Equal(4, s.Pending.Load().Count);

        // The note page is read from disk again (without the ids that failed to write); the retry brings everything.
        s.Reopen();

        Assert.Equal(4, s.Links.RetryPending());
        var book = s.Book.Page;
        foreach (var content in NoteBlocks)
        {
            var bookBlock = book.AllBlocks().Single(b => b.Content == content);
            Assert.Equal($"[[Buch - Test]] (({bookBlock.Id}))", s.Note(content).GetProperty(UsedInValue.Key));
            Assert.Equal(SourceValue.Format([s.Note(content).Id!.Value]), bookBlock.GetProperty("source"));
        }

        Assert.Equal("true", s.Note("Kern").GetProperty(UsedInValue.WithoutChildrenKey));
        Assert.Equal("true", s.Note("Unter A").GetProperty(UsedInValue.WithoutChildrenKey));
        Assert.Empty(s.Pending.Load());
    }

    [Fact]
    public void MixedOldAndNewUsage()
    {
        var journal = Journal.Replace("- Kern\n", "- Kern\n  used-in:: [[Andere]]\n");
        using var s = new LinkSetup(journal: journal);

        s.Links.Adopt(s.Book, s.Note("Kern").Key, new InsertPosition.SectionEnd(s.SectionKey("Abschnitt")));

        // The old usage took the sub-points along: no mark, they stay used through it as well.
        Assert.Null(s.Note("Kern").GetProperty(UsedInValue.WithoutChildrenKey));
        Assert.All(NoteBlocks, content => Assert.True(NoteBlock.IsUsedBlock(s.Note(content))));

        s.Links.RemoveUsage(s.Note("Kern").Id!.Value, "Andere", null);

        // Every sub-point has its own entry now: the remaining usage takes the main point alone.
        Assert.Equal("true", s.Note("Kern").GetProperty(UsedInValue.WithoutChildrenKey));
        Assert.Contains("  used-without-children:: true\n\t- Unter A\n", s.ReadJournal());
        Assert.All(NoteBlocks.Skip(1), content => Assert.NotNull(s.Note(content).GetProperty(UsedInValue.Key)));

        // Each sub-point is used only through its own entry: without it, it is unused.
        s.Links.RemoveUsage(s.Note("Tief A1").Id!.Value, "Buch - Test", BookBlock(s, "Tief A1").Id);

        Assert.False(NoteBlock.IsUsedBlock(s.Note("Tief A1")));
        Assert.True(NoteBlock.IsUsedBlock(s.Note("Unter A")));
    }

    private static readonly string JournalWithOldUsage = Journal.Replace("- Kern\n", "- Kern\n  used-in:: [[Andere]]\n");

    /// <summary>The old usage still passes on to the sub-points: no mark, every sub-point used, no entry of this book.</summary>
    private static void AssertOnlyTheOldUsageLeft(LinkSetup s)
    {
        Assert.Equal("[[Andere]]", s.Note("Kern").GetProperty(UsedInValue.Key));
        Assert.Null(s.Note("Kern").GetProperty(UsedInValue.WithoutChildrenKey));
        Assert.DoesNotContain("used-without-children::", s.ReadJournal());
        Assert.DoesNotContain("[[Buch - Test]]", s.ReadJournal());
        Assert.All(NoteBlocks.Skip(1), content => Assert.True(NoteBlock.IsUsedBlock(s.Note(content))));
    }

    [Fact]
    public void DeleteTextBlock_OldInheritingUsage_KeepsNoMark_UndoRestores()
    {
        using var s = new LinkSetup(journal: JournalWithOldUsage);
        var r = s.Links.Adopt(s.Book, s.Note("Kern").Key, new InsertPosition.SectionEnd(s.SectionKey("Abschnitt")));
        var journalAfterAdopt = s.ReadJournal();

        s.Links.DeleteTextBlock(s.Book, r.TextBlockKey);

        AssertOnlyTheOldUsageLeft(s);

        s.Undo.Undo();

        Assert.Equal(journalAfterAdopt, s.ReadJournal());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SyncDeleted_OldInheritingUsage_KeepsNoMark(bool withRemovedBlock)
    {
        using var s = new LinkSetup(journal: JournalWithOldUsage);
        s.Links.Adopt(s.Book, s.Note("Kern").Key, new InsertPosition.SectionEnd(s.SectionKey("Abschnitt")));
        var text = BookBlock(s, "Kern");

        // As BookSync reports a removed subtree: one effect per linked block, top block first.
        List<SyncEffect> effects =
        [
            .. SourceValue.LinkedBlocksIn(text).Select(linked => withRemovedBlock
                ? new BlockDeleted(linked.Id, linked.Sources) { Removed = text, ParentKey = text.Parent?.Key, Index = 0 }
                : new BlockDeleted(linked.Id, linked.Sources)),
        ];
        s.Links.ApplySyncEffects(s.Book, effects);

        AssertOnlyTheOldUsageLeft(s);
    }

    [Fact]
    public void PendingRemovals_ReplayedInAnyOrder_KeepNoMark()
    {
        using var s = new LinkSetup(journal: JournalWithOldUsage);
        var r = s.Links.Adopt(s.Book, s.Note("Kern").Key, new InsertPosition.SectionEnd(s.SectionKey("Abschnitt")));
        s.Writer.FailNext(s.JournalPath, times: 4);
        s.Links.DeleteTextBlock(s.Book, r.TextBlockKey);
        Assert.Equal(4, s.Pending.Load().Count);

        // The removals wait with the main point's first (as a delete in an older version queued them).
        var queued = s.Pending.Load().ToList();
        queued.ForEach(s.Pending.Remove);
        foreach (var update in queued.OrderBy(u => u.Locator.TreePath.Length))
        {
            s.Pending.Add(update);
        }

        s.Reopen();

        Assert.Equal(4, s.Links.RetryPending());
        AssertOnlyTheOldUsageLeft(s);
    }

    [Fact]
    public void RemovingAnEntry_LeafKeepsNoMark()
    {
        var journal = "- Kern\n  used-in:: [[Andere]]\n- Zweite Notiz\n";
        using var s = new LinkSetup(journal: journal);
        s.Links.Adopt(s.Book, s.Note("Kern").Key, new InsertPosition.SectionEnd(s.SectionKey("Abschnitt")));

        s.Links.RemoveUsage(s.Note("Kern").Id!.Value, "Andere", null);

        Assert.DoesNotContain("used-without-children::", s.ReadJournal());
    }

    private static Block BookBlock(LinkSetup s, string content) => s.Book.Page.AllBlocks().Single(b => b.Content == content);

    private static string WithoutIdLines(string text) => Regex.Replace(text, @"^[\t ]*id:: .*\n", "", RegexOptions.Multiline);

    private static int CountOf(string text, string needle) => text.Split(needle).Length - 1;
}
