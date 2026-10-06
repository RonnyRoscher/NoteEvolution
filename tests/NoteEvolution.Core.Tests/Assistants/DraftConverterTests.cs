using NoteEvolution.Core.Assistants;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Storage;
using NoteEvolution.TestSupport;

namespace NoteEvolution.Core.Tests.Assistants;

public class DraftConverterTests
{
    private const string DraftFile = "pages/Entwurf.md";

    private sealed class Setup : IDisposable
    {
        private static readonly DateTimeOffset Now = new(2026, 3, 15, 14, 30, 5, TimeSpan.Zero);

        public Setup(string draft)
        {
            Tv = TestVault.Create((DraftFile, draft));
            var vault = Tv.Open();
            Page = vault.Pages.Single();
            var backups = new BackupService(vault.Root, new FakeClock(Now));
            var writer = new PageWriter(vault, backups, new SelfWriteRegistry()) { Sleep = _ => { } };
            Converter = new DraftConverter(backups, writer);
        }

        public TestVault Tv { get; }

        public Page Page { get; }

        public DraftConverter Converter { get; }

        public string ReadDraft() => Tv.Read(DraftFile);

        public string BackupsDir => Path.Combine(Tv.Root, ".noteevolution", "backups");

        public void Dispose() => Tv.Dispose();
    }

    private static DraftNode Find(DraftProposal proposal, string content) =>
        proposal.AllNodes().Single(n => n.Block.Content == content);

    [Fact]
    public void Propose_HeuristicPicksShortParentsWithoutSentenceEnd()
    {
        var exactly80 = new string('a', 80);
        var tooLong = new string('b', 81);
        using var s = new Setup(
            "- Kapitel\n" +
            "  - Kind\n" +
            "- Satz mit Punkt.\n" +
            "  - Kind\n" +
            "- Ausruf!\n" +
            "  - Kind\n" +
            "- Frage?\n" +
            "  - Kind\n" +
            "- Ellipse…\n" +
            "  - Kind\n" +
            "- Blatt ohne Kinder\n" +
            $"- {exactly80}\n" +
            "  - Kind\n" +
            $"- {tooLong}\n" +
            "  - Kind\n" +
            "- Mehrzeilig\n" +
            "  zweite Zeile\n" +
            "  - Kind\n" +
            "- Punkt und Leerzeichen. \n" +
            "  - Kind\n");

        var proposal = s.Converter.Propose(s.Page);

        var headings = proposal.AllNodes().Where(n => n.IsHeading).Select(n => n.Block.Content);
        Assert.Equal(["Kapitel", exactly80], headings);
        Assert.All(proposal.AllNodes(), n => Assert.False(n.IsFixed));
    }

    [Fact]
    public void Propose_MirrorsBlockTree()
    {
        using var s = new Setup("- A\n  - B\n    - C\n  - D\n- E\n");

        var proposal = s.Converter.Propose(s.Page);

        Assert.Equal(["A", "E"], proposal.Roots.Select(n => n.Block.Content));
        var a = proposal.Roots[0];
        Assert.Equal(["B", "D"], a.Children.Select(n => n.Block.Content));
        Assert.Equal("C", a.Children[0].Children.Single().Block.Content);
        Assert.Same(a, a.Children[0].Parent);
        Assert.Null(a.Parent);
        Assert.Equal(["A", "B", "C", "D", "E"], proposal.AllNodes().Select(n => n.Block.Content));
    }

    [Fact]
    public void Propose_LevelsFollowHeadingAncestors()
    {
        using var s = new Setup(
            "- Teil\n" +
            "  - Kapitel\n" +
            "    - Abschnitt\n" +
            "      - Ein Satz.\n" +
            "  - Text mit Satz.\n" +
            "    - Kind\n");

        var proposal = s.Converter.Propose(s.Page);

        Assert.Equal(1, proposal.LevelOf(Find(proposal, "Teil")));
        Assert.Equal(2, proposal.LevelOf(Find(proposal, "Kapitel")));
        Assert.Equal(3, proposal.LevelOf(Find(proposal, "Abschnitt")));
        Assert.Equal(4, proposal.LevelOf(Find(proposal, "Ein Satz.")));
        Assert.Equal(2, proposal.LevelOf(Find(proposal, "Text mit Satz.")));
        Assert.Equal(2, proposal.LevelOf(Find(proposal, "Kind")));
    }

