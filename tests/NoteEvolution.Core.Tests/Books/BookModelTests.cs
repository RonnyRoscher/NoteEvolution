using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;
using NoteEvolution.TestSupport;

namespace NoteEvolution.Core.Tests.Books;

public class BookModelTests
{
    private const string IdA = "7f3a91c2-4b1d-4e8a-9c3f-1a2b3c4d5e6f";
    private const string IdB = "81bb02aa-5c2e-4f9b-8d4a-2b3c4d5e6f71";

    private static Page Parse(string text, string path = "Buch - Test.md") =>
        LogseqParser.Parse(path, Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Load_SpecExample_BuildsOutlineAndTextBlocks()
    {
        var b = Book.Load(Parse(Samples.SpecBook));
        var part = b.Root.Children.Single();
        Assert.Equal(("Liebe und Wahrheit", 1), (part.Title, part.Level));
        var tb = part.Children.Single().TextBlocks.Single();
        Assert.Equal("Angst baut Widerstand auf, Vertrauen baut Schwung auf.", tb.Text);
        Assert.Equal(2, tb.Sources.Count);
        Assert.Equal([false, true], tb.Paragraphs.Select(p => p.IsNote));
        Assert.Equal("noch ein Beispiel ergänzen", tb.Paragraphs[1].Text);
        Assert.Equal([1, 1], tb.Paragraphs.Select(p => p.Depth));
    }

    [Fact]
    public void Load_SpecExample_ExposesBookMetadataAndLookups()
    {
        var page = Parse(Samples.SpecBook, "Buch - LoveMagic.md");
        var b = Book.Load(page);
        Assert.Equal("Buch - LoveMagic", b.LinkName);
        Assert.Equal("Buch: LoveMagic", b.Title);
        Assert.Equal("Für alle, die weiterfragen.", b.Dedication);
        Assert.Empty(b.Warnings);

        var section = b.Root.Children.Single().Children.Single();
        var tb = section.TextBlocks.Single();
        Assert.Same(section, tb.Section);
        Assert.Same(b.Root, section.Parent!.Parent);
        Assert.Same(section, b.FindNode(section.Key));
        Assert.Same(b.Root, b.FindNode(Guid.Empty));
        Assert.Null(b.FindNode(Guid.NewGuid()));
        Assert.Same(tb, b.FindTextBlock(tb.Key));
        Assert.Same(tb, b.FindTextBlockById(Guid.Parse("6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70")));
        Assert.Null(b.FindTextBlockById(Guid.NewGuid()));
        Assert.Equal(Guid.Empty, b.Root.Key);
        Assert.Null(b.Root.Block);
        Assert.False(page.IsDirty);
    }

    [Fact]
    public void Load_NoTitleProperty_UsesPageName()
    {
        var b = Book.Load(Parse("type:: book\n\n- # A\n", "Mein Buch.md"));
        Assert.Equal("Mein Buch", b.Title);
        Assert.Null(b.Dedication);
    }

    [Fact]
    public void Load_TextBeforeFirstHeading_IsPrologueUnderRoot()
    {
        var b = Book.Load(Parse("type:: book\n\n- Vorspann\n\t- Absatz\n- # Kapitel\n\t- Inhalt\n"));
        Assert.Equal(2, b.Root.Items.Count);
        var prologue = Assert.IsType<TextBlock>(b.Root.Items[0]);
        Assert.Equal("Vorspann", prologue.Text);
        Assert.Same(b.Root, prologue.Section);
        Assert.Equal(["Absatz"], prologue.Paragraphs.Select(p => p.Text));
        var chapter = Assert.IsType<OutlineNode>(b.Root.Items[1]);
        Assert.Equal("Kapitel", chapter.Title);
        Assert.Equal(["Inhalt"], chapter.TextBlocks.Select(t => t.Text));
    }

    [Fact]
    public void Load_ChildHeadingNotDeeper_AddsWarning_WritesNothing()
    {
        var page = Parse("type:: book\n\n- ## Eltern\n\t- # Kind\n\t- ## Gleich\n\t\t- ### Tief\n");
        var b = Book.Load(page);

        var parent = b.Root.Children.Single();
        Assert.Equal(["Kind", "Gleich"], parent.Children.Select(c => c.Title));
        Assert.Equal(
            [
                "Überschrift ‚Kind‘ ist nicht tiefer als ‚Eltern‘",
                "Überschrift ‚Gleich‘ ist nicht tiefer als ‚Eltern‘",
            ],
            b.Warnings.Select(w => w.Message));
        Assert.Equal(parent.Children.Select(c => c.Key), b.Warnings.Select(w => w.BlockKey));
        Assert.False(page.IsDirty);
    }

    [Fact]
    public void Load_HeadingInsideTextBlock_StaysParagraphWithWarning()
    {
        var page = Parse("type:: book\n\n- # Kapitel\n\t- Text\n\t\t- ## Eingebettet\n\t\t\t- tiefer\n");
        var b = Book.Load(page);
        var tb = b.Root.Children.Single().TextBlocks.Single();
        Assert.Equal(["## Eingebettet", "tiefer"], tb.Paragraphs.Select(p => p.Text));
        Assert.Equal([1, 2], tb.Paragraphs.Select(p => p.Depth));
        Assert.Empty(b.Root.Children.Single().Children);
        var warning = Assert.Single(b.Warnings);
        Assert.Equal("Überschrift ‚Eingebettet‘ innerhalb eines Textblocks", warning.Message);
        Assert.Equal(tb.Paragraphs[0].Block.Key, warning.BlockKey);
        Assert.False(page.IsDirty);
    }

    [Fact]
    public void Load_TextAfterSubsection_KeepsFileOrderInItems()
    {
        var b = Book.Load(Parse(
            "type:: book\n\n- # Kapitel\n\t- vorher\n\t- ## Abschnitt\n\t\t- im Abschnitt\n\t- nachher\n"));
        var chapter = b.Root.Children.Single();
        Assert.Equal(
            ["vorher", "Abschnitt", "nachher"],
            chapter.Items.Select(i => i switch { TextBlock t => t.Text, OutlineNode n => n.Title, _ => "?" }));
        Assert.Equal(["Abschnitt"], chapter.Children.Select(c => c.Title));
        Assert.Equal(["vorher", "nachher"], chapter.TextBlocks.Select(t => t.Text));
    }

    [Fact]
    public void WordCount_ExcludesNotes_SourceCountDistinct()
    {
        var b = Book.Load(Parse(
            "type:: book\n\n" +
            "- # Kapitel\n" +
            "\t- eins zwei drei\n" +
            $"\t  source:: (({IdA})), (({IdB}))\n" +
            "\t\t- vier fünf\n" +
            "\t\t- Notiz sechs sieben #notiz\n" +
            "\t- ## Abschnitt\n" +
            "\t\t- acht\n" +
            $"\t\t  source:: (({IdA}))\n"));
        var chapter = b.Root.Children.Single();
        Assert.Equal(6, chapter.WordCount);
        Assert.Equal(2, chapter.SourceCount);
        Assert.Equal(1, chapter.Children.Single().WordCount);
        Assert.Equal(1, chapter.Children.Single().SourceCount);
        Assert.Equal(6, b.Root.WordCount);
        Assert.Equal(2, b.Root.SourceCount);
        Assert.Equal(0, Book.Load(Parse("type:: book\n")).Root.WordCount);
    }

    [Fact]
    public void IsBook_DetectsTypeBook()
    {
        Assert.True(Book.IsBook(Parse("type:: book\n")));
        Assert.True(Book.IsBook(Parse("Type:: Book\n")));
        Assert.False(Book.IsBook(Parse("type:: page\n")));
        Assert.False(Book.IsBook(Parse("title:: x\n")));
        Assert.False(Book.IsBook(Parse("- type:: book\n")));
    }
}
