using Microsoft.Extensions.Logging;
using NoteEvolution.AI.Understanding;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

public class PlacementChurnTests
{
    private const string Book = "Buch - Alpha";

    private readonly Dictionary<string, string> _store = [];
    private readonly ListLogger _logger = new();
    private readonly PlacementChurn _churn;
    private readonly Guid _section = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly Guid _place = Guid.NewGuid();

    public PlacementChurnTests()
    {
        _churn = new PlacementChurn(() => _store.Keys.ToHashSet(), fingerprint => _store.GetValueOrDefault(fingerprint), _logger);
    }

    private static PlacementCheckPrompt Prompt(string note, string lines, params CheckTopic[] topics) =>
        new("System", $"Notiz:\n{note}\n\nAbschnitte:\n{lines}", topics, "{}");

    private static UnderstandingItem Check(string fingerprint, PlacementCheckPrompt prompt, string note = "Notiz") =>
        new(UnderstandingKind.Placement, fingerprint, $"placement|{Book}|{note}", note, prompt.User, [], Guid.NewGuid(), prompt);

    private static UnderstandingItem Heading(string fingerprint, string identity, string ownText, string title = "Titel") =>
        new(UnderstandingKind.Heading, fingerprint, identity, title, ownText, [], Guid.NewGuid());

    private PlacementCheckPrompt Standard(string summary = "Satz.", string place = "Stelle", string path = "Kapitel") =>
        Prompt("Text", $"1. {path} – {summary}\n   1.1 nach: {place}", new CheckTopic(1, _section, [_place], Path: path, PlaceTexts: [place]));

    [Fact]
    public void Classify_SameText_IsNoReason() =>
        Assert.Equal(PlacementChurnReason.Other, PlacementChurn.Classify(Standard(), Standard()));

    [Fact]
    public void Classify_OtherSummarySentence_IsSummaries() =>
        Assert.Equal(PlacementChurnReason.Summaries, PlacementChurn.Classify(Standard(), Standard(summary: "Neu.")));

    [Fact]
    public void Classify_OtherPlace_IsPlaces() =>
        Assert.Equal(PlacementChurnReason.Places, PlacementChurn.Classify(Standard(), Standard(place: "Andere")));

    [Fact]
    public void Classify_SummaryAndPlace_IsBoth() =>
        Assert.Equal(
            PlacementChurnReason.Summaries | PlacementChurnReason.Places,
            PlacementChurn.Classify(Standard(), Standard(summary: "Neu.", place: "Andere")));

    [Fact]
    public void Classify_OtherSections_IsSections_WhateverElseChanged() =>
        Assert.Equal(PlacementChurnReason.Sections, PlacementChurn.Classify(Standard(), Standard(summary: "Neu.", path: "Anderes")));

    [Fact]
    public void Classify_OtherNoteText_IsNote()
    {
        var other = Prompt("Geändert", "1. Kapitel – Satz.\n   1.1 nach: Stelle", new CheckTopic(1, _section, [_place]));
        Assert.Equal(PlacementChurnReason.Note, PlacementChurn.Classify(Standard(), other));
    }

    [Fact]
    public void Classify_PlaceMovedToAnotherSection_IsPlaces()
    {
        var before = Prompt("Text", "1. A\n   1.1 nach: Stelle\n2. B");
        var after = Prompt("Text", "1. A\n2. B\n   2.1 nach: Stelle");
        Assert.Equal(PlacementChurnReason.Places, PlacementChurn.Classify(before, after));
    }

    [Fact]
    public void Classify_KeysOfABookReadAgain_DoNotCount()
    {
        var reread = Prompt("Text", "1. Kapitel – Satz.\n   1.1 nach: Stelle", new CheckTopic(1, Guid.NewGuid(), [Guid.NewGuid()]));
        Assert.Equal(PlacementChurnReason.Other, PlacementChurn.Classify(Standard(), reread));
    }

    [Fact]
    public void Checks_CountsUnchangedChangedAndWaiting()
    {
        var kept = Check("kept", Standard(), "Bleibt");
        var first = Check("a1", Standard());
        _store["kept"] = "{}";
        _store["a2"] = "{}";
        _churn.Checks(Book, [kept, first]);
        _logger.Lines.Clear();

        _churn.Checks(Book, [kept, Check("a2", Standard(summary: "Neu."))]);

        var line = Assert.Single(_logger.Lines);
        Assert.Contains("2 checks, 1 unchanged, 1 changed, 0 waiting", line);
        Assert.Contains("Summaries 1", line);
    }

