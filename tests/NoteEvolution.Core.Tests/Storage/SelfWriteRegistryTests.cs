using System.Text;
using NoteEvolution.Core.Storage;

namespace NoteEvolution.Core.Tests.Storage;

public class SelfWriteRegistryTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void IsOwnWrite_MatchesRecordedContent_OnlyForThatPath()
    {
        var registry = new SelfWriteRegistry();
        var path = Path.GetFullPath("vault/pages/Idee.md");
        registry.Record(path, Bytes("- eigen\n"));

        Assert.True(registry.IsOwnWrite(path, Bytes("- eigen\n")));
        Assert.False(registry.IsOwnWrite(path, Bytes("- fremd\n")));
        Assert.False(registry.IsOwnWrite(Path.GetFullPath("vault/pages/Andere.md"), Bytes("- eigen\n")));
    }

    [Fact]
    public void IsOwnWrite_PathsCompareFullAndCaseInsensitive()
    {
        var registry = new SelfWriteRegistry();
        registry.Record(Path.GetFullPath("vault/pages/Idee.md"), Bytes("- eigen\n"));

        Assert.True(registry.IsOwnWrite("vault/PAGES/../pages/idee.MD", Bytes("- eigen\n")));
    }

    [Fact]
    public void Record_LatestWriteWins_AndStaysRecorded()
    {
        var registry = new SelfWriteRegistry();
        var path = Path.GetFullPath("vault/pages/Idee.md");
        registry.Record(path, Bytes("- eins\n"));
        registry.Record(path, Bytes("- zwei\n"));

        Assert.False(registry.IsOwnWrite(path, Bytes("- eins\n")));
        Assert.True(registry.IsOwnWrite(path, Bytes("- zwei\n")));
        Assert.True(registry.IsOwnWrite(path, Bytes("- zwei\n")));
    }
}
