using System.Text;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;
using NoteEvolution.TestSupport;

namespace NoteEvolution.Core.Tests.Format;

public class LogseqParserTests
{
    private static Page Parse(string text) => LogseqParser.Parse("test.md", Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Parse_MixedTabsAndSpaces_BuildsTree()
    {
        var p = Parse("- a\n\t- b\n    - c\n- d");

        Assert.Equal(["a", "d"], p.Roots.Select(b => b.Content));
        Assert.Equal(["b", "c"], p.Roots[0].Children.Select(b => b.Content)); // 4 Leerzeichen = 1 Tab
    }

    [Fact]
    public void Parse_PageAndBlockProperties()
    {
        var p = Parse("title:: T\ntype:: book\n\n- x\n  id:: 6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70\n  collapsed:: true\n  zweite Zeile");

        Assert.Equal("book", p.GetPageProperty("type"));
        var b = p.Roots.Single();
        Assert.Equal("x\nzweite Zeile", b.Content);
        Assert.Equal(Guid.Parse("6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70"), b.Id);
        Assert.Equal("true", b.GetProperty("collapsed"));
    }

    [Fact]
    public void Parse_StarBulletAndHeading()
    {
        var p = Parse("* # Titel");

        var b = p.Roots.Single();
        Assert.Equal('*', b.Bullet);
        Assert.Equal("# Titel", b.Content);
    }

    [Fact]
    public void Parse_CodeFence_LinesInsideAreContent()
    {
        var p = Parse("- a\n  ```\n  - kein Block\n  ```\n- b");

        Assert.Equal(2, p.Roots.Count);
        Assert.Contains("- kein Block", p.Roots[0].Content);
    }

    [Fact]
    public void Parse_UnclosedFence_IsReadOnlyWithLine()
    {
        var p = Parse("- a\n  ```\n  - x");

        Assert.True(p.IsReadOnly);
        Assert.StartsWith("Zeile 2", p.ParseError);
        Assert.Equal("a\n```\n- x", p.Roots.Single().Content);
    }

    [Fact]
    public void Parse_FenceOpenedOnBulletLine_IsContentAndHasNoProperties()
    {
        var p = Parse("- ```js\n  key:: kein Wert\n  - kein Block\n  ```\n- b");

        Assert.False(p.IsReadOnly);
        Assert.Equal(2, p.Roots.Count);
        Assert.Empty(p.Roots[0].Properties);
        Assert.Equal("```js\nkey:: kein Wert\n- kein Block\n```", p.Roots[0].Content);
    }

    [Fact]
    public void Parse_FenceOpenedOnBulletLine_PropertiesFollowClosingFence()
    {
        var p = Parse("- ```js\n  code\n  ```\n  id:: 6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70");

        var b = p.Roots.Single();
        Assert.False(p.IsReadOnly);
        Assert.Equal(Guid.Parse("6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70"), b.Id);
        Assert.Equal("```js\ncode\n```", b.Content);
        Assert.Equal(3, b.PropertySection.Start);
        Assert.Equal(1, b.PropertySection.Count);
        Assert.Equal(4, b.PropertySection.InsertIndex);
    }

    [Fact]
    public void PropertySection_InsertIndex_ForBothLayouts()
    {
        var p = Parse("- a\n  x:: 1\n  y:: 2\n  text\n- b\n  text\n- ```\n  code\n  ```\n  text\n- c");

        Assert.Equal(3, p.Roots[0].PropertySection.InsertIndex); // after the last property line
        Assert.Equal(1, p.Roots[1].PropertySection.InsertIndex); // directly after the bullet line
        Assert.Equal(3, p.Roots[2].PropertySection.InsertIndex); // directly after the closing fence
        Assert.Equal(0, p.Roots[2].PropertySection.Count);
        Assert.Equal(1, p.Roots[3].PropertySection.InsertIndex); // block that is only its bullet line
    }

    [Fact]
    public void Parse_DottedAndSlashedPropertyKeys_AreProperties()
    {
        var p = Parse("- item\n  logseq.order-list-type:: number\n  id:: 6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70");

        var b = p.Roots.Single();
        Assert.Equal(Guid.Parse("6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70"), b.Id);
        Assert.Equal("item", b.Content);
        Assert.Equal(
            [new BlockProperty("logseq.order-list-type", "number"), new BlockProperty("id", "6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70")],
            b.Properties);
        Assert.Equal("nlp", Parse("- q\n  logseq.query/nlp-date:: nlp").Roots[0].GetProperty("logseq.query/nlp-date"));
    }

    [Theory]
    [InlineData("6650a1c21b7e4c1d9a0f2b3c4d5e6f70")]
    [InlineData("{6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70}")]
    [InlineData("(6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70)")]
    public void Parse_IdNotInFormatD_IsNull(string value)
    {
        var p = Parse("- a\n  id:: " + value);

        Assert.Equal(value, p.Roots[0].GetProperty("id"));
        Assert.Null(p.Roots[0].Id);
    }

    [Fact]
    public void Parse_InlineTripleBackticks_DoNotOpenFence()
    {
        var p = Parse("- a\n  ```inline``` code\n- b");

        Assert.False(p.IsReadOnly);
        Assert.Equal(["a\n```inline``` code", "b"], p.Roots.Select(b => b.Content));
    }

    [Fact]
    public void Parse_InvalidUtf8_IsReadOnlyWithLine_AndStillHasBlocks()
    {
        byte[] bytes = [.. Encoding.UTF8.GetBytes("- a\n- b "), 0xC3, 0x28, .. Encoding.UTF8.GetBytes("\n- c\n")];

        var p = LogseqParser.Parse("kaputt.md", bytes);

        Assert.True(p.IsReadOnly);
        Assert.StartsWith("Zeile 2:", p.ParseError);
        Assert.Equal(3, p.Roots.Count);
        Assert.Throws<InvalidOperationException>(() => PageSerializer.Serialize(p));
    }

    [Fact]
    public void Parse_SetsPageMetadata()
    {
        var p = LogseqParser.Parse(Path.Combine("vault", "pages", "Buch - LoveMagic.md"), Encoding.UTF8.GetBytes("- a\r\n- b\r\n- c\n"));

        Assert.Equal(Path.Combine("vault", "pages", "Buch - LoveMagic.md"), p.FilePath);
        Assert.Equal("Buch - LoveMagic", p.Name);
        Assert.Equal("\r\n", p.NewLine);
        Assert.False(p.HasBom);
        Assert.False(p.IsReadOnly);
        Assert.Null(p.ParseError);
        Assert.False(p.IsDirty);
    }

    [Fact]
    public void Parse_PrefixLines_ContainEverythingBeforeFirstBullet()
    {
        var p = Parse("\ntitle:: T\nfreier Text\nalias:: x\n\n- a\n");

        Assert.Equal(["", "title:: T", "freier Text", "alias:: x", ""], p.PrefixLines.Select(l => l.Text));
        Assert.Equal([new BlockProperty("title", "T"), new BlockProperty("alias", "x")], p.PageProperties);
        Assert.Null(p.GetPageProperty("fehlt"));
    }

    [Fact]
    public void Parse_BlankLinesAfterContent_BelongToBlockLines_ButNotToContent()
    {
        var p = Parse("- a\n  weiter\n\n\n- b");

        var a = p.Roots[0];
        Assert.Equal(["- a", "  weiter", "", ""], a.Lines.Select(l => l.Text));
        Assert.Equal("a\nweiter", a.Content);
        Assert.Equal(a.Lines, a.BaseLines);
        Assert.False(a.IsDirty);
    }

    [Fact]
    public void Parse_BlockFields_AreFilled()
    {
        var p = Parse("- a\n\t- b\n\t  more");

        var a = p.Roots[0];
        var b = a.Children.Single();
        Assert.Null(a.Parent);
        Assert.Same(a, b.Parent);
        Assert.Equal('-', b.Bullet);
        Assert.Equal("\t", b.Indent);
        Assert.Equal(1, a.SourceLineNumber);
        Assert.Equal(2, b.SourceLineNumber);
        Assert.Equal("b\nmore", b.Content);
        Assert.NotEqual(Guid.Empty, a.Key);
        Assert.NotEqual(a.Key, b.Key);
        Assert.Null(a.Id);
    }

    [Fact]
    public void Parse_ContinuationStripsIndentAndAtMostTwoSpaces()
    {
        var p = Parse("\t- a\n\t     vier Leerzeichen bleiben drei\n  ohne Indent");

        Assert.Equal("a\n   vier Leerzeichen bleiben drei\nohne Indent", p.Roots.Single().Content);
    }

    [Fact]
    public void Parse_PropertiesOnlyDirectlyAfterBulletLine()
    {
        var p = Parse("- a\n  text\n  key:: wert\n- b\n  leer::\n  x:: y");

        Assert.Empty(p.Roots[0].Properties);
        Assert.Equal("a\ntext\nkey:: wert", p.Roots[0].Content);
        Assert.Equal([new BlockProperty("leer", ""), new BlockProperty("x", "y")], p.Roots[1].Properties);
        Assert.Equal("b", p.Roots[1].Content);
    }

    [Fact]
    public void Parse_InvalidId_IsNull()
    {
        var p = Parse("- a\n  id:: keine-guid");

        Assert.Equal("keine-guid", p.Roots[0].GetProperty("id"));
        Assert.Null(p.Roots[0].Id);
    }

    [Fact]
    public void Parse_StackRule_SiblingWithLessIndentThanPrevious()
    {
        var p = Parse("- a\n    - b\n  - c\n\t\t- d\n- e");

        Assert.Equal(["a", "e"], p.Roots.Select(b => b.Content));
        Assert.Equal(["b", "c"], p.Roots[0].Children.Select(b => b.Content));
        Assert.Equal(["d"], p.Roots[0].Children[1].Children.Select(b => b.Content));
    }

    [Fact]
    public void AllBlocks_IsDepthFirstInFileOrder()
    {
        var p = Parse("- a\n\t- b\n\t\t- c\n\t- d\n- e");

        Assert.Equal(["a", "b", "c", "d", "e"], p.AllBlocks().Select(b => b.Content));
    }

    [Fact]
    public void Parse_SpecBook_HasExpectedStructure()
    {
        var p = Parse(Samples.SpecBook);

        Assert.Equal("book", p.GetPageProperty("type"));
        Assert.Equal("Buch: LoveMagic", p.GetPageProperty("title"));
        var part = p.Roots.Single();
        Assert.Equal("# Liebe und Wahrheit", part.Content);
        Assert.Equal("true", part.GetProperty("collapsed"));
        var chapter = part.Children.Single();
        Assert.Equal("## Vertrauen", chapter.Content);
        var text = chapter.Children.Single();
        Assert.Equal("Angst baut Widerstand auf, Vertrauen baut Schwung auf.", text.Content);
        Assert.Equal(Guid.Parse("6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70"), text.Id);
        Assert.Equal(
            "((7f3a91c2-4b1d-4e8a-9c3f-1a2b3c4d5e6f)), ((81bb02aa-5c2e-4f9b-8d4a-2b3c4d5e6f71))",
            text.GetProperty("source"));
        Assert.Equal(2, text.Children.Count);
        Assert.EndsWith("#notiz", text.Children[1].Content);
    }

    [Fact]
    public void Parse_EmptyFile_HasNothing()
    {
        var p = LogseqParser.Parse("leer.md", []);

        Assert.Empty(p.PrefixLines);
        Assert.Empty(p.Roots);
        Assert.Empty(p.PageProperties);
        Assert.Equal("\n", p.NewLine);
    }
}
