using System.Text;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;
using NoteEvolution.TestSupport;

namespace NoteEvolution.Core.Tests.Model;

public class BlockEditTests
{
    private const string IdA = "6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70";
    private const string IdB = "6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f71";

    private static Page Parse(string text) => LogseqParser.Parse("test.md", Encoding.UTF8.GetBytes(text));

    private static string Text(Page page) => Encoding.UTF8.GetString(PageSerializer.Serialize(page));

    private static void AssertDiff(string before, Page after, int[] removed, int[] added)
    {
        var diff = LineDiff.Changed(Encoding.UTF8.GetBytes(before), PageSerializer.Serialize(after));
        Assert.Equal(removed, diff.Removed);
        Assert.Equal(added, diff.Added);
    }

    // ---- SetProperty / RemoveProperty ----

    [Fact]
    public void SetProperty_New_AddsExactlyOneLineAfterLastProperty()
    {
        const string before = "- a\n  collapsed:: true\n  rest\n\t- b";
        var p = Parse(before);

        p.Roots[0].SetProperty("used-in", "[[Buch - X]]");

        Assert.Equal("- a\n  collapsed:: true\n  used-in:: [[Buch - X]]\n  rest\n\t- b", Text(p));
        AssertDiff(before, p, [], [2]);
        Assert.True(p.Roots[0].IsDirty);
        Assert.False(p.Roots[0].Children[0].IsDirty);
        Assert.Equal("[[Buch - X]]", p.Roots[0].GetProperty("used-in"));
    }

    [Fact]
    public void SetProperty_Existing_ReplacesInPlace()
    {
        const string before = "- a\n  used-in:: [[Alt]]\n  collapsed:: true\n- b";
        var p = Parse(before);

        p.Roots[0].SetProperty("used-in", "[[Neu]]");

        Assert.Equal("- a\n  used-in:: [[Neu]]\n  collapsed:: true\n- b", Text(p));
        AssertDiff(before, p, [1], [1]);
    }

    [Fact]
    public void SetProperty_NoProperties_InsertsDirectlyAfterBulletLine_WithBlockIndent()
    {
        var p = Parse("- r\n\t- a\n\t  text");

        p.Roots[0].Children[0].SetProperty("id", IdA);

        Assert.Equal($"- r\n\t- a\n\t  id:: {IdA}\n\t  text", Text(p));
        Assert.Equal(Guid.Parse(IdA), p.Roots[0].Children[0].Id);
        Assert.Equal("a\ntext", p.Roots[0].Children[0].Content);
    }

    [Fact]
    public void SetProperty_BulletLineOpensFence_InsertsAfterClosingFence()
    {
        var p = Parse("- ```js\n  - kein: Block\n  ```\n  mehr");

        p.Roots[0].SetProperty("id", IdA);

        Assert.Equal($"- ```js\n  - kein: Block\n  ```\n  id:: {IdA}\n  mehr", Text(p));
        Assert.Equal(Guid.Parse(IdA), p.Roots[0].Id);
    }

    [Fact]
    public void SetProperty_SameValue_ChangesNothing()
    {
        var p = Parse("- a\n  collapsed:: true\n");

        p.Roots[0].SetProperty("collapsed", "true");

        Assert.False(p.Roots[0].IsDirty);
        Assert.False(p.IsDirty);
    }

    [Fact]
    public void SetProperty_LastLineWithoutEnding_GetsPageNewLine()
    {
        var p = Parse("- a\r\n- b");

        p.Roots[1].SetProperty("id", IdA);

        Assert.Equal($"- a\r\n- b\r\n  id:: {IdA}\r\n", Text(p));
    }

    [Theory]
    [InlineData("bad key", "x")]
    [InlineData("key", "zwei\nZeilen")]
    public void SetProperty_InvalidKeyOrValue_Throws(string key, string value)
    {
        var p = Parse("- a");

        Assert.Throws<ArgumentException>(() => p.Roots[0].SetProperty(key, value));
    }

