using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Links;
using NoteEvolution.Core.Tests.Links;
using NoteEvolution.TestSupport;

namespace NoteEvolution.Core.Tests.Books;

public class AppendixSectionTests
{
    private const string G1 = "0193a1c2-1b7e-7c1d-9a0f-2b3c4d5e6f70";

    private const string Content =
        "title:: Buch: Test\n" +
        "type:: book\n" +
        "\n" +
        "- # Eins\n" +
        "\t- Text eins\n" +
        "- # Zwei\n" +
        "\t- Text zwei\n";

    private const string Glossary =
        "- # Glossar\n" +
        "  anhang:: glossar\n" +
        "\t- Achtsamkeit\n" +
        $"\t  id:: {G1}\n" +
        "\t  Bewusstes Wahrnehmen des Augenblicks.\n";

    private const string TestBook = Content + Glossary;

    private const string Journal =
        "- Thema\n" +
        "\t- Punkt eins\n" +
        "- Weitere\n" +
        "\t- Punkt zwei\n";

    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Appendix_Recognised_ByProperty_NotByTitle()
    {
        using (var s = new LinkSetup(TestBook.Replace("- # Glossar\n", "- # Begriffe\n"), Journal))
        {
            var section = s.Book.Root.Children.Single(n => n.Title == "Begriffe");
            Assert.Equal(AppendixKind.Glossary, section.AppendixKind);
            Assert.Same(section, s.Book.AppendixSection(AppendixKind.Glossary));
        }

        using (var s = new LinkSetup(TestBook.Replace("  anhang:: glossar\n", ""), Journal))
        {
            Assert.Equal(AppendixKind.None, s.Book.Root.Children.Single(n => n.Title == "Glossar").AppendixKind);
            Assert.Null(s.Book.AppendixSection(AppendixKind.Glossary));
        }

        // The value is read case-insensitively; an unknown value and a property below the top level mark nothing.
        const string others =
            "type:: book\n\n" +
            "- # Eins\n\t- ## Unten\n\t  anhang:: quellen\n" +
            "- # Leute\n  anhang:: Menschen\n" +
            "- # Liste\n  anhang:: quellen\n" +
            "- # Sonst\n  anhang:: register\n";
        using (var s = new LinkSetup(others, Journal))
        {
            var kinds = s.Book.Root.Children.Select(n => n.AppendixKind).ToList();
            Assert.Equal([AppendixKind.None, AppendixKind.People, AppendixKind.Sources, AppendixKind.None], kinds);
            Assert.Equal(AppendixKind.None, s.Book.FindNode(s.SectionKey("Unten"))!.AppendixKind);
        }
    }

    [Fact]
    public void ContentChildren_And_WordCount_ExcludeTheAppendix()
    {
        using var s = new LinkSetup(TestBook, Journal);
        var book = s.Book;

        Assert.Equal(["Eins", "Zwei"], book.ContentChildren.Select(n => n.Title));
        Assert.Equal(4, book.Root.WordCount);
        Assert.Equal(4, book.ContentWordCount);
        Assert.True(book.AppendixSection(AppendixKind.Glossary)!.WordCount > 0);
        Assert.Null(book.AppendixSection(AppendixKind.People));
        Assert.Null(book.AppendixSection(AppendixKind.Sources));
        Assert.Empty(book.DuplicateAppendixSections);
    }

