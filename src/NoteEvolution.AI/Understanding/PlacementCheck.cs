using System.Globalization;
using System.Text.Json;
using NoteEvolution.AI.Relevance;
using NoteEvolution.Core.Books;

namespace NoteEvolution.AI.Understanding;

/// <summary>The model's decision: the topic and the place after which the note fits best.</summary>
/// <param name="SectionKey">The chosen topic.</param>
/// <param name="AfterTextBlockKey">The text block the note goes after; null = a new place at the end of the topic.</param>
/// <param name="Reason">The model's one-sentence reason, cleaned.</param>
public sealed record PlacementVerdict(Guid SectionKey, Guid? AfterTextBlockKey, string Reason);

/// <summary>Why a placement check gave no verdict.</summary>
public enum PlacementCheckFailure { NoAnswer, Unusable, Cancelled, Unavailable }

/// <summary>A numbered topic of the prompt; its places are numbered <c>Number.1</c>, <c>Number.2</c>, … in <paramref name="TextBlockKeys"/> order.</summary>
public sealed record CheckTopic(int Number, Guid SectionKey, IReadOnlyList<Guid> TextBlockKeys);

/// <summary>The prompt of a placement check and the numbering it used (needed to read the answer).</summary>
public sealed record PlacementCheckPrompt(string System, string User, IReadOnlyList<CheckTopic> Topics);

/// <summary>"Genauer prüfen": asks the language model which of the best topics and places fits a note. Pure.</summary>
public static class PlacementCheck
{
    private const int MaxTopics = 3;
    private const int MaxPlaces = 3;
    private const int PlaceLength = 120;
    private const int ReasonLimit = 300;
    private const string PathSeparator = " › ";

    private const string SystemPrompt =
        "Du ordnest eine Notiz in ein Buch ein. Wähle aus den nummerierten Themen das passendste und darin die Stelle, nach der die Notiz am besten passt. Antworte nur mit JSON: {\"thema\": Nummer, \"stelle\": \"Nummer.Nummer\" oder \"0\", \"begruendung\": ein Satz auf Deutsch}.";

    /// <summary>
    /// The prompt for the best <paramref name="topics"/> (at most 3) with their <paramref name="places"/> (at most 3 each,
    /// keyed by section). Topics that are not in the book are left out.
    /// </summary>
    public static PlacementCheckPrompt Prompt(Book book, string noteText, IReadOnlyList<TopicHit> topics,
        IReadOnlyDictionary<Guid, IReadOnlyList<PlacementHit>> places)
    {
        var lines = new List<string> { "Notiz:", UnderstandingItems.LimitNote(noteText), "", "Themen:" };
        var numbered = new List<CheckTopic>();
        foreach (var topic in topics)
        {
            if (numbered.Count == MaxTopics)
            {
                break;
            }

            var node = book.FindNode(topic.SectionKey);
            if (node is null)
            {
                continue;
            }

            var number = numbered.Count + 1;
            lines.Add($"{number}. {HeadingPath(book, node)}");
            if (!string.IsNullOrWhiteSpace(topic.Summary))
            {
                lines.Add("   Inhalt: " + topic.Summary.Trim());
            }

            var keys = new List<Guid>();
            foreach (var hit in places.GetValueOrDefault(topic.SectionKey) ?? [])
            {
                if (keys.Count == MaxPlaces)
                {
                    break;
                }

                var block = book.FindTextBlock(hit.TextBlockKey);
                if (block is null)
                {
                    continue;
                }

                keys.Add(block.Key);
                lines.Add($"   {number}.{keys.Count} nach: {FirstLine(block.Text)}");
            }

            lines.Add($"   {number}.0 = neue Stelle am Ende des Themas (Stelle \"0\")");
            numbered.Add(new CheckTopic(number, topic.SectionKey, keys));
        }

        return new PlacementCheckPrompt(SystemPrompt, string.Join("\n", lines), numbered);
    }

    /// <summary>
    /// Reads the model's JSON answer: "thema" as number or numeric string, "stelle" as "N.M" or "0" (new place at the end).
    /// Null if the JSON is invalid or names a topic or place that is not in <paramref name="prompt"/>.
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
                || !root.TryGetProperty("thema", out var themaElement)
                || ReadInt(themaElement) is not { } thema
                || prompt.Topics.FirstOrDefault(t => t.Number == thema) is not { } topic
                || !root.TryGetProperty("stelle", out var stelleElement)
                || ReadPlace(stelleElement, thema, topic) is not { } after)
            {
                return null;
            }

            var reason = root.TryGetProperty("begruendung", out var r) && r.ValueKind == JsonValueKind.String
                ? UnderstandingPrompts.Clean(r.GetString()!, ReasonLimit)
                : "";
            return new PlacementVerdict(topic.SectionKey, after.Key, reason);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>"N.M" (a place of the topic), "0" or "N.0" (new place at the end). Null = not valid.</summary>
    private static Place? ReadPlace(JsonElement stelle, int thema, CheckTopic topic)
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

        if (text == "0")
        {
            return new Place(null);
        }

        var parts = text.Split('.');
        if (parts.Length != 2 || ReadInt(parts[0]) != thema || ReadInt(parts[1]) is not { } index)
        {
            return null;
        }

        if (index == 0)
        {
            return new Place(null);
        }

        return index <= topic.TextBlockKeys.Count ? new Place(topic.TextBlockKeys[index - 1]) : null;
    }

    /// <summary>A valid place; a null Key is a new place at the end of the topic.</summary>
    private readonly record struct Place(Guid? Key);

    private static int? ReadInt(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number when e.TryGetInt32(out var n) => n,
        JsonValueKind.String => ReadInt(e.GetString()!),
        _ => null,
    };

    private static int? ReadInt(string s) =>
        int.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

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

    private static string FirstLine(string content)
    {
        var end = content.IndexOf('\n');
        var line = (end < 0 ? content : content[..end]).TrimEnd('\r').Trim();
        return line.Length <= PlaceLength ? line : line[..(PlaceLength - 1)] + "…";
    }
}

