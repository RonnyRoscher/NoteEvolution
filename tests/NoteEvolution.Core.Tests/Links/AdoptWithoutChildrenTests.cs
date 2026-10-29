using NoteEvolution.Core.Books;
using NoteEvolution.Core.Links;

namespace NoteEvolution.Core.Tests.Links;

public class AdoptWithoutChildrenTests
{
    private const string Journal =
        "- Kern #notiz\n" +
        "\t- Unter A\n" +
        "\t\t- Tiefer\n" +
        "\t- Unter B\n" +
        "- Zweite Notiz\n";

    private const string Book =
        "title:: Buch: Test\ntype:: book\n\n" +
        "- # Kapitel\n" +
        "\t- Hallo Welt\n" +
        "\t\t- Erstes Detail\n" +
        "\t- Nächster Textblock\n";

    [Fact]
    public void Adopt_WithoutChildren_BookHasOnlyBlockText_UsedInOnBlock_Undo()
    {
        using var s = new LinkSetup(Book, Journal);
        var textKey = s.Text("Hallo Welt").Key;

        var r = s.Links.Adopt(s.Book, s.Note("Kern #notiz").Key, new InsertPosition.After(textKey), includeChildren: false);

        var noteId = s.Note("Kern #notiz").Id;
        Assert.Equal(
            "title:: Buch: Test\ntype:: book\n\n- # Kapitel\n\t- Hallo Welt\n\t\t- Erstes Detail\n" +
            $"\t- Kern\n\t  id:: {r.TextBlockId}\n\t  source:: (({noteId}))\n\t  status:: relevant\n" +
            "\t- Nächster Textblock\n",
            s.ReadBook());
        Assert.Equal(
            $"- Kern #notiz\n  id:: {noteId}\n  used-in:: [[Buch - Test]] (({r.TextBlockId}))\n  used-without-children:: true\n" +
            "\t- Unter A\n\t\t- Tiefer\n\t- Unter B\n- Zweite Notiz\n",
            s.ReadJournal());
        Assert.Equal(1, CountOf(s.ReadJournal(), "used-in::"));

        s.Undo.Undo();

        Assert.Equal(Book, s.ReadBook());
        Assert.DoesNotContain("used-in::", s.ReadJournal());
        Assert.DoesNotContain("used-without-children::", s.ReadJournal());
    }

    [Fact]
    public void WithoutChildren_SubPointsStayUnused_WithChildrenTheyAreUsed()
    {
        using var s = new LinkSetup(Book, Journal);
        var textKey = s.Text("Hallo Welt").Key;
        bool Used(string content) => NoteEvolution.Core.Vaults.NoteBlock.IsUsedBlock(s.Note(content));

        // A sub-point adopted alone: its own sub-point stays unused, the main point too.
        s.Links.Adopt(s.Book, s.Note("Unter A").Key, new InsertPosition.After(textKey), includeChildren: false);

        Assert.True(Used("Unter A"));
        Assert.False(Used("Tiefer"));
        Assert.False(Used("Kern #notiz"));

        // The main point alone: its sub-points stay unused (only "Unter A" has a usage of its own).
        s.Links.Adopt(s.Book, s.Note("Kern #notiz").Key, new InsertPosition.After(textKey), includeChildren: false);

        Assert.True(Used("Kern #notiz"));
        Assert.False(Used("Unter B"));
        Assert.False(Used("Tiefer"));

        // Adopted with its sub-points as well, the mark goes: now they are used through it.
        s.Links.Adopt(s.Book, s.Note("Kern #notiz").Key, new InsertPosition.After(textKey));

        Assert.Null(s.Note("Kern #notiz").GetProperty("used-without-children"));
        Assert.True(Used("Unter B"));
        Assert.True(Used("Tiefer"));
        Assert.Equal(1, CountOf(s.ReadJournal(), "used-without-children::"));

        // Adopting it alone again does not bring the mark back: an earlier usage took its sub-points.
        s.Links.Adopt(s.Book, s.Note("Kern #notiz").Key, new InsertPosition.After(textKey), includeChildren: false);

        Assert.True(Used("Unter B"));
        Assert.Equal(1, CountOf(s.ReadJournal(), "used-without-children::"));
    }

