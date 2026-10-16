using System.Globalization;
using System.Text.Json;
using NoteEvolution.AI.Language;
using NoteEvolution.AI.Relevance;
using NoteEvolution.Core.Books;

namespace NoteEvolution.AI.Understanding;

/// <summary>The model's decision: the section and the place after which the note fits best.</summary>
/// <param name="SectionKey">The chosen section.</param>
/// <param name="AfterTextBlockKey">The text block the note goes after; null = a new place at the end of the section.</param>
/// <param name="Reason">The model's one-sentence reason, cleaned.</param>
/// <param name="Summary">The chosen section's summary, if the prompt had one.</param>
public sealed record PlacementVerdict(Guid SectionKey, Guid? AfterTextBlockKey, string Reason, string? Summary = null);

/// <summary>Why a placement check gave no verdict.</summary>
public enum PlacementCheckFailure { NoAnswer, Unusable, Cancelled, Unavailable }

/// <summary>A numbered section of the prompt; its places are numbered <c>Number.1</c>, <c>Number.2</c>, … in <paramref name="TextBlockKeys"/> order.</summary>
public sealed record CheckTopic(int Number, Guid SectionKey, IReadOnlyList<Guid> TextBlockKeys, string? Summary = null);

/// <summary>The prompt of a placement check, the numbering it used (needed to read the answer) and the answer's JSON schema.</summary>
public sealed record PlacementCheckPrompt(string System, string User, IReadOnlyList<CheckTopic> Topics, string Schema);

/// <summary>
/// "Genauer prüfen": asks the language model which section of the best chapters and which place in it fits a note. The
/// best topics only pick the chapters; the model then sees every section of them, so it chooses the sub-chapter itself.
/// Pure.
/// </summary>
public static class PlacementCheck
{
    /// <summary>How many of the best topics the caller passes: enough to find three different chapters.</summary>
    public const int TopicLimit = 9;

    private const int MaxChapters = 3;
    private const int MaxSectionsPerChapter = 6;
    private const int MaxSections = 14;
    private const int MaxPlacesPerSection = 2;
    private const int MaxPlaces = 6;
    private const int PlaceLength = 100;
    private const int SummaryLength = 160;
    private const int ReasonLength = 200;
    private const int ReasonLimit = 300;
    private const string PathSeparator = " › ";

    private const string SystemPrompt =
        "Du ordnest eine Notiz in ein Buch ein. Du bekommst nummerierte Abschnitte mit ihrem Inhalt und einzelnen Textstellen darin. " +
        "Wähle den Abschnitt, zu dessen Thema die Notiz inhaltlich gehört, und darin die Textstelle, nach der sie am besten passt. " +
        "Entscheide nach dem Inhalt, nicht nach einzelnen gleichen Wörtern. \"N.M\" heißt: nach Textstelle M von Abschnitt N; " +
        "\"N.0\" heißt: neu am Ende von Abschnitt N. Antworte nur mit JSON: " +
        "{\"begruendung\": ein kurzer Satz auf Deutsch (höchstens 20 Wörter), \"stelle\": \"N.M\"}.";

    /// <summary>The request options of a check: a short answer constrained to <paramref name="prompt"/>'s schema.</summary>
    public static ChatOptions Options(PlacementCheckPrompt prompt) =>
        new(UnderstandingPrompts.CheckTokens, Json: true, Schema: prompt.Schema);

