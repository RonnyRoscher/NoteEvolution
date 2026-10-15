namespace NoteEvolution.UI.State;

/// <summary>The program's version as shown in the window title (see Directory.Build.targets for how it is counted).</summary>
public static class AppVersion
{
    public const string Name = "NoteEvolution";

    /// <summary>"NoteEvolution v0.1.3.1"; the name alone without a version.</summary>
    public static string Title(Version? version) =>
        version is null ? Name : $"{Name} v{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}.{Math.Max(version.Revision, 0)}";
}
