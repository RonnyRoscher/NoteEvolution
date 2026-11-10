using Microsoft.Extensions.Logging;
using NoteEvolution.AI.Understanding;

namespace NoteEvolution.UI.State;

/// <summary>Why a note's placement check got a new prompt; several can apply.</summary>
[Flags]
internal enum PlacementChurnReason
{
    /// <summary>The prompt differs only in something else (its schema, the model).</summary>
    Other = 0,

    /// <summary>No earlier check of this note in this session (also after its first line changed).</summary>
    New = 1,

    /// <summary>The note's own text changed.</summary>
    Note = 2,

    /// <summary>Other sections are offered: the headings' paths or their order differ.</summary>
    Sections = 4,

    /// <summary>The same sections, with other places in them.</summary>
    Places = 8,

    /// <summary>The same sections, with another first sentence of a summary.</summary>
    Summaries = 16,
}

/// <summary>
/// Measures how much a change of the book costs, for the log only; it changes no computation. Writes which headings got a
/// new fingerprint and why, which notes' placement checks got a new prompt and why, how many of them need the model, and
/// whether a repeated check then came to another result. Everything is compared as text, as the keys of a book read again
/// are new. The comparison starts with the session; another book starts it over.
/// </summary>
/// <param name="storedFingerprints">The fingerprints the store has results for.</param>
/// <param name="storedText">The stored result of a fingerprint, if any.</param>
internal sealed class PlacementChurn(
    Func<IReadOnlySet<string>> storedFingerprints, Func<string, string?> storedText, ILogger logger)
{
    /// <summary>The section and place a verdict chose, as the prompt showed them.</summary>
    private sealed record Choice(string Section, string? Place);

    /// <summary>A check that needs the model: why it does, what the note had before, and its prompt.</summary>
    private sealed record Pending(PlacementChurnReason Reason, Choice? Before, PlacementCheckPrompt Prompt);

    /// <summary>A prompt read back: the note's text, and each section's path, summary sentence and places.</summary>
    private sealed record PromptParts(string Note, IReadOnlyList<PromptSection> Sections);

    private sealed record PromptSection(string Path, string Summary, List<string> Places);

    private const string SectionsMarker = "\n\nAbschnitte:";
    private const string SummarySeparator = " – ";
    private const string PlaceIndent = "   ";
    private const string PlaceMarker = "nach: ";

    // Used under the session's feed gate only.
    private string? _book;
    private IReadOnlyList<UnderstandingItem>? _headingsSeen;
    private Dictionary<string, UnderstandingItem> _headings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UnderstandingItem> _checks = new(StringComparer.Ordinal);

    // Also used by the store's writer thread; the store is never read under this lock.
    private readonly Lock _gate = new();
    private Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<PlacementChurnReason, (int SamePlace, int SameSection, int OtherSection, int NoEarlier)> _outcomes = [];

    /// <summary>Why <paramref name="current"/> differs from <paramref name="previous"/>, read from the text the model sees.</summary>
    internal static PlacementChurnReason Classify(PlacementCheckPrompt previous, PlacementCheckPrompt current)
    {
        var before = Parse(previous);
        var after = Parse(current);
        var reason = before.Note == after.Note ? PlacementChurnReason.Other : PlacementChurnReason.Note;
        if (!before.Sections.Select(s => s.Path).SequenceEqual(after.Sections.Select(s => s.Path), StringComparer.Ordinal))
        {
            return reason | PlacementChurnReason.Sections;
        }

        if (!before.Sections.Select(s => s.Summary).SequenceEqual(after.Sections.Select(s => s.Summary), StringComparer.Ordinal))
        {
            reason |= PlacementChurnReason.Summaries;
        }

        if (!before.Sections.SelectMany(s => s.Places).SequenceEqual(after.Sections.SelectMany(s => s.Places), StringComparer.Ordinal)
            || !before.Sections.Select(s => s.Places.Count).SequenceEqual(after.Sections.Select(s => s.Places.Count)))
        {
            reason |= PlacementChurnReason.Places;
        }

        return reason;
    }

    /// <summary>The placement checks of <paramref name="book"/> were replaced. Call it under the session's feed gate; never throws.</summary>
    public void Checks(string book, IReadOnlyList<UnderstandingItem> checks)
    {
        try
        {
            Use(book);
            if (checks.Count == 0)
            {
                return;
            }

            var stored = storedFingerprints();
            Dictionary<string, Pending> earlier;
            lock (_gate)
            {
                earlier = _pending;
            }

            // A note without a check in this build (waiting for its core statement) keeps its open comparison.
            var pending = new Dictionary<string, Pending>(earlier, StringComparer.Ordinal);
            var reasons = new SortedDictionary<PlacementChurnReason, int>();
            var unchanged = 0;
            foreach (var item in checks.Where(item => item.Check is not null))
            {
                _checks.TryGetValue(item.Identity, out var previous);
                _checks[item.Identity] = item;
                if (previous?.Fingerprint == item.Fingerprint)
                {
                    unchanged++;
                    continue;
                }

                var reason = previous is null ? PlacementChurnReason.New : Classify(previous.Check!, item.Check!);
                reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
                var carried = previous is not null ? earlier.GetValueOrDefault(previous.Fingerprint) : null;
                if (previous is not null)
                {
                    pending.Remove(previous.Fingerprint);
                }

                if (!stored.Contains(item.Fingerprint))
                {
                    pending[item.Fingerprint] = new Pending(
                        reason | (carried?.Reason ?? PlacementChurnReason.Other),
                        carried is not null ? carried.Before : ChoiceOf(previous),
                        item.Check!);
                }
            }

            lock (_gate)
            {
                _pending = pending;
            }

            if (reasons.Count > 0)
            {
                logger.LogInformation(
                    "Placement churn in {Book}: {Total} checks, {Unchanged} unchanged, {Changed} changed, {Pending} waiting for the model; why changed: {Reasons}",
                    book, unchanged + reasons.Values.Sum(), unchanged, reasons.Values.Sum(), pending.Count, Describe(reasons));
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Measuring the placement checks failed");
        }
    }

    /// <summary>The book's summary items are the work now. Call it under the session's feed gate; never throws.</summary>
    public void Headings(string book, IReadOnlyList<UnderstandingItem> items)
    {
        try
        {
            Use(book);
            if (ReferenceEquals(items, _headingsSeen))
            {
                return;
            }

            var first = _headingsSeen is null;
            _headingsSeen = items;
            int added = 0, own = 0, belowOnly = 0;
            var changed = new List<string>();
            var next = new Dictionary<string, UnderstandingItem>(StringComparer.Ordinal);

            // A sub-heading of a renamed chapter has another identity, but the same content.
            var known = _headings.Values.Select(item => item.Fingerprint).ToHashSet(StringComparer.Ordinal);
            foreach (var item in items)
            {
                next[item.Identity] = item;
                if (known.Contains(item.Fingerprint))
                {
                    continue;
                }

                if (!_headings.TryGetValue(item.Identity, out var previous))
                {
                    added++;
                    changed.Add(item.Fingerprint);
                }
                else
                {
                    if (previous.OwnText == item.OwnText && previous.Title == item.Title)
                    {
                        belowOnly++;
                    }
                    else
                    {
                        own++;
                    }

                    changed.Add(item.Fingerprint);
                }
            }

            _headings = next;
            if (changed.Count > 0 && !first)
            {
                var stored = storedFingerprints();
                logger.LogInformation(
                    "Heading churn in {Book}: {Changed} of {Total} headings changed, {Pending} waiting for the model: {Own} by their own text, {BelowOnly} only through a sub-heading, {Added} new or renamed",
                    book, changed.Count, items.Count, changed.Count(fingerprint => !stored.Contains(fingerprint)), own, belowOnly, added);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Measuring the headings failed");
        }
    }

    /// <summary>A placement was stored; any thread, never throws.</summary>
    public void Stored(string fingerprint)
    {
        try
        {
            Pending? done;
            lock (_gate)
            {
                _pending.Remove(fingerprint, out done);
            }

            if (done is null)
            {
                return;
            }

            var now = storedText(fingerprint) is { } text ? ChoiceIn(done.Prompt, text) : null;
            var outcome = done.Before is null || now is null ? "no earlier result"
                : done.Before == now ? "same place"
                : done.Before.Section == now.Section ? "same section"
                : "other section";
            string totals;
            lock (_gate)
            {
                var counts = _outcomes.GetValueOrDefault(done.Reason);
                _outcomes[done.Reason] = outcome switch
                {
                    "same place" => counts with { SamePlace = counts.SamePlace + 1 },
                    "same section" => counts with { SameSection = counts.SameSection + 1 },
                    "other section" => counts with { OtherSection = counts.OtherSection + 1 },
                    _ => counts with { NoEarlier = counts.NoEarlier + 1 },
                };
                totals = string.Join("; ", _outcomes.OrderBy(entry => entry.Key).Select(entry =>
                    $"{Name(entry.Key)}: same place {entry.Value.SamePlace}, same section {entry.Value.SameSection}, other section {entry.Value.OtherSection}, no earlier {entry.Value.NoEarlier}"));
            }

            logger.LogInformation("Placement recheck ({Reason}): {Outcome}. So far, {Totals}", Name(done.Reason), outcome, totals);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Measuring a placement's result failed");
        }
    }

    /// <summary>A build kept <paramref name="kept"/> stored results and checks the others again for <paramref name="reasons"/>; never throws.</summary>
    public void Keep(string book, int kept, IReadOnlyDictionary<RecheckReason, int> reasons)
    {
        try
        {
            var rechecked = reasons.Values.Sum();
            if (kept + rechecked == 0)
            {
                return;
            }

            logger.LogInformation(
                "Placement keep in {Book}: {Kept} kept, {Rechecked} rechecked; why: {Reasons}",
                book, kept, rechecked, string.Join(", ", reasons.OrderBy(entry => entry.Key).Select(entry => $"{entry.Key} {entry.Value}")));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Measuring the kept placements failed");
        }
    }

    /// <summary>Another book starts the comparison over.</summary>
    private void Use(string book)
    {
        if (string.Equals(_book, book, StringComparison.Ordinal))
        {
            return;
        }

        _book = book;
        _headingsSeen = null;
        _headings = new Dictionary<string, UnderstandingItem>(StringComparer.Ordinal);
        _checks.Clear();
        lock (_gate)
        {
            _pending = new Dictionary<string, Pending>(StringComparer.Ordinal);
        }
    }

    private Choice? ChoiceOf(UnderstandingItem? check) =>
        check?.Check is { } prompt && storedText(check.Fingerprint) is { } text ? ChoiceIn(prompt, text) : null;

    /// <summary>
    /// The section and place of the stored verdict <paramref name="text"/>: by the path and place text it was stored with
    /// (they hold after a restart, its keys do not), else as <paramref name="prompt"/> showed them.
    /// </summary>
    private static Choice? ChoiceIn(PlacementCheckPrompt prompt, string text)
    {
        if (PlacementCheck.Deserialize(text) is not { } verdict)
        {
            return null;
        }

        if (verdict.SectionPath is { } path)
        {
            return new Choice(path, verdict.AfterText);
        }

        if (prompt.Topics.FirstOrDefault(topic => topic.SectionKey == verdict.SectionKey) is not { } topic)
        {
            return null;
        }

        var sections = Parse(prompt).Sections;
        if (topic.Number > sections.Count)
        {
            return null;
        }

        var section = sections[topic.Number - 1];
        var index = verdict.AfterTextBlockKey is { } after ? topic.TextBlockKeys.ToList().IndexOf(after) : -1;
        return new Choice(section.Path, index >= 0 && index < section.Places.Count ? section.Places[index] : null);
    }

    private static PromptParts Parse(PlacementCheckPrompt prompt)
    {
        var at = prompt.User.IndexOf(SectionsMarker, StringComparison.Ordinal);
        if (at < 0)
        {
            return new PromptParts(prompt.User, []);
        }

        var sections = new List<PromptSection>();
        foreach (var line in prompt.User[(at + SectionsMarker.Length)..].Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith(PlaceIndent, StringComparison.Ordinal) && sections.Count > 0)
            {
                var marker = line.IndexOf(PlaceMarker, StringComparison.Ordinal);
                sections[^1].Places.Add(marker < 0 ? line : line[(marker + PlaceMarker.Length)..]);
                continue;
            }

            // "3. Path – first sentence": the number follows the section's position, so it is left out.
            var text = line[(line.IndexOf(' ') + 1)..];
            var separator = text.IndexOf(SummarySeparator, StringComparison.Ordinal);
            sections.Add(separator < 0
                ? new PromptSection(text, "", [])
                : new PromptSection(text[..separator], text[(separator + SummarySeparator.Length)..], []));
        }

        return new PromptParts(prompt.User[..at], sections);
    }

    private static string Describe(IEnumerable<KeyValuePair<PlacementChurnReason, int>> reasons) =>
        string.Join(", ", reasons.Select(entry => $"{Name(entry.Key)} {entry.Value}"));

    private static string Name(PlacementChurnReason reason) => reason.ToString().Replace(", ", "+", StringComparison.Ordinal);
}