    [Fact]
    public void WithoutChildren_RemovingTheLastSource_RemovesTheMark()
    {
        using var s = new LinkSetup(Book, Journal);
        var r = s.Links.Adopt(s.Book, s.Note("Kern #notiz").Key, new InsertPosition.After(s.Text("Hallo Welt").Key), includeChildren: false);
        Assert.Contains("used-without-children:: true", s.ReadJournal());

        s.Links.RemoveSource(s.Book, r.TextBlockKey, s.Note("Kern #notiz").Id!.Value);

        Assert.DoesNotContain("used-in::", s.ReadJournal());
        Assert.DoesNotContain("used-without-children::", s.ReadJournal());
    }

    [Fact]
    public void WithoutChildren_NotePageNotWritten_TheRetryWritesTheMark()
    {
        using var s = new LinkSetup(Book, Journal);
        s.Writer.FailNext(s.JournalPath, times: 2);

        var r = s.Links.Adopt(s.Book, s.Note("Kern #notiz").Key, new InsertPosition.After(s.Text("Hallo Welt").Key), includeChildren: false);

        Assert.True(r.NoteUpdatePending);
        Assert.Equal(Journal, s.ReadJournal());
        Assert.True(Assert.Single(s.Pending.Load()).WithoutChildren);

        // The note page is read from disk again (without the change that failed to write); the retry brings both lines.
        s.Reopen();

        Assert.Equal(1, s.Links.RetryPending());
        Assert.Contains($"  used-in:: [[Buch - Test]] (({r.TextBlockId}))\n  used-without-children:: true\n", s.ReadJournal());
    }

    [Fact]
    public void PendingFile_WithoutTheField_ReadsAsNoAdoption()
    {
        using var s = new LinkSetup(Book, Journal);
        s.Pending.Add(new PendingNoteUpdate(Guid.NewGuid(), new NoteLocator("journals/x.md", [0], "00"), "Buch - Test", Guid.NewGuid(), Remove: false, WithoutChildren: true));
        var file = Path.Combine(s.Tv.Root, ".noteevolution", "pending.json");
        var json = File.ReadAllText(file);
        Assert.Contains("\"withoutChildren\": true", json);
        File.WriteAllText(file, json.Replace(",\n    \"withoutChildren\": true", "").Replace(",\r\n    \"withoutChildren\": true", ""));

        var update = Assert.Single(new PendingStore(s.Tv.Root).Load());

        Assert.DoesNotContain("withoutChildren", File.ReadAllText(file));
        Assert.Null(update.WithoutChildren);
    }

    [Fact]
    public void WithChildren_NoMark()
    {
        using var s = new LinkSetup(Book, Journal);

        s.Links.Adopt(s.Book, s.Note("Kern #notiz").Key, new InsertPosition.After(s.Text("Hallo Welt").Key));

        Assert.DoesNotContain("used-without-children::", s.ReadJournal());
    }

    [Fact]
    public void AsHeading_WithoutChildren_Marks_UndoRemovesIt()
    {
        using var s = new LinkSetup(Book, Journal);
        var section = s.Book.Root.Children.Single().Key;

        s.Links.AdoptAsHeading(s.Book, s.Note("Kern #notiz").Key, section, asSubheading: true, "(ohne Titel)", includeChildren: false);

        Assert.Contains("  used-in:: [[Buch - Test]]\n  used-without-children:: true\n", s.ReadJournal());

        s.Undo.Undo();

        Assert.DoesNotContain("used-without-children::", s.ReadJournal());
    }

