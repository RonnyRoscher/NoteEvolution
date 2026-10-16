using NoteEvolution.AI.Understanding;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;

namespace NoteEvolution.AI.Tests.Understanding;

public sealed class StoreUnderstandingTests : IDisposable
{
    private const string Chat = "chat-1";
    private const string Embed = "embed-1";

    private readonly TestVault _vault = TestVault.Create(
        ("pages/Buch.md", "title:: Buch\ntype:: book\n\n- # Liebe\n\t- Liebe Text\n\t- ## Vertrauen\n\t\t- Vertrauen Text\n"));
    private readonly TempDir _dir = new();
    private readonly UnderstandingStore _store;
    private readonly StoreUnderstanding _understanding;

    public StoreUnderstandingTests()
    {
        _store = UnderstandingStore.Open(_dir.Path);
        _understanding = new StoreUnderstanding(_store, Chat, Embed);
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
        _vault.Dispose();
    }

    private Book LoadBook() => _vault.Open().Books.Single();

    private static OutlineNode Section(Book book, string title) => book.Root.Children.Prepend(book.Root)
        .SelectMany(n => n.Children.Prepend(n)).First(n => n.Title == title);

    private static UnderstandingItem Item(Book book, string title) =>
        UnderstandingItems.ForBook(book, Chat).Single(i => i.Key == Section(book, title).Key);

    private static UnderstandingEntry Entry(UnderstandingKind kind, string fingerprint, string identity, string text,
        string? embedModel = Embed, float[]? vector = null) =>
        new(kind, fingerprint, identity, text, Chat, embedModel, embedModel is null ? null : vector ?? [1f, 0f]);

    [Fact]
    public void Heading_CurrentEntryByFingerprint()
    {
        var book = LoadBook();
        var item = Item(book, "Vertrauen");
        _store.Put(Entry(UnderstandingKind.Heading, item.Fingerprint, item.Identity, "Über Vertrauen", vector: [0f, 1f]));

        var summary = _understanding.Heading(book, Section(book, "Vertrauen"));

        Assert.NotNull(summary);
        Assert.Equal("Über Vertrauen", summary.Value.Text);
        Assert.Equal([0f, 1f], summary.Value.Vector);
        Assert.Null(_understanding.Heading(book, Section(book, "Liebe")));
        Assert.Null(_understanding.Heading(book, book.Root));
    }

    [Fact]
    public void Heading_FallsBackToLatestByIdentity()
    {
        var book = LoadBook();
        var item = Item(book, "Liebe");
        _store.Put(Entry(UnderstandingKind.Heading, "older-text", item.Identity, "Alt"));
        _store.Put(Entry(UnderstandingKind.Note, "a-note", item.Identity, "Notiz")); // another kind does not count

        Assert.Equal("Alt", _understanding.Heading(book, Section(book, "Liebe"))?.Text);

        _store.Put(Entry(UnderstandingKind.Heading, item.Fingerprint, item.Identity, "Neu", embedModel: null));
        _store.SetVector(item.Fingerprint, Embed, [0.5f, 0.5f]);
        Assert.Equal("Neu", _understanding.Heading(book, Section(book, "Liebe"))?.Text);
    }

    [Fact]
    public void Heading_VectorOfAnotherEmbedModel_NotUsed()
    {
        var book = LoadBook();
        var item = Item(book, "Liebe");
        _store.Put(Entry(UnderstandingKind.Heading, item.Fingerprint, item.Identity, "Neu", embedModel: "embed-2"));

        Assert.Null(_understanding.Heading(book, Section(book, "Liebe")));
        Assert.NotNull(new StoreUnderstanding(_store, Chat, "embed-2").Heading(book, Section(book, "Liebe")));
    }

    [Fact]
    public void Notes_ByMappedFingerprint_NoStaleFallback()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        _store.Put(Entry(UnderstandingKind.Note, "fa", "note:a", "Kern A", vector: [1f, 0f]));
        _store.Put(Entry(UnderstandingKind.Note, "fb-old", "note:b", "Kern B alt", vector: [0f, 1f]));
        _store.Put(Entry(UnderstandingKind.Note, "fc", "note:c", "Kern C", embedModel: "embed-2"));

        Assert.Null(_understanding.NoteCore(a));
        Assert.Empty(_understanding.NoteCores());

        _understanding.UpdateNotes(new Dictionary<Guid, string> { [a] = "fa", [b] = "fb-new", [Guid.NewGuid()] = "fc" });

        Assert.Equal([1f, 0f], _understanding.NoteCore(a)!);
        Assert.Null(_understanding.NoteCore(b)); // edited: no core until the new one is there
        var cores = _understanding.NoteCores();
        Assert.Equal(a, Assert.Single(cores).Key);

