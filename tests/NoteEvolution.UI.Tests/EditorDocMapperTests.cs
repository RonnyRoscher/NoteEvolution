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
        [.. (para["content"]?.AsArray() ?? []).Select(n => (string)n!["type"]! switch
        {
            "hardBreak" => "<br>",
            "blockRef" => $"<ref {(string)n["attrs"]!["id"]!}>",
            _ => (string)n["text"]!,
        } + string.Concat((n["marks"]?.AsArray() ?? []).Select(m => "|" + (string)m!["type"]!)))];

    private static readonly Guid RefA = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");

    private static readonly Guid RefB = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");

    private static readonly string A = BlockRefs.Format(RefA);

    private static readonly string B = BlockRefs.Format(RefB);

    /// <summary>Labels as the pane builds them: A is a heading, B a text block, anything else missing.</summary>
    private static RefLabel Label(Guid id) =>
        id == RefA ? new RefLabel("→ Eins", "Eins", false)
        : id == RefB ? new RefLabel("→ Eins: Erster Text", "Eins › Erster Text", false)
        : new RefLabel("→ Verweis fehlt", "Fehlt", true);

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
    public void ToJson_WritesStatus_TextBlockAndPara()
    {
        var keys = Enumerable.Range(3, 3).Select(i => Guid.Parse($"0199a000-0000-7000-8000-00000000000{i}")).ToArray();
        var snapshot = new SectionSnapshot(Scope, false,
        [
            new SnapshotTextBlock(BlockKey, null, "Text",
                [
                    new SnapshotParagraph(keys[0], "Eins", 1, false, BlockStatus.Relevant),
                    new SnapshotParagraph(keys[1], "Zwei", 1, true, BlockStatus.Remark),
                    new SnapshotParagraph(keys[2], "Drei", 2, false, BlockStatus.Done),
                ],
                [], BlockStatus.Wording),
        ]);

        var block = JsonNode.Parse(EditorDocMapper.ToJson(snapshot))!["content"]![0]!;

        Assert.Equal("formulierung", (string?)block["attrs"]!["status"]);
        var paras = block["content"]!.AsArray();
        // The block's own text carries no status of its own; the text block has it.
        Assert.False(paras[0]!["attrs"]!.AsObject().ContainsKey("status"));
        Assert.Equal(["relevant", "bemerkung", "fertig"], paras.Skip(1).Select(p => (string?)p!["attrs"]!["status"]));
    }

    [Fact]
    public void ToJson_Draft_NoStatusAttr()
    {
        var child = Guid.Parse("0199a000-0000-7000-8000-000000000003");

        var block = JsonNode.Parse(EditorDocMapper.ToJson(OneBlock("Text", [new SnapshotParagraph(child, "Absatz", 1, false)])))!
            ["content"]![0]!;

        Assert.False(block["attrs"]!.AsObject().ContainsKey("status"));
        Assert.All(block["content"]!.AsArray(), para => Assert.False(para!["attrs"]!.AsObject().ContainsKey("status")));
    }

    [Fact]
    public void FromJson_IgnoresStatus_IsNoteRoundTrips()
    {
        var child = Guid.Parse("0199a000-0000-7000-8000-000000000003");
        var other = Guid.Parse("0199a000-0000-7000-8000-000000000004");
        var snapshot = new SectionSnapshot(Scope, false,
        [
            new SnapshotTextBlock(BlockKey, null, "Text",
                [new SnapshotParagraph(child, "Notiz", 1, true, BlockStatus.Remark), new SnapshotParagraph(other, "Fertig", 1, false, BlockStatus.Done)],
                [], BlockStatus.Relevant),
        ]);

        var back = EditorDocMapper.FromJson(EditorDocMapper.ToJson(snapshot), Scope, false);

        var block = (SnapshotTextBlock)back.Nodes.Single();
        Assert.Equal(BlockStatus.Draft, block.Status);
        Assert.Equal([BlockStatus.Draft, BlockStatus.Draft], block.Paragraphs.Select(p => p.Status));
        Assert.Equal([true, false], block.Paragraphs.Select(p => p.IsNote));
        Assert.Equal(Describe(snapshot), Describe(back));
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

    [Fact]
    public void ToJson_SetsDatesOnTextBlocks_NotOnHeadings()
    {
        var heading = Guid.Parse("0199a000-0000-7000-8000-000000000003");
        var other = Guid.Parse("0199a000-0000-7000-8000-000000000004");
        var snapshot = new SectionSnapshot(Scope, true,
        [
            new SnapshotHeading(heading, 1, "Kapitel"),
            new SnapshotTextBlock(BlockKey, null, "Text", [], []),
            new SnapshotTextBlock(other, null, "Ohne Datum", [], []),
        ]);
        var label = new BlockDateLabel("geändert 19.10.2026 14:32", "Geändert: 19.10.2026 14:32");

        var doc = JsonNode.Parse(EditorDocMapper.ToJson(snapshot, key => key == BlockKey || key == heading ? label : null))!;

        var nodes = doc["content"]!.AsArray();
        Assert.False(nodes[0]!["attrs"]!.AsObject().ContainsKey("dates"));
        Assert.Equal("geändert 19.10.2026 14:32", (string?)nodes[1]!["attrs"]!["dates"]);
        Assert.Equal("Geändert: 19.10.2026 14:32", (string?)nodes[1]!["attrs"]!["datesTitle"]);
        Assert.False(nodes[2]!["attrs"]!.AsObject().ContainsKey("dates"));
        Assert.False(nodes[2]!["attrs"]!.AsObject().ContainsKey("datesTitle"));
    }

    public static TheoryData<string> RefTexts() => new()
    {
        $"Siehe {A} hier",
        $"{A} am Anfang",
        $"am Ende {A}",
        $"{A}{B}",
        $"zwei {A} {B} nacheinander",
        $"**fett {A}** und *kursiv {B}*",
        $"_kursiv {A}_ und snake_case",
        $"**{A}**",
        $"Zeile {A}\nzweite {B} Zeile",
        // Case-insensitive on read: an upper-case id is kept as it is in the file.
        $"groß (({RefA.ToString("D").ToUpperInvariant()})) bleibt",
        // A missing target, and something that is no reference.
        $"fehlt {BlockRefs.Format(Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"))} und ((keine)) Referenz",
    };

    [Theory]
    [MemberData(nameof(RefTexts))]
    public void Mapper_UnchangedTextWithReferences_KeepsItsMarkdown(string text)
    {
        var detail = Guid.Parse("0199a000-0000-7000-8000-000000000003");
        var snapshot = OneBlock(text, [new SnapshotParagraph(detail, text, 1, false)]);
        var json = EditorDocMapper.ToJson(snapshot, refOf: Label);

        var back = (SnapshotTextBlock)EditorDocMapper.FromJson(json, Scope, false).Nodes.Single();

        Assert.Equal(text, back.Text);
        Assert.Equal(text, back.Paragraphs.Single().Text);
    }

    [Fact]
    public void ToJson_ReferenceIsANodeWithLabelTitleAndMarks()
    {
        var missing = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
        var doc = JsonNode.Parse(EditorDocMapper.ToJson(
            OneBlock($"Siehe **{A}** und {B}{BlockRefs.Format(missing)}, nicht ((keine))"), refOf: Label))!;
        var para = FirstPara(doc);

        Assert.Equal(
            ["Siehe ", $"<ref {RefA}>|bold", " und ", $"<ref {RefB}>", $"<ref {missing}>", ", nicht ((keine))"],
            Inline(para));
        var nodes = para["content"]!.AsArray();
        Assert.Equal("→ Eins", (string?)nodes[1]!["attrs"]!["label"]);
        Assert.Equal("Eins", (string?)nodes[1]!["attrs"]!["title"]);
        Assert.False((bool)nodes[1]!["attrs"]!["missing"]!);
        Assert.Equal("→ Verweis fehlt", (string?)nodes[4]!["attrs"]!["label"]);
        Assert.True((bool)nodes[4]!["attrs"]!["missing"]!);
    }

    [Fact]
    public void ToJson_ReferenceWithoutLabel_IsPlainText_KeepsItsMarkdown()
    {
        // No label: a block outside the book (a note), no cross reference; it stays text and joins the text around it.
        var note = Guid.Parse("0190a000-0000-7000-8000-0000000000aa");
        var text = $"Siehe (({note.ToString("D").ToUpperInvariant()})) und {A}";
        RefLabel? LabelOrNone(Guid id) => id == note ? null : Label(id);
        var json = EditorDocMapper.ToJson(OneBlock(text), refOf: LabelOrNone);

        Assert.Equal([$"Siehe {BlockRefs.Format(note)} und ", $"<ref {RefA}>"], Inline(FirstPara(JsonNode.Parse(json)!)));
        var back = (SnapshotTextBlock)EditorDocMapper.FromJson(json, Scope, false).Nodes.Single();
        Assert.Equal(text, back.Text);
    }

    [Fact]
    public void FromJson_ChangedLabels_SameSnapshot()
    {
        var snapshot = OneBlock($"Siehe {A} und **{B}**");
        var doc = JsonNode.Parse(EditorDocMapper.ToJson(snapshot, refOf: Label))!;
        var changed = (JsonObject)doc.DeepClone();
        foreach (var node in FirstPara(changed)["content"]!.AsArray().Where(n => (string?)n!["type"] == "blockRef"))
        {
            node!["attrs"]!["label"] = "→ anders";
            node["attrs"]!["title"] = "anders";
            node["attrs"]!["missing"] = true;
        }

        Assert.Equal(Describe(snapshot), Describe(EditorDocMapper.FromJson(changed.ToJsonString(), Scope, false)));
    }

    [Fact]
    public void FromJson_ChangedTextWithReferences_WritesThemBack()
    {
        var doc = JsonNode.Parse(EditorDocMapper.ToJson(OneBlock("alt"), refOf: Label))!;
        var para = FirstPara(doc);
        para["content"] = new JsonArray(
            new JsonObject { ["type"] = "blockRef", ["attrs"] = new JsonObject { ["id"] = RefA.ToString("D"), ["label"] = "→ Eins" } },
            new JsonObject { ["type"] = "text", ["text"] = " neu *x* " },
            new JsonObject
            {
                ["type"] = "blockRef",
                ["attrs"] = new JsonObject { ["id"] = RefB.ToString("D") },
                ["marks"] = new JsonArray(new JsonObject { ["type"] = "bold" }),
            },
            new JsonObject { ["type"] = "blockRef", ["attrs"] = new JsonObject { ["id"] = "keine-id" } });

        var back = (SnapshotTextBlock)EditorDocMapper.FromJson(doc.ToJsonString(), Scope, false).Nodes.Single();

        // A node without a valid id is dropped.
        Assert.Equal($"{A} neu *x\\* **{B}**", back.Text);
    }

    [Fact]
    public void ToJson_WithoutLabels_StillGivesTheNode()
    {
        var para = FirstPara(JsonNode.Parse(EditorDocMapper.ToJson(OneBlock($"x {A}")))!);

        Assert.Equal(["x ", $"<ref {RefA}>"], Inline(para));
    }

    [Fact]
    public void FromJson_IgnoresDates()
    {
        var label = new BlockDateLabel("erstellt 12.10.2026", "Erstellt: 12.10.2026 09:15");
        var snapshot = OneBlock("Text", [new SnapshotParagraph(Guid.Parse("0199a000-0000-7000-8000-000000000003"), "Absatz", 1, false)]);
        var doc = JsonNode.Parse(EditorDocMapper.ToJson(snapshot, _ => label))!;
        var changed = (JsonObject)doc.DeepClone();
        changed["content"]![0]!["attrs"]!["dates"] = "geändert 01.01.2030 00:00";
        changed["content"]![0]!["attrs"]!["datesTitle"] = "anders";

        var back = EditorDocMapper.FromJson(doc.ToJsonString(), Scope, false);
        var backChanged = EditorDocMapper.FromJson(changed.ToJsonString(), Scope, false);

        Assert.Equal(Describe(snapshot), Describe(back));
        Assert.Equal(Describe(back), Describe(backChanged));
        Assert.Equal(((SnapshotTextBlock)back.Nodes.Single()) with { Paragraphs = [], Sources = [] },
            ((SnapshotTextBlock)backChanged.Nodes.Single()) with { Paragraphs = [], Sources = [] });
    }
}
