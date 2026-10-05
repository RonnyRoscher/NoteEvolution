using System.Text;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Storage;

namespace NoteEvolution.Core.Tests.Storage;

public class ConflictTests
{
    private const string IdA = "6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70";

    private const string Base =
        "- eins\n" +
        "- zwei\n" +
        "\t- zwei-a\n" +
        "- drei\n";

    private static Page Parse(string text) => LogseqParser.Parse("Seite.md", Encoding.UTF8.GetBytes(text));

    private static string Text(Page page) => Encoding.UTF8.GetString(PageSerializer.Serialize(page));

    private static Block B(Page page, string content) => page.AllBlocks().Single(b => b.Content == content);

    private static Dictionary<Guid, ConflictChoice> Choose(IEnumerable<BlockConflict> conflicts, ConflictChoice choice) =>
        conflicts.ToDictionary(c => c.Local.Key, _ => choice);

    [Fact]
    public void Detect_LocalDirtyAndExternalChangedSameBlock_IsConflict()
    {
        var local = Parse(Base);
        var zwei = B(local, "zwei");
        zwei.SetContent("zwei lokal");
        var external = Parse(Base.Replace("- zwei\n", "- zwei extern\n"));

        var conflict = Assert.Single(ConflictDetector.Detect(local, external));

        Assert.Same(zwei, conflict.Local);
        Assert.Same(B(external, "zwei extern"), conflict.External);
        Assert.False(conflict.LocalRemoved);
    }

    [Fact]
    public void Detect_DifferentBlocksChanged_NoConflict_MergeKeepsBoth()
    {
        var local = Parse(Base);
        var eins = B(local, "eins");
        eins.SetContent("eins lokal");
        var external = Parse(Base.Replace("- drei\n", "- drei extern\n"));

        Assert.Empty(ConflictDetector.Detect(local, external));
        var merged = PageMerger.Merge(local, external, new Dictionary<Guid, ConflictChoice>());

        Assert.Equal("- eins lokal\n- zwei\n\t- zwei-a\n- drei extern\n", Text(merged));
        Assert.True(merged.IsDirty);
        Assert.Equal(eins.Key, B(merged, "eins lokal").Key);
        Assert.Equal(B(local, "zwei-a").Key, B(merged, "zwei-a").Key);
    }

    [Fact]
    public void Merge_Both_InsertsExternalAfterLocal()
    {
        var local = Parse(Base);
        B(local, "zwei").SetContent("zwei lokal");
        var external = Parse(Base.Replace("- zwei\n", "- zwei extern\n"));
        var conflicts = ConflictDetector.Detect(local, external);

        var merged = PageMerger.Merge(local, external, Choose(conflicts, ConflictChoice.Both));

        Assert.Equal("- eins\n- zwei lokal\n\t- zwei-a\n- zwei extern\n- drei\n", Text(merged));
        Assert.Equal(B(local, "zwei lokal").Key, B(merged, "zwei lokal").Key);
        Assert.True(merged.IsDirty);
    }

    [Fact]
    public void Merge_Both_ExternalCopyHasNoId_SoIdsStayUnique()
    {
        var text = $"- a\n  id:: {IdA}\n- b\n";
        var local = Parse(text);
        B(local, "a").SetContent("a lokal");
        var external = Parse(text.Replace("- a\n", "- a extern\n"));

        var merged = PageMerger.Merge(local, external, Choose(ConflictDetector.Detect(local, external), ConflictChoice.Both));

        Assert.Equal($"- a lokal\n  id:: {IdA}\n- a extern\n- b\n", Text(merged));
    }

