using System.Text.Json;
using System.Text.Json.Serialization;
using NoteEvolution.AI.Understanding;
using NoteEvolution.Core.Books;

namespace NoteEvolution.UI.State;

/// <summary>How the section the user put a note into relates to the one the model had suggested.</summary>
public enum OutcomeRelation
{
    /// <summary>The suggested section itself.</summary>
    SameSection,

    /// <summary>Another section of the same chapter (the same top-level heading; the prologue counts as one).</summary>
    SameChapter,

    /// <summary>A section of another chapter.</summary>
    OtherChapter,
}

/// <summary>
/// One adoption of a note that had a suggestion: what was suggested (section path, fit, whether the suggestion was
/// stale), where the note went, how the two relate, and whether it was taken from the "Relevant" tab (which shows the
/// suggestion, so such an adoption tends to follow it).
/// </summary>
public sealed record PlacementOutcome(
    DateTimeOffset Time, string Book, string Note, string SuggestedPath, PlacementFit Fit, bool Stale, string ChosenPath,
    OutcomeRelation Relation, bool FromRelevant);

/// <summary>How many adoptions hit the suggested section, the same chapter or another one.</summary>
public readonly record struct OutcomeCounts(int SameSection, int SameChapter, int OtherChapter)
{
    public int Total => SameSection + SameChapter + OtherChapter;
}

/// <summary>
/// The measurement of how often the model's suggestion is where the user puts the note: one line per adoption in
/// <c>&lt;vault&gt;/.noteevolution/placement-outcomes.jsonl</c> (kept apart from the understanding store, which may be
/// deleted). It only records; nothing is decided from it. An adoption that is undone later stays recorded.
/// </summary>
public static class PlacementOutcomes
{
    public const string FileName = "placement-outcomes.jsonl";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The file of <paramref name="vaultRoot"/>'s outcomes.</summary>
    public static string PathIn(string vaultRoot) => Path.Combine(vaultRoot, ".noteevolution", FileName);

    /// <summary>
    /// The outcome of adopting a note with <paramref name="placement"/> into (or as) the element
    /// <paramref name="chosenElementKey"/> of <paramref name="book"/>; null when the suggested section or the element
    /// is not in the book (nothing to compare). The suggested section is found by its key, else by its stored path.
    /// </summary>
    public static PlacementOutcome? Of(
        Book book, NotePlacement placement, Guid chosenElementKey, string note, bool fromRelevant, DateTimeOffset now)
    {
        var verdict = placement.Verdict;
        var suggested = book.FindNode(verdict.SectionKey)
                        ?? (verdict.SectionPath is { } path ? SectionPath.Find(book, path) : null);
        if (suggested is null || BookElements.Find(book, chosenElementKey) is not { } element
            || book.FindNode(BookElements.SectionOf(book, element)) is not { } chosen)
        {
            return null;
        }

        return new PlacementOutcome(
            now, book.LinkName, note, SectionPath.Of(suggested), verdict.Fit, placement.Stale, SectionPath.Of(chosen),
            Relate(suggested, chosen), fromRelevant);
    }

    /// <summary>How <paramref name="chosen"/> relates to <paramref name="suggested"/> (both sections of one book).</summary>
    public static OutcomeRelation Relate(OutlineNode suggested, OutlineNode chosen) =>
        suggested.Key == chosen.Key ? OutcomeRelation.SameSection
        : ChapterOf(suggested).Key == ChapterOf(chosen).Key ? OutcomeRelation.SameChapter
        : OutcomeRelation.OtherChapter;

    /// <summary>The top-level heading the section belongs to; the root (prologue) for itself.</summary>
    private static OutlineNode ChapterOf(OutlineNode node)
    {
        while (node.Parent is { Parent: not null } parent)
        {
            node = parent;
        }

        return node;
    }

    /// <summary>Appends <paramref name="outcome"/> to the vault's file (the folder is created if missing).</summary>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be written.</exception>
    public static void Append(string vaultRoot, PlacementOutcome outcome)
    {
        var file = PathIn(vaultRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.AppendAllText(file, JsonSerializer.Serialize(outcome, Json) + "\n");
    }

    /// <summary>The vault's recorded outcomes, oldest first; a missing file gives none, an unreadable line is skipped.</summary>
    public static IReadOnlyList<PlacementOutcome> Read(string vaultRoot)
    {
        var file = PathIn(vaultRoot);
        var result = new List<PlacementOutcome>();
        string[] lines;
        try
        {
            if (!File.Exists(file))
            {
                return result;
            }

            lines = File.ReadAllLines(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        foreach (var line in lines.Where(line => line.Trim().Length > 0))
        {
            try
            {
                if (JsonSerializer.Deserialize<PlacementOutcome>(line, Json) is { } outcome)
                {
                    result.Add(outcome);
                }
            }
            catch (JsonException)
            {
                // A damaged line (e.g. a write cut short) does not hide the others.
            }
        }

        return result;
    }

    /// <summary>The counts of <paramref name="outcomes"/> for <paramref name="book"/> (its link name).</summary>
    public static OutcomeCounts Count(IEnumerable<PlacementOutcome> outcomes, string book)
    {
        var counts = default(OutcomeCounts);
        foreach (var outcome in outcomes.Where(o => string.Equals(o.Book, book, StringComparison.OrdinalIgnoreCase)))
        {
            counts = outcome.Relation switch
            {
                OutcomeRelation.SameSection => counts with { SameSection = counts.SameSection + 1 },
                OutcomeRelation.SameChapter => counts with { SameChapter = counts.SameChapter + 1 },
                _ => counts with { OtherChapter = counts.OtherChapter + 1 },
            };
        }

        return counts;
    }
}
