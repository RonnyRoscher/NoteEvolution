using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Tests.Links;
using NoteEvolution.TestSupport;

namespace NoteEvolution.Core.Tests.Books;

public class BookSyncTests
{
    private const string B1 = "6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70";
    private const string N1 = "7f3a91c2-4b1d-4e8a-9c3f-1a2b3c4d5e6f";
    private const string N2 = "81bb02aa-5c2e-4f9b-8d4a-2b3c4d5e6f71";

    private const string TestBook =
        "title:: Buch: Test\n" +
        "type:: book\n" +
        "\n" +
        "- Vorspann\n" +
        "- # Kapitel\n" +
        "\t- Einleitung\n" +
        "\t- ## Abschnitt\n" +
        "\t  collapsed:: true\n" +
        "\t\t- Erster Textblock\n" +
        $"\t\t  id:: {B1}\n" +
        $"\t\t  source:: (({N1})), (({N2}))\n" +
        "\t\t\t- Absatz eins\n" +
        "\t\t\t- Absatz zwei #notiz\n" +
        "\t\t- Zweiter Textblock\n" +
        "\t\t- ### Unterabschnitt\n" +
        "\t\t\t- Text im Unterabschnitt\n" +
        "\t\t- Nachtrag\n" +
        "\t- ## Zweiter Abschnitt\n" +
        "\t\t- Anderswo\n";

    private const string TestJournal =
        "- Erste Quelle\n" +
        $"  id:: {N1}\n" +
        $"  used-in:: [[Buch - Test]] (({B1}))\n" +
        "- Zweite Quelle\n" +
        $"  id:: {N2}\n" +
        $"  used-in:: [[Buch - Test]] (({B1}))\n";

    private const string ErsterTextblock =
        "\t\t- Erster Textblock\n" +
        $"\t\t  id:: {B1}\n" +
        $"\t\t  source:: (({N1})), (({N2}))\n" +
        "\t\t\t- Absatz eins\n" +
        "\t\t\t- Absatz zwei #notiz\n";

    [Fact]
    public void Apply_UnchangedSnapshot_NotChanged_BytesIdentical()
    {
        using var s = new LinkSetup(TestBook, TestJournal);
        var book = s.Book;
        var scopes = book.Page.AllBlocks().Select(b => book.FindNode(b.Key)).OfType<OutlineNode>().Select(n => n.Key)
            .Prepend(Guid.Empty).ToList();
        Assert.Equal(5, scopes.Count);

        foreach (var scope in scopes)
        {
            foreach (var manuscript in new[] { false, true })
            {
                var result = BookSync.Apply(book, BookSnapshot.Create(book, s.Vault, scope, manuscript));

                Assert.False(result.Changed);
                Assert.Empty(result.Effects);
                Assert.Equal(TestBook, Serialize(book));
                Assert.False(book.Page.IsDirty);
            }
        }
    }

    [Fact]
    public void Apply_TextEdit_ChangesOnlyThatLine()
    {
        using var s = new LinkSetup(TestBook, TestJournal);
        var snapshot = Section(s, "Abschnitt");
        var block = TextNode(snapshot, "Zweiter Textblock");

        var result = BookSync.Apply(s.Book, Replace(snapshot, block, block with { Text = "Zweiter Textblock, überarbeitet" }));

        Assert.True(result.Changed);
        Assert.Empty(result.Effects);
        var after = Serialize(s.Book);
        Assert.Equal(TestBook.Replace("\t\t- Zweiter Textblock\n", "\t\t- Zweiter Textblock, überarbeitet\n"), after);
        var (removed, added) = LineDiff.Changed(Encoding.UTF8.GetBytes(TestBook), Encoding.UTF8.GetBytes(after));
        Assert.Single(removed);
        Assert.Single(added);
    }

    [Fact]
    public void Apply_NewKeyAfterExisting_InsertsWithoutId()
    {
        using var s = new LinkSetup(TestBook, TestJournal);
        var snapshot = Section(s, "Abschnitt");
        var newKey = Guid.NewGuid();
        var paragraphKey = Guid.NewGuid();
        var nodes = snapshot.Nodes.ToList();
        nodes.Insert(1, new SnapshotTextBlock(newKey, null, "Neuer Block", [new SnapshotParagraph(paragraphKey, "Neuer Absatz", 1, false)], []));

        var result = BookSync.Apply(s.Book, snapshot with { Nodes = nodes });

        Assert.True(result.Changed);
        Assert.Empty(result.Effects);
        Assert.Equal(
            TestBook.Replace("\t\t- Zweiter Textblock\n", "\t\t- Neuer Block\n\t\t\t- Neuer Absatz\n\t\t- Zweiter Textblock\n"),
            Serialize(s.Book));
        var created = Book.Load(s.Book.Page).FindTextBlock(newKey);
        Assert.NotNull(created);
        Assert.Null(created.Block.Id);
        Assert.Equal(paragraphKey, Assert.Single(created.Paragraphs).Block.Key);
    }

