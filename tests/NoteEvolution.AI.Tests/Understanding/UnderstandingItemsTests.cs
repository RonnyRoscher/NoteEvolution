using NoteEvolution.AI.Understanding;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Vaults;
using NoteEvolution.TestSupport;

namespace NoteEvolution.AI.Tests.Understanding;

public class UnderstandingItemsTests
{
    private const string Model = "qwen2.5:7b";

    private static string BookText(string a1 = "Absatz eins.", string extra = "") =>
        "title:: Buch\ntype:: book\n\n" +
        "- # Kapitel A\n\t- " + a1 + "\n" + extra +
        "\t- ## Unter A\n\t\t- Text unter A.\n" +
        "- # Kapitel B\n\t- Text in B.\n";

    private static IReadOnlyList<UnderstandingItem> Items(string text, string model = Model)
    {
        using var tv = TestVault.Create(("pages/Buch.md", text));
        return UnderstandingItems.ForBook(tv.Open().Books.Single(), model);
    }

    private static UnderstandingItem Item(IReadOnlyList<UnderstandingItem> items, string title) =>
        items.Single(i => i.Title == title);

    [Fact]
    public void ForBook_ChildrenBeforeParents()
    {
        var items = Items(BookText());

        Assert.Equal(["Unter A", "Kapitel A", "Kapitel B"], items.Select(i => i.Title));
        Assert.Equal([Item(items, "Unter A").Fingerprint], Item(items, "Kapitel A").ChildFingerprints);
    }

    [Fact]
    public void Fingerprint_ParagraphChange_ChangesItsHeadingAndAllAbove_NotSiblings()
    {
        var before = Items(BookText());
        var after = Items(BookText().Replace("Text unter A.", "Text unter A, geändert."));

        Assert.NotEqual(Item(before, "Unter A").Fingerprint, Item(after, "Unter A").Fingerprint);
        Assert.NotEqual(Item(before, "Kapitel A").Fingerprint, Item(after, "Kapitel A").Fingerprint);
        Assert.Equal(Item(before, "Kapitel B").Fingerprint, Item(after, "Kapitel B").Fingerprint);
    }

    [Fact]
    public void Fingerprint_ModelOrPromptVersionChange_ChangesAll()
    {
        var a = Items(BookText(), "qwen2.5:7b");
        var b = Items(BookText(), "llama3:8b");

        Assert.All(a.Zip(b), pair => Assert.NotEqual(pair.First.Fingerprint, pair.Second.Fingerprint));
        Assert.Equal(1, UnderstandingItems.PromptVersion);
    }

    [Fact]
    public void RemarkBlocks_Excluded_RelevantIncluded()
    {
        var items = Items(BookText(extra: "\t- Eine Bemerkung #notiz\n\t- Relevanter Text\n\t  status:: relevant\n"));

        var own = Item(items, "Kapitel A").OwnText;
        Assert.DoesNotContain("Bemerkung", own);
        Assert.Contains("Relevanter Text", own);
        Assert.Contains("Absatz eins.", own);
    }

    [Fact]
    public void OwnText_Over6000_ShortenedEvenly_KeepsEachBlockStart()
    {
        var blocks = string.Concat(new[] { 'X', 'Y', 'Z' }.Select(c => "\t- " + c + new string('a', 3499) + "\n"));
        var items = Items("title:: Buch\ntype:: book\n\n- # Kapitel\n" + blocks);

        var own = Item(items, "Kapitel").OwnText;
        Assert.True(own.Length <= UnderstandingItems.HeadingInputLimit);
        var parts = own.Split('\n');
        Assert.Equal(3, parts.Length);
        Assert.Equal(['X', 'Y', 'Z'], parts.Select(p => p[0]));
        Assert.True(parts.All(p => p.Length > 1900));
    }

    [Fact]
    public void Identity_SameTitleInTwoChapters_Differs()
    {
        var items = Items("title:: Buch\ntype:: book\n\n- # Eins\n\t- ## Einleitung\n\t- ## Einleitung\n- # Zwei\n\t- ## Einleitung\n");

        var identities = items.Where(i => i.Title == "Einleitung").Select(i => i.Identity).ToList();
        Assert.Equal(3, identities.Distinct().Count());
        Assert.Contains("book:Buch/Eins › Einleitung#0", identities);
        Assert.Contains("book:Buch/Eins › Einleitung#1", identities);
        Assert.Contains("book:Buch/Zwei › Einleitung#0", identities);
    }

