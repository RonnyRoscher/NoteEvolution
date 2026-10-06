using NoteEvolution.TestSupport;
using NoteEvolution.UI.Platform;

namespace NoteEvolution.UI.Tests;

/// <summary>Platform services without dialogs: the "picked" paths are set by the test.</summary>
public sealed class FakePlatformServices : IPlatformServices, IDisposable
{
    private readonly TempDir _userData = new();

    public string? FolderToPick { get; set; }

    public string? SaveFileToPick { get; set; }

    public List<string> OpenedUris { get; } = [];

    public string UserDataDirectory => _userData.Path;

    public Task<string?> PickFolderAsync() => Task.FromResult(FolderToPick);

    public Task<string?> PickSaveFileAsync(string suggestedName) => Task.FromResult(SaveFileToPick);

    public void OpenExternal(string uri) => OpenedUris.Add(uri);

    public void Dispose() => _userData.Dispose();
}