    [Fact]
    public void Apply_NewFirstBlock_BeforeFirstTextBlock_ElseBeforeSubheading_ElseAtEnd()
    {
        using (var s = new LinkSetup(TestBook, TestJournal))
        {
            var snapshot = Section(s, "Kapitel");
            BookSync.Apply(s.Book, snapshot with { Nodes = [NewText("Neu"), .. snapshot.Nodes] });
            Assert.Equal(TestBook.Replace("\t- Einleitung\n", "\t- Neu\n\t- Einleitung\n"), Serialize(s.Book));
        }

        const string onlySubheading = "type:: book\n\n- # K\n\t- ## U\n\t\t- x\n- # L\n";
        using (var s = new LinkSetup(onlySubheading))
        {
            BookSync.Apply(s.Book, Section(s, "K") with { Nodes = [NewText("Neu")] });
            Assert.Equal("type:: book\n\n- # K\n\t- Neu\n\t- ## U\n\t\t- x\n- # L\n", Serialize(s.Book));
        }

        using (var s = new LinkSetup(onlySubheading))
        {
            BookSync.Apply(s.Book, Section(s, "L") with { Nodes = [NewText("Neu")] });
            Assert.Equal("type:: book\n\n- # K\n\t- ## U\n\t\t- x\n- # L\n\t- Neu\n", Serialize(s.Book));
        }
    }

    [Fact]
    public void Apply_SplitOfLinkedBlock_NewIdSameSources_EffectSplit()
    {
        using var s = new LinkSetup(TestBook, TestJournal);
        var snapshot = Section(s, "Abschnitt");
        var original = TextNode(snapshot, "Erster Textblock");
        var newKey = Guid.NewGuid();
        var first = original with { Text = "Erster", Paragraphs = [original.Paragraphs[0]] };
        var second = new SnapshotTextBlock(newKey, original.Key, "Textblock", [original.Paragraphs[1]], original.Sources);

        var result = BookSync.Apply(s.Book, Replace(snapshot, original, first, second));

        Assert.True(result.Changed);
        var book = Book.Load(s.Book.Page);
        var created = book.FindTextBlock(newKey)!;
        var newId = created.Block.Id!.Value;
        Assert.NotEqual(Guid.Parse(B1), newId);
        Assert.Equal(
            TestBook.Replace(
                ErsterTextblock,
                "\t\t- Erster\n" +
                $"\t\t  id:: {B1}\n" +
                $"\t\t  source:: (({N1})), (({N2}))\n" +
                "\t\t\t- Absatz eins\n" +
                "\t\t- Textblock\n" +
                $"\t\t  id:: {newId}\n" +
                $"\t\t  source:: (({N1})), (({N2}))\n" +
                "\t\t\t- Absatz zwei #notiz\n"),
            Serialize(s.Book));
        var split = Assert.IsType<BlockSplit>(Assert.Single(result.Effects));
        Assert.Equal(newId, split.NewBookBlockId);
        Assert.Equal([Guid.Parse(N1), Guid.Parse(N2)], split.Sources);
    }

    [Fact]
    public void Apply_SplitOfUnlinkedBlock_NewBlockWithoutId_NoEffect()
    {
        using var s = new LinkSetup(TestBook, TestJournal);
        var snapshot = Section(s, "Abschnitt");
        var original = TextNode(snapshot, "Zweiter Textblock");
        var newKey = Guid.NewGuid();

        var result = BookSync.Apply(
            s.Book,
            Replace(snapshot, original, original with { Text = "Zweiter" }, new SnapshotTextBlock(newKey, original.Key, "Textblock", [], [])));

        Assert.Empty(result.Effects);
        Assert.Equal(TestBook.Replace("\t\t- Zweiter Textblock\n", "\t\t- Zweiter\n\t\t- Textblock\n"), Serialize(s.Book));
    }