    [Fact]
    public void EditorSnapshotAndSync_LeaveTheAppendixUntouched()
    {
        using var s = new LinkSetup(TestBook, Journal);
        var book = s.Book;
        var snapshot = BookSnapshot.Create(book, s.Vault, book.Root.Key, true);
        Assert.Equal(["Eins", "Text eins", "Zwei", "Text zwei"], snapshot.Nodes.Select(Shown));

        // Edit "Text zwei", delete "Text eins" and add a new text block at the end of the manuscript.
        var two = snapshot.Nodes.OfType<SnapshotTextBlock>().Single(t => t.Text == "Text zwei");
        var added = new SnapshotTextBlock(Guid.NewGuid(), null, "Ganz am Ende", [], []);
        var edited = snapshot with
        {
            Nodes =
            [
                .. snapshot.Nodes
                    .Where(n => n is not SnapshotTextBlock { Text: "Text eins" })
                    .Select(n => n == two ? two with { Text = "Text zwei, geändert" } : n),
                added,
            ],
        };

        var result = BookSync.Apply(book, edited, s.Vault, Now);
        Assert.True(result.Changed);
        s.Writer.Save(book.Page);

        var bytes = File.ReadAllBytes(s.BookPath);
        var appendix = Encoding.UTF8.GetBytes(Glossary);
        Assert.True(bytes.AsSpan().EndsWith(appendix), Encoding.UTF8.GetString(bytes));
        var text = s.ReadBook();
        Assert.DoesNotContain("Text eins", text);
        Assert.Contains("Text zwei, geändert", text);
        Assert.True(text.IndexOf("Ganz am Ende", StringComparison.Ordinal) < text.IndexOf("- # Glossar", StringComparison.Ordinal));

        // An unchanged save of the reloaded book changes nothing.
        var reloaded = s.Book;
        Assert.False(BookSync.Apply(reloaded, BookSnapshot.Create(reloaded, s.Vault, Guid.Empty, true), s.Vault, Now).Changed);
    }

    [Fact]
    public void SnapshotWithAppendixKey_IsRefused()
    {
        using var s = new LinkSetup(TestBook, Journal);
        var book = s.Book;
        var glossary = book.AppendixSection(AppendixKind.Glossary)!;
        var entry = glossary.TextBlocks.Single();
        var snapshot = BookSnapshot.Create(book, s.Vault, Guid.Empty, true);

        SectionSnapshot[] refused =
        [
            snapshot with { Nodes = [.. snapshot.Nodes, new SnapshotTextBlock(entry.Key, null, "Achtsamkeit", [], [])] },
            snapshot with { Nodes = [.. snapshot.Nodes, new SnapshotHeading(glossary.Key, 1, "Glossar")] },
            snapshot with
            {
                Nodes =
                [
                    .. snapshot.Nodes,
                    new SnapshotTextBlock(Guid.NewGuid(), null, "Neu", [new SnapshotParagraph(entry.Key, "x", 1, false)], []),
                ],
            },
        ];

        foreach (var bad in refused)
        {
            Assert.Throws<ArgumentException>(() => BookSync.Apply(book, bad, s.Vault, Now));
            Assert.Equal(TestBook, Serialize(book));
        }

        Assert.Throws<ArgumentException>(() => BookSnapshot.Create(book, s.Vault, glossary.Key, true));
        Assert.Throws<ArgumentException>(() => BookSync.Apply(book, new SectionSnapshot(glossary.Key, true, []), s.Vault, Now));
        Assert.Equal(TestBook, Serialize(book));
    }

    [Fact]
    public void NewTopLevelSections_GoBeforeTheAppendix()
    {
        using var s = new LinkSetup(TestBook, Journal);
        var book = s.Book;

        // "+ Abschnitt" in the outline.
        OutlineEditor.AddHeading(book, Guid.Empty, "Drei");
        book = Book.Load(book.Page);
        Assert.Equal(["Eins", "Zwei", "Drei", "Glossar"], book.Root.Children.Select(n => n.Title));

        // "Neuer Abschnitt danach" on the last section.
        var three = book.ContentChildren.Last();
        var added = ManuscriptEditor.InsertAfter(book, new BookElement(ElementKind.Heading, three.Key), Now);
        book = Book.Load(book.Page);
        Assert.Equal([.. book.ContentChildren.Select(n => n.Key), book.AppendixSection(AppendixKind.Glossary)!.Key], book.Root.Children.Select(n => n.Key));
        Assert.Equal(added, book.ContentChildren.Last().Key);

        // Dragging a section to the end of the root.
        var one = book.Root.Children.First();
        OutlineEditor.MoveSection(book, one.Key, Guid.Empty, book.Root.Children.Count() - 1);
        book = Book.Load(book.Page);
        Assert.Equal(["Zwei", "Drei", "", "Eins", "Glossar"], book.Root.Children.Select(n => n.Title));
        Assert.EndsWith(Glossary, Serialize(book));
    }

