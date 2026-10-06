using NoteEvolution.TestSupport;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

public class UiSettingsTests
{
    [Fact]
    public void UiSettings_RoundTrip()
    {
        using var dir = new TempDir();
        var settings = new UiSettings
        {
            LastVault = @"C:\Vaults\Test",
            OutlineWidth = 312.5,
            NotesWidth = 401,
            OutlineCollapsed = true,
            NotesCollapsed = true,
            Theme = ThemeChoice.Dark,
            FontSizePt = 15,
            LineWidthCh = 82,
            HideUsed = true,
            LogseqHintShown = true,
        };

        settings.Save(dir.Path);
        var loaded = UiSettings.Load(dir.Path);

        Assert.True(File.Exists(Path.Combine(dir.Path, "ui.json")));
        Assert.Equal(@"C:\Vaults\Test", loaded.LastVault);
        Assert.Equal(312.5, loaded.OutlineWidth);
        Assert.Equal(401, loaded.NotesWidth);
        Assert.True(loaded.OutlineCollapsed);
        Assert.True(loaded.NotesCollapsed);
        Assert.Equal(ThemeChoice.Dark, loaded.Theme);
        Assert.Equal(15, loaded.FontSizePt);
        Assert.Equal(82, loaded.LineWidthCh);
        Assert.True(loaded.HideUsed);
        Assert.True(loaded.LogseqHintShown);
    }

    [Fact]
    public void Load_MissingOrCorruptFile_GivesDefaults()
    {
        using var dir = new TempDir();
        AssertDefaults(UiSettings.Load(Path.Combine(dir.Path, "fehlt")));

        dir.Write("ui.json", "{ kein json");
        AssertDefaults(UiSettings.Load(dir.Path));
    }

    [Fact]
    public void Save_CreatesTheDirectory()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "a", "NoteEvolution");

        new UiSettings { OutlineWidth = 300 }.Save(target);

        Assert.Equal(300, UiSettings.Load(target).OutlineWidth);
    }

    private static void AssertDefaults(UiSettings settings)
    {
        Assert.Null(settings.LastVault);
        Assert.Equal(260, settings.OutlineWidth);
        Assert.Equal(380, settings.NotesWidth);
        Assert.False(settings.OutlineCollapsed);
        Assert.False(settings.NotesCollapsed);
        Assert.Equal(ThemeChoice.System, settings.Theme);
        Assert.Equal(12, settings.FontSizePt);
        Assert.Equal(70, settings.LineWidthCh);
        Assert.False(settings.HideUsed);
        Assert.False(settings.LogseqHintShown);
    }
}