    [Fact]
    public void ApplySyncEffects_Split_AddsSecondUsageToNote()
    {
        using var s = new LinkSetup(TestBook, TestJournal);
        var book = s.Book;
        var snapshot = Section(s, "Abschnitt");
        var original = TextNode(snapshot, "Erster Textblock");
        var newKey = Guid.NewGuid();
        var result = BookSync.Apply(
            book,
            Replace(snapshot, original, original with { Text = "Erster" }, new SnapshotTextBlock(newKey, original.Key, "Textblock", [], original.Sources)));
        var newId = Book.Load(book.Page).FindTextBlock(newKey)!.Block.Id!.Value;

        s.Writer.Save(book.Page);
        s.Links.ApplySyncEffects(book, result.Effects);

        Assert.Equal(
            TestJournal.Replace(
                $"  used-in:: [[Buch - Test]] (({B1}))\n",
                $"  used-in:: [[Buch - Test]] (({B1})), [[Buch - Test]] (({newId}))\n"),
            s.ReadJournal());
        Assert.Empty(s.Pending.Load());
    }

    [Fact]
    public void Apply_MissingLinkedBlock_RemovedWithDeleteEffect()
    {
        using var s = new LinkSetup(TestBook, TestJournal);
        var book = s.Book;
        var snapshot = Section(s, "Abschnitt");

        var result = BookSync.Apply(book, Replace(snapshot, TextNode(snapshot, "Erster Textblock")));

        Assert.True(result.Changed);
        var expectedBook = TestBook.Replace(ErsterTextblock, "");
        Assert.Equal(expectedBook, Serialize(book));
        var deleted = Assert.IsType<BlockDeleted>(Assert.Single(result.Effects));
        Assert.Equal(Guid.Parse(B1), deleted.BookBlockId);
        Assert.Equal([Guid.Parse(N1), Guid.Parse(N2)], deleted.Sources);

        s.Writer.Save(book.Page);
        s.Links.ApplySyncEffects(book, result.Effects);

        Assert.Equal(expectedBook, s.ReadBook());
        Assert.Equal($"- Erste Quelle\n  id:: {N1}\n- Zweite Quelle\n  id:: {N2}\n", s.ReadJournal());
        Assert.Equal("Löschen", s.Undo.NextDescription);

        s.Undo.Undo();

        Assert.Equal(TestBook, s.ReadBook());
        Assert.Equal(TestJournal, s.ReadJournal());
        Assert.Empty(s.Pending.Load());
    }

    [Fact]
    public void Apply_TwoLinkedBlocksMissing_TwoDeleteEffects_UndoRestoresBothFiles()
    {
        const string b2 = "0199a1c2-1b7e-7c1d-9a0f-2b3c4d5e6f72";
        var bookText = TestBook.Replace("\t\t- Zweiter Textblock\n", $"\t\t- Zweiter Textblock\n\t\t  id:: {b2}\n\t\t  source:: (({N1}))\n");
        var journalText = TestJournal.Replace(
            $"  id:: {N1}\n  used-in:: [[Buch - Test]] (({B1}))\n",
            $"  id:: {N1}\n  used-in:: [[Buch - Test]] (({B1})), [[Buch - Test]] (({b2}))\n");
        using var s = new LinkSetup(bookText, journalText);
        var book = s.Book;
        var snapshot = Manuscript(s, "Kapitel");

        var result = BookSync.Apply(
            book, Replace(Replace(snapshot, TextNode(snapshot, "Erster Textblock")), TextNode(snapshot, "Zweiter Textblock")));

        Assert.Equal(
            [Guid.Parse(B1), Guid.Parse(b2)],
            result.Effects.Select(e => Assert.IsType<BlockDeleted>(e).BookBlockId));
        s.Writer.Save(book.Page);
        s.Links.ApplySyncEffects(book, result.Effects);
        Assert.Equal(TestBook.Replace(ErsterTextblock + "\t\t- Zweiter Textblock\n", ""), s.ReadBook());
        Assert.Equal($"- Erste Quelle\n  id:: {N1}\n- Zweite Quelle\n  id:: {N2}\n", s.ReadJournal());

        s.Undo.Undo();
        s.Undo.Undo();

        Assert.Equal(bookText, s.ReadBook());
        Assert.Equal(journalText, s.ReadJournal());
        Assert.False(s.Undo.CanUndo);
    }

    [Fact]
    public void Apply_Manuscript_MissingTextInSubsection_Removed()
    {
        using var s = new LinkSetup(TestBook, TestJournal);
        var snapshot = Manuscript(s, "Kapitel");

        var result = BookSync.Apply(s.Book, Replace(snapshot, TextNode(snapshot, "Text im Unterabschnitt")));

        Assert.Empty(result.Effects);
        Assert.Equal(TestBook.Replace("\t\t\t- Text im Unterabschnitt\n", ""), Serialize(s.Book));
    }