    [Fact]
    public void Checks_FirstBuild_CountsAsNew_SameChecksAgainAreSilent()
    {
        var check = Check("a1", Standard());
        _churn.Checks(Book, [check]);
        Assert.Contains("New 1", Assert.Single(_logger.Lines));

        _logger.Lines.Clear();
        _churn.Checks(Book, [check]);

        Assert.Empty(_logger.Lines);
    }

    [Fact]
    public void Checks_NoteMissingForAWhile_IsNotNewWhenItReturns()
    {
        // A note waiting for its core statement has no check for a while.
        var kept = Check("kept", Standard(), "Bleibt");
        _churn.Checks(Book, [kept, Check("a1", Standard())]);
        _churn.Checks(Book, [kept]);
        _logger.Lines.Clear();

        _churn.Checks(Book, [kept, Check("a2", Standard(place: "Andere"))]);

        Assert.Contains("Places 1", Assert.Single(_logger.Lines));
    }

    [Fact]
    public void Checks_AnotherBook_StartsOver()
    {
        _churn.Checks(Book, [Check("a1", Standard())]);
        _logger.Lines.Clear();

        _churn.Checks("Buch - Beta", [Check("a2", Standard(summary: "Neu."))]);

        Assert.Contains("New 1", Assert.Single(_logger.Lines));
    }

    private static readonly IReadOnlyDictionary<string, RecheckReason> NoRechecks = new Dictionary<string, RecheckReason>();

    /// <summary>A build checks the fingerprints again by these rules (its keep line is cleared from the log).</summary>
    private void RecheckedBy(string fingerprint, RecheckReason reason, params (string Fingerprint, RecheckReason Reason)[] more)
    {
        var rechecks = more.Prepend((fingerprint, reason)).ToDictionary(r => r.Item1, r => r.Item2);
        var count = _logger.Lines.Count;
        _churn.Keep(Book, 0, 0, rechecks.GroupBy(r => r.Value).ToDictionary(g => g.Key, g => g.Count()), rechecks);
        _logger.Lines.RemoveRange(count, _logger.Lines.Count - count);
    }

    private string Verdict(Guid section, Guid? after = null) =>
        PlacementCheck.Serialize(new PlacementVerdict(section, after, "Grund"), new PlacementCheckPrompt("", "", [], ""));

    /// <summary>A first check with a stored result, then a changed check that waits for the model, whose result arrives.</summary>
    private string Recheck(PlacementCheckPrompt before, string resultBefore, PlacementCheckPrompt after, string resultAfter)
    {
        _store["a1"] = resultBefore;
        _churn.Checks(Book, [Check("a1", before)]);
        RecheckedBy("a2", RecheckReason.NewCandidate);
        _churn.Checks(Book, [Check("a2", after)]);
        _logger.Lines.Clear();
        _store["a2"] = resultAfter;
        _churn.Stored("a2");
        return Assert.Single(_logger.Lines);
    }

    [Fact]
    public void Stored_SamePlaceAsBefore_IsCounted()
    {
        var line = Recheck(Standard(), Verdict(_section, _place), Standard(summary: "Neu."), Verdict(_section, _place));

        Assert.Contains("(NewCandidate): same place", line);
        Assert.Contains("NewCandidate: same place 1, same section 0, other section 0, no earlier 0", line);
    }

    [Fact]
    public void Stored_SameSectionOtherPlace_IsCounted()
    {
        var line = Recheck(Standard(), Verdict(_section, _place), Standard(summary: "Neu."), Verdict(_section));

        Assert.Contains("(NewCandidate): same section", line);
    }

    [Fact]
    public void Stored_OtherSection_IsCounted_AcrossNewKeys()
    {
        var before = Prompt("Text", "1. A\n2. B", new CheckTopic(1, _section, [], Path: "A"), new CheckTopic(2, _other, [], Path: "B"));

        // The book was read again: its keys are new, the sections are the same.
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var after = Prompt("Text", "1. A – Neu.\n2. B", new CheckTopic(1, a, [], Path: "A"), new CheckTopic(2, b, [], Path: "B"));

        Assert.Contains("(NewCandidate): same place", Recheck(before, Verdict(_section), after, Verdict(a)));
        _logger.Lines.Clear();
        _store.Clear();
        var other = new PlacementChurn(() => _store.Keys.ToHashSet(), fingerprint => _store.GetValueOrDefault(fingerprint), _logger);
        _store["b1"] = Verdict(_section);
        other.Checks(Book, [Check("b1", before)]);
        other.Checks(Book, [Check("b2", after)]);
        _logger.Lines.Clear();
        _store["b2"] = Verdict(b);
        other.Stored("b2");

        Assert.Contains("(First): other section", Assert.Single(_logger.Lines));
    }