    [Fact]
    public void RemoveProperty_RemovesOnlyThatLine_CaseInsensitive()
    {
        const string before = "- a\n  id:: " + IdA + "\n  collapsed:: true\n  text\n";
        var p = Parse(before);

        p.Roots[0].RemoveProperty("Collapsed");

        Assert.Equal($"- a\n  id:: {IdA}\n  text\n", Text(p));
        AssertDiff(before, p, [2], []);
        Assert.True(p.Roots[0].IsDirty);
    }

    [Fact]
    public void RemoveProperty_Missing_ChangesNothing()
    {
        var p = Parse("- a\n  text");

        p.Roots[0].RemoveProperty("id");

        Assert.False(p.IsDirty);
    }

    // ---- SetContent ----

    [Fact]
    public void SetContent_OnlyDifferingLinesChange()
    {
        var before = $"- r\n\t- a\n\t  id:: {IdA}\n\t  zwei\n\t  drei\n";
        var p = Parse(before);
        var block = p.Roots[0].Children[0];

        block.SetContent("a2\nzwei\ndrei neu");

        Assert.Equal($"- r\n\t- a2\n\t  id:: {IdA}\n\t  zwei\n\t  drei neu\n", Text(p));
        AssertDiff(before, p, [1, 4], [1, 4]);
        Assert.Equal("a2\nzwei\ndrei neu", block.Content);
        Assert.True(block.IsDirty);
    }

    [Fact]
    public void SetContent_UnchangedContinuation_KeepsItsOwnPrefix()
    {
        const string before = "- a\n zwei\n";
        var p = Parse(before);

        p.Roots[0].SetContent("eins\nzwei");

        Assert.Equal("- eins\n zwei\n", Text(p));
        AssertDiff(before, p, [0], [0]);
    }

    [Fact]
    public void SetContent_MoreLines_InsertedAfterLastContentLine_BeforeTrailingBlankLines()
    {
        var p = Parse($"- a\n  id:: {IdA}\n  zwei\n\n- b");

        p.Roots[0].SetContent("a\nzwei\n\ndrei");

        Assert.Equal($"- a\n  id:: {IdA}\n  zwei\n\n  drei\n\n- b", Text(p));
    }

    [Fact]
    public void SetContent_NoContinuation_NewLinesGoAfterProperties()
    {
        var p = Parse($"- a\n  id:: {IdA}\n- b");

        p.Roots[0].SetContent("a\nzwei");

        Assert.Equal($"- a\n  id:: {IdA}\n  zwei\n- b", Text(p));
    }

    [Fact]
    public void SetContent_FewerLines_RemovesSurplusContinuations()
    {
        var p = Parse("- a\n  zwei\n  drei\n\n- b");

        p.Roots[0].SetContent("a");

        Assert.Equal("- a\n\n- b", Text(p));
    }

    [Fact]
    public void SetContent_Unchanged_ChangesNothing()
    {
        var p = Parse("* a\n  zwei");

        p.Roots[0].SetContent("a\nzwei");

        Assert.False(p.IsDirty);
    }

    [Theory]
    [InlineData("- ```js\n  a\n  ```\n  id:: " + IdA + "\n  mehr", "```js\na\nb\n```\nmehr",
        "- ```js\n  a\n  b\n  ```\n  id:: " + IdA + "\n  mehr")]
    [InlineData("- a\n  id:: " + IdA + "\n  text", "```js\ntext\n```",
        "- ```js\n  text\n  ```\n  id:: " + IdA + "\n")]
    [InlineData("- ```js\n  a\n  ```\n  id:: " + IdA, "plain\na",
        "- plain\n  id:: " + IdA + "\n  a\n")]
    public void SetContent_FenceStateChanges_PropertiesStayInPropertyPosition(string before, string content, string expected)
    {
        var p = Parse(before);
        var properties = p.Roots[0].Properties.ToList();

        p.Roots[0].SetContent(content);

        Assert.Equal(expected, Text(p));
        Assert.Equal(properties, p.Roots[0].Properties);
        Assert.Equal(content, p.Roots[0].Content);
        var reparsed = Parse(Text(p)).Roots.Single();
        Assert.Equal(properties, reparsed.Properties);
        Assert.Equal(content, reparsed.Content);
        Assert.Equal(Guid.Parse(IdA), reparsed.Id);
    }