    [Fact]
    public void Apply_MissingUnlinkedBlocksAndParagraphs_RemovedWithoutEffect()
    {
        using var s = new LinkSetup(TestBook, TestJournal);
        var snapshot = Section(s, "Abschnitt");
        var first = TextNode(snapshot, "Erster Textblock");

        var result = BookSync.Apply(
            s.Book,
            Replace(Replace(snapshot, TextNode(snapshot, "Zweiter Textblock")), first, first with { Paragraphs = [first.Paragraphs[1]] }));

        Assert.Empty(result.Effects);
        Assert.Equal(
            TestBook.Replace("\t\t\t- Absatz eins\n", "").Replace("\t\t- Zweiter Textblock\n", ""),
            Serialize(s.Book));
    }

    [Fact]
    public void Apply_ParagraphDepths_BuildChildTree_ClampsJumps()
    {
        using var s = new LinkSetup(TestBook, TestJournal);
        var snapshot = Section(s, "Abschnitt");
        var block = TextNode(snapshot, "Erster Textblock");
        var (eins, zwei) = (block.Paragraphs[0], block.Paragraphs[1]);
        var neuA = new SnapshotParagraph(Guid.NewGuid(), "Neu A", 3, false);
        var neuB = new SnapshotParagraph(Guid.NewGuid(), "Neu B", 1, true);

        BookSync.Apply(
            s.Book,
            Replace(snapshot, block, block with { Paragraphs = [eins with { Depth = 2 }, neuA, zwei with { Depth = 5 }, neuB] }));

        Assert.Equal(
            TestBook.Replace(
                "\t\t\t- Absatz eins\n\t\t\t- Absatz zwei #notiz\n",
                "\t\t\t- Absatz eins\n" +
                "\t\t\t\t- Neu A\n" +
                "\t\t\t\t\t- Absatz zwei #notiz\n" +
                "\t\t\t- Neu B #notiz\n"),
            Serialize(s.Book));
        var paragraphs = Book.Load(s.Book.Page).FindTextBlock(block.Key)!.Paragraphs;
        Assert.Equal([1, 2, 3, 1], paragraphs.Select(p => p.Depth));
        Assert.Equal([eins.Key, neuA.Key, zwei.Key, neuB.Key], paragraphs.Select(p => p.Block.Key));
    }

    [Fact]
    public void Apply_ToggleNote_AddsAndStripsTag()
    {
        using var s = new LinkSetup(Samples.SpecBook);
        var snapshot = Section(s, "Vertrauen");
        var block = Assert.IsType<SnapshotTextBlock>(Assert.Single(snapshot.Nodes));
        var toggled = block with { Paragraphs = [.. block.Paragraphs.Select(p => p with { IsNote = !p.IsNote })] };

        var result = BookSync.Apply(s.Book, Replace(snapshot, block, toggled));

        Assert.True(result.Changed);
        var after = Serialize(s.Book);
        Assert.Equal(
            Samples.SpecBook
                .Replace("ist trainierbar.\n", "ist trainierbar. #notiz\n")
                .Replace("ergänzen #notiz\n", "ergänzen\n"),
            after);
        var (removed, added) = LineDiff.Changed(Encoding.UTF8.GetBytes(Samples.SpecBook), Encoding.UTF8.GetBytes(after));
        Assert.Equal(2, removed.Count);
        Assert.Equal(2, added.Count);
    }

    [Fact]
    public void Apply_SectionView_TextAfterSubsection_StaysWhenUntouched()
    {
        using var s = new LinkSetup(TestBook, TestJournal);
        var snapshot = Section(s, "Abschnitt");
        Assert.Equal(
            ["Erster Textblock", "Zweiter Textblock", "Nachtrag"],
            snapshot.Nodes.Cast<SnapshotTextBlock>().Select(t => t.Text));
        var first = TextNode(snapshot, "Erster Textblock");

        BookSync.Apply(s.Book, Replace(snapshot, first, first with { Text = "Erster Textblock!" }));

        var edited = TestBook.Replace("\t\t- Erster Textblock\n", "\t\t- Erster Textblock!\n");
        Assert.Equal(edited, Serialize(s.Book));

        // The vault's Book view is stale after Apply; the editor works on a rebuilt one.
        var book = Book.Load(s.Book.Page);
        var again = BookSnapshot.Create(book, s.Vault, s.SectionKey("Abschnitt"), includeSubsections: false);
        BookSync.Apply(book, again with { Nodes = [.. again.Nodes, NewText("Schluss")] });

        Assert.Equal(edited.Replace("\t\t- Nachtrag\n", "\t\t- Nachtrag\n\t\t- Schluss\n"), Serialize(s.Book));
    }

