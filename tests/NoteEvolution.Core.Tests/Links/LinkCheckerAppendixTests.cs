using NoteEvolution.Core.Links;

namespace NoteEvolution.Core.Tests.Links;

public class LinkCheckerAppendixTests
{
    private const string P1 = "0193a1c2-1b7e-7c1d-9a0f-2b3c4d5e6f71";
    private const string P2 = "0193a1c2-1b7e-7c1d-9a0f-2b3c4d5e6f75";
    private const string S1 = "0193a1c2-1b7e-7c1d-9a0f-2b3c4d5e6f72";
    private const string S3 = "0193a1c2-1b7e-7c1d-9a0f-2b3c4d5e6f74";
    private const string Gone = "0193a1c2-1b7e-7c1d-9a0f-2b3c4d5e6f79";

    private static LinkChecker Checker(LinkSetup s) => new(s.Vault, s.Links, s.Writer);

    private const string Head = "title:: Buch: Test\ntype:: book\n\n";

    private static string Cite(string id, string? locator = null) =>
        "{{quelle ((" + id + "))" + (locator is null ? "" : " " + locator) + "}}";

    // A book whose text cites a source that is no entry (S3) and a source (S1) whose authors are P1 (a person) and
    // Gone (no entry); a deleted person's ((id)) in the text is a plain cross reference.
    private static readonly string MissingBook =
        Head +
        "- # Eins\n" +
        $"\t- Weg {Cite(S3, "S. 2")} und (({Gone})) da\n" +
        $"\t- Da {Cite(S1)}\n" +
        $"- # Menschen\n  anhang:: menschen\n\t- Eva\n\t  id:: {P1}\n\t  kuerzel:: E\n" +
        $"- # Quellen\n  anhang:: quellen\n\t- Buch\n\t  id:: {S1}\n\t  autoren:: (({P1})), (({Gone}))\n";

    [Fact]
    public void MissingPersonAndSourceRefs_Listed_RemovedAfterConfirm()
    {
        using var s = new LinkSetup(MissingBook);
        var checker = Checker(s);

        var report = checker.Analyze();

        Assert.Equal(
            [(Guid.Parse(S3), false), (Guid.Parse(Gone), true)],
            report.MissingAppendixRefs.Select(m => (m.MissingId, m.IsAuthor)));
        // The person reference in the text stays a cross reference: it cannot be told from a deleted target.
        Assert.Equal([Guid.Parse(Gone)], report.MissingCrossRefs.Select(m => m.TargetId));

        Assert.Equal(2, checker.RemoveMissingAppendixRefs(report));

        Assert.Equal(
            Head +
            "- # Eins\n" +
            $"\t- Weg und (({Gone})) da\n" +
            $"\t- Da {Cite(S1)}\n" +
            $"- # Menschen\n  anhang:: menschen\n\t- Eva\n\t  id:: {P1}\n\t  kuerzel:: E\n" +
            $"- # Quellen\n  anhang:: quellen\n\t- Buch\n\t  id:: {S1}\n\t  autoren:: (({P1}))\n",
            s.ReadBook());
        var after = checker.Analyze();
        Assert.Empty(after.MissingAppendixRefs);
        Assert.Equal(0, checker.RemoveMissingAppendixRefs(after));
    }

    [Fact]
    public void RemoveMissingAppendixRefs_LastAuthorGone_DropsTheProperty_KeepsTheSource()
    {
        var book = Head + $"- # Quellen\n  anhang:: quellen\n\t- Buch\n\t  id:: {S1}\n\t  art:: buch\n\t  autoren:: (({Gone}))\n";
        using var s = new LinkSetup(book);
        var checker = Checker(s);

        Assert.Equal(1, checker.RemoveMissingAppendixRefs(checker.Analyze()));

        Assert.Equal(Head + $"- # Quellen\n  anhang:: quellen\n\t- Buch\n\t  id:: {S1}\n\t  art:: buch\n", s.ReadBook());
    }

    [Fact]
    public void RemoveMissingAppendixRefs_EntryBackMeanwhile_KeepsTheReference()
    {
        using var s = new LinkSetup(MissingBook);
        var checker = Checker(s);
        var report = checker.Analyze();
        // The person entry that gets the id of the missing author: only the citation still has no target.
        s.Book.Page.AllBlocks().Single(b => b.Content == "Eva").SetProperty("id", Gone);
        s.Writer.Save(s.Book.Page);

        Assert.Equal(1, checker.RemoveMissingAppendixRefs(report));

        Assert.Contains($"autoren:: (({P1})), (({Gone}))", s.ReadBook());
        Assert.DoesNotContain("{{quelle ((" + S3, s.ReadBook());
    }

    [Fact]
    public void RemoveMissingAppendixRefs_ReadOnlyBook_LeftInPlace_Counted()
    {
        var book = MissingBook + "- ```\n  offen\n";
        using var s = new LinkSetup(book);
        Assert.True(s.Book.Page.IsReadOnly);
        var checker = Checker(s);
        var report = checker.Analyze();
        Assert.Equal(2, report.MissingAppendixRefs.Count);

        Assert.Equal(0, checker.RemoveMissingAppendixRefs(report, out var readOnly));

        Assert.Equal(2, readOnly);
        Assert.Equal(book, s.ReadBook());
    }

    [Fact]
    public void RemoveMissingAppendixRefs_SaveFails_PageUnchanged_Throws()
    {
        using var s = new LinkSetup(MissingBook);
        var checker = Checker(s);
        var report = checker.Analyze();
        s.Writer.FailNext(s.Book.Page.FilePath);

        Assert.Throws<IOException>(() => checker.RemoveMissingAppendixRefs(report));

        Assert.Equal(MissingBook, s.ReadBook());
        Assert.False(s.Book.Page.IsDirty);
    }

    [Fact]
    public void DuplicateSections_And_Abbreviations_Listed()
    {
        var book =
            Head +
            "- # Eins\n\t- Text\n" +
            $"- # Menschen\n  anhang:: menschen\n\t- Eva\n\t  id:: {P1}\n\t  kuerzel:: ab\n\t- Adam\n\t  id:: {P2}\n\t  kuerzel:: AB\n\t- Kain\n\t  kuerzel:: K\n" +
            "- # Noch mehr Menschen\n  anhang:: menschen\n\t- Abel\n\t  kuerzel:: K2\n";
        using var s = new LinkSetup(book);
        var checker = Checker(s);
        var before = s.ReadBook();

        var report = checker.Analyze();

        var section = Assert.Single(report.DuplicateAppendixSections);
        Assert.Equal("Noch mehr Menschen", section.Title);
        Assert.Equal("Buch - Test", section.BookLinkName);
        var abbreviation = Assert.Single(report.DuplicateAbbreviations);
        Assert.Equal("ab", abbreviation.Abbreviation, ignoreCase: true);
        Assert.Equal(["Eva", "Adam"], abbreviation.Names);
        // Only told, nothing deleted or changed.
        Assert.Empty(report.MissingAppendixRefs);
        Assert.Equal(before, s.ReadBook());
    }

    [Fact]
    public void BookWithoutAppendix_NothingReported()
    {
        using var s = new LinkSetup();

        var report = Checker(s).Analyze();

        Assert.Empty(report.MissingAppendixRefs);
        Assert.Empty(report.DuplicateAppendixSections);
        Assert.Empty(report.DuplicateAbbreviations);
    }
}
