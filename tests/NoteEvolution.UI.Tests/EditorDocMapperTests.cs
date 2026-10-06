using System.Text.Json.Nodes;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Editor;

namespace NoteEvolution.UI.Tests;

public class EditorDocMapperTests
{
    private static readonly Guid Scope = Guid.Parse("0199a000-0000-7000-8000-000000000001");

    private static readonly Guid BlockKey = Guid.Parse("0199a000-0000-7000-8000-000000000002");

    /// <summary>Everything a snapshot says, as one comparable text.</summary>
    internal static string Describe(SectionSnapshot snapshot) =>
        $"{snapshot.ScopeKey} {snapshot.IncludeSubsections}\n" + string.Join("\n", snapshot.Nodes.Select(node => node switch
        {
            SnapshotHeading h => $"H {h.Key} {h.Level} [{h.Text}]",
            SnapshotTextBlock t =>
                $"T {t.Key} {t.SplitFrom} [{t.Text}] " +
                string.Join(" ", t.Paragraphs.Select(p => $"(P {p.Key} {p.Depth} {p.IsNote} [{p.Text}])")) + " " +
                string.Join(" ", t.Sources.Select(s => $"(S {s.NoteId} {s.IsBroken} [{s.Label}])")),
            _ => "?",
        }));

    private static SectionSnapshot OneBlock(string text, IReadOnlyList<SnapshotParagraph>? paragraphs = null) =>
        new(Scope, false, [new SnapshotTextBlock(BlockKey, null, text, paragraphs ?? [], [])]);

    /// <summary>The first paragraph of the first text block in an editor document.</summary>
    private static JsonObject FirstPara(JsonNode doc) =>
        doc["content"]!.AsArray().First(n => (string?)n!["type"] == "textBlock")!["content"]![0]!.AsObject();

    private static List<string> Inline(JsonObject para) =>
        [.. (para["content"]?.AsArray() ?? []).Select(n => (string)n!["type"]! == "hardBreak"
            ? "<br>"
            : (string)n["text"]! + string.Concat((n["marks"]?.AsArray() ?? []).Select(m => "|" + (string)m!["type"]!)))];

    [Fact]
    public void Mapper_RoundTrip_SpecExample()
    {
        using var tv = TestVault.Create(("pages/Buch - LoveMagic.md", Samples.SpecBook));
        var vault = tv.Open();
        var book = vault.Books.Single();
        var vertrauen = book.Root.Children.Single().Children.Single();

        foreach (var (scope, manuscript) in new[] { (book.Root.Key, true), (vertrauen.Key, false), (vertrauen.Key, true) })
        {
            var snapshot = BookSnapshot.Create(book, vault, scope, manuscript);

            var json = EditorDocMapper.ToJson(snapshot);
            var back = EditorDocMapper.FromJson(json, scope, manuscript);

            Assert.Equal(Describe(snapshot), Describe(back));
        }

        var section = BookSnapshot.Create(book, vault, vertrauen.Key, false);
        var block = (SnapshotTextBlock)section.Nodes.Single();
        Assert.Equal(2, block.Sources.Count);
        Assert.Equal(["Gute Interpretationsvarianten zu sehen ist trainierbar.", "noch ein Beispiel ergänzen"], block.Paragraphs.Select(p => p.Text));
        Assert.True(block.Paragraphs[1].IsNote);
    }

    [Fact]
    public void Mapper_BoldItalicAndHardBreak()
    {
        var doc = JsonNode.Parse(EditorDocMapper.ToJson(OneBlock("**fett** und *kursiv*\nzweite Zeile")))!;
        var para = FirstPara(doc);

        Assert.Equal(["fett|bold", " und ", "kursiv|italic", "<br>", "zweite Zeile"], Inline(para));

        // The editor created this text itself: no Markdown to keep, it is written from the runs.
        para["attrs"]!["md"] = null;
        var back = EditorDocMapper.FromJson(doc.ToJsonString(), Scope, false);

        Assert.Equal("**fett** und *kursiv*\nzweite Zeile", ((SnapshotTextBlock)back.Nodes.Single()).Text);
    }