    [Theory]
    [InlineData(ConflictChoice.Mine, "- eins\n- zwei lokal\n\t- zwei-a\n- drei\n", true)]
    [InlineData(ConflictChoice.Theirs, "- eins\n- zwei extern\n\t- zwei-a\n- drei\n", false)]
    public void Merge_MineOrTheirs_TakesThatVersion(ConflictChoice choice, string expected, bool dirty)
    {
        var local = Parse(Base);
        B(local, "zwei").SetContent("zwei lokal");
        var external = Parse(Base.Replace("- zwei\n", "- zwei extern\n"));

        var merged = PageMerger.Merge(local, external, Choose(ConflictDetector.Detect(local, external), choice));

        Assert.Equal(expected, Text(merged));
        Assert.Equal(dirty, merged.IsDirty);
    }

    [Fact]
    public void Merge_MissingChoiceForConflict_Throws()
    {
        var local = Parse(Base);
        B(local, "zwei").SetContent("zwei lokal");
        var external = Parse(Base.Replace("- zwei\n", "- zwei extern\n"));

        Assert.Throws<ArgumentException>(() => PageMerger.Merge(local, external, new Dictionary<Guid, ConflictChoice>()));
    }

    [Fact]
    public void Detect_SameEditOnBothSides_NoConflict()
    {
        var local = Parse(Base);
        B(local, "zwei").SetContent("zwei neu");
        var external = Parse(Base.Replace("- zwei\n", "- zwei neu\n"));

        Assert.Empty(ConflictDetector.Detect(local, external));
        var merged = PageMerger.Merge(local, external, new Dictionary<Guid, ConflictChoice>());
        Assert.Equal("- eins\n- zwei neu\n\t- zwei-a\n- drei\n", Text(merged));
        Assert.False(merged.IsDirty);
    }

    [Fact]
    public void Detect_LocallyChanged_DeletedExternally_IsConflictWithoutExternal()
    {
        var local = Parse(Base);
        var drei = B(local, "drei");
        drei.SetContent("drei lokal");
        var external = Parse("- eins\n- zwei\n\t- zwei-a\n");

        var conflict = Assert.Single(ConflictDetector.Detect(local, external));
        Assert.Same(drei, conflict.Local);
        Assert.Null(conflict.External);

        var mine = PageMerger.Merge(local, external, Choose([conflict], ConflictChoice.Mine));
        Assert.Equal("- eins\n- zwei\n\t- zwei-a\n- drei lokal\n", Text(mine));
        Assert.Equal(drei.Key, B(mine, "drei lokal").Key);

        var theirs = PageMerger.Merge(local, external, Choose([conflict], ConflictChoice.Theirs));
        Assert.Equal("- eins\n- zwei\n\t- zwei-a\n", Text(theirs));
    }

    [Fact]
    public void Detect_UnchangedLocally_DeletedExternally_NoConflict_StaysDeleted()
    {
        var local = Parse(Base);
        B(local, "eins").SetContent("eins lokal");
        var external = Parse("- eins\n- zwei\n\t- zwei-a\n");

        Assert.Empty(ConflictDetector.Detect(local, external));
        var merged = PageMerger.Merge(local, external, new Dictionary<Guid, ConflictChoice>());
        Assert.Equal("- eins lokal\n- zwei\n\t- zwei-a\n", Text(merged));
    }

    [Fact]
    public void Merge_LocalRemoval_UnchangedExternally_StaysRemoved()
    {
        var local = Parse(Base);
        local.RemoveBlock(B(local, "zwei"));
        var external = Parse(Base.Replace("- drei\n", "- drei extern\n"));

        Assert.Empty(ConflictDetector.Detect(local, external));
        var merged = PageMerger.Merge(local, external, new Dictionary<Guid, ConflictChoice>());

        Assert.Equal("- eins\n- drei extern\n", Text(merged));
        Assert.True(merged.IsDirty);
    }

    [Fact]
    public void Merge_LocalRemoval_ExternalAddedChild_ChildIsKept()
    {
        var local = Parse(Base);
        local.RemoveBlock(B(local, "zwei"));
        var external = Parse(Base.Replace("\t- zwei-a\n", "\t- zwei-a\n\t- zwei-b extern\n"));

        Assert.Empty(ConflictDetector.Detect(local, external));
        var merged = PageMerger.Merge(local, external, new Dictionary<Guid, ConflictChoice>());

        Assert.Equal("- eins\n- zwei-b extern\n- drei\n", Text(merged));
    }