    [Fact]
    public void AdoptInto_AfterDetail_WithoutChildren_BookHasOnlyBlockText_UsedInOnBlock_Undo()
    {
        using var s = new LinkSetup(Book, Journal);
        var detailKey = s.Text("Hallo Welt").Paragraphs.Single(p => p.Text == "Erstes Detail").Block.Key;

        var r = s.Links.AdoptInto(s.Book, s.Note("Kern #notiz").Key, new IntoPosition.AfterDetail(detailKey), includeChildren: false);

        var book = s.ReadBook();
        Assert.Contains("\t\t- Erstes Detail\n\t\t- Kern\n\t\t  status:: relevant\n\t- Nächster Textblock\n", book);
        Assert.DoesNotContain("Unter A", book);
        Assert.DoesNotContain("Tiefer", book);
        Assert.Contains($"used-in:: [[Buch - Test]] (({r.TextBlockId}))", s.ReadJournal());
        Assert.Equal(1, CountOf(s.ReadJournal(), "used-in::"));

        s.Undo.Undo();

        Assert.Equal(Book, s.ReadBook());
        Assert.DoesNotContain("used-in::", s.ReadJournal());
    }

    [Fact]
    public void AdoptInto_AtCursor_WithoutChildren_OnlyTextGoesIn()
    {
        using var s = new LinkSetup(Book, Journal);
        var textKey = s.Text("Hallo Welt").Key;

        s.Links.AdoptInto(s.Book, s.Note("Kern #notiz").Key, new IntoPosition.AtCursor(textKey, 6), includeChildren: false);

        var book = s.ReadBook();
        Assert.Contains("Hallo KernWelt", book);
        Assert.DoesNotContain("Unter A", book);
        Assert.DoesNotContain("Unter B", book);
        Assert.Equal(1, CountOf(s.ReadJournal(), "used-in::"));
        Assert.Contains("- Kern #notiz\n  id::", s.ReadJournal());

        s.Undo.Undo();

        Assert.Equal(Book, s.ReadBook());
        Assert.DoesNotContain("used-in::", s.ReadJournal());
    }

    [Fact]
    public void AdoptAsHeading_WithoutChildren_HeadingWithoutTextBlocks_UsedInOnBlock_Undo()
    {
        using var s = new LinkSetup(Book, Journal);

        var r = s.Links.AdoptAsHeading(s.Book, s.Note("Kern #notiz").Key, Guid.Empty, asSubheading: false, "(ohne Titel)", includeChildren: false);

        var heading = s.Book.FindNode(r.HeadingKey)!;
        Assert.Equal("Kern", heading.Title);
        Assert.Empty(heading.TextBlocks);
        Assert.Empty(heading.Items.OfType<OutlineNode>());
        var book = s.ReadBook();
        Assert.DoesNotContain("Unter A", book);
        Assert.DoesNotContain("Unter B", book);
        Assert.Contains("used-in:: [[Buch - Test]]\n", s.ReadJournal());
        Assert.Equal(1, CountOf(s.ReadJournal(), "used-in::"));

        s.Undo.Undo();

        Assert.Equal(Book, s.ReadBook());
        Assert.DoesNotContain("used-in::", s.ReadJournal());
    }

    [Fact]
    public void AdoptAsHeading_WithoutChildren_NoteWithoutSubPoints_IsAccepted()
    {
        using var s = new LinkSetup(Book, Journal);

        var r = s.Links.AdoptAsHeading(s.Book, s.Note("Zweite Notiz").Key, Guid.Empty, asSubheading: false, "(ohne Titel)", includeChildren: false);

        Assert.Equal("Zweite Notiz", s.Book.FindNode(r.HeadingKey)!.Title);
    }