    [Fact]
    public void AdoptAsHeading_AtTopLevel_GoesBeforeTheAppendix()
    {
        using var s = new LinkSetup(TestBook, Journal);

        s.Links.AdoptAsHeading(s.Book, s.Note("Thema").Key, Guid.Empty, asSubheading: false, "(ohne Titel)");
        Assert.Equal(["Eins", "Zwei", "Thema", "Glossar"], s.Book.Root.Children.Select(n => n.Title));

        s.Links.AdoptAsHeading(s.Book, s.Note("Weitere").Key, s.SectionKey("Thema"), asSubheading: false, "(ohne Titel)");
        Assert.Equal(["Eins", "Zwei", "Thema", "Weitere", "Glossar"], s.Book.Root.Children.Select(n => n.Title));
        Assert.EndsWith(Glossary, s.ReadBook());

        var glossary = s.Book.AppendixSection(AppendixKind.Glossary)!.Key;
        Assert.Throws<ArgumentException>(() =>
            s.Links.AdoptAsHeading(s.Book, s.Note("Thema").Key, glossary, asSubheading: true, "(ohne Titel)"));
    }

    [Fact]
    public void Adopt_IntoTheAppendix_IsRefused()
    {
        using var s = new LinkSetup(TestBook, Journal);
        var glossary = s.Book.AppendixSection(AppendixKind.Glossary)!;
        var entry = glossary.TextBlocks.Single().Key;
        var note = s.Note("Thema").Key;

        InsertPosition[] positions =
        [
            new InsertPosition.After(entry),
            new InsertPosition.SectionStart(glossary.Key),
            new InsertPosition.SectionEnd(glossary.Key),
        ];
        foreach (var position in positions)
        {
            Assert.Throws<ArgumentException>(() => s.Links.Adopt(s.Book, note, position));
            Assert.Equal(TestBook, s.ReadBook());
        }

        IntoPosition[] into =
        [
            new IntoPosition.AtCursor(entry, 0),
            new IntoPosition.FirstChild(entry),
            new IntoPosition.FirstChild(glossary.Key),
        ];
        foreach (var position in into)
        {
            Assert.Throws<ArgumentException>(() => s.Links.AdoptInto(s.Book, note, position));
            Assert.Equal(TestBook, s.ReadBook());
        }

        Assert.Equal(Journal, s.ReadJournal());
    }

    [Fact]
    public void ToHeading_LastTextBlockOfTheText_GoesBeforeTheAppendix()
    {
        using var s = new LinkSetup(TestBook, Journal);
        var book = s.Book;
        var last = book.ContentChildren.Last().TextBlocks.Single().Key;

        // "Als Überschrift" at the top level on the last text block of the last section of the text.
        var key = ManuscriptEditor.ToHeading(book, last, 1, Now);

        book = Book.Load(book.Page);
        Assert.Equal(["Eins", "Zwei", "Text zwei", "Glossar"], book.Root.Children.Select(n => n.Title));
        Assert.Equal(key, book.ContentChildren.Last().Key);
        Assert.Equal(AppendixKind.None, book.FindNode(key)!.AppendixKind);
        Assert.EndsWith(Glossary, Serialize(book));
    }

