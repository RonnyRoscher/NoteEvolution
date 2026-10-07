using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Editor;

/// <summary>
/// The TipTap editor in <c>wwwroot/js/editor.bundle.js</c> (built from <c>Editor/js</c>): one instance per editor
/// component (register it as transient). The script only forwards events; they arrive here and go to the callbacks.
/// </summary>
public sealed class TipTapInterop(IJSRuntime js, AppState state) : IEditorInterop
{
    private const string ModulePath = "./_content/NoteEvolution.UI/js/editor.bundle.js";

    private IJSObjectReference? _module;
    private IJSObjectReference? _editor;
    private DotNetObjectReference<TipTapInterop>? _self;
    private IEditorCallbacks? _callbacks;

    public async Task InitAsync(ElementReference host, IEditorCallbacks callbacks)
    {
        _callbacks = callbacks;
        _self = DotNetObjectReference.Create(this);
        _module = await js.InvokeAsync<IJSObjectReference>("import", ModulePath);
        _editor = await _module.InvokeAsync<IJSObjectReference>("createEditor", host, _self);
    }

    public async Task SetDocumentAsync(string docJson, bool manuscript, bool showChips)
    {
        if (_editor is null)
        {
            throw new InvalidOperationException("The editor is not initialized.");
        }

        await _editor.InvokeVoidAsync("setDocument", docJson, manuscript, showChips);
    }

    [JSInvokable]
    public Task DocumentChanged(string docJson) => _callbacks?.OnDocumentChanged(docJson) ?? Task.CompletedTask;

    [JSInvokable]
    public Task CursorBlockChanged(string? textBlockKey) =>
        _callbacks?.OnCursorBlockChanged(ParseOrNull(textBlockKey)) ?? Task.CompletedTask;

    [JSInvokable]
    public Task ChipClicked(string noteId) =>
        _callbacks is not null && Guid.TryParse(noteId, out var id) ? _callbacks.OnChipClicked(id) : Task.CompletedTask;

    [JSInvokable]
    public Task ChipRemoved(string textBlockKey, string noteId) =>
        _callbacks is not null && Guid.TryParse(textBlockKey, out var key) && Guid.TryParse(noteId, out var id)
            ? _callbacks.OnChipRemoved(key, id)
            : Task.CompletedTask;

    /// <summary>
    /// Something was dropped into the editor from outside. Only a note card counts: it records itself in
    /// <see cref="AppState.DraggedNoteKey"/> while it is dragged (R27); anything else (a file, text from another
    /// program) is ignored.
    /// </summary>
    [JSInvokable]
    public Task NoteDropped(string? afterKey) =>
        _callbacks is not null && state.DraggedNoteKey is { } noteKey
            ? _callbacks.OnNoteDropped(noteKey, ParseOrNull(afterKey))
            : Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_editor is not null)
            {
                await _editor.InvokeVoidAsync("destroy");
                await _editor.DisposeAsync();
            }

            if (_module is not null)
            {
                await _module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
            // The window is closing; there is nothing left to clean up.
        }

        _self?.Dispose();
        _callbacks = null;
    }

    private static Guid? ParseOrNull(string? value) => Guid.TryParse(value, out var guid) ? guid : null;
}