    /// <summary>The journal with "Unter A" used by another book already (it and "Tiefer" are hidden with "Verwendete ausblenden").</summary>
    private const string JournalWithUsedSubPoint =
        "- Kern #notiz\n" +
        "\t- Unter A\n" +
        "\t  used-in:: [[Buch - Anders]]\n" +
        "\t\t- Tiefer\n" +
        "\t- Unter B\n" +
        "- Zweite Notiz\n" +
        "\t- Benutzt\n" +
        "\t  used-in:: [[Buch - Anders]]\n";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HideUsed_AHiddenUsedSubPointIsNotCopied(bool hideUsed)
    {
        using var s = new LinkSetup(Book, JournalWithUsedSubPoint);

        s.Links.Adopt(s.Book, s.Note("Kern #notiz").Key, new InsertPosition.After(s.Text("Hallo Welt").Key), hideUsed: hideUsed);

        var book = s.ReadBook();
        Assert.Contains("Unter B", book);
        Assert.Equal(!hideUsed, book.Contains("Unter A"));
        Assert.Equal(!hideUsed, book.Contains("Tiefer"));

        // The main point is used with what it took along: the hidden sub-point keeps its own usage only.
        Assert.DoesNotContain("used-without-children::", s.ReadJournal());
        Assert.Equal(1, CountOf(s.ReadJournal(), "used-in:: [[Buch - Anders]]\n\t\t- Tiefer"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HideUsed_AdoptInto_AHiddenUsedSubPointIsNotCopied(bool hideUsed)
    {
        using var s = new LinkSetup(Book, JournalWithUsedSubPoint);

        s.Links.AdoptInto(s.Book, s.Note("Kern #notiz").Key, new IntoPosition.FirstChild(s.Text("Hallo Welt").Key), hideUsed: hideUsed);

        var book = s.ReadBook();
        Assert.Contains("Unter B", book);
        Assert.Equal(!hideUsed, book.Contains("Unter A"));
        Assert.Equal(!hideUsed, book.Contains("Tiefer"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HideUsed_AsHeading_OnlyTheShownSubPointsBecomeTextBlocks(bool hideUsed)
    {
        using var s = new LinkSetup(Book, JournalWithUsedSubPoint);

        var r = s.Links.AdoptAsHeading(s.Book, s.Note("Kern #notiz").Key, Guid.Empty, asSubheading: false, "(ohne Titel)", hideUsed: hideUsed);

        var texts = s.Book.FindNode(r.HeadingKey)!.TextBlocks.Select(t => t.Text).ToList();
        Assert.Equal(hideUsed ? ["Unter B"] : ["Unter A", "Unter B"], texts);
        Assert.Equal(!hideUsed, s.ReadBook().Contains("Tiefer"));

        // The hidden sub-point got no usage of this book.
        Assert.Equal(!hideUsed, s.ReadJournal().Contains("used-in:: [[Buch - Anders]], [[Buch - Test]]"));
    }

    [Fact]
    public void HideUsed_AsHeading_AllSubPointsHidden_IsRefused()
    {
        using var s = new LinkSetup(Book, JournalWithUsedSubPoint);

        Assert.Throws<ArgumentException>(() =>
            s.Links.AdoptAsHeading(s.Book, s.Note("Zweite Notiz").Key, Guid.Empty, asSubheading: false, "(ohne Titel)", hideUsed: true));
        Assert.Equal(Book, s.ReadBook());
    }

    [Fact]
    public void WithoutChildren_BlockWithoutSubPoints_GetsNoMark()
    {
        using var s = new LinkSetup(Book, JournalWithUsedSubPoint);
        var textKey = s.Text("Hallo Welt").Key;

        // Without sub-points, and with its only sub-point hidden: alone is the same as with its subtree.
        s.Links.Adopt(s.Book, s.Note("Unter B").Key, new InsertPosition.After(textKey), includeChildren: false);
        s.Links.Adopt(s.Book, s.Note("Zweite Notiz").Key, new InsertPosition.After(textKey), includeChildren: false, hideUsed: true);

        Assert.DoesNotContain("used-without-children::", s.ReadJournal());
        Assert.Equal(2, CountOf(s.ReadJournal(), "[[Buch - Test]]"));
    }

    [Fact]
    public void Undo_OwnOnlyThenFull_UndoingTheFullOne_BringsTheMarkBack()
    {
        using var s = new LinkSetup(Book, Journal);
        var textKey = s.Text("Hallo Welt").Key;
        var first = s.Links.Adopt(s.Book, s.Note("Kern #notiz").Key, new InsertPosition.After(textKey), includeChildren: false);
        s.Links.Adopt(s.Book, s.Note("Kern #notiz").Key, new InsertPosition.After(textKey));
        Assert.DoesNotContain("used-without-children::", s.ReadJournal());

        s.Undo.Undo();

        Assert.Contains($"  used-in:: [[Buch - Test]] (({first.TextBlockId}))\n  used-without-children:: true\n", s.ReadJournal());
        Assert.False(NoteEvolution.Core.Vaults.NoteBlock.IsUsedBlock(s.Note("Unter B")));
    }

    [Fact]
    public void Undo_OwnOnlyThenFull_NoteChangedMeanwhile_TheRemainingUsageGetsTheMark()
    {
        using var s = new LinkSetup(Book, Journal);
        var textKey = s.Text("Hallo Welt").Key;
        var first = s.Links.Adopt(s.Book, s.Note("Kern #notiz").Key, new InsertPosition.After(textKey), includeChildren: false);
        s.Links.Adopt(s.Book, s.Note("Kern #notiz").Key, new InsertPosition.After(textKey));
        ChangeNote(s, "Kern #notiz");

        s.Undo.Undo();

        var journal = s.ReadJournal();
        Assert.Contains($"used-in:: [[Buch - Test]] (({first.TextBlockId}))\n", journal);
        Assert.Equal(1, CountOf(journal, "[[Buch - Test]]"));
        Assert.Contains("used-without-children:: true", journal);
        Assert.Contains("farbe:: rot", journal);
    }

    [Fact]
    public void Undo_FullThenOwnOnly_UndoingTheFullOne_TheRemainingOwnOnlyUsageGetsTheMark()
    {
        using var s = new LinkSetup(Book, Journal);
        s.Links.Adopt(s.Book, s.Note("Kern #notiz").Key, new InsertPosition.After(s.Text("Hallo Welt").Key));
        var ownOnly = s.Links.AdoptInto(
            s.Book, s.Note("Kern #notiz").Key, new IntoPosition.FirstChild(s.Text("Nächster Textblock").Key), includeChildren: false);
        Assert.DoesNotContain("used-without-children::", s.ReadJournal());

        // The text block changed since: taking back the own-only adoption is refused, its usage stays.
        s.Text("Nächster Textblock").Block.SetContent("Nächster Textblock, geändert");
        Assert.Throws<InvalidOperationException>(s.Undo.Undo);

        s.Undo.Undo();

        var journal = s.ReadJournal();
        Assert.Contains($"  used-in:: [[Buch - Test]] (({ownOnly.TextBlockId}))\n  used-without-children:: true\n", journal);
        Assert.False(NoteEvolution.Core.Vaults.NoteBlock.IsUsedBlock(s.Note("Unter B")));
    }

    [Fact]
    public void Undo_SubPointAloneThenNoteAsHeading_TheSubPointGetsItsMarkBack()
    {
        using var s = new LinkSetup(Book, Journal);
        s.Links.Adopt(s.Book, s.Note("Unter A").Key, new InsertPosition.After(s.Text("Hallo Welt").Key), includeChildren: false);
        Assert.False(NoteEvolution.Core.Vaults.NoteBlock.IsUsedBlock(s.Note("Tiefer")));

        // As a heading the sub-point goes with its own sub-points: the mark goes.
        s.Links.AdoptAsHeading(s.Book, s.Note("Kern #notiz").Key, Guid.Empty, asSubheading: false, "(ohne Titel)");
        Assert.DoesNotContain("used-without-children::", s.ReadJournal());
        Assert.True(NoteEvolution.Core.Vaults.NoteBlock.IsUsedBlock(s.Note("Tiefer")));

        s.Undo.Undo();

        Assert.Equal(1, CountOf(s.ReadJournal(), "used-without-children:: true"));
        Assert.False(NoteEvolution.Core.Vaults.NoteBlock.IsUsedBlock(s.Note("Tiefer")));
    }

    [Fact]
    public void UndoDelete_NoteChangedMeanwhile_TheOwnOnlyUsageComesBackWithItsMark()
    {
        using var s = new LinkSetup(Book, Journal);
        var r = s.Links.Adopt(s.Book, s.Note("Kern #notiz").Key, new InsertPosition.After(s.Text("Hallo Welt").Key), includeChildren: false);
        s.Links.DeleteTextBlock(s.Book, r.TextBlockKey);
        Assert.DoesNotContain("used-without-children::", s.ReadJournal());
        ChangeNote(s, "Kern #notiz");

        s.Undo.Undo();

        Assert.Contains($"  used-in:: [[Buch - Test]] (({r.TextBlockId}))\n  used-without-children:: true\n", s.ReadJournal());
        Assert.Contains("farbe:: rot", s.ReadJournal());
    }

    [Fact]
    public void UndoDelete_NoteChangedMeanwhile_AFullUsageComesBackWithoutTheMark()
    {
        using var s = new LinkSetup(Book, Journal);
        var full = s.Links.Adopt(s.Book, s.Note("Kern #notiz").Key, new InsertPosition.After(s.Text("Hallo Welt").Key));
        s.Links.DeleteTextBlock(s.Book, full.TextBlockKey);

        // Adopted alone meanwhile (its undo refused, so it stays): it has the mark while it is the only usage.
        s.Links.AdoptInto(s.Book, s.Note("Kern #notiz").Key, new IntoPosition.FirstChild(s.Text("Nächster Textblock").Key), includeChildren: false);
        Assert.Contains("used-without-children:: true", s.ReadJournal());
        s.Text("Nächster Textblock").Block.SetContent("Nächster Textblock, geändert");
        Assert.Throws<InvalidOperationException>(s.Undo.Undo);

        s.Undo.Undo();

        Assert.Equal(2, CountOf(s.ReadJournal(), "[[Buch - Test]]"));
        Assert.DoesNotContain("used-without-children::", s.ReadJournal());
        Assert.True(NoteEvolution.Core.Vaults.NoteBlock.IsUsedBlock(s.Note("Unter B")));
    }

    [Fact]
    public void Pending_AFullAdoption_IsReplayedWithoutTheOldMark()
    {
        using var s = new LinkSetup(Book, Journal);
        var textKey = s.Text("Hallo Welt").Key;
        s.Links.Adopt(s.Book, s.Note("Kern #notiz").Key, new InsertPosition.After(textKey), includeChildren: false);
        Assert.Contains("used-without-children:: true", s.ReadJournal());
        s.Writer.FailNext(s.JournalPath, times: 2);

        var full = s.Links.Adopt(s.Book, s.Note("Kern #notiz").Key, new InsertPosition.After(textKey));

        Assert.True(full.NoteUpdatePending);
        Assert.False(Assert.Single(s.Pending.Load()).WithoutChildren);
        s.Reopen();
        Assert.Contains("used-without-children:: true", s.ReadJournal());

        Assert.Equal(1, s.Links.RetryPending());

        Assert.Equal(2, CountOf(s.ReadJournal(), "[[Buch - Test]]"));
        Assert.DoesNotContain("used-without-children::", s.ReadJournal());
    }

    [Fact]
    public void Pending_AnAddThatIsNoAdoption_KeepsTheMark()
    {
        using var s = new LinkSetup(Book, Journal);
        s.Links.Adopt(s.Book, s.Note("Kern #notiz").Key, new InsertPosition.After(s.Text("Hallo Welt").Key), includeChildren: false);
        var noteId = s.Note("Kern #notiz").Id!.Value;
        s.Pending.Add(new PendingNoteUpdate(
            noteId, new NoteLocator(LinkSetup.JournalFile, [0], "00"), "Buch - Test", Guid.NewGuid(), Remove: false));

        Assert.Equal(1, s.Links.RetryPending());

        Assert.Equal(2, CountOf(s.ReadJournal(), "[[Buch - Test]]"));
        Assert.Contains("used-without-children:: true", s.ReadJournal());
    }

    /// <summary>Changes the note's own lines outside the link service (another property), saved to its file.</summary>
    private static void ChangeNote(LinkSetup s, string content)
    {
        s.Note(content).SetProperty("farbe", "rot");
        s.Writer.Save(s.Journal);
    }

    private static int CountOf(string text, string needle) =>
        text.Split(needle).Length - 1;
}
