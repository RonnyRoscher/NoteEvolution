using Bunit;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Vaults;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>The hint at a note card that the open note says nearly the same as a note the book already uses.</summary>
public class DuplicateHintTests : UiTestContext
{
    private const string AlphaPath = "pages/Buch - Alpha.md";
    private const string TextId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string BetaTextId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    private const string UsedId = "11111111-1111-4111-8111-111111111111";
    private const string PartId = "22222222-2222-4222-8222-222222222222";
    private const string BetaUsedId = "33333333-3333-4333-8333-333333333333";

    private readonly TestVault _vault = TestVault.Create(
        (AlphaPath,
            "title:: Alpha\ntype:: book\n\n" +
            "- # Eins\n" +
            "\t- Erster Text\n" +
            "\t  id:: " + TextId + "\n" +
            "\t  source:: ((" + UsedId + ")) ((" + PartId + "))\n" +
            "- # Zwei\n" +
            "\t- Zweiter Text\n"),
        ("pages/Buch - Beta.md",
            "title:: Beta\ntype:: book\n\n" +
            "- # Beta Eins\n" +
            "\t- Beta Text\n" +
            "\t  id:: " + BetaTextId + "\n" +
            "\t  source:: ((" + BetaUsedId + "))\n"),
        ("pages/Ideen.md",
            "- Schon Quelle\n" +
            "  id:: " + UsedId + "\n" +
            "  used-in:: [[Buch - Alpha]] ((" + TextId + "))\n" +
            "- Fast gleich\n" +
            "- Knapp darunter\n" +
            "- Ganz anders\n" +
            "- Wie in Beta\n" +
            "- Nur in Beta Quelle\n" +
            "  id:: " + BetaUsedId + "\n" +
            "  used-in:: [[Buch - Beta]] ((" + BetaTextId + "))\n" +
            "- Teilweise verwendet\n" +
            "  used-in:: \n" +
            "\t- Verwendeter Unterpunkt\n" +
            "\t  id:: " + PartId + "\n" +
            "\t  used-in:: [[Buch - Alpha]] ((" + TextId + "))\n"));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _vault.Dispose();
        }
    }

    private static NoteBlock Note(VaultSession session, string content) =>
        session.Notes.All().Single(n => n.Block.Content == content);

    /// <summary>Opens the vault with vectors by note text: the used note points one way, the others lie at the given cosine to it.</summary>
    private async Task<VaultSession> OpenAsync()
    {
        var session = await OpenSessionAsync(_vault);
        State.CurrentBook = session.Vault.FindBook("Buch - Alpha");
        var vectors = new Dictionary<string, float[]>
        {
            ["Schon Quelle"] = [1f, 0f, 0f],
            ["Fast gleich"] = [0.96f, 0.28f, 0f],
            ["Knapp darunter"] = [0.9f, 0.4359f, 0f],
            ["Ganz anders"] = [0f, 1f, 0f],
            ["Nur in Beta Quelle"] = [0f, 0f, 1f],
            ["Wie in Beta"] = [0f, 0.1f, 0.995f],
            ["Verwendeter Unterpunkt"] = [0f, 0.6f, 0.8f],
            ["Teilweise verwendet"] = [0f, 0.6f, 0.8f],
        };
        session.NoteVectors = key =>
            session.Notes.Get(key) is { } note && vectors.TryGetValue(note.Block.Content, out var vector) ? vector : null;
        return session;
    }

    private IRenderedComponent<NoteCard> RenderCard(NoteBlock note) => Render<NoteCard>(p => p.Add(c => c.Note, note));

    private TextBlock First => State.CurrentBook!.Root.Children.First().TextBlocks.First();

    [Fact]
    public async Task Card_OfANoteNearlyLikeAUsedOne_ShowsTheHintWithPercent()
    {
        var session = await OpenAsync();

        var cut = RenderCard(Note(session, "Fast gleich"));

        var jump = cut.Find(".ne-note-duplicate-jump");
        Assert.Contains("96 %", jump.TextContent);
        Assert.Contains("Schon Quelle", jump.GetAttribute("title"));
        Assert.NotNull(cut.Find(".ne-note-duplicate-link"));
    }

    [Theory]
    [InlineData("Knapp darunter")]
    [InlineData("Ganz anders")]
    [InlineData("Schon Quelle")]
    public async Task Card_BelowTheThresholdOrUsed_ShowsNoHint(string content)
    {
        var session = await OpenAsync();

        var cut = RenderCard(Note(session, content));

        Assert.Empty(cut.FindAll(".ne-note-duplicate"));
    }

    [Fact]
    public async Task Card_LikeANoteUsedOnlyInAnotherBook_ShowsNoHint()
    {
        var session = await OpenAsync();

        var cut = RenderCard(Note(session, "Wie in Beta"));

        Assert.Empty(cut.FindAll(".ne-note-duplicate"));
    }

    [Fact]
    public async Task Hint_IgnoresTheNotesOwnUsedSubPoint()
    {
        var session = await OpenAsync();
        var note = Note(session, "Teilweise verwendet");
        var hints = new DuplicateHints(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        // The sub-point is a source of the book and has the very same vector as its parent.
        var hint = hints.Of(note, State.CurrentBook!, session.Notes, session.NoteVectors!, null);

        Assert.False(note.IsUsed);
        Assert.Null(hint);
    }

    [Fact]
    public async Task Jump_GoesToTheBlockWithTheUsedNoteAsSource()
    {
        var session = await OpenAsync();
        State.CurrentSectionKey = State.CurrentBook!.Root.Children.Last().Key;
        var cut = RenderCard(Note(session, "Fast gleich"));

        await cut.Find(".ne-note-duplicate-jump").ClickAsync(new());

        Assert.Equal(First.Key, State.CursorTextBlockKey);
    }

    [Fact]
    public async Task Link_MakesTheNoteAFurtherSourceWithoutAddingText()
    {
        var session = await OpenAsync();
        var note = Note(session, "Fast gleich");
        var cut = RenderCard(note);

        await cut.Find(".ne-note-duplicate-link").ClickAsync(new());

        var linked = Note(session, "Fast gleich");
        Assert.True(linked.IsUsed);
        Assert.Contains(linked.Block.Id!.Value, First.AllSources);
        Assert.Equal("Erster Text", First.Text);
        Assert.Equal(2, State.CurrentBook!.Root.Children.SelectMany(n => n.TextBlocks).Count());
        Assert.False(File.Exists(Path.Combine(_vault.Root, ".noteevolution", "placement-outcomes.jsonl")));

        // The note is used now: its card has no hint any more.
        var again = RenderCard(linked);
        Assert.Empty(again.FindAll(".ne-note-duplicate"));
    }

    [Fact]
    public async Task Link_CanBeUndone_TheNoteIsOpenAgainWithItsHint()
    {
        var session = await OpenAsync();
        var cut = RenderCard(Note(session, "Fast gleich"));
        await cut.Find(".ne-note-duplicate-link").ClickAsync(new());
        Assert.Equal("Als Quelle verknüpfen", session.Undo.NextDescription);

        session.Undo.Undo();
        State.RefreshBook();

        var note = Note(session, "Fast gleich");
        Assert.False(note.IsUsed);
        Assert.DoesNotContain(note.Block.Id!.Value, First.AllSources);
        Assert.NotEmpty(RenderCard(note).FindAll(".ne-note-duplicate"));
    }

    [Fact]
    public async Task Hint_IsComputedOncePerBookState()
    {
        var session = await OpenAsync();
        var asked = 0;
        var vectors = session.NoteVectors!;
        session.NoteVectors = key =>
        {
            asked++;
            return vectors(key);
        };
        var note = Note(session, "Fast gleich");

        Assert.NotNull(session.DuplicateOf(note, State.CurrentBook!));
        var first = asked;
        Assert.NotNull(session.DuplicateOf(note, State.CurrentBook!));

        Assert.Equal(first, asked);
    }
}
