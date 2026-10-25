using System.Text;
using System.Text.Json.Nodes;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Text;

namespace NoteEvolution.UI.Editor;

/// <summary>
/// Converts between a <see cref="SectionSnapshot"/> and the editor's document (TipTap JSON):
/// <c>doc → (textBlock | heading)+</c>; <c>heading{key, level}</c> with plain text; <c>textBlock{key, splitFrom,
/// sources}</c> with <c>para+</c>; <c>para{key, depth, isNote, md}</c> with text (marks <c>bold</c>, <c>italic</c>) and
/// <c>hardBreak</c> (<c>"\n"</c>). The first <c>para</c> is the block's own text (depth 0, no key of its own).
/// <para>
/// Each <c>para</c> carries the Markdown it was loaded with (<c>md</c>). If its runs are unchanged,
/// <see cref="FromJson"/> returns that Markdown as it is, so an untouched paragraph never changes its file line
/// (<c>_x_</c> stays <c>_x_</c>, a literal <c>*</c> stays unescaped). A changed one is written with
/// <see cref="InlineMarkdown.Format"/>; a typed <c>*</c> or <c>_</c> gets a backslash where it would otherwise read
/// as emphasis (ruling R28), and a <c>\*</c> or <c>\_</c> in the file shows as the bare character.
/// </para>
/// <para>
/// A text block may also carry <c>dates</c> and <c>datesTitle</c> (its created/updated dates, see
/// <see cref="BlockDateText"/>); they are only shown, <see cref="FromJson"/> ignores them.
/// </para>
/// <para>
/// A block reference <c>((id))</c> in a paragraph is an inline atom <c>blockRef{id, label, title, missing, foreign}</c>
/// (with the marks of the text around it, see <see cref="RefLabels"/>); it is one unit of the shown text and is written
/// back as <c>((id))</c>, its label, title, missing and foreign are only shown. A reference to a block outside the book (no
/// cross reference, spec 2.1) is such a node too, with <c>foreign</c> and its reference text as label
/// (<see cref="RefLabel.ForeignOf"/>): every reference is one position of the shown text, as <see cref="InlineMarkdown"/>
/// counts it.
/// </para>
/// <para>
/// A block reference to a person of the appendix is an inline atom <c>personRef{id, label, title, missing}</c> instead
/// (label: the abbreviation, see <see cref="AppendixLabels.People"/>), written back as <c>((id))</c> too. A source
/// citation <c>{{quelle ((id)) Fundstelle}}</c> is an inline atom <c>sourceCite{id, locator, label, title, missing}</c>
/// (label "[3]" or "[3, S. 12]", see <see cref="AppendixLabels.CitationLabel"/>), written back with
/// <see cref="SourceCitations.Format"/>. Each is one unit of the shown text; only id and locator go back.
/// </para>
/// </summary>
public static class EditorDocMapper
{
    /// <param name="snapshot">The book section to show.</param>
    /// <param name="datesOf">The dates label of a text block by its key; <c>null</c> (or a <c>null</c> label): none.</param>
    /// <param name="refOf">The label of a reference by its target id; <c>null</c>: the reference text as its label. A
    /// <c>null</c> label: no cross reference (a block outside the book, spec 2.1), a foreign node.</param>
    /// <param name="personOf">The label of a person by id; <c>null</c> (or a <c>null</c> label): the reference is no person.</param>
    /// <param name="sourceOf">The label of a cited source by id (<see cref="AppendixLabels.Sources"/>); <c>null</c>: "–".</param>
    public static string ToJson(
        SectionSnapshot snapshot, Func<Guid, BlockDateLabel?>? datesOf = null, Func<Guid, RefLabel?>? refOf = null,
        Func<Guid, RefLabel?>? personOf = null, Func<Guid, RefLabel>? sourceOf = null)
    {
        refOf ??= id => new RefLabel(BlockRefs.Format(id), "", false);
        var labels = new Labels(refOf, personOf ?? (_ => null), sourceOf ?? (_ => new RefLabel("–", "", false)));
        var content = new JsonArray();
        foreach (var node in snapshot.Nodes)
        {
            content.Add(node switch
            {
                SnapshotHeading heading => Heading(heading),
                SnapshotTextBlock text => TextBlock(text, datesOf?.Invoke(text.Key), labels),
                _ => throw new ArgumentException($"Unknown snapshot node {node.GetType().Name}.", nameof(snapshot)),
            });
        }

        if (content.Count == 0)
        {
            // The document needs a block; one to type in. It is only written once it is typed in.
            content.Add(TextBlock(new SnapshotTextBlock(Guid.CreateVersion7(), null, "", [], []), null, labels));
        }

        return new JsonObject { ["type"] = "doc", ["content"] = content }.ToJsonString();
    }