    /// <summary>
    /// The prompt for the chapters of the best <paramref name="topics"/> (at most 3, best first): each chapter's sections
    /// (the chapter itself if it has own text blocks, then its sub-headings; a chapter without sub-headings stands alone),
    /// at most 6 per chapter and 14 in all, each with the first sentence of its summary (<paramref name="summaryOf"/>, else the topic's) and
    /// up to 2 of the best <paramref name="places"/> (at most 6 in all). Topics that are not in the book are left out.
    /// </summary>
    public static PlacementCheckPrompt Prompt(Book book, string noteText, IReadOnlyList<TopicHit> topics,
        IReadOnlyDictionary<Guid, IReadOnlyList<PlacementHit>> places, Func<OutlineNode, string?>? summaryOf = null)
    {
        var chapters = topics
            .Select(topic => book.FindNode(topic.SectionKey))
            .OfType<OutlineNode>()
            .Select(ChapterOf)
            .Distinct()
            .Take(MaxChapters)
            .ToList();
        var sections = chapters.SelectMany(chapter => SectionsOf(chapter).Take(MaxSectionsPerChapter)).Take(MaxSections).ToList();
        var topicSummaries = topics.Where(t => !string.IsNullOrWhiteSpace(t.Summary))
            .GroupBy(t => t.SectionKey)
            .ToDictionary(g => g.Key, g => g.First().Summary!);

        // The best places overall, each with the section it belongs to.
        var placesOf = sections.ToDictionary(s => s.Key, _ => new List<TextBlock>());
        var taken = 0;
        foreach (var hit in places.Values.SelectMany(list => list).OrderByDescending(hit => hit.Score))
        {
            if (taken == MaxPlaces)
            {
                break;
            }

            if (book.FindTextBlock(hit.TextBlockKey) is not { } block
                || SectionOf(block, sections) is not { } section
                || placesOf[section.Key] is not { Count: < MaxPlacesPerSection } list
                || list.Any(b => b.Key == block.Key))
            {
                continue;
            }

            list.Add(block);
            taken++;
        }

        var lines = new List<string> { "Notiz:", UnderstandingItems.LimitNote(noteText), "", "Abschnitte:" };
        var numbered = new List<CheckTopic>();
        var choices = new List<string>();
        foreach (var section in sections)
        {
            var number = numbered.Count + 1;
            var own = summaryOf?.Invoke(section);
            var summary = string.IsNullOrWhiteSpace(own) ? topicSummaries.GetValueOrDefault(section.Key) : own;
            var line = $"{number}. {HeadingPath(book, section)}";
            if (!string.IsNullOrWhiteSpace(summary))
            {
                line += " – " + FirstSentence(summary);
            }

            lines.Add(line);
            choices.Add($"{number}.0");
            var keys = new List<Guid>();
            foreach (var block in placesOf[section.Key])
            {
                keys.Add(block.Key);
                lines.Add($"   {number}.{keys.Count} nach: {FirstLine(block.Text)}");
                choices.Add($"{number}.{keys.Count}");
            }

            numbered.Add(new CheckTopic(number, section.Key, keys, string.IsNullOrWhiteSpace(summary) ? null : summary.Trim()));
        }

        return new PlacementCheckPrompt(SystemPrompt, string.Join("\n", lines), numbered, Schema(choices));
    }

