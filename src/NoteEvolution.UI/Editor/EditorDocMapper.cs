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
/// </summary>
public static class EditorDocMapper
{
    public static string ToJson(SectionSnapshot snapshot)
    {
        var content = new JsonArray();
        foreach (var node in snapshot.Nodes)
        {
            content.Add(node switch
            {
                SnapshotHeading heading => Heading(heading),
                SnapshotTextBlock text => TextBlock(text),
                _ => throw new ArgumentException($"Unknown snapshot node {node.GetType().Name}.", nameof(snapshot)),
            });
        }

        if (content.Count == 0)
        {
            // The document needs a block; one to type in. It is only written once it is typed in.
            content.Add(TextBlock(new SnapshotTextBlock(Guid.CreateVersion7(), null, "", [], [])));
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

    private static JsonObject TextBlock(SnapshotTextBlock text)
    {
        var paragraphs = new JsonArray(Para(null, text.Text, 0, false));
        foreach (var paragraph in text.Paragraphs)
        {
            paragraphs.Add(Para(paragraph.Key, paragraph.Text, paragraph.Depth, paragraph.IsNote));
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

        return new JsonObject
        {
            ["type"] = "textBlock",
            ["attrs"] = new JsonObject
            {
                ["key"] = text.Key.ToString("D"),
                ["splitFrom"] = text.SplitFrom?.ToString("D"),
                ["sources"] = sources,
            },
            ["content"] = paragraphs,
        };
    }

    private static JsonObject Para(Guid? key, string markdown, int depth, bool isNote)
    {
        var result = new JsonObject
        {
            ["type"] = "para",
            ["attrs"] = new JsonObject
            {
                ["key"] = key?.ToString("D"),
                ["depth"] = depth,
                ["isNote"] = isNote,
                ["md"] = markdown,
            },
        };
        var content = new JsonArray();
        foreach (var run in Display(markdown))
        {
            if (run.Text == "\n")
            {
                content.Add(new JsonObject { ["type"] = "hardBreak" });
                continue;
            }

            var item = new JsonObject { ["type"] = "text", ["text"] = run.Text };
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
                    var marks = (item["marks"] as JsonArray ?? []).Select(m => (string?)m?["type"]).ToList();
                    runs.Add(new InlineRun((string?)item["text"] ?? "", marks.Contains("bold"), marks.Contains("italic")));
                    break;
            }
        }

        if (Attr<string?>(para, "md", null) is { } loaded && Equivalent(Display(loaded), runs))
        {
            return loaded;
        }

        return Write(runs);
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
    /// What the editor shows of a Markdown text: the runs, with <c>\*</c> and <c>\_</c> as bare characters and each
    /// line break as a run of its own (<c>"\n"</c>, no emphasis).
    /// </summary>
    private static List<InlineRun> Display(string markdown)
    {
        var result = new List<InlineRun>();
        foreach (var run in InlineMarkdown.Parse(markdown))
        {
            var lines = UnescapeMarkers(run.Text).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                {
                    result.Add(new InlineRun("\n", false, false));
                }

                if (lines[i].Length > 0)
                {
                    result.Add(run with { Text = lines[i] });
                }
            }
        }

        return result;
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

    private static string EscapeMarkers(string text) => text.Replace("*", "\\*").Replace("_", "\\_");

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
