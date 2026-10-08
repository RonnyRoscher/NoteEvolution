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

    /// <summary>The <c>keepCursor</c> argument of each <see cref="SetDocumentAsync"/>, in the order of <see cref="Documents"/>.</summary>
    public List<bool> KeepCursor { get; } = [];

    /// <summary>The keys passed to <see cref="RevealAsync"/>, in order.</summary>
    public List<Guid> Reveals { get; } = [];

    /// <summary>All calls in order: <c>doc</c> for <see cref="SetDocumentAsync"/>, <c>reveal:{key}</c> for <see cref="RevealAsync"/>.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>The document shown last.</summary>
    public string Json => Documents[^1].Json;

    public bool Disposed { get; private set; }

    /// <summary>
    /// Simulates the editor's report after a new document: when set, the cursor it returns is reported to
    /// <see cref="Callbacks"/> from within every <see cref="SetDocumentAsync"/> (whatever was asked of the selection).
    /// </summary>
    public Func<CursorInfo?>? CursorAfterLoad { get; set; }

    public Task InitAsync(ElementReference host, IEditorCallbacks callbacks)
    {
        Callbacks = callbacks;
        return Task.CompletedTask;
    }

    public async Task SetDocumentAsync(string docJson, bool showChips, bool keepCursor)
    {
        Documents.Add((docJson, showChips));
        KeepCursor.Add(keepCursor);
        Calls.Add("doc");
        if (CursorAfterLoad is { } cursor && Callbacks is { } callbacks)
        {
            await callbacks.OnCursorChanged(cursor());
        }
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