    /// <summary>
    /// Reads the model's JSON answer: "stelle" as "N.M" (a place of section N), "N.0" or "N" (new place at the end of
    /// section N), as string or number. Null if the JSON is invalid or names a section or place that is not in
    /// <paramref name="prompt"/>.
    /// </summary>
    public static PlacementVerdict? Parse(string json, PlacementCheckPrompt prompt)
    {
        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json[start..(end + 1)]);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("stelle", out var stelleElement)
                || ReadPlace(stelleElement, prompt) is not { } place)
            {
                return null;
            }

            var reason = root.TryGetProperty("begruendung", out var r) && r.ValueKind == JsonValueKind.String
                ? UnderstandingPrompts.Clean(r.GetString()!, ReasonLimit)
                : "";
            return new PlacementVerdict(place.Section.SectionKey, place.Key, reason, place.Section.Summary);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>"N.M" (a place of section N), "N.0" or "N" (new place at the end). Null = not valid.</summary>
    private static Place? ReadPlace(JsonElement stelle, PlacementCheckPrompt prompt)
    {
        var text = stelle.ValueKind switch
        {
            JsonValueKind.Number => stelle.GetRawText(),
            JsonValueKind.String => stelle.GetString()!.Trim(),
            _ => null,
        };
        if (text is null)
        {
            return null;
        }

        var parts = text.Split('.');
        if (parts.Length > 2
            || ReadInt(parts[0]) is not { } number
            || prompt.Topics.FirstOrDefault(t => t.Number == number) is not { } section)
        {
            return null;
        }

        var index = parts.Length == 2 ? ReadInt(parts[1]) : 0;
        return index switch
        {
            0 => new Place(section, null),
            { } i when i <= section.TextBlockKeys.Count => new Place(section, section.TextBlockKeys[i - 1]),
            _ => null,
        };
    }

    /// <summary>A valid place in a section; a null Key is a new place at the end of the section.</summary>
    private readonly record struct Place(CheckTopic Section, Guid? Key);

    private static int? ReadInt(string s) =>
        int.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>The answer's JSON schema: a short reason first (the model thinks before it chooses), then one of <paramref name="choices"/>.</summary>
    private static string Schema(IReadOnlyList<string> choices) => JsonSerializer.Serialize(new
    {
        type = "object",
        properties = new
        {
            begruendung = new { type = "string", maxLength = ReasonLength },
            stelle = new { type = "string", @enum = choices },
        },
        required = new[] { "begruendung", "stelle" },
    });

    /// <summary>The top-level heading <paramref name="node"/> lies in; the root (prologue) is its own chapter.</summary>
    private static OutlineNode ChapterOf(OutlineNode node)
    {
        while (node.Parent is { Parent: not null } parent)
        {
            node = parent;
        }

        return node;
    }

    /// <summary>The sections of a chapter to choose from: the chapter itself if it has own text blocks or no sub-headings, then its sub-headings.</summary>
    private static IEnumerable<OutlineNode> SectionsOf(OutlineNode chapter)
    {
        if (chapter.Parent is null || !chapter.Children.Any() || chapter.TextBlocks.Any())
        {
            yield return chapter;
        }

        if (chapter.Parent is null)
        {
            yield break;
        }

        foreach (var child in chapter.Children)
        {
            yield return child;
        }
    }

    /// <summary>
    /// The section of <paramref name="sections"/> a text block belongs to: the one it lies in most deeply. A block under a
    /// sub-heading that was left out (over the limit) belongs to none, not to its chapter.
    /// </summary>
    private static OutlineNode? SectionOf(TextBlock block, IReadOnlyList<OutlineNode> sections)
    {
        for (var node = block.Section; node is not null; node = node.Parent)
        {
            if (sections.FirstOrDefault(s => s.Key == node.Key) is { } section)
            {
                return section;
            }

            if (node.Parent?.Parent?.Parent is null) // a chapter or sub-heading that is not offered
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>The titles from the top level down, joined with " › "; the root (no heading) is the book's title.</summary>
    private static string HeadingPath(Book book, OutlineNode node)
    {
        var titles = new List<string>();
        for (var n = node; n is { Parent: not null }; n = n.Parent)
        {
            titles.Insert(0, n.Title);
        }

        return titles.Count == 0 ? book.Title : string.Join(PathSeparator, titles);
    }

    /// <summary>The first sentence of a summary, at most <see cref="SummaryLength"/> characters.</summary>
    private static string FirstSentence(string summary)
    {
        // A sentence ends at ".", "!" or "?" before a space, unless the word before the "." is an abbreviation like "z.",
        // "B." or "bzw.".
        var text = summary.Trim();
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (text[i] is '.' or '!' or '?' && text[i + 1] == ' ')
            {
                var word = text[(text.LastIndexOf(' ', i) + 1)..i];
                if (text[i] != '.' || (word.Length > 1 && !Abbreviations.Contains(word)))
                {
                    text = text[..(i + 1)];
                    break;
                }
            }
        }

        return text.Length <= SummaryLength ? text : text[..(SummaryLength - 1)] + "…";
    }

    private static readonly HashSet<string> Abbreviations = new(
        ["bzw", "ca", "d.h", "etc", "evtl", "ggf", "inkl", "insb", "Nr", "S", "u.a", "usw", "vgl", "z.B"], StringComparer.OrdinalIgnoreCase);

    private static string FirstLine(string content)
    {
        var end = content.IndexOf('\n');
        var line = (end < 0 ? content : content[..end]).TrimEnd('\r').Trim();
        return line.Length <= PlaceLength ? line : line[..(PlaceLength - 1)] + "…";
    }
}