    [Fact]
    public void Apply_SectionView_ReorderAndBlockFromOtherSection_MovesOnlyThose()
    {
        using var s = new LinkSetup(TestBook, TestJournal);
        var snapshot = Section(s, "Abschnitt");
        var anderswo = TextNode(Section(s, "Zweiter Abschnitt"), "Anderswo");
        var nodes = new SnapshotNode[]
        {
            TextNode(snapshot, "Zweiter Textblock"), TextNode(snapshot, "Erster Textblock"), TextNode(snapshot, "Nachtrag"), anderswo,
        };

        var result = BookSync.Apply(s.Book, snapshot with { Nodes = nodes });

        Assert.Empty(result.Effects);
        Assert.Equal(
            TestBook
                .Replace(ErsterTextblock + "\t\t- Zweiter Textblock\n", "\t\t- Zweiter Textblock\n" + ErsterTextblock)
                .Replace("\t\t- Nachtrag\n\t- ## Zweiter Abschnitt\n\t\t- Anderswo\n", "\t\t- Nachtrag\n\t\t- Anderswo\n\t- ## Zweiter Abschnitt\n"),
            Serialize(s.Book));
    }

    [Fact]
    public void Apply_Manuscript_BlockMovedUnderOtherHeading_Reindented()
    {
        using var s = new LinkSetup(TestBook, TestJournal);
        var snapshot = Manuscript(s, "Kapitel");
        var moved = TextNode(snapshot, "Erster Textblock");
        var nodes = snapshot.Nodes.Where(n => n != moved).ToList();
        nodes.Insert(nodes.IndexOf(TextNode(snapshot, "Einleitung")) + 1, moved);

        var result = BookSync.Apply(s.Book, snapshot with { Nodes = nodes });

        Assert.Empty(result.Effects);
        Assert.Equal(
            TestBook
                .Replace(ErsterTextblock, "")
                .Replace(
                    "\t- Einleitung\n",
                    "\t- Einleitung\n" +
                    "\t- Erster Textblock\n" +
                    $"\t  id:: {B1}\n" +
                    $"\t  source:: (({N1})), (({N2}))\n" +
                    "\t\t- Absatz eins\n" +
                    "\t\t- Absatz zwei #notiz\n"),
            Serialize(s.Book));
    }

    [Fact]
    public void Apply_Manuscript_HeadingRenamed_LevelKept_MissingHeadingKept()
    {
        using var s = new LinkSetup(TestBook, TestJournal);
        var snapshot = Manuscript(s, "Kapitel");
        var section = snapshot.Nodes.OfType<SnapshotHeading>().Single(h => h.Text == "Abschnitt");
        var sub = snapshot.Nodes.OfType<SnapshotHeading>().Single(h => h.Text == "Unterabschnitt");
        var nodes = snapshot.Nodes
            .Select(n => n == section ? section with { Level = 3, Text = "Neuer Name" } : n)
            .Where(n => n != sub)
            .ToList();
        nodes.Add(new SnapshotHeading(Guid.NewGuid(), 1, "Unbekannt"));
        nodes.Add(NewText("Nach unbekannter Überschrift"));

        var result = BookSync.Apply(s.Book, snapshot with { Nodes = nodes });

        Assert.True(result.Changed);
        Assert.Equal(
            TestBook
                .Replace("\t- ## Abschnitt\n", "\t- ## Neuer Name\n")
                .Replace("\t\t- Anderswo\n", "\t\t- Anderswo\n\t\t- Nach unbekannter Überschrift\n"),
            Serialize(s.Book));
    }

    [Fact]
    public void Apply_Manuscript_NewBlockDirectlyAfterHeading_GoesBeforeSubheading()
    {
        const string text = "type:: book\n\n- # K\n\t- ## U\n\t\t- x\n\t- danach\n";
        using var s = new LinkSetup(text);
        var snapshot = Manuscript(s, "K");
        var nodes = snapshot.Nodes.ToList();
        nodes.Insert(1, NewText("Neu"));

        BookSync.Apply(s.Book, snapshot with { Nodes = nodes });

        Assert.Equal("type:: book\n\n- # K\n\t- Neu\n\t- ## U\n\t\t- x\n\t- danach\n", Serialize(s.Book));
    }

