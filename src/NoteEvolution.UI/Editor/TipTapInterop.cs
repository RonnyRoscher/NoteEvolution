using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using NoteEvolution.Core.Books;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Editor;

/// <summary>
/// The TipTap editor in <c>wwwroot/js/editor.bundle.js</c> (built from <c>Editor/js</c>): one instance per editor
/// component (register it as transient). The script only forwards events; they arrive here and go to the callbacks.
/// </summary>
public sealed class TipTapInterop(IJSRuntime js, AppState state) : IEditorInterop
{
    private const string ModulePath = "./_content/NoteEvolution.UI/js/editor.bundle.js";

    /// <summary>The height of the section bar in px; the script keeps the bar's top this far above the pane's visible bottom.</summary>
    private const int BarHeight = 40;

    private IJSObjectReference? _module;
    private IJSObjectReference? _editor;
    private DotNetObjectReference<TipTapInterop>? _self;
    private IEditorCallbacks? _callbacks;

    public async Task InitAsync(ElementReference host, IEditorCallbacks callbacks)
    {
        _callbacks = callbacks;
        _self = DotNetObjectReference.Create(this);
        _module = await js.InvokeAsync<IJSObjectReference>("import", ModulePath);
        _editor = await _module.InvokeAsync<IJSObjectReference>("createEditor", host, _self, new { barHeight = BarHeight });
    }

    public async Task SetDocumentAsync(string docJson, bool showChips, bool keepCursor) =>
        await Initialized.InvokeVoidAsync("setDocument", docJson, showChips, keepCursor);

    public async Task RevealAsync(Guid elementKey) =>
        await Initialized.InvokeVoidAsync("reveal", elementKey == Guid.Empty ? null : elementKey.ToString("D"));

    /// <summary>Passes the nodes to the script as an array of <c>{ key, ownTextOnly }</c>.</summary>
    public async Task SetMarkedAsync(IReadOnlyList<MarkedNode> nodes) =>
        await Initialized.InvokeVoidAsync(
            "setMarked", [nodes.Select(node => new { key = node.Key.ToString("D"), ownTextOnly = node.OwnTextOnly }).ToArray()]);

    [JSInvokable]
    public Task DocumentChanged(string docJson) => _callbacks?.OnDocumentChanged(docJson) ?? Task.CompletedTask;

    /// <summary>
    /// The cursor moved: <paramref name="kind"/> is <c>heading</c>, <c>textBlock</c> or <c>detail</c> (<c>null</c>
    /// when the cursor is in no element). A call with an unknown kind or a key that is not a GUID is ignored.
    /// </summary>
    [JSInvokable]
    public Task CursorChanged(string? kind, string? key, string? textBlockKey, int offset)
    {
        if (_callbacks is null)
        {
            return Task.CompletedTask;
        }

        if (kind is null)
        {
            return _callbacks.OnCursorChanged(null);
        }

        ElementKind? parsed = kind switch
        {
            "heading" => ElementKind.Heading,
            "textBlock" => ElementKind.TextBlock,
            "detail" => ElementKind.Detail,
            _ => null,
        };
        if (parsed is not { } elementKind || !Guid.TryParse(key, out var elementKey))
        {
            return Task.CompletedTask;
        }

        if (elementKind == ElementKind.Heading)
        {
            return _callbacks.OnCursorChanged(new CursorInfo(elementKind, elementKey, null, offset));
        }

        return Guid.TryParse(textBlockKey, out var blockKey)
            ? _callbacks.OnCursorChanged(new CursorInfo(elementKind, elementKey, blockKey, offset))
            : Task.CompletedTask;
    }

    /// <summary>A structure command's shortcut; <paramref name="name"/> is a <see cref="Editor.SectionCommand"/> member name, others are ignored.</summary>
    [JSInvokable]
    public Task SectionCommand(string name) =>
        // TryParse also takes numbers; only the exact member name counts.
        _callbacks is not null && Enum.TryParse<SectionCommand>(name, out var command) && name == command.ToString()
            ? _callbacks.OnSectionCommand(command)
            : Task.CompletedTask;

    [JSInvokable]
    public Task SectionBoxMoved(double barTop, bool visible) =>
        _callbacks?.OnSectionBoxMoved(barTop, visible) ?? Task.CompletedTask;

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

    private IJSObjectReference Initialized => _editor ?? throw new InvalidOperationException("The editor is not initialized.");

    private static Guid? ParseOrNull(string? value) => Guid.TryParse(value, out var guid) ? guid : null;
}