    [Fact]
    public void Propose_ExistingHeadings_AreFixedAndCountAsAncestors()
    {
        using var s = new Setup(
            "- ## Fest\n" +
            "  - Unter fest\n" +
            "    - Kind\n" +
            "- #Tag ist keine Überschrift\n" +
            "  - Anderes Kind\n");

        var proposal = s.Converter.Propose(s.Page);

        var fixedNode = Find(proposal, "## Fest");
        Assert.True(fixedNode.IsFixed);
        Assert.True(fixedNode.IsHeading);
        Assert.False(Find(proposal, "#Tag ist keine Überschrift").IsFixed);
        Assert.True(Find(proposal, "Unter fest").IsHeading);
        Assert.Equal(2, proposal.LevelOf(Find(proposal, "Unter fest")));
        Assert.Equal(3, proposal.LevelOf(Find(proposal, "Kind")));
    }

    [Fact]
    public void FixedNode_CannotBeToggledOff()
    {
        using var s = new Setup("- # Fest\n  - Kind\n");
        var node = s.Converter.Propose(s.Page).Roots.Single();

        Assert.Throws<InvalidOperationException>(() => node.IsHeading = false);
        node.IsHeading = true;

        Assert.True(node.IsHeading);
    }

    [Fact]
    public void Toggle_ChangesLevelsBelow()
    {
        using var s = new Setup(
            "- Teil\n" +
            "  - Kapitel\n" +
            "    - Kind\n");
        var proposal = s.Converter.Propose(s.Page);
        var kapitel = Find(proposal, "Kapitel");
        var kind = Find(proposal, "Kind");
        Assert.Equal(3, proposal.LevelOf(kind));

        proposal.Roots.Single().IsHeading = false;
        Assert.Equal(1, proposal.LevelOf(kapitel));
        Assert.Equal(2, proposal.LevelOf(kind));

        kind.IsHeading = true;
        proposal.Roots.Single().IsHeading = true;
        Assert.Equal(2, proposal.LevelOf(kapitel));
        Assert.Equal(3, proposal.LevelOf(kind));
    }

    [Fact]
    public void Apply_PrefixesHeadingsByLevel_AddsTypeBook_KeepsEverythingElse()
    {
        using var s = new Setup(
            "title:: Mein Buch\n" +
            "\n" +
            "- Teil\n" +
            "  - Kapitel\n" +
            "    - Ein Satz.\n" +
            "  - ## Fest\n" +
            "    - Unter fest\n" +
            "      - Text\n");

        var proposal = s.Converter.Propose(s.Page);
        s.Converter.Apply(s.Page, proposal);

        Assert.Equal(
            "title:: Mein Buch\n" +
            "type:: book\n" +
            "\n" +
            "- # Teil\n" +
            "  - ## Kapitel\n" +
            "    - Ein Satz.\n" +
            "  - ## Fest\n" +
            "    - ### Unter fest\n" +
            "      - Text\n",
            s.ReadDraft());
    }

    [Fact]
    public void Apply_HonorsUserToggles()
    {
        using var s = new Setup("- Teil\n  - Kapitel\n    - Kind\n");
        var proposal = s.Converter.Propose(s.Page);
        proposal.Roots.Single().IsHeading = false;
        Find(proposal, "Kind").IsHeading = true;

        s.Converter.Apply(s.Page, proposal);

        Assert.Equal("type:: book\n\n- Teil\n  - # Kapitel\n    - ## Kind\n", s.ReadDraft());
    }

    [Fact]
    public void Apply_CreatesBackupFirst()
    {
        const string original = "- Teil\n  - Kind\n";
        using var s = new Setup(original);

        s.Converter.Apply(s.Page, s.Converter.Propose(s.Page));

        var backups = Directory.GetFiles(s.BackupsDir, "*", SearchOption.AllDirectories);
        var forced = Assert.Single(backups, f => f.EndsWith(".143005.bak", StringComparison.Ordinal));
        Assert.Equal(original, File.ReadAllText(forced));
        Assert.NotEqual(original, s.ReadDraft());
    }

    [Fact]
    public void Apply_KeepsCollapsedAndOtherLines()
    {
        using var s = new Setup(
            "- Teil\n" +
            "  collapsed:: true\n" +
            "  - Kapitel\n" +
            "    id:: 6502b3a0-0000-7000-8000-000000000001\n" +
            "    - Text mit  doppelten Leerzeichen\n" +
            "      zweite Zeile\n" +
            "\n" +
            "  - Zweites Kind\n");
        var before = s.ReadDraft();

        s.Converter.Apply(s.Page, s.Converter.Propose(s.Page));

        Assert.Equal(
            "type:: book\n\n" +
            before.Replace("- Teil", "- # Teil").Replace("- Kapitel", "- ## Kapitel"),
            s.ReadDraft());
    }