    [Fact]
    public void Detect_LocalRemoval_ChangedExternally_IsConflict()
    {
        var local = Parse(Base);
        var eins = B(local, "eins");
        local.RemoveBlock(eins);
        var external = Parse(Base.Replace("- eins\n", "- eins extern\n"));

        var conflict = Assert.Single(ConflictDetector.Detect(local, external));
        Assert.Same(eins, conflict.Local);
        Assert.True(conflict.LocalRemoved);
        Assert.Equal("eins extern", conflict.External!.Content);

        var mine = PageMerger.Merge(local, external, Choose([conflict], ConflictChoice.Mine));
        Assert.Equal("- zwei\n\t- zwei-a\n- drei\n", Text(mine));

        foreach (var keep in new[] { ConflictChoice.Theirs, ConflictChoice.Both })
        {
            var merged = PageMerger.Merge(local, external, Choose([conflict], keep));
            Assert.Equal("- eins extern\n- zwei\n\t- zwei-a\n- drei\n", Text(merged));
        }
    }

    [Fact]
    public void Merge_LocalNewBlock_GoesAfterItsPredecessor_ExternalInsertedAbove()
    {
        var local = Parse(Base);
        var neu = Block.CreateDetached("neu");
        local.InsertBlock(null, 2, neu);
        var external = Parse("- null\n" + Base.Replace("- drei\n", "- drei extern\n"));

        Assert.Empty(ConflictDetector.Detect(local, external));
        var merged = PageMerger.Merge(local, external, new Dictionary<Guid, ConflictChoice>());

        Assert.Equal("- null\n- eins\n- zwei\n\t- zwei-a\n- neu\n- drei extern\n", Text(merged));
        Assert.Equal(neu.Key, B(merged, "neu").Key);
    }

    [Fact]
    public void Merge_LocalNewFirstChild_GoesFirstUnderItsParent()
    {
        var local = Parse(Base);
        local.InsertBlock(B(local, "zwei"), 0, Block.CreateDetached("neu"));
        var external = Parse(Base.Replace("- eins\n", "- eins extern\n"));

        var merged = PageMerger.Merge(local, external, new Dictionary<Guid, ConflictChoice>());

        Assert.Equal("- eins extern\n- zwei\n\t- neu\n\t- zwei-a\n- drei\n", Text(merged));
    }

    [Fact]
    public void Merge_LocalChange_FollowsBlockShiftedByExternalInsert()
    {
        var local = Parse(Base);
        B(local, "drei").SetContent("drei lokal");
        var external = Parse("- null\n" + Base);

        Assert.Empty(ConflictDetector.Detect(local, external));
        var merged = PageMerger.Merge(local, external, new Dictionary<Guid, ConflictChoice>());

        Assert.Equal("- null\n- eins\n- zwei\n\t- zwei-a\n- drei lokal\n", Text(merged));
    }

    [Fact]
    public void Detect_MatchesById_WhenExternalMovedAndChangedBlock()
    {
        var text = $"- a\n  id:: {IdA}\n- b\n- c\n";
        var local = Parse(text);
        var a = B(local, "a");
        a.SetContent("a lokal");
        var external = Parse($"- b\n- c\n- a extern\n  id:: {IdA}\n");

        var conflict = Assert.Single(ConflictDetector.Detect(local, external));

        Assert.Same(a, conflict.Local);
        Assert.Equal("a extern", conflict.External!.Content);
    }