    private static NoteBlock Note(TestVault tv, string firstLine) =>
        new NoteRepository(tv.Open()).All().First(n => n.Block.Content.StartsWith(firstLine));

    [Fact]
    public void ForNote_Under120Chars_Null()
    {
        using var tv = TestVault.Create(("pages/Idee.md", "- Kurze Idee\n"));

        Assert.Null(UnderstandingItems.ForNote(Note(tv, "Kurze"), Model));
    }

    [Fact]
    public void ForNote_Over4000_Trimmed()
    {
        using var tv = TestVault.Create(("pages/Idee.md", "- Lange Idee " + new string('b', 5000) + "\n"));

        var item = UnderstandingItems.ForNote(Note(tv, "Lange"), Model);

        Assert.NotNull(item);
        Assert.Equal(UnderstandingKind.Note, item.Kind);
        Assert.Equal(UnderstandingItems.NoteInputLimit, item.OwnText.Length);
        Assert.StartsWith("note:pages/Idee.md#Lange Idee", item.Identity);
    }

    [Fact]
    public void NoteFingerprint_SameAsForNote()
    {
        using var tv = TestVault.Create(("pages/Idee.md", "- Idee " + new string('c', 300) + "\n\t- Detail\n"));
        var note = Note(tv, "Idee");

        var item = UnderstandingItems.ForNote(note, Model)!;

        Assert.Equal(item.Fingerprint, UnderstandingItems.NoteFingerprint(UnderstandingItems.NoteText(note), Model));
        Assert.NotEqual(item.Fingerprint, UnderstandingItems.NoteFingerprint(UnderstandingItems.NoteText(note), "x"));
    }

    private static PlacementCheckPrompt Prompt(string system = "S", string user = "U", string schema = "{}") =>
        new(system, user, [], schema);

    [Fact]
    public void ForPlacement_FingerprintFollowsPrompt()
    {
        var key = Guid.NewGuid();
        var prompt = Prompt();

        var item = UnderstandingItems.ForPlacement(key, "note:pages/Idee.md#Idee", "Buch", prompt, Model);

        Assert.Equal(UnderstandingItems.ForPlacement(key, "note:pages/Idee.md#Idee", "Buch", Prompt(), Model).Fingerprint, item.Fingerprint);
        Assert.NotEqual(item.Fingerprint, UnderstandingItems.ForPlacement(key, "note:pages/Idee.md#Idee", "Buch", Prompt(user: "U2"), Model).Fingerprint);
        Assert.NotEqual(item.Fingerprint, UnderstandingItems.ForPlacement(key, "note:pages/Idee.md#Idee", "Buch", Prompt(system: "S2"), Model).Fingerprint);
        Assert.NotEqual(item.Fingerprint, UnderstandingItems.ForPlacement(key, "note:pages/Idee.md#Idee", "Buch", Prompt(schema: "[]"), Model).Fingerprint);
        Assert.NotEqual(item.Fingerprint, UnderstandingItems.ForPlacement(key, "note:pages/Idee.md#Idee", "Buch", prompt, "other").Fingerprint);
        Assert.Equal("placement|Buch|note:pages/Idee.md#Idee", item.Identity);
        Assert.Equal(UnderstandingKind.Placement, item.Kind);
        Assert.Equal(key, item.Key);
        Assert.Same(prompt, item.Check);
    }

    [Fact]
    public void NoteIdentity_SameAsForNote()
    {
        using var tv = TestVault.Create(("pages/Idee.md", "- Idee " + new string('c', 300) + "\n"));
        var note = Note(tv, "Idee");

        var item = UnderstandingItems.ForNote(note, Model)!;

        Assert.Equal(item.Identity, UnderstandingItems.NoteIdentity(note.Block.Content, note.Page.FilePath));
        Assert.False(string.IsNullOrEmpty(UnderstandingItems.NoteIdentity("Kurz", note.Page.FilePath)));
    }

    [Fact]
    public void ForNote_PagePathWithHash_TitleIsFirstLine()
    {
        using var tv = TestVault.Create(("pages/C#.md", "- Idee " + new string('d', 300) + "\n"));
        var note = Note(tv, "Idee");

        var item = UnderstandingItems.ForNote(note, Model)!;

        Assert.Equal("Idee " + new string('d', 75), item.Title);
        Assert.StartsWith("note:pages/C#.md#Idee ", item.Identity);
    }
}