    [Fact]
    public void AppendixSection_CannotBeMovedIndentedOrRenamed()
    {
        const string book = Content + Glossary + "- # Drei\n";
        using var s = new LinkSetup(book, Journal);
        var b = s.Book;
        var glossary = b.AppendixSection(AppendixKind.Glossary)!.Key;
        var two = s.SectionKey("Zwei");
        var three = s.SectionKey("Drei");
        var heading = new BookElement(ElementKind.Heading, glossary);

        Action[] refused =
        [
            () => OutlineEditor.Rename(b, glossary, "Begriffe"),
            () => OutlineEditor.MoveSection(b, glossary, Guid.Empty, 0),
            () => OutlineEditor.MoveSection(b, two, glossary, 0),
            () => OutlineEditor.AddHeading(b, glossary, "Neu"),
            () => ManuscriptEditor.Indent(b, glossary),
            () => ManuscriptEditor.Outdent(b, glossary),
            () => ManuscriptEditor.RemoveHeading(b, glossary, Now),
            () => ManuscriptEditor.SetHeadingLevel(b, glossary, 2),
            () => ManuscriptEditor.InsertAfter(b, heading, Now),
            () => ManuscriptEditor.InsertChild(b, heading, Now),
            () => ManuscriptEditor.Wrap(b, heading),
        ];

        foreach (var action in refused)
        {
            Assert.Throws<ArgumentException>(action);
            Assert.Equal(book, Serialize(b));
        }

        Assert.False(ManuscriptEditor.CanIndent(b, glossary));
        Assert.False(ManuscriptEditor.CanOutdent(b, glossary));
        Assert.False(ManuscriptEditor.CanWrap(b, heading));

        // A section after the appendix is indented into the previous section of the text, not into the appendix.
        ManuscriptEditor.Indent(b, three);
        b = Book.Load(b.Page);
        Assert.Equal(two, b.FindNode(three)!.Parent!.Key);
        Assert.EndsWith(Glossary, Serialize(b));
    }

    [Fact]
    public void CrossRefIndex_And_BookElements_IgnoreAppendixEntries()
    {
        using var s = new LinkSetup(TestBook.Replace("\t- Text zwei\n", $"\t- Text zwei (({G1}))\n"), Journal);
        var book = s.Book;
        var glossary = book.AppendixSection(AppendixKind.Glossary)!;
        var entry = glossary.TextBlocks.Single();

        var index = CrossRefIndex.Of(book);
        Assert.Null(index.Target(Guid.Parse(G1)));
        Assert.Empty(index.To(Guid.Parse(G1)));
        Assert.Empty(index.Missing);

        Assert.Null(BookElements.Find(book, glossary.Key));
        Assert.Null(BookElements.Find(book, entry.Key));
        Assert.Equal(s.SectionKey("Zwei"), BookElements.LastHeadingKey(book));
        Assert.Throws<ArgumentException>(() =>
            ManuscriptEditor.SetStatus(book, new BookElement(ElementKind.TextBlock, entry.Key), BlockStatus.Done, Now));
    }

    [Fact]
    public void DuplicateAppendixSection_FirstWins_SecondListed()
    {
        const string second = "- # Noch ein Glossar\n  anhang:: glossar\n\t- Zweiter Begriff\n";
        using var s = new LinkSetup(TestBook + second, Journal);
        var book = s.Book;

        var first = book.AppendixSection(AppendixKind.Glossary)!;
        Assert.Equal("Glossar", first.Title);
        var duplicate = Assert.Single(book.DuplicateAppendixSections);
        Assert.Equal("Noch ein Glossar", duplicate.Title);
        Assert.Equal(AppendixKind.Glossary, duplicate.AppendixKind);
        Assert.Equal(["Eins", "Zwei"], book.ContentChildren.Select(n => n.Title));
        Assert.Equal(4, book.ContentWordCount);
        Assert.DoesNotContain(
            BookSnapshot.Create(book, s.Vault, Guid.Empty, true).Nodes,
            n => n.Key == duplicate.Key || duplicate.TextBlocks.Any(t => t.Key == n.Key));
    }

    private static string Shown(SnapshotNode node) => node switch
    {
        SnapshotHeading heading => heading.Text,
        SnapshotTextBlock text => text.Text,
        _ => "",
    };

    private static string Serialize(Book book) => Encoding.UTF8.GetString(PageSerializer.Serialize(book.Page));
}
