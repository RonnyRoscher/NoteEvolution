using NoteEvolution.Core.Links;
using NoteEvolution.Core.Storage;

namespace NoteEvolution.Core.Tests.Links;

public class LinkCheckerTests
{
    private const string Note1 = "0190a000-0000-7000-8000-000000000001";
    private const string Note2 = "0190a000-0000-7000-8000-000000000002";
    private const string Block1 = "0190b000-0000-7000-8000-000000000001";
    private const string Block2 = "0190b000-0000-7000-8000-000000000002";
    private const string Gone = "0190c000-0000-7000-8000-00000000dead";

    private const string BookHead = "title:: Buch: Test\ntype:: book\n\n- # Kapitel\n";

    private static string BookWith(string blocks) => BookHead + blocks;

    private static string LinkedBlock(string id, string source) =>
        $"\t- Text {id[^1]}\n\t  id:: {id}\n\t  source:: (({source}))\n";

    private static string Journal(string usedIn = "") =>
        "- Notiz\n" + $"  id:: {Note1}\n" + (usedIn.Length > 0 ? $"  used-in:: {usedIn}\n" : "");

    private static LinkChecker Checker(LinkSetup s) => new(s.Vault, s.Links);

    [Fact]
    public void Analyze_SourceWithoutUsedIn_ReportedAndFixed()
    {
        using var s = new LinkSetup(BookWith(LinkedBlock(Block1, Note1)), Journal());
        var checker = Checker(s);

        var report = checker.Analyze();

        var missing = Assert.Single(report.Missing);
        Assert.Equal(new MissingUsage(Guid.Parse(Note1), new UsedInEntry("Buch - Test", Guid.Parse(Block1))), missing);
        Assert.Empty(report.Orphans);
        Assert.Empty(report.Broken);

        Assert.Equal(1, checker.FixMissing(report));

        Assert.Equal(Journal($"[[Buch - Test]] (({Block1}))"), s.ReadJournal());
        Assert.Equal(BookWith(LinkedBlock(Block1, Note1)), s.ReadBook());
        Assert.Empty(checker.Analyze().Missing);
    }

    [Fact]
    public void Analyze_SourceOnParagraph_IsChecked()
    {
        var paragraph = $"\t\t- Absatz\n\t\t  id:: {Block2}\n\t\t  source:: (({Note1}))\n";
        using var s = new LinkSetup(BookWith("\t- Text\n" + paragraph), Journal());

        var missing = Assert.Single(Checker(s).Analyze().Missing);

        Assert.Equal(Guid.Parse(Block2), missing.Entry.BookBlockId);
    }

    [Fact]
    public void FixMissing_PageLinkWithoutBlock_IsReplacedByPreciseEntry()
    {
        using var s = new LinkSetup(
            BookWith(LinkedBlock(Block1, Note1)), Journal("[[Anderes Buch]], [[buch - test]]"));
        var checker = Checker(s);

        Assert.Equal(1, checker.FixMissing(checker.Analyze()));

        Assert.Equal(Journal($"[[Anderes Buch]], [[Buch - Test]] (({Block1}))"), s.ReadJournal());
    }

    [Fact]
    public void FixMissing_ReadOnlyNote_IsQueuedAndFileUnchanged()
    {
        var journal = Journal() + "- ```\n  offen\n";
        using var s = new LinkSetup(BookWith(LinkedBlock(Block1, Note1)), journal);
        var checker = Checker(s);
        var report = checker.Analyze();
        Assert.Single(report.Missing);

        Assert.Equal(1, checker.FixMissing(report));

        Assert.Equal(journal, s.ReadJournal());
        Assert.Single(s.Pending.Load());
    }

    [Fact]
    public void Analyze_UsedInPointingToMissingBlock_IsOrphan()
    {
        using var s = new LinkSetup(BookWith(LinkedBlock(Block1, Note1)), Journal($"[[Buch - Test]] (({Gone}))"));

        var report = Checker(s).Analyze();

        var orphan = Assert.Single(report.Orphans);
        Assert.Equal(s.Note("Notiz").Key, orphan.NoteBlockKey);
        Assert.Equal(new UsedInEntry("Buch - Test", Guid.Parse(Gone)), orphan.Entry);
        Assert.Single(report.Missing);
    }

    [Fact]
    public void Analyze_UsedInPointingToMissingBook_IsOrphan_WithAndWithoutBlock()
    {
        using var s = new LinkSetup(
            BookWith(""), Journal($"[[Fehlt]] (({Block1})), [[Auch weg]], [[buch - test]]"));

        var report = Checker(s).Analyze();

        Assert.Equal(
            [new UsedInEntry("Fehlt", Guid.Parse(Block1)), new UsedInEntry("Auch weg", null)],
            report.Orphans.Select(o => o.Entry));
    }