    [Fact]
    public void Apply_MultiLineHeading_PrefixesOnlyFirstLine()
    {
        using var s = new Setup("- Teil\n  Fortsetzung\n  - Kind\n");
        var proposal = s.Converter.Propose(s.Page);
        var node = proposal.Roots.Single();
        Assert.False(node.IsHeading);
        node.IsHeading = true;

        s.Converter.Apply(s.Page, proposal);

        Assert.Equal("type:: book\n\n- # Teil\n  Fortsetzung\n  - Kind\n", s.ReadDraft());
    }

    [Fact]
    public void Apply_PreservesCrLf()
    {
        using var s = new Setup("- Teil\r\n  - Kind\r\n");

        s.Converter.Apply(s.Page, s.Converter.Propose(s.Page));

        Assert.Equal("type:: book\r\n\r\n- # Teil\r\n  - Kind\r\n", s.ReadDraft());
    }

    [Fact]
    public void Apply_AddsTypeBook()
    {
        using var s = new Setup("title:: Entwurf\ntags:: x\n\n- Teil\n  - Kind\n");

        s.Converter.Apply(s.Page, s.Converter.Propose(s.Page));

        Assert.Equal("book", s.Page.GetPageProperty("type"));
        Assert.Equal("title:: Entwurf\ntags:: x\ntype:: book\n\n- # Teil\n  - Kind\n", s.ReadDraft());
    }

    [Fact]
    public void Apply_OtherTypeValue_IsOverwritten()
    {
        using var s = new Setup("type:: note\n\n- Teil\n  - Kind\n");

        s.Converter.Apply(s.Page, s.Converter.Propose(s.Page));

        Assert.Equal("type:: book\n\n- # Teil\n  - Kind\n", s.ReadDraft());
    }

    [Fact]
    public void Apply_TypeBookInOtherCase_IsKept()
    {
        using var s = new Setup("type:: Book\n\n- Teil\n  - Kind\n");

        s.Converter.Apply(s.Page, s.Converter.Propose(s.Page));

        Assert.Equal("type:: Book\n\n- # Teil\n  - Kind\n", s.ReadDraft());
    }

    [Fact]
    public void Apply_NothingToChange_DoesNotBackupOrWrite()
    {
        const string text = "type:: book\n\n- # Teil\n  - Kind\n";
        using var s = new Setup(text);

        s.Converter.Apply(s.Page, s.Converter.Propose(s.Page));

        Assert.Equal(text, s.ReadDraft());
        Assert.False(Directory.Exists(s.BackupsDir));
    }

    [Fact]
    public void Apply_ReadOnlyPage_ThrowsBeforeBackupAndWritesNothing()
    {
        const string broken = "- Teil\n  - Kind\n  ```\n  nie geschlossen\n";
        using var s = new Setup(broken);
        Assert.True(s.Page.IsReadOnly);
        var proposal = s.Converter.Propose(s.Page);

        Assert.IsAssignableFrom<InvalidOperationException>(
            Assert.Throws<ReadOnlyPageException>(() => s.Converter.Apply(s.Page, proposal)));

        Assert.Equal(broken, s.ReadDraft());
        Assert.False(Directory.Exists(s.BackupsDir));
    }

    [Fact]
    public void Apply_LevelAboveSix_ThrowsBeforeAnyChange()
    {
        using var s = new Setup(
            "- A\n  - B\n    - C\n      - D\n        - E\n          - F\n            - G\n              - H\n");
        var proposal = s.Converter.Propose(s.Page);
        Assert.True(Find(proposal, "G").IsHeading);
        Assert.Equal(7, proposal.LevelOf(Find(proposal, "G")));
        var before = s.ReadDraft();

        Assert.Throws<ArgumentException>(() => s.Converter.Apply(s.Page, proposal));

        Assert.Equal(before, s.ReadDraft());
        Assert.False(Directory.Exists(s.BackupsDir));
        Assert.False(s.Page.IsDirty);
    }

    [Fact]
    public void Apply_ProposalOfOtherPage_Throws()
    {
        using var s = new Setup("- Teil\n  - Kind\n");
        using var other = new Setup("- Teil\n  - Kind\n");
        var foreign = other.Converter.Propose(other.Page);

        Assert.Throws<ArgumentException>(() => s.Converter.Apply(s.Page, foreign));

        Assert.False(s.Page.IsDirty);
    }
}