    [Theory]
    [InlineData("- a\n- b", "a\n- x\nmore")]
    [InlineData("- a\n- b\n  ```\n  c\n  ```", "a\n```")]
    [InlineData("- a\n- b", "```js\ncode")]
    [InlineData("- a\n- b", "a\r\n-\r\nb")]
    [InlineData("- a\n- b", "a\r  * b")]
    public void SetContent_TextThatWouldChangeTheTree_ThrowsAndChangesNothing(string before, string content)
    {
        var p = Parse(before);

        Assert.Throws<ArgumentException>(() => Block.ValidateContent(content));
        Assert.Throws<ArgumentException>(() => p.Roots[0].SetContent(content));

        Assert.Equal(before, Text(p));
        Assert.False(p.IsDirty);
    }

    [Theory]
    [InlineData("a\n\\- x\n\\```")]
    [InlineData("a\n```\n- im Zaun\n```")]
    [InlineData("a\n\n  b")]
    public void ValidateContent_TextKeepingTheTree_DoesNotThrow(string content)
    {
        Block.ValidateContent(content);
        Block.CreateDetached(content);
    }

    [Fact]
    public void SetContent_BulletLikeLineInsideFence_IsAllowed()
    {
        var p = Parse("- a\n- b\n");

        p.Roots[0].SetContent("a\n```\n- kein Block\n```");

        Assert.Equal("- a\n  ```\n  - kein Block\n  ```\n- b\n", Text(p));
        Assert.Equal(2, Parse(Text(p)).Roots.Count);
    }

    // ---- EnsureId / CreateDetached / CloneDetached ----

    [Fact]
    public void EnsureId_ReturnsExisting_OrAddsLowercaseUuid()
    {
        var p = Parse($"- a\n  id:: {IdA}\n- b\n");

        Assert.Equal(Guid.Parse(IdA), p.Roots[0].EnsureId());
        Assert.False(p.Roots[0].IsDirty);

        var id = p.Roots[1].EnsureId();

        var value = p.Roots[1].GetProperty("id");
        Assert.Matches("^[0-9a-f-]{36}$", value);
        Assert.Equal(id.ToString("D"), value);
        Assert.Equal(id, p.Roots[1].EnsureId());
        Assert.Equal($"- a\n  id:: {IdA}\n- b\n  id:: {value}\n", Text(p));
    }

    [Fact]
    public void CreateDetached_BuildsBulletPropertiesAndContinuations()
    {
        var block = Block.CreateDetached("x\nzwei", [new BlockProperty("source", $"(({IdA}))")]);

        Assert.Equal(["- x", $"  source:: (({IdA}))", "  zwei"], block.Lines.Select(l => l.Text));
        Assert.Equal("x\nzwei", block.Content);
        Assert.Equal($"(({IdA}))", block.GetProperty("source"));
        Assert.Empty(block.BaseLines);
        Assert.Null(block.Parent);
    }

    [Fact]
    public void CloneDetached_WithoutProperties_CopiesChildrenContentOnly()
    {
        var p = Parse($"- a\n  id:: {IdA}\n  collapsed:: true\n  text\n\n\t- b\n\t  id:: {IdB}\n\t\t- c\n- d\n");
        var original = p.Roots[0];

        var clone = original.CloneDetached(withoutProperties: true);

        Assert.Null(clone.Parent);
        Assert.NotEqual(original.Key, clone.Key);
        Assert.Equal(["- a", "  text"], clone.Lines.Select(l => l.Text));
        Assert.Empty(clone.Properties);
        var b = Assert.Single(clone.Children);
        Assert.Same(clone, b.Parent);
        Assert.Equal(["\t- b"], b.Lines.Select(l => l.Text));
        Assert.Equal(["\t\t- c"], Assert.Single(b.Children).Lines.Select(l => l.Text));
        Assert.Empty(clone.BaseLines);
        Assert.False(p.IsDirty);
        Assert.Equal(2, original.Properties.Count);
    }