    [Fact]
    public void Apply_TextWithMarkdownSyntax_IsEscaped_CreateReturnsItUnchanged()
    {
        using var s = new LinkSetup(TestBook, TestJournal);
        var snapshot = Section(s, "Abschnitt");
        var block = TextNode(snapshot, "Zweiter Textblock");
        const string typed = "# Kein Titel\n- keine Liste\n```";

        BookSync.Apply(s.Book, Replace(snapshot, block, block with { Text = typed }));

        Assert.Equal(
            TestBook.Replace("\t\t- Zweiter Textblock\n", "\t\t- \\# Kein Titel\n\t\t  \\- keine Liste\n\t\t  \\```\n"),
            Serialize(s.Book));
        var reloaded = Book.Load(LogseqParser.Parse(s.BookPath, PageSerializer.Serialize(s.Book.Page)));
        Assert.Empty(reloaded.Warnings);
        var again = BookSnapshot.Create(reloaded, s.Vault, reloaded.Root.Children.Single().Children.First().Key, false);
        Assert.Equal(typed, again.Nodes.Cast<SnapshotTextBlock>().ElementAt(1).Text);
    }

    [Fact]
    public void Apply_TypedPropertyLines_AreMasked_SameSnapshotAgain_NotChanged()
    {
        using var s = new LinkSetup(TestBook, TestJournal);
        var snapshot = Section(s, "Abschnitt");
        var first = TextNode(snapshot, "Erster Textblock");
        var edited = Replace(
            snapshot,
            first,
            first with { Text = "Erster Textblock\nfazit:: gut" },
            new SnapshotTextBlock(Guid.NewGuid(), null, "Neu\nsource:: ((" + N1 + "))\nid::", [], []));

        Assert.True(BookSync.Apply(s.Book, edited).Changed);
        var once = Serialize(s.Book);

        var again = BookSync.Apply(s.Book, edited);

        Assert.False(again.Changed);
        Assert.Equal(once, Serialize(s.Book));
        Assert.Equal(
            TestBook.Replace(
                ErsterTextblock,
                ErsterTextblock.Replace("\t\t\t- Absatz eins\n", "\t\t  fazit:\\: gut\n\t\t\t- Absatz eins\n") +
                $"\t\t- Neu\n\t\t  source:\\: (({N1}))\n\t\t  id:\\:\n"),
            once);
        var reloaded = Book.Load(LogseqParser.Parse(s.BookPath, PageSerializer.Serialize(s.Book.Page)));
        var texts = reloaded.Root.Children.Single().Children.First().TextBlocks.ToList();
        Assert.Equal([Guid.Parse(N1), Guid.Parse(N2)], texts[0].Sources);
        Assert.DoesNotContain(texts[0].Block.Properties, p => p.Key == "fazit");
        Assert.Empty(texts[1].Block.Properties);
        Assert.Null(texts[1].Block.Id);
    }

    [Fact]
    public void Apply_CarriageReturnsInTexts_AreLineBreaks_AppliedCompletely()
    {
        using var s = new LinkSetup(TestBook, TestJournal);
        var snapshot = Section(s, "Abschnitt");
        var first = TextNode(snapshot, "Erster Textblock");
        var second = TextNode(snapshot, "Zweiter Textblock");
        var edited = Replace(
            Replace(snapshot, first, first with { Text = "Erster!" }),
            second,
            second with { Text = "a\r\n-\r\nb\rc" });

        var result = BookSync.Apply(s.Book, edited);

        Assert.True(result.Changed);
        Assert.Equal(
            TestBook
                .Replace("\t\t- Erster Textblock\n", "\t\t- Erster!\n")
                .Replace("\t\t- Zweiter Textblock\n", "\t\t- a\n\t\t  \\-\n\t\t  b\n\t\t  c\n"),
            Serialize(s.Book));
        var again = BookSnapshot.Create(Book.Load(s.Book.Page), s.Vault, s.SectionKey("Abschnitt"), includeSubsections: false);
        Assert.Equal("a\n-\nb\nc", again.Nodes.Cast<SnapshotTextBlock>().ElementAt(1).Text);
    }

