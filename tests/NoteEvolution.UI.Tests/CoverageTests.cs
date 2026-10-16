using System.Text;
using NoteEvolution.AI.Understanding;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Links;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>The coverage numbers per section (spec 6.1, 8).</summary>
public class CoverageTests
{
    private const string LinkName = "Buch - Alpha";
    private static readonly Guid SchlafText = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SchlafSub = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TraeumeText = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateOnly Day = new(2026, 3, 1);

    /// <summary># Schlaf (a text block with a sub-block) › ## Träume (a text block); # Arbeit (a text block).</summary>
    private sealed record Sample(Book Book, OutlineNode Schlaf, OutlineNode Traeume, OutlineNode Arbeit, TextBlock ArbeitText);

    private static Sample Load()
    {
        var book = Book.Load(LogseqParser.Parse(LinkName + ".md", Encoding.UTF8.GetBytes(
            "title:: Alpha\ntype:: book\n\n"
            + $"- # Schlaf\n\t- Über den Schlaf.\n\t  id:: {SchlafText}\n\t\t- Ein Unterpunkt.\n\t\t  id:: {SchlafSub}\n"
            + $"\t- ## Träume\n\t\t- Träume ordnen.\n\t\t  id:: {TraeumeText}\n"
            + "- # Arbeit\n\t- Konzentration braucht Ruhe.\n")));
        var schlaf = book.Root.Children.Single(n => n.Title == "Schlaf");
        var arbeit = book.Root.Children.Single(n => n.Title == "Arbeit");
        return new Sample(book, schlaf, schlaf.Children.Single(), arbeit, arbeit.TextBlocks.Single());
    }

    private static CoverageNote Used(params UsedInEntry[] usages) => new(Guid.NewGuid(), Day, true, usages, null);

    private static CoverageNote Placed(Guid section, Guid? after = null, PlacementFit fit = PlacementFit.Good, bool stale = false,
        Guid? embedding = null, DateOnly? date = null) =>
        new(Guid.NewGuid(), date ?? Day, false, [],
            new NotePlacement(new PlacementVerdict(section, after, "", Fit: fit, EmbeddingSectionKey: embedding), stale));

    private static CoverageNote Unchecked() => new(Guid.NewGuid(), Day, false, [], null);

    [Fact]
    public void Used_BlockIdInSection_CountsThere_SubBlockToo()
    {
        var s = Load();
        var coverage = CoverageCalculator.Compute(s.Book,
        [
            Used(new UsedInEntry(LinkName, SchlafText)),
            Used(new UsedInEntry(LinkName.ToUpperInvariant(), SchlafSub)),
            Used(new UsedInEntry(LinkName, TraeumeText)),
        ]);

        Assert.Equal(2, coverage.Sections[s.Schlaf.Key].Used);
        Assert.Equal(1, coverage.Sections[s.Traeume.Key].Used);
        Assert.Equal(0, coverage.UsedWithoutPlace);
    }

    [Fact]
    public void Used_WithoutOrUnknownId_WithoutPlace_OtherBookIgnored()
    {
        var s = Load();
        var coverage = CoverageCalculator.Compute(s.Book,
        [
            Used(new UsedInEntry(LinkName, null)),
            Used(new UsedInEntry(LinkName, Guid.NewGuid())),
            Used(new UsedInEntry("Buch - Beta", SchlafText)),
        ]);

        Assert.Equal(2, coverage.UsedWithoutPlace);
        Assert.All(coverage.Sections.Values, section => Assert.Equal(0, section.TotalUsed));
        Assert.Equal(0, coverage.Unused);
    }

    [Fact]
    public void Assigned_BySectionAndByAfterBlock()
    {
        var s = Load();
        var coverage = CoverageCalculator.Compute(s.Book,
        [
            Placed(s.Traeume.Key),
            // The text block decides the section, not the verdict's section.
            Placed(s.Schlaf.Key, after: s.ArbeitText.Key),
        ]);

        Assert.Equal(1, coverage.Sections[s.Traeume.Key].Assigned);
        Assert.Equal(1, coverage.Sections[s.Arbeit.Key].Assigned);
        Assert.Equal(0, coverage.Sections[s.Schlaf.Key].Assigned);
    }

    [Fact]
    public void ChapterTotals_OwnPlusChildren()
    {
        var s = Load();
        var coverage = CoverageCalculator.Compute(s.Book,
        [
            Used(new UsedInEntry(LinkName, SchlafText)),
            Used(new UsedInEntry(LinkName, TraeumeText)),
            Placed(s.Schlaf.Key),
            Placed(s.Traeume.Key),
            Placed(s.Traeume.Key, embedding: s.Arbeit.Key),
        ]);

        var schlaf = coverage.Sections[s.Schlaf.Key];
        Assert.Equal((1, 1, 0), (schlaf.Used, schlaf.Assigned, schlaf.Uncertain));
        Assert.Equal((2, 3, 1), (schlaf.TotalUsed, schlaf.TotalAssigned, schlaf.TotalUncertain));
        Assert.False(schlaf.IsLeaf);
        Assert.True(coverage.Sections[s.Traeume.Key].IsLeaf);
        var root = coverage.Sections[Guid.Empty];
        Assert.Equal((2, 3), (root.TotalUsed, root.TotalAssigned));
    }

