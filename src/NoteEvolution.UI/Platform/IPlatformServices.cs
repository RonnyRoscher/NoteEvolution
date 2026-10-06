namespace NoteEvolution.UI.Platform;

/// <summary>What the UI needs from the host (desktop shell, later mobile): dialogs, the browser and a data folder.</summary>
public interface IPlatformServices
{
    /// <summary>Lets the user choose a folder; <c>null</c> if the dialog was cancelled.</summary>
    Task<string?> PickFolderAsync();

    /// <summary>Lets the user choose where to save a file, proposing <paramref name="suggestedName"/>; <c>null</c> if cancelled.</summary>
    Task<string?> PickSaveFileAsync(string suggestedName);

    /// <summary>Opens <paramref name="uri"/> with the program the system has registered for it (e.g. <c>logseq://</c>).</summary>
    void OpenExternal(string uri);

    /// <summary>The per-user folder for app data such as <c>ui.json</c> (<c>%APPDATA%/NoteEvolution</c>, <c>~/.config/NoteEvolution</c>).</summary>
    string UserDataDirectory { get; }
}
