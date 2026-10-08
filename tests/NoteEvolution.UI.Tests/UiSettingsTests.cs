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

    [Fact]
    public void RememberBook_PerVault_RoundTrips_IgnoringCaseAndTrailingSeparator()
    {
        using var dir = new TempDir();
        using var vault = new TempDir();
        var settings = new UiSettings();

        settings.RememberBook(vault.Path, Path.Combine(vault.Path, "pages", "Mein Buch.md"));
        settings.Save(dir.Path);
        var loaded = UiSettings.Load(dir.Path);

        Assert.Equal("pages/Mein Buch.md", loaded.LastBookOf(vault.Path));
        Assert.Equal("pages/Mein Buch.md", loaded.LastBookOf(vault.Path.ToUpperInvariant() + Path.DirectorySeparatorChar));
        Assert.Null(loaded.LastBookOf(dir.Path));
    }

    [Fact]
    public void AiModelId_RoundTrips()
    {
        using var dir = new TempDir();
        Assert.Null(new UiSettings().AiModelId);

        new UiSettings { AiModelId = "bge-m3-int8" }.Save(dir.Path);

        Assert.Equal("bge-m3-int8", UiSettings.Load(dir.Path).AiModelId);
    }

    [Theory]
    [InlineData(2, 10, 5, 50)]
    [InlineData(99, 20, 500, 100)]
    [InlineData(14, 14, 80, 80)]
    public void Load_ClampsHandEditedValues(int fontSize, int expectedFont, int lineWidth, int expectedWidth)
    {
        using var dir = new TempDir();
        dir.Write("ui.json", $"{{ \"FontSizePt\": {fontSize}, \"LineWidthCh\": {lineWidth} }}");

        var loaded = UiSettings.Load(dir.Path);

        Assert.Equal(expectedFont, loaded.FontSizePt);
        Assert.Equal(expectedWidth, loaded.LineWidthCh);
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
