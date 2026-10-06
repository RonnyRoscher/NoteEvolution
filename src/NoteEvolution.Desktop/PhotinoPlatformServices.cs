using System.Diagnostics;
using Microsoft.Extensions.Localization;
using NoteEvolution.UI.Platform;
using NoteEvolution.UI.Resources;
using Photino.NET;

namespace NoteEvolution.Desktop;

/// <summary>Platform services of the desktop shell: Photino's native dialogs and the system's URI handlers.</summary>
public sealed class PhotinoPlatformServices(IStringLocalizer<Strings> strings) : IPlatformServices
{
    /// <summary><c>%APPDATA%/NoteEvolution</c> on Windows, <c>~/.config/NoteEvolution</c> on Linux and macOS.</summary>
    public static string DefaultUserDataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NoteEvolution");

    /// <summary>The main window, the owner of the dialogs; set once the app is built.</summary>
    public PhotinoWindow? Window { get; set; }

    public string UserDataDirectory => DefaultUserDataDirectory;

    public async Task<string?> PickFolderAsync()
    {
        var folders = await RequireWindow().ShowOpenFolderAsync(strings["PickVaultTitle"], null, false);
        return folders?.FirstOrDefault(f => !string.IsNullOrEmpty(f));
    }

    public async Task<string?> PickSaveFileAsync(string suggestedName)
    {
        var extension = Path.GetExtension(suggestedName);
        (string Name, string[] Extensions)[] filters = string.IsNullOrEmpty(extension)
            ? []
            : [(extension.TrimStart('.').ToUpperInvariant(), ["*" + extension])];
        var defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), suggestedName);
        var path = await RequireWindow().ShowSaveFileAsync(strings["SaveFileTitle"], defaultPath, filters);
        return string.IsNullOrEmpty(path) ? null : path;
    }

    public void OpenExternal(string uri)
    {
        using var process = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    private PhotinoWindow RequireWindow() =>
        Window ?? throw new InvalidOperationException("The main window is not created yet.");
}