    [Fact]
    public void Apply_DemotedLinkedBlockDeletedAsParagraph_NoteLosesUsage_UndoRestores()
    {
        const string b2 = "0199a1c2-1b7e-7c1d-9a0f-2b3c4d5e6f72";
        var bookText = TestBook.Replace("\t\t- Zweiter Textblock\n", $"\t\t- Zweiter Textblock\n\t\t  id:: {b2}\n\t\t  source:: (({N2}))\n");
        var journalText = TestJournal.Replace(
            $"  id:: {N2}\n  used-in:: [[Buch - Test]] (({B1}))\n",
            $"  id:: {N2}\n  used-in:: [[Buch - Test]] (({B1})), [[Buch - Test]] (({b2}))\n");
        using var s = new LinkSetup(bookText, journalText);

        // Step 1: the linked text block becomes a paragraph of the first one; it keeps key, id and source.
        var snapshot = Section(s, "Abschnitt");
        var first = TextNode(snapshot, "Erster Textblock");
        var second = TextNode(snapshot, "Zweiter Textblock");
        var demoted = Replace(
            Replace(snapshot, second),
            first,
            first with { Paragraphs = [.. first.Paragraphs, new SnapshotParagraph(second.Key, "Zweiter Textblock", 1, false)] });
        Assert.Empty(BookSync.Apply(s.Book, demoted).Effects);
        s.Writer.Save(s.Book.Page);
        var afterDemotion = s.ReadBook();
        Assert.Contains($"\t\t\t- Absatz zwei #notiz\n\t\t\t- Zweiter Textblock\n\t\t\t  id:: {b2}\n\t\t\t  source:: (({N2}))\n", afterDemotion);

        // Step 2: the paragraph is deleted.
        var book = Book.Load(s.Book.Page);
        var current = BookSnapshot.Create(book, s.Vault, s.SectionKey("Abschnitt"), includeSubsections: false);
        var block = TextNode(current, "Erster Textblock");
        var result = BookSync.Apply(book, Replace(current, block, block with { Paragraphs = [.. block.Paragraphs.SkipLast(1)] }));

        var deleted = Assert.IsType<BlockDeleted>(Assert.Single(result.Effects));
        Assert.Equal((Guid.Parse(b2), Guid.Parse(N2)), (deleted.BookBlockId, Assert.Single(deleted.Sources)));
        s.Writer.Save(book.Page);
        s.Links.ApplySyncEffects(book, result.Effects);
        Assert.Equal(TestJournal, s.ReadJournal());

        s.Undo.Undo();

        Assert.Equal(afterDemotion, s.ReadBook());
        Assert.Equal(journalText, s.ReadJournal());
    }

    [Fact]
    public void Apply_LinkedBlockWithLinkedParagraphDeleted_EffectsForBoth_OneUndoRestoresAll()
    {
        const string b2 = "0199a1c2-1b7e-7c1d-9a0f-2b3c4d5e6f72";
        var bookText = TestBook.Replace("\t\t\t- Absatz eins\n", $"\t\t\t- Absatz eins\n\t\t\t  id:: {b2}\n\t\t\t  source:: (({N2}))\n");
        var journalText = TestJournal.Replace(
            $"  id:: {N2}\n  used-in:: [[Buch - Test]] (({B1}))\n",
            $"  id:: {N2}\n  used-in:: [[Buch - Test]] (({B1})), [[Buch - Test]] (({b2}))\n");
        using var s = new LinkSetup(bookText, journalText);
        var book = s.Book;
        var snapshot = Section(s, "Abschnitt");

        var result = BookSync.Apply(book, Replace(snapshot, TextNode(snapshot, "Erster Textblock")));

        Assert.Equal(
            [(Guid.Parse(B1), 2), (Guid.Parse(b2), 1)],
            result.Effects.Select(e => Assert.IsType<BlockDeleted>(e)).Select(d => (d.BookBlockId, d.Sources.Count)));
        s.Writer.Save(book.Page);
        s.Links.ApplySyncEffects(book, result.Effects);
        Assert.Equal($"- Erste Quelle\n  id:: {N1}\n- Zweite Quelle\n  id:: {N2}\n", s.ReadJournal());

        s.Undo.Undo();

        Assert.False(s.Undo.CanUndo);
        Assert.Equal(bookText, s.ReadBook());
        Assert.Equal(journalText, s.ReadJournal());
        Assert.Empty(s.Pending.Load());
    }

    [Fact]
    public void Apply_DuplicateKeys_Throws_NothingChanged()
    {
        using var s = new LinkSetup(TestBook, TestJournal);
        var snapshot = Section(s, "Abschnitt");
        var first = TextNode(snapshot, "Erster Textblock");

        Assert.Throws<ArgumentException>(() =>
            BookSync.Apply(s.Book, snapshot with { Nodes = [first with { Text = "geändert" }, first] }));

        Assert.Equal(TestBook, Serialize(s.Book));
        Assert.False(s.Book.Page.IsDirty);
    }