    [Fact]
    public void CloneDetached_WithProperties_KeepsThem()
    {
        var p = Parse($"- a\n  id:: {IdA}\n  text\n");

        var clone = p.Roots[0].CloneDetached(withoutProperties: false);

        Assert.Equal(["- a", $"  id:: {IdA}", "  text"], clone.Lines.Select(l => l.Text));
    }

    // ---- InsertBlock ----

    [Fact]
    public void InsertBlock_IntoTabIndentedParent_UsesTabs_AndCrlf()
    {
        var p = Parse("- a\r\n\t- b\r\n");

        p.InsertBlock(p.Roots[0].Children[0], 0, Block.CreateDetached("neu"));

        Assert.Equal("- a\r\n\t- b\r\n\t\t- neu\r\n", Text(p));
        var inserted = p.Roots[0].Children[0].Children.Single();
        Assert.True(inserted.IsDirty);
        Assert.Empty(inserted.BaseLines);
        Assert.Same(p.Roots[0].Children[0], inserted.Parent);
    }

    [Fact]
    public void InsertBlock_IntoSpaceIndentedParent_UsesSpaces()
    {
        var p = Parse("- a\n  - b");

        p.InsertBlock(p.Roots[0].Children[0], 0, Block.CreateDetached("neu"));

        Assert.Equal("- a\n  - b\n    - neu\n", Text(p));
    }

    [Fact]
    public void InsertBlock_AfterLastLineWithoutEnding_AddsEnding()
    {
        const string before = "- a\r\n- b";
        var p = Parse(before);

        p.InsertBlock(null, 2, Block.CreateDetached("neu"));

        Assert.Equal("- a\r\n- b\r\n- neu\r\n", Text(p));
        AssertDiff(before, p, [1], [1, 2]);
    }

    [Fact]
    public void InsertBlock_ParentWithChildren_TakesFirstChildIndentAndNeighbourBullet()
    {
        var p = Parse("- a\n   * b\n");

        p.InsertBlock(p.Roots[0], 1, Block.CreateDetached("x"));

        Assert.Equal("- a\n   * b\n   * x\n", Text(p));
    }

    [Fact]
    public void InsertBlock_ChildlessRootParent_UsesTabUnit_AndParentBullet()
    {
        var p = Parse("* a\n");

        p.InsertBlock(p.Roots[0], 0, Block.CreateDetached("x"));

        Assert.Equal("* a\n\t* x\n", Text(p));
    }

    [Fact]
    public void InsertBlock_KeepsBlankLinesWithPreviousBlock()
    {
        const string before = "- a\n\n- b\n";
        var p = Parse(before);

        p.InsertBlock(null, 1, Block.CreateDetached("x"));

        Assert.Equal("- a\n\n- x\n- b\n", Text(p));
        AssertDiff(before, p, [], [2]);
    }

    [Fact]
    public void InsertBlock_ClonedSubtree_RendersInTargetStyle()
    {
        var note = Parse("- Kern\n  collapsed:: true\n  - Detail\n    mehr\n");
        var book = Parse("- B\n\t- Abschnitt\n");
        var section = book.Roots[0].Children[0];

        book.InsertBlock(section, 0, note.Roots[0].CloneDetached(withoutProperties: true));
        var copy = section.Children.Single();
        copy.SetProperty("id", IdA);
        copy.SetProperty("source", $"(({IdB}))");

        Assert.Equal(
            $"- B\n\t- Abschnitt\n\t\t- Kern\n\t\t  id:: {IdA}\n\t\t  source:: (({IdB}))\n\t\t\t- Detail\n\t\t\t  mehr\n",
            Text(book));
        Assert.Equal("- Kern\n  collapsed:: true\n  - Detail\n    mehr\n", Text(note));
        Assert.True(copy.Children.Single().IsDirty);
    }

