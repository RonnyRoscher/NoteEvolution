using Microsoft.AspNetCore.Components;
using NoteEvolution.UI.Editor;

namespace NoteEvolution.UI.Tests;

/// <summary>
/// The editor without JavaScript: records the shown documents and revealed elements; tests raise the editor's events
/// via <see cref="Callbacks"/>.
/// </summary>
public sealed class FakeEditorInterop : IEditorInterop
{
    public IEditorCallbacks? Callbacks { get; private set; }

    public List<(string Json, bool ShowChips)> Documents { get; } = [];

    /// <summary>The keys passed to <see cref="RevealAsync"/>, in order.</summary>
    public List<Guid> Reveals { get; } = [];

    /// <summary>All calls in order: <c>doc</c> for <see cref="SetDocumentAsync"/>, <c>reveal:{key}</c> for <see cref="RevealAsync"/>.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>The document shown last.</summary>
    public string Json => Documents[^1].Json;

    public bool Disposed { get; private set; }

    public Task InitAsync(ElementReference host, IEditorCallbacks callbacks)
    {
        Callbacks = callbacks;
        return Task.CompletedTask;
    }

    public Task SetDocumentAsync(string docJson, bool showChips)
    {
        Documents.Add((docJson, showChips));
        Calls.Add("doc");
        return Task.CompletedTask;
    }

    public Task RevealAsync(Guid elementKey)
    {
        Reveals.Add(elementKey);
        Calls.Add($"reveal:{elementKey}");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
