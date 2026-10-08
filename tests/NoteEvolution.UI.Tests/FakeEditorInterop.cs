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
    /// <see cref="Callbacks"/> from within <see cref="SetDocumentAsync"/>. Without <c>keepCursor</c> it is reported
    /// anyway (an editor that reports more than asked, which the pane must ignore); with <c>keepCursor</c> only when
    /// <see cref="CursorPlaced"/>, as the editor reports the kept cursor only if one had been reported before.
    /// </summary>
    public Func<CursorInfo?>? CursorAfterLoad { get; set; }

    /// <summary>
    /// Whether the editor has reported a cursor for the shown document (tests set it when they place the cursor via
    /// <see cref="Callbacks"/>). Each <see cref="SetDocumentAsync"/> keeps it only with <c>keepCursor</c> and a report.
    /// </summary>
    public bool CursorPlaced { get; set; }

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
        var placed = CursorPlaced;
        CursorPlaced = false;
        if (CursorAfterLoad is { } cursor && Callbacks is { } callbacks && (!keepCursor || placed))
        {
            CursorPlaced = keepCursor;
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