    [Fact]
    public void Stored_ComparesByPath_AfterRestart_SamePlace()
    {
        // The earlier result was stored by the session before the restart: its keys are not in this session's prompt.
        var before = Prompt("Text", "1. Kapitel – Satz.\n   1.1 nach: Stelle", new CheckTopic(1, Guid.NewGuid(), [Guid.NewGuid()], Path: "Kapitel", PlaceTexts: ["Stelle"]));
        var old = before.Topics[0];
        var resultBefore = PlacementCheck.Serialize(new PlacementVerdict(old.SectionKey, old.TextBlockKeys[0], "Grund"), before);
        var session = Prompt("Text", "1. Kapitel – Satz.\n   1.1 nach: Stelle", new CheckTopic(1, Guid.NewGuid(), [Guid.NewGuid()], Path: "Kapitel", PlaceTexts: ["Stelle"]));
        var today = Prompt("Text", "1. Kapitel – Neu.\n   1.1 nach: Stelle", new CheckTopic(1, _section, [_place], Path: "Kapitel", PlaceTexts: ["Stelle"]));
        var resultAfter = PlacementCheck.Serialize(new PlacementVerdict(_section, _place, "Grund"), today);

        var line = Recheck(session, resultBefore, today, resultAfter);

        Assert.Contains("(NewCandidate): same place", line);
    }

    private string LegacyVerdict(Guid section, Guid? after) =>
        PlacementCheck.Serialize(new PlacementVerdict(section, after, "Grund"), new PlacementCheckPrompt("", "", [], ""));

    [Theory]
    [InlineData("Kapitel#0 › Unter#0", "Stelle", "same place")]
    [InlineData("Kapitel#0 › Unter#0", "Andere", "same section")]
    [InlineData("Anderes#0", "Stelle", "other section")]
    public void Stored_LegacyBeforeNewNow_ComparesInOneFormat(string newPath, string newPlace, string expected)
    {
        // An old result (no path stored) read through its prompt, a new one by its stored path and place text.
        var longPlace = new string('x', 100);
        var legacy = Prompt("Text", "1. Kapitel › Unter\n   1.1 nach: " + new string('x', 99) + "…",
            new CheckTopic(1, _section, [_place], Path: "Kapitel#0 › Unter#0", PlaceTexts: [longPlace]));
        var fresh = Prompt("Text", "1. Kapitel › Unter – Neu.\n   1.1 nach: " + newPlace,
            new CheckTopic(1, _other, [Guid.NewGuid()], Path: newPath, PlaceTexts: [newPlace == "Stelle" ? longPlace : newPlace]));
        var newResult = PlacementCheck.Serialize(new PlacementVerdict(_other, fresh.Topics[0].TextBlockKeys[0], "Grund"), fresh);

        Assert.Contains($"): {expected}.", Recheck(legacy, LegacyVerdict(_section, _place), fresh, newResult));
    }

    [Fact]
    public void Stored_LegacyBeforeWithoutPathInPrompt_SaysNoEarlierResult()
    {
        var legacy = Prompt("Text", "1. Kapitel\n   1.1 nach: Stelle", new CheckTopic(1, _section, [_place]));
        var fresh = Standard(summary: "Neu.");
        var newResult = PlacementCheck.Serialize(new PlacementVerdict(_section, _place, "Grund"), fresh);

        Assert.Contains("(NewCandidate): no earlier result", Recheck(legacy, LegacyVerdict(_section, _place), fresh, newResult));
    }

    [Fact]
    public void KeepLine_SameCountsAgain_IsLoggedOnce()
    {
        var reasons = new Dictionary<RecheckReason, int> { [RecheckReason.Model] = 1 };
        _churn.Keep(Book, 2, 0, reasons, NoRechecks);
        _churn.Keep(Book, 2, 0, reasons, NoRechecks);
        Assert.Single(_logger.Lines);

        _churn.Keep(Book, 3, 0, reasons, NoRechecks);
        Assert.Equal(2, _logger.Lines.Count);
    }