    /// <exception cref="ArgumentException">The JSON is not an editor document.</exception>
    public static SectionSnapshot FromJson(string json, Guid scopeKey, bool includeSubsections)
    {
        var doc = JsonNode.Parse(json) as JsonObject
                  ?? throw new ArgumentException("The editor document is not a JSON object.", nameof(json));
        var nodes = new List<SnapshotNode>();
        foreach (var node in Children(doc))
        {
            switch ((string?)node["type"])
            {
                case "heading":
                    nodes.Add(new SnapshotHeading(
                        KeyOf(node) ?? Guid.CreateVersion7(), Attr(node, "level", 1), PlainText(node)));
                    break;
                case "textBlock":
                    nodes.Add(ReadTextBlock(node));
                    break;
            }
        }

        return new SectionSnapshot(scopeKey, includeSubsections, nodes);
    }

    private static JsonObject Heading(SnapshotHeading heading)
    {
        var result = new JsonObject
        {
            ["type"] = "heading",
            ["attrs"] = new JsonObject { ["key"] = heading.Key.ToString("D"), ["level"] = heading.Level },
        };
        if (heading.Text.Length > 0)
        {
            result["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = heading.Text });
        }

        return result;
    }

    private static JsonObject TextBlock(SnapshotTextBlock text, BlockDateLabel? dates, Labels labels)
    {
        var paragraphs = new JsonArray(Para(null, text.Text, 0, false, BlockStatus.Draft, labels));
        foreach (var paragraph in text.Paragraphs)
        {
            paragraphs.Add(Para(paragraph.Key, paragraph.Text, paragraph.Depth, paragraph.IsNote, paragraph.Status, labels));
        }

        var sources = new JsonArray();
        foreach (var source in text.Sources)
        {
            sources.Add(new JsonObject
            {
                ["id"] = source.NoteId.ToString("D"),
                ["label"] = source.Label,
                ["broken"] = source.IsBroken,
            });
        }

        var attrs = new JsonObject
        {
            ["key"] = text.Key.ToString("D"),
            ["splitFrom"] = text.SplitFrom?.ToString("D"),
            ["sources"] = sources,
        };
        AddStatus(attrs, text.Status);
        if (dates is not null)
        {
            attrs["dates"] = dates.Text;
            attrs["datesTitle"] = dates.Title;
        }

        return new JsonObject { ["type"] = "textBlock", ["attrs"] = attrs, ["content"] = paragraphs };
    }

    /// <summary>How the shown references and citations are labelled (see <see cref="ToJson"/>).</summary>
    private sealed record Labels(Func<Guid, RefLabel?> RefOf, Func<Guid, RefLabel?> PersonOf, Func<Guid, RefLabel> SourceOf);

    private static JsonObject Para(Guid? key, string markdown, int depth, bool isNote, BlockStatus status, Labels labels)
    {
        var attrs = new JsonObject
        {
            ["key"] = key?.ToString("D"),
            ["depth"] = depth,
            ["isNote"] = isNote,
            ["md"] = markdown,
        };
        AddStatus(attrs, status);
        var result = new JsonObject { ["type"] = "para", ["attrs"] = attrs };
        var content = new JsonArray();
        foreach (var (run, unit) in Shown(markdown))
        {
            if (run.Text == "\n")
            {
                content.Add(new JsonObject { ["type"] = "hardBreak" });
                continue;
            }

            var item = unit switch
            {
                RefUnit(var id) when labels.PersonOf(id) is { } person => Atom("personRef", id, person.Text, person),
                RefUnit(var id) => BlockRef(id, labels.RefOf(id) ?? RefLabel.ForeignOf(id)),
                CiteUnit(var id, var locator) => Cite(id, locator, labels.SourceOf(id)),
                _ => new JsonObject { ["type"] = "text", ["text"] = run.Text },
            };

            var marks = new JsonArray();
            if (run.Bold)
            {
                marks.Add(new JsonObject { ["type"] = "bold" });
            }

            if (run.Italic)
            {
                marks.Add(new JsonObject { ["type"] = "italic" });
            }

            if (marks.Count > 0)
            {
                item["marks"] = marks;
            }

            content.Add(item);
        }

        if (content.Count > 0)
        {
            result["content"] = content;
        }

        return result;
    }

    private static JsonObject Atom(string type, Guid id, string text, RefLabel label) => new()
    {
        ["type"] = type,
        ["attrs"] = new JsonObject
        {
            ["id"] = id.ToString("D"),
            ["label"] = text,
            ["title"] = label.Title,
            ["missing"] = label.Missing,
        },
    };

    private static JsonObject BlockRef(Guid id, RefLabel label)
    {
        var item = Atom("blockRef", id, label.Text, label);
        if (label.Foreign)
        {
            item["attrs"]!["foreign"] = true;
        }

        return item;
    }

    private static JsonObject Cite(Guid id, string? locator, RefLabel source)
    {
        var item = Atom("sourceCite", id, AppendixLabels.CitationLabel(source, locator), source);
        item["attrs"]!["locator"] = locator;
        return item;
    }

    /// <summary>A unit of the shown text that is one node: a block reference or a source citation.</summary>
    private abstract record Unit;

    private sealed record RefUnit(Guid Id) : Unit;

    private sealed record CiteUnit(Guid Id, string? Locator) : Unit;

    /// <summary>
    /// The runs of <see cref="Display"/>, each reference or citation with its unit (<c>null</c> for text; adjacent text
    /// runs of the same emphasis are joined).
    /// </summary>
    private static List<(InlineRun Run, Unit? Unit)> Shown(string markdown)
    {
        var result = new List<(InlineRun Run, Unit? Unit)>();
        foreach (var run in Display(markdown))
        {
            if (run.Text != "\n" && UnitOf(run) is { } unit)
            {
                result.Add((run, unit));
            }
            else if (run.Text != "\n" && result.Count > 0 && result[^1] is { Unit: null } last && last.Run.Text != "\n"
                     && last.Run.Bold == run.Bold && last.Run.Italic == run.Italic)
            {
                result[^1] = (last.Run with { Text = last.Run.Text + run.Text }, null);
            }
            else
            {
                result.Add((run, null));
            }
        }

        return result;
    }

    /// <summary>Adds the <c>status</c> attribute with the file value; a draft has none.</summary>
    private static void AddStatus(JsonObject attrs, BlockStatus status)
    {
        if (BlockStatuses.FileValue(status) is { } value)
        {
            attrs["status"] = value;
        }
    }

    private static SnapshotTextBlock ReadTextBlock(JsonObject node)
    {
        var text = "";
        var paragraphs = new List<SnapshotParagraph>();
        var previous = 0;
        var first = true;
        foreach (var para in Children(node))
        {
            var markdown = Markdown(para);
            if (first)
            {
                text = markdown;
                first = false;
                continue;
            }

            // Depth 1 … previous depth + 1, as the editor keeps it.
            var depth = Math.Clamp(Attr(para, "depth", 1), 1, previous + 1);
            previous = depth;
            paragraphs.Add(new SnapshotParagraph(KeyOf(para) ?? Guid.CreateVersion7(), markdown, depth, Attr(para, "isNote", false)));
        }

        var sources = new List<SourceInfo>();
        if (node["attrs"]?["sources"] is JsonArray array)
        {
            foreach (var source in array.OfType<JsonObject>())
            {
                if (Guid.TryParse((string?)source["id"], out var id))
                {
                    sources.Add(new SourceInfo(id, (string?)source["label"] ?? "", (bool?)source["broken"] ?? false));
                }
            }
        }

        var splitFrom = Guid.TryParse(Attr<string?>(node, "splitFrom", null), out var from) ? from : (Guid?)null;
        return new SnapshotTextBlock(KeyOf(node) ?? Guid.CreateVersion7(), splitFrom, text, paragraphs, sources);
    }

    /// <summary>The paragraph's Markdown: as loaded if its runs are unchanged, else written from the runs.</summary>
    private static string Markdown(JsonObject para)
    {
        var runs = new List<InlineRun>();
        foreach (var item in Children(para))
        {
            switch ((string?)item["type"])
            {
                case "hardBreak":
                    runs.Add(new InlineRun("\n", false, false));
                    break;
                case "text":
                    runs.Add(Marked(item, (string?)item["text"] ?? ""));
                    break;
                case "blockRef" or "personRef" when Guid.TryParse(Attr<string?>(item, "id", null), out var id):
                    runs.Add(Marked(item, BlockRefs.Format(id)));
                    break;
                case "sourceCite" when Guid.TryParse(Attr<string?>(item, "id", null), out var id):
                    runs.Add(Marked(item, SourceCitations.Format(id, Attr<string?>(item, "locator", null))));
                    break;
            }
        }

        if (Attr<string?>(para, "md", null) is { } loaded && Equivalent(Display(loaded), runs))
        {
            return loaded;
        }

        return Write(runs);
    }

    /// <summary>A run of <paramref name="text"/> with the bold and italic marks of the editor node.</summary>
    private static InlineRun Marked(JsonObject item, string text)
    {
        var marks = (item["marks"] as JsonArray ?? []).Select(m => (string?)m?["type"]).ToList();
        return new InlineRun(text, marks.Contains("bold"), marks.Contains("italic"));
    }

    /// <summary>The unit of a run of <see cref="Display"/> that is a block reference or a citation; <c>null</c> for text.</summary>
    private static Unit? UnitOf(InlineRun run)
    {
        if (run.Text.Length == 0)
        {
            return null;
        }

        if (SourceCitations.LengthAt(run.Text, 0) == run.Text.Length)
        {
            var citation = SourceCitations.Find(run.Text)[0];
            return new CiteUnit(citation.Id, citation.Locator);
        }

        return BlockRefs.LengthAt(run.Text, 0) == run.Text.Length ? new RefUnit(BlockRefs.Find(run.Text)[0].Id) : null;
    }

    /// <summary>Formats the runs with every <c>*</c>/<c>_</c> escaped, then drops the escapes the reading does not need.</summary>
    private static string Write(List<InlineRun> runs)
    {
        var text = InlineMarkdown.Format(runs.Select(r => r with { Text = EscapeMarkers(r.Text) }));
        var display = Display(text);
        for (var i = 0; i + 1 < text.Length; i++)
        {
            if (text[i] != '\\')
            {
                continue;
            }

            if (text[i + 1] is '*' or '_')
            {
                var trial = text.Remove(i, 1);
                if (Equivalent(Display(trial), display))
                {
                    text = trial;
                    continue;
                }
            }

            // Skip the escaped character.
            i++;
        }

        return text;
    }

    /// <summary>
    /// What the editor shows of a Markdown text: the runs, with <c>\*</c> and <c>\_</c> as bare characters, each
    /// line break as a run of its own (<c>"\n"</c>, no emphasis) and each block reference as a run of its own, written
    /// as <see cref="BlockRefs.Format"/> writes it (the editor shows the id, not how its letters were cased; see
    /// <see cref="UnitOf"/>). A source citation is a run of its own too, as <see cref="SourceCitations.Format"/> writes
    /// it; its locator is taken as it is (no escapes removed).
    /// </summary>
    private static List<InlineRun> Display(string markdown)
    {
        var result = new List<InlineRun>();
        foreach (var run in InlineMarkdown.Parse(markdown))
        {
            var start = 0;
            foreach (var citation in SourceCitations.Find(run.Text))
            {
                AddDisplayed(result, run, run.Text[start..citation.Start]);
                result.Add(run with { Text = SourceCitations.Format(citation.Id, citation.Locator) });
                start = citation.Start + citation.Length;
            }

            AddDisplayed(result, run, run.Text[start..]);
        }

        return result;
    }

    /// <summary>Adds the text of <paramref name="run"/> outside its citations to the runs of <see cref="Display"/>.</summary>
    private static void AddDisplayed(List<InlineRun> result, InlineRun run, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        var lines = UnescapeMarkers(text).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                result.Add(new InlineRun("\n", false, false));
            }

            var line = lines[i];
            var start = 0;
            foreach (var reference in BlockRefs.Find(line))
            {
                if (reference.Start > start)
                {
                    result.Add(run with { Text = line[start..reference.Start] });
                }

                result.Add(run with { Text = BlockRefs.Format(reference.Id) });
                start = reference.Start + reference.Length;
            }

            if (start < line.Length)
            {
                result.Add(run with { Text = line[start..] });
            }
        }
    }

    /// <summary>
    /// Same characters, and the same emphasis on every character that is not whitespace (the Markdown writer may
    /// move emphasis off whitespace at its edges).
    /// </summary>
    private static bool Equivalent(List<InlineRun> a, List<InlineRun> b)
    {
        var flagsA = Flags(a);
        var flagsB = Flags(b);
        if (flagsA.Text != flagsB.Text)
        {
            return false;
        }

        for (var i = 0; i < flagsA.Text.Length; i++)
        {
            if (!char.IsWhiteSpace(flagsA.Text[i]) && flagsA.Styles[i] != flagsB.Styles[i])
            {
                return false;
            }
        }

        return true;
    }

    private static (string Text, List<(bool, bool)> Styles) Flags(List<InlineRun> runs)
    {
        var text = new StringBuilder();
        var styles = new List<(bool, bool)>();
        foreach (var run in runs)
        {
            text.Append(run.Text);
            styles.AddRange(Enumerable.Repeat((run.Bold, run.Italic), run.Text.Length));
        }

        return (text.ToString(), styles);
    }

    /// <summary>Escapes every <c>*</c> and <c>_</c>, except in a source citation (its locator is written as it is).</summary>
    private static string EscapeMarkers(string text)
    {
        var sb = new StringBuilder();
        var start = 0;
        foreach (var citation in SourceCitations.Find(text))
        {
            sb.Append(Escaped(text[start..citation.Start])).Append(text, citation.Start, citation.Length);
            start = citation.Start + citation.Length;
        }

        return sb.Append(Escaped(text[start..])).ToString();

        static string Escaped(string part) => part.Replace("*", "\\*").Replace("_", "\\_");
    }

    /// <summary>Removes the backslash of <c>\*</c> and <c>\_</c>; other backslash escapes stay as they are.</summary>
    private static string UnescapeMarkers(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                if (text[i + 1] is not ('*' or '_'))
                {
                    sb.Append('\\');
                }

                sb.Append(text[++i]);
                continue;
            }

            sb.Append(text[i]);
        }

        return sb.ToString();
    }

    private static string PlainText(JsonObject node) =>
        string.Concat(Children(node).Select(item => (string?)item["text"] ?? ""));

    private static IEnumerable<JsonObject> Children(JsonObject node) =>
        (node["content"] as JsonArray ?? []).OfType<JsonObject>();

    private static Guid? KeyOf(JsonObject node) => Guid.TryParse(Attr<string?>(node, "key", null), out var key) ? key : null;

    private static T Attr<T>(JsonObject node, string name, T fallback) =>
        node["attrs"]?[name] is JsonValue value && value.TryGetValue<T>(out var result) ? result : fallback;
}