    [Fact]
    public void Mapper_UnchangedText_KeepsItsMarkdown()
    {
        const string text = "_kursiv_ und 5 * 3 und \\*Stern\\* in snake_case";
        var json = EditorDocMapper.ToJson(OneBlock(text));

        Assert.Equal(["kursiv|italic", " und 5 * 3 und *Stern* in snake_case"], Inline(FirstPara(JsonNode.Parse(json)!)));
        Assert.Equal(text, ((SnapshotTextBlock)EditorDocMapper.FromJson(json, Scope, false).Nodes.Single()).Text);
    }

    [Fact]
    public void Mapper_ChangedText_EscapesTypedMarkersOnlyWhereNeeded()
    {
        var doc = JsonNode.Parse(EditorDocMapper.ToJson(OneBlock("alt")))!;
        var para = FirstPara(doc);
        para["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "a *b* c, snake_case, [[my_page]], 5 * 3" });

        var back = EditorDocMapper.FromJson(doc.ToJsonString(), Scope, false);

        Assert.Equal("a *b\\* c, snake_case, [[my_page]], 5 * 3", ((SnapshotTextBlock)back.Nodes.Single()).Text);
    }

    [Fact]
    public void Mapper_ParagraphsSourcesAndSplitFrom()
    {
        var note = Guid.Parse("0199a000-0000-7000-8000-0000000000aa");
        var child = Guid.Parse("0199a000-0000-7000-8000-000000000003");
        var original = new SectionSnapshot(Scope, false,
        [
            new SnapshotTextBlock(BlockKey, null, "Text", [new SnapshotParagraph(child, "Notiz", 1, true)],
                [new SourceInfo(note, "2026-10-01 – Idee", false)]),
        ]);
        var doc = JsonNode.Parse(EditorDocMapper.ToJson(original))!;
        var block = doc["content"]![0]!;
        Assert.Equal(BlockKey.ToString("D"), (string?)block["attrs"]!["key"]);
        Assert.Equal(note.ToString("D"), (string?)block["attrs"]!["sources"]![0]!["id"]);

        // A part split off in the editor: new key, splitFrom, a sub-bullet too deep.
        var split = (JsonObject)block.DeepClone();
        var newKey = Guid.Parse("0199a000-0000-7000-8000-000000000004");
        split["attrs"]!["key"] = newKey.ToString("D");
        split["attrs"]!["splitFrom"] = BlockKey.ToString("D");
        split["content"]![1]!["attrs"]!["key"] = Guid.Parse("0199a000-0000-7000-8000-000000000005").ToString("D");
        split["content"]![1]!["attrs"]!["depth"] = 4;
        doc["content"]!.AsArray().Add(split);

        var back = EditorDocMapper.FromJson(doc.ToJsonString(), Scope, false);

        var second = (SnapshotTextBlock)back.Nodes[1];
        Assert.Equal(newKey, second.Key);
        Assert.Equal(BlockKey, second.SplitFrom);
        Assert.Equal(1, second.Paragraphs.Single().Depth);
        Assert.True(second.Paragraphs.Single().IsNote);
        Assert.Equal("2026-10-01 – Idee", second.Sources.Single().Label);
        Assert.Equal(Describe(original), Describe(back with { Nodes = [back.Nodes[0]] }));
    }

    [Fact]
    public void Mapper_EmptySection_GivesOneEmptyBlockToTypeIn()
    {
        var json = EditorDocMapper.ToJson(new SectionSnapshot(Scope, false, []));

        var back = EditorDocMapper.FromJson(json, Scope, false);

        var block = (SnapshotTextBlock)back.Nodes.Single();
        Assert.Equal("", block.Text);
        Assert.NotEqual(Guid.Empty, block.Key);
    }
}