    [Fact]
    public void KeepLine_CountsKeptAndReasons()
    {
        _churn.Keep(Book, 5, 0, new Dictionary<RecheckReason, int> { [RecheckReason.NoteText] = 2, [RecheckReason.Model] = 1 }, NoRechecks);

        var line = Assert.Single(_logger.Lines);
        Assert.Equal("Placement keep in Buch - Alpha: 5 kept, 0 upgraded, 3 rechecked; why: NoteText 2, Model 1", line);
    }

    [Fact]
    public void KeepLine_UpgradedCountedApartFromKept()
    {
        _churn.Keep(Book, 0, 4, new Dictionary<RecheckReason, int>(), NoRechecks);

        Assert.Equal("Placement keep in Buch - Alpha: 0 kept, 4 upgraded, 0 rechecked; why: ", Assert.Single(_logger.Lines));
    }

    [Fact]
    public void Stored_CountedByTheRuleThatMadeTheCheckAgain()
    {
        _store["a1"] = Verdict(_section, _place);
        _store["b1"] = Verdict(_section, _place);
        _churn.Checks(Book, [Check("a1", Standard(), "A"), Check("b1", Standard(), "B")]);
        RecheckedBy("a2", RecheckReason.NoteText, ("b2", RecheckReason.Rebuilt));
        _churn.Checks(Book, [Check("a2", Standard(summary: "Neu."), "A"), Check("b2", Standard(summary: "Neu."), "B")]);
        _logger.Lines.Clear();

        _store["a2"] = Verdict(_section, _place);
        _churn.Stored("a2");
        _store["b2"] = PlacementCheck.Serialize(new PlacementVerdict(_other, null, "Grund"),
            Prompt("Text", "1. Anderes", new CheckTopic(1, _other, [], Path: "Anderes")));
        _churn.Stored("b2");

        Assert.StartsWith("Placement recheck (NoteText): same place", _logger.Lines[0]);
        Assert.StartsWith("Placement recheck (Rebuilt): other section", _logger.Lines[1]);
        Assert.Contains("NoteText: same place 1, same section 0, other section 0, no earlier 0; Rebuilt: same place 0, same section 0, other section 1, no earlier 0", _logger.Lines[1]);
    }

    [Fact]
    public void Stored_SectionRenamedInThisSession_IsTheSameSection()
    {
        using var vault = NoteEvolution.TestSupport.TestVault.Create(("pages/Buch.md", "title:: Buch\ntype:: book\n\n- # Alt\n\t- Text.\n- # Anderes\n\t- Mehr.\n"));
        var page = vault.Open().Books.Single().Page;
        var names = new SectionNames();
        names.Remember(NoteEvolution.Core.Books.Book.Load(page)); // a build before the rename
        page.AllBlocks().Single(b => b.Content == "# Alt").SetContent("# Neu");
        var today = NoteEvolution.Core.Books.Book.Load(page);
        var (renamed, link) = (today.Root.Children.First(), today.LinkName);

        // The earlier result is from the session before (its key is unknown), stored with the old name.
        string Result(Guid key, string path) => PlacementCheck.Serialize(new PlacementVerdict(key, null, "Grund"),
            Prompt("Text", "1. " + path, new CheckTopic(1, key, [], Path: path)));
        string Outcome(PlacementChurn churn)
        {
            _store.Clear();
            _logger.Lines.Clear();
            _store["r1"] = Result(Guid.NewGuid(), "Alt#0");
            churn.Checks(link, [Check("r1", Standard())]);
            churn.Checks(link, [Check("r2", Standard(path: "Neu"))]);
            _logger.Lines.Clear();
            _store["r2"] = Result(renamed.Key, "Neu#0");
            churn.Stored("r2");
            return Assert.Single(_logger.Lines);
        }

        Assert.Contains("): same place", Outcome(new PlacementChurn(() => _store.Keys.ToHashSet(), f => _store.GetValueOrDefault(f), _logger, names)));
        Assert.Contains("): other section", Outcome(new PlacementChurn(() => _store.Keys.ToHashSet(), f => _store.GetValueOrDefault(f), _logger)));
    }

    [Fact]
    public void KeepLine_NothingKeptOrRechecked_IsSilent()
    {
        _churn.Keep(Book, 0, 0, new Dictionary<RecheckReason, int>(), NoRechecks);

        Assert.Empty(_logger.Lines);
    }

