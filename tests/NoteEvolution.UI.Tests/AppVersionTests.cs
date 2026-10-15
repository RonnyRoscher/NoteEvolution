using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

public class AppVersionTests
{
    [Fact]
    public void Title_ShowsAllFourParts()
    {
        Assert.Equal("NoteEvolution v0.1.3.1", AppVersion.Title(new Version(0, 1, 3, 1)));
        Assert.Equal("NoteEvolution v0.0.0.0", AppVersion.Title(new Version(0, 0)));
        Assert.Equal("NoteEvolution", AppVersion.Title(null));
    }

    [Fact]
    public void Assembly_HasTheCountedVersion()
    {
        // Built in a repository the version is counted from the history (Directory.Build.targets): it is never 1.0.0.0,
        // the SDK's default.
        var version = typeof(AppVersion).Assembly.GetName().Version!;
        Assert.NotEqual(new Version(1, 0, 0, 0), version);
        Assert.InRange(version.Minor, 0, 9);
        Assert.InRange(version.Build, 0, 9);
        Assert.InRange(version.Revision, 0, 9);
    }
}
