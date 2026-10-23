using Microsoft.AspNetCore.Components;
using NoteEvolution.Core.Books;
using NoteEvolution.UI.Editor;

namespace NoteEvolution.UI.Tests;

/// <summary>
/// The editor without JavaScript: records the shown documents, revealed elements and marked nodes; tests raise the editor's events
/// via <see cref="Callbacks"/>.
/// </summary>
public sealed class FakeEditorInterop : IEditorInterop, IDisposable
{
    public IEditorCallbacks? Callbacks { get; private set; }

    public List<(string Json, bool ShowChips)> Documents { get; } = [];

    /// <summary>The <c>keepCursor</c> argument of each <see cref="SetDocumentAsync"/>, in the order of <see cref="Documents"/>.</summary>
    public List<bool> KeepCursor { get; } = [];

    /// <summary>The keys passed to <see cref="RevealAsync"/>, in order.</summary>
    public List<Guid> Reveals { get; } = [];

    /// <summary>The offsets passed to <see cref="RevealAsync"/>, in the order of <see cref="Reveals"/>.</summary>
    public List<int> RevealOffsets { get; } = [];

    /// <summary>The lists passed to <see cref="SetMarkedAsync"/>, in order.</summary>
    public List<IReadOnlyList<MarkedNode>> Marked { get; } = [];

    /// <summary>The labels passed to <see cref="SetBlockDatesAsync"/>, in order.</summary>
    public List<IReadOnlyDictionary<Guid, BlockDateLabel?>> BlockDates { get; } = [];

    /// <summary>When set, the next <see cref="SetBlockDatesAsync"/> throws it (and shows nothing).</summary>
    public Exception? FailBlockDates { get; set; }

    /// <summary>
    /// All calls in order: <c>doc</c> for <see cref="SetDocumentAsync"/>, <c>reveal:{key}</c> for
    /// <see cref="RevealAsync"/>, <c>marked</c> for <see cref="SetMarkedAsync"/>, <c>dates</c> for
    /// <see cref="SetBlockDatesAsync"/>, <c>refs</c> for <see cref="SetRefLabelsAsync"/>.
    /// </summary>
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

    public Task SplitAtCursorAsync()
    {
        Calls.Add("split");
        return Task.CompletedTask;
    }

    public Task RevealAsync(Guid elementKey, int offset = 0)
    {
        Reveals.Add(elementKey);
        RevealOffsets.Add(offset);
        Calls.Add($"reveal:{elementKey}");
        return Task.CompletedTask;
    }

    public Task SetMarkedAsync(IReadOnlyList<MarkedNode> nodes)
    {
        Marked.Add([.. nodes]);
        Calls.Add("marked");
        return Task.CompletedTask;
    }

    public Task SetBlockDatesAsync(IReadOnlyDictionary<Guid, BlockDateLabel?> dates)
    {
        if (FailBlockDates is { } failure)
        {
            FailBlockDates = null;
            throw failure;
        }

        BlockDates.Add(new Dictionary<Guid, BlockDateLabel?>(dates));
        Calls.Add("dates");
        return Task.CompletedTask;
    }

    /// <summary>The labels passed to <see cref="SetRefLabelsAsync"/>, in order.</summary>
    public List<IReadOnlyDictionary<Guid, RefLabel>> RefLabels { get; } = [];

    public Task SetRefLabelsAsync(IReadOnlyDictionary<Guid, RefLabel> labels)
    {
        RefLabels.Add(new Dictionary<Guid, RefLabel>(labels));
        Calls.Add("refs");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Does nothing: the test's service container disposes the editors it handed out per pane
    /// (<see cref="UiTestContext"/>'s <c>UseEditorPerPane</c>) synchronously, which an <see cref="IAsyncDisposable"/> alone
    /// does not allow. <see cref="Disposed"/> stays what the pane did (<see cref="DisposeAsync"/>).
    /// </summary>
    public void Dispose()
    {
    }
}