    [Fact]
    public void Homeless_UnusedPoorFit_NewestFirst()
    {
        var s = Load();
        var older = Placed(s.Arbeit.Key, fit: PlacementFit.Poor, date: new DateOnly(2026, 3, 1));
        var newer = Placed(s.Arbeit.Key, fit: PlacementFit.Poor, date: new DateOnly(2026, 3, 2));
        var coverage = CoverageCalculator.Compute(s.Book, [older, Placed(s.Arbeit.Key, fit: PlacementFit.Partial), newer]);

        Assert.Equal([newer.Key, older.Key], coverage.Homeless);
    }

    [Fact]
    public void Uncertain_ByIsUncertain()
    {
        var s = Load();
        var uncertain = Placed(s.Traeume.Key, embedding: s.Arbeit.Key);
        var coverage = CoverageCalculator.Compute(s.Book,
        [
            uncertain,
            Placed(s.Traeume.Key, embedding: s.Schlaf.Key), // the embedding's section is an ancestor: not uncertain
            Placed(s.Traeume.Key),
        ]);

        Assert.Equal([uncertain.Key], coverage.Uncertain);
        Assert.Equal(1, coverage.Sections[s.Traeume.Key].Uncertain);
        Assert.Equal(3, coverage.Sections[s.Traeume.Key].Assigned);
    }

    [Fact]
    public void Checked_OnlyCurrentPlacements_Complete()
    {
        var s = Load();
        var coverage = CoverageCalculator.Compute(s.Book,
            [Placed(s.Arbeit.Key), Placed(s.Arbeit.Key, stale: true), Unchecked(), Used(new UsedInEntry(LinkName, SchlafText))]);

        Assert.Equal(1, coverage.Checked);
        Assert.Equal(3, coverage.Unused);
        Assert.False(coverage.Complete);
        Assert.Equal(2, coverage.Sections[s.Arbeit.Key].Assigned);
        Assert.True(CoverageCalculator.Compute(s.Book, [Placed(s.Arbeit.Key), Used()]).Complete);
    }

    [Fact]
    public void Complete_CountsGivenUpAndImpossibleChecksAsSettled()
    {
        var s = Load();
        var givenUp = Unchecked() with { NoCheckComing = true };
        var staleGivenUp = Placed(s.Arbeit.Key, stale: true) with { NoCheckComing = true };

        // One note's check was given up (or it has no topic to offer), another's new check too: no check will come.
        var coverage = CoverageCalculator.Compute(s.Book, [Placed(s.Arbeit.Key), givenUp, staleGivenUp]);

        Assert.Equal(1, coverage.Checked);
        Assert.Equal(3, coverage.Unused);
        Assert.Equal(3, coverage.Settled);
        Assert.True(coverage.Complete);
        Assert.False(CoverageCalculator.Compute(s.Book, [Placed(s.Arbeit.Key), givenUp, Unchecked()]).Complete);
    }

    [Fact]
    public void BookLinkName_TellsWhichBook()
    {
        var s = Load();
        var coverage = CoverageCalculator.Compute(s.Book, []);

        Assert.Equal(LinkName, coverage.BookLinkName);
        Assert.True(coverage.IsFor(s.Book));
        Assert.False(coverage.IsFor(null));
    }

    [Fact]
    public void IsEmpty_LeafWithoutNotes()
    {
        var s = Load();
        var coverage = CoverageCalculator.Compute(s.Book, [Placed(s.Arbeit.Key)]);

        Assert.True(coverage.IsEmpty(s.Traeume.Key));
        Assert.False(coverage.IsEmpty(s.Schlaf.Key)); // has a sub-section
        Assert.False(coverage.IsEmpty(s.Arbeit.Key));
        Assert.False(coverage.IsEmpty(Guid.NewGuid()));
    }

    [Fact]
    public void Placement_SectionOrBlockGone_NotCounted()
    {
        var s = Load();
        var coverage = CoverageCalculator.Compute(s.Book,
        [
            Placed(Guid.NewGuid(), fit: PlacementFit.Poor),
            Placed(s.Arbeit.Key, after: Guid.NewGuid()),
        ]);

        Assert.All(coverage.Sections.Values, section => Assert.Equal(0, section.TotalAssigned));
        Assert.Empty(coverage.Homeless);
        Assert.Equal(0, coverage.Checked);
        Assert.Equal(2, coverage.Unused);
    }
}