    [Fact]
    public void Merge_LocalMove_IsReapplied()
    {
        var local = Parse(Base);
        local.MoveBlock(B(local, "drei"), B(local, "eins"), 0);
        var external = Parse(Base.Replace("\t- zwei-a\n", "\t- zwei-a extern\n"));

        Assert.Empty(ConflictDetector.Detect(local, external));
        var merged = PageMerger.Merge(local, external, new Dictionary<Guid, ConflictChoice>());

        Assert.Equal("- eins\n\t- drei\n- zwei\n\t- zwei-a extern\n", Text(merged));
    }

    [Fact]
    public void Merge_LocalSiblingSwap_IsReapplied()
    {
        var local = Parse(Base);
        local.MoveBlock(B(local, "drei"), null, 0);
        var external = Parse(Base.Replace("\t- zwei-a\n", "\t- zwei-a extern\n"));

        var merged = PageMerger.Merge(local, external, new Dictionary<Guid, ConflictChoice>());

        Assert.Equal("- drei\n- eins\n- zwei\n\t- zwei-a extern\n", Text(merged));
    }

    [Fact]
    public void Merge_PagePropertiesChangedLocally_AreKept()
    {
        const string text = "title:: T\n\n- x\n";
        var local = Parse(text);
        local.SetPageProperty("status", "lokal");
        var external = Parse(text.Replace("- x\n", "- x extern\n"));

        var merged = PageMerger.Merge(local, external, new Dictionary<Guid, ConflictChoice>());

        Assert.Equal("title:: T\nstatus:: lokal\n\n- x extern\n", Text(merged));
    }

    [Fact]
    public void Merge_PagePropertiesChangedOnBothSides_KeepsBothLines()
    {
        const string text = "title:: T\n\n- x\n";
        var local = Parse(text);
        local.SetPageProperty("status", "lokal");
        var external = Parse("title:: T\nautor:: extern\n\n- x\n");

        var merged = PageMerger.Merge(local, external, new Dictionary<Guid, ConflictChoice>());

        Assert.Equal("title:: T\nautor:: extern\nstatus:: lokal\n\n- x\n", Text(merged));
    }

    [Fact]
    public void Merge_ChangesOnlySinceLastSave_AreReapplied()
    {
        var local = Parse(Base);
        B(local, "eins").SetContent("eins gespeichert");
        local.MarkSaved();
        B(local, "drei").SetContent("drei lokal");
        var external = Parse("- eins gespeichert\n- zwei extern\n\t- zwei-a\n- drei\n");

        Assert.Empty(ConflictDetector.Detect(local, external));
        var merged = PageMerger.Merge(local, external, new Dictionary<Guid, ConflictChoice>());

        Assert.Equal("- eins gespeichert\n- zwei extern\n\t- zwei-a\n- drei lokal\n", Text(merged));
    }

    [Fact]
    public void Merge_CrlfFile_ReappliedLinesKeepCrlf()
    {
        var crlf = Base.Replace("\n", "\r\n");
        var local = Parse(crlf);
        B(local, "zwei-a").SetContent("zwei-a lokal\nzweite Zeile");
        local.InsertBlock(null, 3, Block.CreateDetached("vier"));
        var external = Parse(crlf.Replace("- eins\r\n", "- eins extern\r\n"));

        var merged = PageMerger.Merge(local, external, new Dictionary<Guid, ConflictChoice>());

        Assert.Equal("- eins extern\r\n- zwei\r\n\t- zwei-a lokal\r\n\t  zweite Zeile\r\n- drei\r\n- vier\r\n", Text(merged));
    }

    [Fact]
    public void Merge_NoLocalChanges_EqualsExternal_NotDirty()
    {
        var local = Parse(Base);
        var external = Parse("- eins\n- zwei extern\n- drei\n\t- drei-a\n");

        var merged = PageMerger.Merge(local, external, new Dictionary<Guid, ConflictChoice>());

        Assert.Equal("- eins\n- zwei extern\n- drei\n\t- drei-a\n", Text(merged));
        Assert.False(merged.IsDirty);
        Assert.NotSame(external, merged);
    }
}