    [Fact]
    public void InsertBlock_BlockAlreadyInPage_Throws()
    {
        var p = Parse("- a\n- b\n");

        Assert.Throws<ArgumentException>(() => p.InsertBlock(null, 0, p.Roots[1]));
    }

    // ---- RemoveBlock ----

    [Fact]
    public void RemoveBlock_RemovesSubtree_PageIsDirty()
    {
        var p = Parse("- a\n\t- b\n\t  text\n\t\t- c\n- d\n");
        var b = p.Roots[0].Children[0];

        p.RemoveBlock(b);

        Assert.Equal("- a\n- d\n", Text(p));
        Assert.Null(b.Parent);
        Assert.Empty(p.Roots[0].Children);
        Assert.True(p.IsDirty);
        Assert.Equal(["a", "d"], p.AllBlocks().Select(x => x.Content));
    }

    [Fact]
    public void RemovedBlock_CanBeInsertedAgain()
    {
        var p = Parse("- a\n- b\n");
        var b = p.Roots[1];

        p.RemoveBlock(b);
        p.InsertBlock(null, 0, b);

        Assert.Equal("- b\n- a\n", Text(p));
    }

    [Fact]
    public void RestoreBlock_PutsRemovedBlockBackByteIdentical()
    {
        // Odd indentation, a `*` bullet, mixed endings and a trailing blank line that InsertBlock would re-render.
        const string before = "- a\n   * b\r\n     text b\n\n\t\t- c\n   * d\n";
        var p = Parse(before);
        var b = p.Roots[0].Children[0];

        p.RemoveBlock(b);
        p.RestoreBlock(p.Roots[0], 0, b);

        Assert.Equal(before, Text(p));
        Assert.Same(p.Roots[0], b.Parent);
        Assert.True(p.IsDirty);
        Assert.Equal(["a", "b\ntext b", "c", "d"], p.AllBlocks().Select(x => x.Content));
    }

    [Fact]
    public void RestoreBlock_LastBlockWithoutFinalNewline_StaysWithout()
    {
        const string before = "- a\n- b";
        var p = Parse(before);
        var b = p.Roots[1];

        p.RemoveBlock(b);
        p.RestoreBlock(null, 5, b);

        Assert.Equal(before, Text(p));
    }

    [Fact]
    public void RestoreLines_PutsBackLinesAndDirtyFlag()
    {
        var p = Parse("- a\n  collapsed:: true\n");
        var a = p.Roots[0];
        var lines = a.Lines.ToList();

        a.EnsureId();
        a.RestoreLines(lines, isDirty: false);

        Assert.Null(a.Id);
        Assert.Equal("- a\n  collapsed:: true\n", Text(p));
        Assert.False(p.IsDirty);
    }

    // ---- MoveBlock ----

    [Fact]
    public void MoveBlock_ReindentsSubtree_OtherLinesUnchanged()
    {
        var before = $"- a\n  text a\n\t- b\n\t  id:: {IdA}\n\t  text b\n\t\t- c\n- d\n";
        var p = Parse(before);
        var b = p.Roots[0].Children[0];

        p.MoveBlock(b, null, 2);

        Assert.Equal($"- a\n  text a\n- d\n- b\n  id:: {IdA}\n  text b\n\t- c\n", Text(p));
        AssertDiff(before, p, [2, 3, 4, 5], [3, 4, 5, 6]);
        Assert.Null(b.Parent);
        Assert.Same(b, p.Roots[2]);
        Assert.Equal(Guid.Parse(IdA), b.Id);
        Assert.True(b.IsDirty);
        Assert.True(b.Children[0].IsDirty);
        Assert.False(p.Roots[0].IsDirty);
        Assert.False(p.Roots[1].IsDirty);
    }

    [Fact]
    public void MoveBlock_UnderParentWithChildren_TakesFirstChildIndent()
    {
        var p = Parse("- a\n\t- b\n\t\t- c\n- d\n  - e\n");

        p.MoveBlock(p.Roots[0].Children[0], p.Roots[1], 1);

        Assert.Equal("- a\n- d\n  - e\n  - b\n  \t- c\n", Text(p));
        var reparsed = Parse(Text(p));
        Assert.Equal(["e", "b"], reparsed.Roots[1].Children.Select(c => c.Content));
        Assert.Equal("c", reparsed.Roots[1].Children[1].Children.Single().Content);
    }

