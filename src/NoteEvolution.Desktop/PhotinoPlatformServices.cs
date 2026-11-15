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
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var window = RequireWindow();
        if (OperatingSystem.IsWindows())
        {
            // Let the click handler's render go out before the modal dialog blocks.
            await Task.Yield();
            var chosen = ShowWindowsSaveDialog(window, folder, suggestedName, extension);
            return string.IsNullOrEmpty(chosen) ? null : chosen;
        }

        (string Name, string[] Extensions)[] filters = string.IsNullOrEmpty(extension)
            ? []
            : [(extension.TrimStart('.').ToUpperInvariant(), ["*" + extension])];

        // Photino's dialog does not open for a default path that names a file which does not exist yet, so it gets
        // the suggested file only if there is one, and the folder otherwise.
        var suggested = Path.Combine(folder, suggestedName);
        var path = await window.ShowSaveFileAsync(strings["SaveFileTitle"], File.Exists(suggested) ? suggested : folder, filters);
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        return Path.HasExtension(path) ? path : path + extension;
    }

    /// <summary>The system dialog on the window's thread, as Photino's own dialogs run.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private string? ShowWindowsSaveDialog(PhotinoWindow window, string folder, string suggestedName, string extension)
    {
        string? result = null;
        var owner = window.WindowHandle;
        var title = strings["SaveFileTitle"].Value;
        window.Invoke(() => result = WindowsSaveDialog.Show(owner, title, folder, suggestedName, extension));
        return result;
    }

    public async Task<string?> PickImageFileAsync()
    {
        (string Name, string[] Extensions)[] filters =
            [(strings["PickImageFilter"], ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp"])];
        var files = await RequireWindow().ShowOpenFileAsync(
            strings["PickImageTitle"], Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), false, filters);
        return files?.FirstOrDefault(f => !string.IsNullOrEmpty(f));
    }

    public void OpenExternal(string uri)
    {
        using var process = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    private PhotinoWindow RequireWindow() =>
        Window ?? throw new InvalidOperationException("The main window is not created yet.");
}