    [Fact]
    public void Stored_TwoChangesBeforeTheModelAnswered_ComparesWithTheLastResult()
    {
        _store["a1"] = Verdict(_section, _place);
        _churn.Checks(Book, [Check("a1", Standard())]);
        _churn.Checks(Book, [Check("a2", Standard(summary: "Neu."))]);
        _churn.Checks(Book, [Check("a3", Standard(summary: "Neu.", place: "Andere"))]);
        _logger.Lines.Clear();
        _store["a3"] = Verdict(_section);

        _churn.Stored("a3");

        Assert.Contains("(First): same section", Assert.Single(_logger.Lines));
    }

    [Fact]
    public void Stored_WithoutEarlierResult_SaysSo()
    {
        _churn.Checks(Book, [Check("a1", Standard())]);
        _logger.Lines.Clear();
        _store["a1"] = Verdict(_section, _place);

        _churn.Stored("a1");

        Assert.Contains("(First): no earlier result", Assert.Single(_logger.Lines));
    }

    [Fact]
    public void Stored_SomethingNotWaited_IsSilent()
    {
        _churn.Stored("unknown");

        Assert.Empty(_logger.Lines);
    }

    [Fact]
    public void Headings_TellOwnChangesFromThoseThroughASubHeading()
    {
        _churn.Headings(Book, [Heading("c1", "c", "Text"), Heading("p1", "p", "Oben"), Heading("q1", "q", "Ruhig")]);
        Assert.Empty(_logger.Lines);

        _store["p2"] = "Zusammenfassung";
        _churn.Headings(Book, [Heading("c2", "c", "Neu"), Heading("p2", "p", "Oben"), Heading("q1", "q", "Ruhig"), Heading("n1", "n", "Neu")]);

        var line = Assert.Single(_logger.Lines);
        Assert.Contains("3 of 4 headings changed, 2 waiting", line);
        Assert.Contains("1 by their own text, 1 only through a sub-heading, 1 new or renamed", line);
    }

    [Fact]
    public void Headings_SubHeadingOfARenamedChapter_DoesNotCount()
    {
        _churn.Headings(Book, [Heading("s1", "Alt/Unter", "Text"), Heading("p1", "Alt", "Oben", "Alt")]);

        _churn.Headings(Book, [Heading("s1", "Neu/Unter", "Text"), Heading("p2", "Neu", "Oben", "Neu")]);

        var line = Assert.Single(_logger.Lines);
        Assert.Contains("1 of 2 headings changed", line);
        Assert.Contains("1 new or renamed", line);
    }

    [Fact]
    public void Stored_NoteWithoutACheckInBetween_StillCounted()
    {
        var kept = Check("kept", Standard(), "Bleibt");
        _store["kept"] = "{}";
        _store["a1"] = Verdict(_section, _place);
        _churn.Checks(Book, [kept, Check("a1", Standard())]);
        RecheckedBy("a2", RecheckReason.NewCandidate);
        _churn.Checks(Book, [kept, Check("a2", Standard(summary: "Neu."))]);

        // The note waits for its core statement in one build; its open check comes back unchanged.
        RecheckedBy("other", RecheckReason.Model);
        _churn.Checks(Book, [kept]);
        _logger.Lines.Clear();
        _store["a2"] = Verdict(_section, _place);
        _churn.Stored("a2");

        Assert.Contains("(NewCandidate): same place", Assert.Single(_logger.Lines));
    }

    [Fact]
    public void Headings_SameListAgain_IsSilent()
    {
        IReadOnlyList<UnderstandingItem> items = [Heading("c1", "c", "Text")];
        _churn.Headings(Book, items);
        _churn.Headings(Book, items);

        Assert.Empty(_logger.Lines);
    }

    [Fact]
    public void FailingStore_NeverThrows()
    {
        var churn = new PlacementChurn(() => throw new ObjectDisposedException("store"), _ => throw new ObjectDisposedException("store"), _logger);
        churn.Checks(Book, [Check("a1", Standard())]);
        churn.Headings(Book, [Heading("c1", "c", "Text")]);
        churn.Headings(Book, [Heading("c2", "c", "Neu")]);
        churn.Stored("a1");
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Information)
            {
                Lines.Add(formatter(state, exception));
            }
        }
    }
}