    [Fact]
    public void MoveBlock_LastBlockWithoutEnding_GetsEnding()
    {
        var p = Parse("- a\n- b");

        p.MoveBlock(p.Roots[1], null, 0);

        Assert.Equal("- b\n- a\n", Text(p));
    }

    [Fact]
    public void MoveBlock_IntoOwnSubtree_Throws()
    {
        var p = Parse("- a\n\t- b\n");

        Assert.Throws<ArgumentException>(() => p.MoveBlock(p.Roots[0], p.Roots[0].Children[0], 0));
    }

    // ---- Page properties / MarkSaved ----

    [Fact]
    public void SetPageProperty_NoProperties_InsertsFirstLineAndBlankLine()
    {
        var p = Parse("- a");

        p.SetPageProperty("type", "book");

        Assert.Equal("type:: book\n\n- a", Text(p));
        Assert.Equal("book", p.GetPageProperty("type"));
        Assert.True(p.IsDirty);
    }

    [Fact]
    public void SetPageProperty_New_GoesAfterLastPageProperty()
    {
        var p = Parse("title:: T\nalias:: x\n\n- a\n");
        Assert.Null(p.GetPageProperty("type"));

        p.SetPageProperty("type", "book");

        Assert.Equal("title:: T\nalias:: x\ntype:: book\n\n- a\n", Text(p));
        Assert.Equal("book", p.GetPageProperty("type"));
    }

    [Fact]
    public void SetPageProperty_Existing_ReplacesInPlace()
    {
        var p = Parse("type:: note\r\nalias:: x\r\n\r\n- a\r\n");
        Assert.Equal("note", p.GetPageProperty("type"));

        p.SetPageProperty("type", "book");

        Assert.Equal("type:: book\r\nalias:: x\r\n\r\n- a\r\n", Text(p));
        Assert.Equal("book", p.GetPageProperty("type"));
    }

    [Fact]
    public void MarkSaved_SetsBaseLinesAndClearsDirty()
    {
        var p = Parse("- a\n- b\n");
        p.Roots[0].SetContent("a2");
        p.RemoveBlock(p.Roots[1]);
        p.InsertBlock(null, 1, Block.CreateDetached("c"));

        p.MarkSaved();

        Assert.False(p.IsDirty);
        Assert.All(p.AllBlocks(), b => Assert.Equal(b.Lines, b.BaseLines));
        Assert.All(p.AllBlocks(), b => Assert.False(b.IsDirty));
    }

    // ---- IndentStyle ----

    [Fact]
    public void IndentStyle_ChildIndent_FollowsRules()
    {
        var p = Parse("- a\n  - b\n- c\n\t- d\n");
        var a = p.Roots[0];
        var b = a.Children[0];

        Assert.Equal("", IndentStyle.ChildIndent(null));
        Assert.Equal("  ", IndentStyle.ChildIndent(a));       // first child's indent
        Assert.Equal("    ", IndentStyle.ChildIndent(b));     // b.Indent + ("  " minus "")
        Assert.Equal("\t\t", IndentStyle.ChildIndent(p.Roots[1].Children[0]));
        Assert.Equal("\t", IndentStyle.ChildIndent(Parse("- x").Roots[0]));
    }

    [Fact]
    public void IndentStyle_BulletFor_NeighbourThenParentThenDash()
    {
        var p = Parse("* a\n\t- b\n- c\n");

        Assert.Equal('-', IndentStyle.BulletFor(p.Roots[0], 0)); // next neighbour b
        Assert.Equal('-', IndentStyle.BulletFor(p.Roots[0], 1)); // previous neighbour b
        Assert.Equal('*', IndentStyle.BulletFor(Parse("* x").Roots[0], 0)); // parent
        Assert.Equal('-', IndentStyle.BulletFor(null, 0));
    }
}