    [Fact]
    public void Analyze_UsedInPointingToBlockWithoutThisSource_IsNotReported()
    {
        using var s = new LinkSetup(
            BookWith(LinkedBlock(Block1, Note2)), Journal($"[[Buch - Test]] (({Block1}))"));

        var report = Checker(s).Analyze();

        Assert.Empty(report.Orphans);
        Assert.Single(report.Broken);
    }

    [Fact]
    public void Resolve_MarkUnknown_KeepsPageLinkOnly()
    {
        using var s = new LinkSetup(
            BookWith(""), Journal($"[[Buch - Test]] (({Gone})), [[Anderes Buch]] (({Block2}))"));
        var checker = Checker(s);
        var orphan = checker.Analyze().Orphans.First();

        checker.Resolve(orphan, OrphanResolution.MarkUnknown);

        Assert.Equal(Journal($"[[Anderes Buch]] (({Block2})), [[Buch - Test]]"), s.ReadJournal());
        Assert.DoesNotContain(checker.Analyze().Orphans, o => o.Entry.PageName == "Buch - Test");
    }

    [Fact]
    public void Resolve_MarkUnknown_PageLinkAlreadyThere_RemovesOnlyOrphan()
    {
        using var s = new LinkSetup(BookWith(""), Journal($"[[Buch - Test]], [[Buch - Test]] (({Gone}))"));
        var checker = Checker(s);
        var orphan = Assert.Single(checker.Analyze().Orphans);

        checker.Resolve(orphan, OrphanResolution.MarkUnknown);

        Assert.Equal(Journal("[[Buch - Test]]"), s.ReadJournal());
    }

    [Fact]
    public void Resolve_Remove_DeletesEntryAndEmptyProperty()
    {
        using var s = new LinkSetup(BookWith(""), Journal($"[[Buch - Test]] (({Gone}))"));
        var checker = Checker(s);
        var orphan = Assert.Single(checker.Analyze().Orphans);

        checker.Resolve(orphan, OrphanResolution.Remove);

        Assert.Equal(Journal(), s.ReadJournal());
        Assert.Empty(checker.Analyze().Orphans);
    }

    [Fact]
    public void Resolve_Remove_OrphanWithoutBlockId_RemovesEntry()
    {
        using var s = new LinkSetup(BookWith(""), Journal($"[[Fehlt]], [[Buch - Test]] (({Gone}))"));
        var checker = Checker(s);
        var orphan = checker.Analyze().Orphans.First(o => o.Entry.BookBlockId is null);

        checker.Resolve(orphan, OrphanResolution.Remove);

        Assert.Equal(Journal($"[[Buch - Test]] (({Gone}))"), s.ReadJournal());
    }

    [Fact]
    public void Resolve_NoteBlockGone_Throws()
    {
        using var s = new LinkSetup(BookWith(""), Journal($"[[Buch - Test]] (({Gone}))"));
        var checker = Checker(s);
        var orphan = Assert.Single(checker.Analyze().Orphans);
        s.Journal.RemoveBlock(s.Note("Notiz"));

        Assert.Throws<InvalidOperationException>(() => checker.Resolve(orphan, OrphanResolution.Remove));
    }

    [Fact]
    public void Resolve_MarkUnknown_ReadOnlyNote_ThrowsWithoutChange()
    {
        var journal = Journal($"[[Buch - Test]] (({Gone}))") + "- ```\n  offen\n";
        using var s = new LinkSetup(BookWith(""), journal);
        var checker = Checker(s);
        var orphan = Assert.Single(checker.Analyze().Orphans);

        Assert.Throws<ReadOnlyPageException>(() => checker.Resolve(orphan, OrphanResolution.MarkUnknown));

        Assert.Equal(journal, s.ReadJournal());
        Assert.Empty(s.Pending.Load());
    }

    [Fact]
    public void Analyze_SourceToMissingNote_IsBroken()
    {
        var paragraph = $"\t\t- Absatz\n\t\t  id:: {Block2}\n\t\t  source:: (({Gone}))\n";
        using var s = new LinkSetup(BookWith("\t- Text\n" + paragraph), Journal());

        var report = Checker(s).Analyze();

        var broken = Assert.Single(report.Broken);
        Assert.Equal("Buch - Test", broken.BookLinkName);
        Assert.Equal(Guid.Parse(Gone), broken.MissingNoteId);
        Assert.Equal(s.Book.Page.AllBlocks().Single(b => b.Id == Guid.Parse(Block2)).Key, broken.TextBlockKey);
        Assert.Empty(report.Missing);
    }

    [Fact]
    public void Analyze_CleanVault_EmptyReport()
    {
        using var s = new LinkSetup(
            BookWith(LinkedBlock(Block1, Note1)), Journal($"[[Buch - Test]] (({Block1}))"));

        var report = Checker(s).Analyze();

        Assert.Empty(report.Missing);
        Assert.Empty(report.Orphans);
        Assert.Empty(report.Broken);
        Assert.Equal(0, Checker(s).FixMissing(report));
    }
}