    [Fact]
    public void Create_ThenApply_RoundTripsSpecExample()
    {
        const string journal = $"- Angst und Vertrauen: eine Beobachtung vom Wochenende\n  id:: {N1}\n";
        using var s = new LinkSetup(Samples.SpecBook, journal);
        var book = s.Book;

        var manuscript = BookSnapshot.Create(book, s.Vault, Guid.Empty, includeSubsections: true);

        Assert.Equal(Guid.Empty, manuscript.ScopeKey);
        Assert.True(manuscript.IncludeSubsections);
        Assert.Equal(3, manuscript.Nodes.Count);
        var part = Assert.IsType<SnapshotHeading>(manuscript.Nodes[0]);
        Assert.Equal((s.SectionKey("Liebe und Wahrheit"), 1, "Liebe und Wahrheit"), (part.Key, part.Level, part.Text));
        var chapter = Assert.IsType<SnapshotHeading>(manuscript.Nodes[1]);
        Assert.Equal((s.SectionKey("Vertrauen"), 2, "Vertrauen"), (chapter.Key, chapter.Level, chapter.Text));
        var text = Assert.IsType<SnapshotTextBlock>(manuscript.Nodes[2]);
        var textBlock = book.FindTextBlockById(Guid.Parse(B1))!;
        Assert.Equal(textBlock.Key, text.Key);
        Assert.Null(text.SplitFrom);
        Assert.Equal("Angst baut Widerstand auf, Vertrauen baut Schwung auf.", text.Text);
        Assert.Equal(
            [
                (textBlock.Paragraphs[0].Block.Key, "Gute Interpretationsvarianten zu sehen ist trainierbar.", 1, false),
                (textBlock.Paragraphs[1].Block.Key, "noch ein Beispiel ergänzen", 1, true),
            ],
            text.Paragraphs.Select(p => (p.Key, p.Text, p.Depth, p.IsNote)));
        Assert.Equal(
            [
                new SourceInfo(Guid.Parse(N1), "2026-03-01 – Angst und Vertrauen: eine Beobachtung vo", false),
                new SourceInfo(Guid.Parse(N2), N2, true),
            ],
            text.Sources);

        var section = BookSnapshot.Create(book, s.Vault, s.SectionKey("Vertrauen"), includeSubsections: false);
        Assert.Equal(text.Key, Assert.IsType<SnapshotTextBlock>(Assert.Single(section.Nodes)).Key);
        var fromPart = BookSnapshot.Create(book, s.Vault, s.SectionKey("Liebe und Wahrheit"), includeSubsections: true);
        Assert.Equal(part.Key, fromPart.Nodes[0].Key);
        Assert.Empty(BookSnapshot.Create(book, s.Vault, s.SectionKey("Liebe und Wahrheit"), includeSubsections: false).Nodes);

        foreach (var snapshot in new[] { manuscript, section, fromPart })
        {
            var result = BookSync.Apply(book, snapshot);
            Assert.False(result.Changed);
            Assert.Equal(Samples.SpecBook, Serialize(book));
        }
    }

    private static string Serialize(Book book) => Encoding.UTF8.GetString(PageSerializer.Serialize(book.Page));

    private static SectionSnapshot Section(LinkSetup s, string title) =>
        BookSnapshot.Create(s.Book, s.Vault, s.SectionKey(title), includeSubsections: false);

    private static SectionSnapshot Manuscript(LinkSetup s, string title) =>
        BookSnapshot.Create(s.Book, s.Vault, s.SectionKey(title), includeSubsections: true);

    private static SnapshotTextBlock TextNode(SectionSnapshot snapshot, string text) =>
        snapshot.Nodes.OfType<SnapshotTextBlock>().Single(t => t.Text == text);

    private static SnapshotTextBlock NewText(string text) => new(Guid.NewGuid(), null, text, [], []);

    /// <summary>The snapshot with <paramref name="old"/> replaced by <paramref name="replacements"/> (none = removed).</summary>
    private static SectionSnapshot Replace(SectionSnapshot snapshot, SnapshotNode old, params SnapshotNode[] replacements) =>
        snapshot with { Nodes = [.. snapshot.Nodes.SelectMany(n => n == old ? replacements : [n])] };
}
