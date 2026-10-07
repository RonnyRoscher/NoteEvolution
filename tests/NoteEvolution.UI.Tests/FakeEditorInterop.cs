using Microsoft.AspNetCore.Components;
using NoteEvolution.UI.Editor;

namespace NoteEvolution.UI.Tests;

/// <summary>The editor without JavaScript: records the shown documents; tests raise the editor's events via <see cref="Callbacks"/>.</summary>
public sealed class FakeEditorInterop : IEditorInterop
{
    public IEditorCallbacks? Callbacks { get; private set; }

    public List<(string Json, bool Manuscript, bool ShowChips)> Documents { get; } = [];

    /// <summary>The document shown last.</summary>
    public string Json => Documents[^1].Json;

    public bool Disposed { get; private set; }

    public Task InitAsync(ElementReference host, IEditorCallbacks callbacks)
    {
        Callbacks = callbacks;
        return Task.CompletedTask;
    }

    public Task SetDocumentAsync(string docJson, bool manuscript, bool showChips)
    {
        Documents.Add((docJson, manuscript, showChips));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