        _store.Put(Entry(UnderstandingKind.Note, "fb-new", "note:b", "Kern B", vector: [0f, 2f]));
        Assert.Equal([0f, 2f], _understanding.NoteCore(b)!);
        Assert.Equal(new[] { a, b }.Order(), _understanding.NoteCores().Select(c => c.Key).Order());

        _understanding.UpdateNotes(new Dictionary<Guid, string> { [b] = "fb-new" });
        Assert.Null(_understanding.NoteCore(a));
        Assert.Equal(b, Assert.Single(_understanding.NoteCores()).Key);
    }

    [Fact]
    public void UpdateNotes_WhileReading_IsSafe()
    {
        var keys = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToList();
        for (var i = 0; i < keys.Count; i++)
            _store.Put(Entry(UnderstandingKind.Note, "f" + i, "note:" + i, "Kern", vector: [i, 1f]));
        var map = keys.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => "f" + x.i);

        Parallel.For(0, 200, i =>
        {
            if (i % 10 == 0) _understanding.UpdateNotes(map);
            else _ = _understanding.NoteCores();
        });

        Assert.Equal(50, _understanding.NoteCores().Count);
    }

    [Fact]
    public void StoreUnderstanding_StoreDisposed_ReturnsNothing()
    {
        var book = LoadBook();
        var item = Item(book, "Liebe");
        var note = Guid.NewGuid();
        _store.Put(Entry(UnderstandingKind.Heading, item.Fingerprint, item.Identity, "Über Liebe"));
        _store.Put(Entry(UnderstandingKind.Note, "fn", "note:n", "Kern"));
        _understanding.UpdateNotes(new Dictionary<Guid, string> { [note] = "fn" });

        // The vault was switched while a relevance search still runs: no understanding, no exception.
        _store.Dispose();

        Assert.Null(_understanding.Heading(book, Section(book, "Liebe")));
        Assert.Null(_understanding.Heading(book, Section(book, "Vertrauen")));
        Assert.Null(_understanding.NoteCore(note));
        Assert.Empty(_understanding.NoteCores());
    }

    [Fact]
    public void Items_SameBookInstance_ComputedOnce()
    {
        var book = LoadBook();
        Assert.Null(_understanding.CachedItems(book));

        var items = _understanding.Items(book);

        // The same snapshot gives the same list (no new ForBook); another snapshot is computed anew, with equal items.
        Assert.Same(items, _understanding.Items(book));
        Assert.Same(items, _understanding.CachedItems(book));
        Assert.Equal(UnderstandingItems.ForBook(book, Chat).Select(i => i.Fingerprint), items.Select(i => i.Fingerprint));
        var reloaded = LoadBook();
        Assert.NotSame(items, _understanding.Items(reloaded));
        Assert.Equal(items.Select(i => i.Fingerprint), _understanding.Items(reloaded).Select(i => i.Fingerprint));
    }

    [Fact]
    public void NoteCore_Miss_CachedUntilTheStoreChangesForIt()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        _understanding.UpdateNotes(new Dictionary<Guid, string> { [a] = "fa", [b] = "fb" });
        Assert.Null(_understanding.NoteCore(a));
        Assert.Empty(_understanding.NoteCores());

        // Written behind the store's back (another connection): the misses are remembered, the store is not asked again.
        using (var other = UnderstandingStore.Open(_dir.Path))
        {
            other.Put(Entry(UnderstandingKind.Note, "fa", "note:a", "Kern A", vector: [1f, 0f]));
            other.Put(Entry(UnderstandingKind.Note, "fb", "note:b", "Kern B", vector: [0f, 1f]));
        }

        Assert.Null(_understanding.NoteCore(a));
        Assert.Empty(_understanding.NoteCores());

        // A result stored through the store (as the worker does) ends the miss of its fingerprint only.
        _store.Put(Entry(UnderstandingKind.Note, "fa", "note:a", "Kern A", embedModel: null));
        Assert.Null(_understanding.NoteCore(a)); // no vector yet
        _store.SetVector("fa", Embed, [2f, 0f]);
        Assert.Equal([2f, 0f], _understanding.NoteCore(a)!);
        Assert.Null(_understanding.NoteCore(b));
        Assert.Equal(a, Assert.Single(_understanding.NoteCores()).Key);
    }

    private static UnderstandingEntry PlacementEntry(string fingerprint, string identity, string json) =>
        new(UnderstandingKind.Placement, fingerprint, identity, json, Chat, null, null);

    private static string Verdict(Guid section, Guid? after = null) =>
        PlacementCheck.Serialize(new PlacementVerdict(section, after, "Passt.", "Zusammenfassung", PlacementFit.Good, section));

    private Guid SetUpPlacementNote(Book book, string fingerprint = "pf", string identity = "placement|Buch|note:n")
    {
        var note = Guid.NewGuid();
        _understanding.UpdatePlacements(book.LinkName, new Dictionary<Guid, (string, string)> { [note] = (fingerprint, identity) });
        return note;
    }

    [Fact]
    public void Placement_Current()
    {
        var book = LoadBook();
        var section = Section(book, "Vertrauen");
        var after = section.TextBlocks.Single().Key;
        var note = SetUpPlacementNote(book);
        _store.Put(PlacementEntry("pf", "placement|Buch|note:n", Verdict(section.Key, after)));

        var placement = _understanding.Placement(book, note);

        Assert.NotNull(placement);
        Assert.False(placement.Stale);
        Assert.Equal(section.Key, placement.Verdict.SectionKey);
        Assert.Equal(after, placement.Verdict.AfterTextBlockKey);
        Assert.Equal(PlacementState.Current, _understanding.PlacementStateOf(note));
    }

    [Fact]
    public void Placement_StaleFromIdentity()
    {
        var book = LoadBook();
        var note = SetUpPlacementNote(book, "pf-new");
        _store.Put(PlacementEntry("pf-old", "placement|Buch|note:n", Verdict(Section(book, "Liebe").Key)));

        var placement = _understanding.Placement(book, note);

        Assert.NotNull(placement);
        Assert.True(placement.Stale);
        Assert.Equal(PlacementState.Stale, _understanding.PlacementStateOf(note));
    }

    [Fact]
    public void Placement_Missing_Null()
    {
        var book = LoadBook();
        var note = SetUpPlacementNote(book);

        Assert.Null(_understanding.Placement(book, note));
        Assert.Null(_understanding.Placement(book, Guid.NewGuid()));
        Assert.Equal(PlacementState.Missing, _understanding.PlacementStateOf(note));
    }

    [Fact]
    public void Placement_OtherBook_Null()
    {
        var book = LoadBook();
        var note = SetUpPlacementNote(book);
        _store.Put(PlacementEntry("pf", "placement|Buch|note:n", Verdict(Section(book, "Liebe").Key)));
        _understanding.UpdatePlacements("Anderes Buch", new Dictionary<Guid, (string, string)> { [note] = ("pf", "placement|Buch|note:n") });

        Assert.Null(_understanding.Placement(book, note));
    }

    [Fact]
    public void Placement_SectionGone_Null()
    {
        var book = LoadBook();
        var note = SetUpPlacementNote(book);
        _store.Put(PlacementEntry("pf", "placement|Buch|note:n", Verdict(Guid.NewGuid())));

        Assert.Null(_understanding.Placement(book, note));
    }

    [Fact]
    public void Placement_AfterBlockGone_Null()
    {
        var book = LoadBook();
        var note = SetUpPlacementNote(book);
        _store.Put(PlacementEntry("pf", "placement|Buch|note:n", Verdict(Section(book, "Liebe").Key, Guid.NewGuid())));

        Assert.Null(_understanding.Placement(book, note));
    }

    [Fact]
    public void Placement_InvalidJson_Null()
    {
        var book = LoadBook();
        var note = SetUpPlacementNote(book);
        _store.Put(PlacementEntry("pf", "placement|Buch|note:n", "{not json"));

        Assert.Null(_understanding.Placement(book, note));
    }

    [Fact]
    public void PlacementState_Failed_AfterMaxAttempts()
    {
        var book = LoadBook();
        var note = SetUpPlacementNote(book);
        for (var i = 0; i < UnderstandingWorker.MaxAttempts - 1; i++) _store.RecordFailure("pf");
        Assert.Equal(PlacementState.Missing, _understanding.PlacementStateOf(note));

        _store.RecordFailure("pf");

        Assert.Equal(PlacementState.Failed, _understanding.PlacementStateOf(note));
    }

    [Fact]
    public void Placement_CachedUntilEntryChanged()
    {
        var book = LoadBook();
        var note = SetUpPlacementNote(book);
        Assert.Null(_understanding.Placement(book, note));

        // Written behind the store's back: the miss is remembered, the store is not asked again.
        using (var other = UnderstandingStore.Open(_dir.Path))
            other.Put(PlacementEntry("pf", "placement|Buch|note:n", Verdict(Section(book, "Liebe").Key)));
        Assert.Null(_understanding.Placement(book, note));

        // A result stored through the store (as the worker does) ends the miss.
        _store.Put(PlacementEntry("pf", "placement|Buch|note:n", Verdict(Section(book, "Vertrauen").Key)));
        Assert.Equal(Section(book, "Vertrauen").Key, _understanding.Placement(book, note)?.Verdict.SectionKey);
    }

    [Fact]
    public void Placement_StoreClosed_Null()
    {
        var book = LoadBook();
        var note = SetUpPlacementNote(book);
        _store.Put(PlacementEntry("pf", "placement|Buch|note:n", Verdict(Section(book, "Liebe").Key)));
        _store.Dispose();

        Assert.Null(_understanding.Placement(book, note));
        Assert.Equal(PlacementState.Missing, _understanding.PlacementStateOf(note));
    }
}
