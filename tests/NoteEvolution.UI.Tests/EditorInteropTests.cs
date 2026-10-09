using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using NoteEvolution.Core.Books;
using NoteEvolution.UI.Editor;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>The C# side of the editor bridge: what <see cref="TipTapInterop"/> makes of the script's calls.</summary>
public class EditorInteropTests
{
    private const string Key = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";

    private const string BlockKey = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";

    private readonly FakeJs _js = new();

    private readonly RecordingCallbacks _callbacks = new();

    private async Task<TipTapInterop> InitAsync()
    {
        var interop = new TipTapInterop(_js, new AppState());
        await interop.InitAsync(default, _callbacks);
        return interop;
    }

    [Fact]
    public async Task CursorChanged_ParsesKinds()
    {
        var interop = await InitAsync();

        await interop.CursorChanged("heading", Key, BlockKey, 3);
        await interop.CursorChanged("textBlock", BlockKey, BlockKey, 0);
        await interop.CursorChanged("detail", Key, BlockKey, 5);
        await interop.CursorChanged(null, null, null, 0);

        Assert.Equal(
            [
                new CursorInfo(ElementKind.Heading, Guid.Parse(Key), null, 3),
                new CursorInfo(ElementKind.TextBlock, Guid.Parse(BlockKey), Guid.Parse(BlockKey), 0),
                new CursorInfo(ElementKind.Detail, Guid.Parse(Key), Guid.Parse(BlockKey), 5),
                null,
            ],
            _callbacks.Cursors);
    }

    [Fact]
    public async Task CursorChanged_BadKey_Ignored()
    {
        var interop = await InitAsync();

        await interop.CursorChanged("textBlock", "no-key", BlockKey, 0);
        await interop.CursorChanged("heading", null, null, 0);
        await interop.CursorChanged("detail", Key, null, 0);
        await interop.CursorChanged("detail", Key, "broken", 0);
        await interop.CursorChanged("paragraph", Key, BlockKey, 0);

        Assert.Empty(_callbacks.Cursors);
    }

    [Fact]
    public async Task SectionCommand_ParsesNames_UnknownIgnored()
    {
        var interop = await InitAsync();

        foreach (var name in new[] { "InsertAfter", "InsertChild", "Indent", "Outdent", "RemoveHeading", "Delete", "Explode", "insertafter", "2", "" })
        {
            await interop.SectionCommand(name);
        }

        Assert.Equal(
            [
                SectionCommand.InsertAfter, SectionCommand.InsertChild, SectionCommand.Indent, SectionCommand.Outdent,
                SectionCommand.RemoveHeading, SectionCommand.Delete,
            ],
            _callbacks.Commands);
    }

    [Fact]
    public async Task SectionCommand_ParsesRangeNames()
    {
        var interop = await InitAsync();

        foreach (var name in new[] { "RangeUp", "RangeDown", "Merge", "Wrap", "rangeup", "Range" })
        {
            await interop.SectionCommand(name);
        }

        Assert.Equal([SectionCommand.RangeUp, SectionCommand.RangeDown, SectionCommand.Merge, SectionCommand.Wrap], _callbacks.Commands);
    }

    [Fact]
    public async Task SetMarked_Serializes_KeysAndOwnTextOnly()
    {
        var interop = await InitAsync();

        await interop.SetMarkedAsync([new MarkedNode(Guid.Parse(Key), false), new MarkedNode(Guid.Parse(BlockKey), true)]);
        await interop.SetMarkedAsync([]);

        var payloads = _js.Calls.Where(c => c.Method == "setMarked").Select(c => JsonSerializer.Serialize(c.Args.Single())).ToArray();
        Assert.Equal(
            [$$"""[{"key":"{{Key}}","ownTextOnly":false},{"key":"{{BlockKey}}","ownTextOnly":true}]""", "[]"],
            payloads);
    }

    [Fact]
    public async Task SectionBoxMoved_Forwarded()
    {
        var interop = await InitAsync();

        await interop.SectionBoxMoved(123.5, true);
        await interop.SectionBoxMoved(0, false);

        Assert.Equal([(123.5, true), (0.0, false)], _callbacks.Boxes);
    }

    [Fact]
    public async Task Init_PassesBarHeight_RevealAndSetDocumentReachTheEditor()
    {
        var interop = await InitAsync();

        await interop.SetDocumentAsync("{}", true, keepCursor: false);
        await interop.RevealAsync(Guid.Parse(Key));
        await interop.RevealAsync(Guid.Empty);

        var options = _js.Calls.Single(c => c.Method == "createEditor").Args[2]!;
        Assert.Equal(40, options.GetType().GetProperty("barHeight")!.GetValue(options));
        Assert.Equal(
            [("setDocument", ["{}", true, false]), ("reveal", [Key]), ("reveal", [null])],
            _js.Calls.Where(c => c.Method is "setDocument" or "reveal").Select(c => (c.Method, c.Args)).ToArray(),
            new CallComparer());
    }

    private sealed class CallComparer : IEqualityComparer<(string Method, object?[] Args)>
    {
        public bool Equals((string Method, object?[] Args) x, (string Method, object?[] Args) y) =>
            x.Method == y.Method && x.Args.SequenceEqual(y.Args);

        public int GetHashCode((string Method, object?[] Args) obj) => obj.Method.GetHashCode();
    }

    /// <summary>The script side: every call is recorded; calls that want an object get one that records too.</summary>
    private sealed class FakeJs : IJSRuntime, IJSObjectReference
    {
        public List<(string Method, object?[] Args)> Calls { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Calls.Add((identifier, args ?? []));
            return ValueTask.FromResult(typeof(TValue) == typeof(IJSObjectReference) ? (TValue)(object)this : default!);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingCallbacks : IEditorCallbacks
    {
        public List<CursorInfo?> Cursors { get; } = [];

        public List<SectionCommand> Commands { get; } = [];

        public List<(double BarTop, bool Visible)> Boxes { get; } = [];

        public Task OnDocumentChanged(string docJson) => Task.CompletedTask;

        public Task OnCursorChanged(CursorInfo? cursor)
        {
            Cursors.Add(cursor);
            return Task.CompletedTask;
        }

        public Task OnSectionCommand(SectionCommand command)
        {
            Commands.Add(command);
            return Task.CompletedTask;
        }

        public Task OnSectionBoxMoved(double barTop, bool visible)
        {
            Boxes.Add((barTop, visible));
            return Task.CompletedTask;
        }

        public Task OnChipClicked(Guid noteId) => Task.CompletedTask;

        public Task OnChipRemoved(Guid textBlockKey, Guid noteId) => Task.CompletedTask;

        public Task OnNoteDropped(Guid noteBlockKey, Guid? afterTextBlockKey) => Task.CompletedTask;
    }
}
