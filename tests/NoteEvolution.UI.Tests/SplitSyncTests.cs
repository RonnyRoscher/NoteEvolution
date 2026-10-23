using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NoteEvolution.AI.Relevance;
using NoteEvolution.AI.Search;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Editor;
using NoteEvolution.UI.Resources;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>The two halves of a split view stay in step: a save in one shows in the other, and "Relevant" follows the active half.</summary>
public class SplitSyncTests : UiTestContext
{
    private readonly TestVault _vault = TestVault.Create(
        ("pages/Buch - Alpha.md",
            "title:: Alpha\ntype:: book\n\n" +
            "- # Eins\n" +
            "\t- Erster Text\n" +
            "- # Zwei\n" +
            "\t- Dritter Text\n"));

    private Book Book => State.CurrentBook!;

    private string BookFile => File.ReadAllText(Path.Combine(_vault.Root, "pages", "Buch - Alpha.md"));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _vault.Dispose();
        }
    }

    /// <summary>Records the section of every topic it is asked about.</summary>
    private sealed class RecordingRelevance : IRelevanceService
    {
        public ConcurrentQueue<Guid> Sections { get; } = new();

        public bool IsAvailable => true;

        public IReadOnlyList<SearchHit> Relevant(TopicRequest topic, Func<Guid, bool> include, int limit = 30)
        {
            Sections.Enqueue(topic.SectionKey);
            return [];
        }

        public IReadOnlyList<SectionHit> WhereTo(Guid noteKey, string noteText, Book book, int limit = 5) => [];

        public IReadOnlyList<PlacementHit> Placements(Guid noteKey, string noteText, Book book, int limit = 3) => [];

        public IReadOnlyList<TopicHit> Topics(Guid noteKey, string noteText, Book book, int limit = 3) => [];

        public IReadOnlyList<SearchHit> Sources(Book book, Guid textBlockKey, Func<Guid, bool> include, int limit = 5) => [];
    }

    private Guid SectionKey(string title) => Book.Root.Children.Single(n => n.Title == title).Key;

    private Guid KeyOf(string content) => Book.Page.AllBlocks().Single(b => b.Content == content).Key;

    private CursorInfo CursorIn(string content) => new(ElementKind.TextBlock, KeyOf(content), KeyOf(content), 0);

    /// <summary>Opens the vault at "Eins", splits the view and renders both editors (<c>Editors[0]</c> upper, <c>Editors[1]</c> lower).</summary>
    private async Task<(IRenderedComponent<EditorPane> Upper, IRenderedComponent<EditorPane> Lower)> RenderEditorsAsync(
        Action<VaultSession>? configure = null)
    {
        UseEditorPerPane();
        var session = await OpenSessionAsync(_vault);
        configure?.Invoke(session);
        State.CurrentSectionKey = SectionKey("Eins");
        State.SetSplit(true);
        var upper = Render<EditorPane>(p => p.Add(e => e.View, State.Views[0]));
        var lower = Render<EditorPane>(p => p.Add(e => e.View, State.Views[1]));
        upper.WaitForAssertion(() => Assert.NotEmpty(Editors[0].Documents));
        lower.WaitForAssertion(() => Assert.NotEmpty(Editors[1].Documents));
        return (upper, lower);
    }

    private static async Task ClickIntoAsync(IRenderedComponent<EditorPane> pane, FakeEditorInterop editor, CursorInfo cursor)
    {
        await pane.InvokeAsync(() => editor.Callbacks!.OnEditorFocused());
        await pane.InvokeAsync(() => editor.Callbacks!.OnCursorChanged(cursor));
    }

    private static string WithFirstText(FakeEditorInterop editor, string text)
    {
        var doc = JsonNode.Parse(editor.Json)!;
        var block = doc["content"]!.AsArray().First(n => (string?)n!["type"] == "textBlock")!;
        block["content"]![0]!["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
        return doc.ToJsonString();
    }

    /// <summary>The editor's document with the text block that starts with <paramref name="old"/> showing <paramref name="text"/>.</summary>
    private static string WithText(FakeEditorInterop editor, string old, string text)
    {
        var doc = JsonNode.Parse(editor.Json)!;
        var block = doc["content"]!.AsArray().First(n =>
            (string?)n!["type"] == "textBlock" && (string?)n["content"]![0]!["content"]![0]!["text"] == old)!;
        block["content"]![0]!["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
        return doc.ToJsonString();
    }

    private string Text(string key) => Services.GetRequiredService<IStringLocalizer<Strings>>()[key].Value;

    private string BookPath => Path.Combine(_vault.Root, "pages", "Buch - Alpha.md");

    [Fact]
    public async Task UpperSaveFails_LowerHalfRefusesInputAndFocus_UntilTheUpperTextIsSaved_ThenBothTextsAreKept()
    {
        var (upper, lower) = await RenderEditorsAsync();
        await ClickIntoAsync(upper, Editors[0], CursorIn("Erster Text"));
        Assert.Same(State.Views[0], State.ActiveView);

        using (new FileStream(BookPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await upper.InvokeAsync(() => Editors[0].Callbacks!.OnDocumentChanged(WithText(Editors[0], "Erster Text", "Oben, nicht gespeichert")));

            // Text waiting for its debounce locks nothing: the lower half may still be clicked into.
            Assert.Empty(lower.FindAll(".ne-editor-hint"));

            // Typing in the lower half (its editor kept the focus): the upper text cannot be saved, so the lower half
            // does not become active, does not take the change and shows the book again.
            var lowerDocuments = Editors[1].Documents.Count;
            await lower.InvokeAsync(() => Editors[1].Callbacks!.OnDocumentChanged(WithText(Editors[1], "Dritter Text", "Unten verloren")));

            Assert.Same(State.Views[0], State.ActiveView);
            Assert.True(State.Views[0].HasFailedEditorText!());
            Assert.False(State.Views[1].HasUnsavedEditorText!());
            Assert.True(Editors[1].Documents.Count > lowerDocuments);
            Assert.True(Editors[1].KeepCursor[^1]);
            lower.WaitForAssertion(() => Assert.Equal(Text("EditorOtherHalfUnsaved"), lower.Find(".ne-editor-hint").TextContent));
            Assert.True(lower.Find(".ne-editor-host").HasAttribute("inert"));
            Assert.Empty(upper.FindAll(".ne-editor-hint"));

            // The focus does not make it active either.
            await lower.InvokeAsync(() => Editors[1].Callbacks!.OnEditorFocused());
            Assert.Same(State.Views[0], State.ActiveView);
        }

        Assert.DoesNotContain("Oben, nicht gespeichert", BookFile);

        // The file is writable again: the upper text is saved, the lock goes, and the lower half shows the upper text.
        await upper.InvokeAsync(() => State.Views[0].FlushEditor!());
        Assert.Contains("Oben, nicht gespeichert", BookFile);
        lower.WaitForAssertion(() => Assert.Empty(lower.FindAll(".ne-editor-hint")));
        Assert.False(lower.Find(".ne-editor-host").HasAttribute("inert"));
        lower.WaitForAssertion(() => Assert.Contains("Oben, nicht gespeichert", Editors[1].Json));

        await ClickIntoAsync(lower, Editors[1], CursorIn("Dritter Text"));
        Assert.Same(State.Views[1], State.ActiveView);
        await lower.InvokeAsync(() => Editors[1].Callbacks!.OnDocumentChanged(WithText(Editors[1], "Dritter Text", "Unten getippt")));
        await lower.InvokeAsync(() => State.Views[1].FlushEditor!());

        Assert.Contains("Oben, nicht gespeichert", BookFile);
        Assert.Contains("Unten getippt", BookFile);
        Assert.DoesNotContain("Unten verloren", BookFile);
    }

    [Fact]
    public async Task TypingInTheInactiveHalf_MakesItActive_AfterTheOtherHalfSaved()
    {
        var (upper, lower) = await RenderEditorsAsync();
        await upper.InvokeAsync(() => Editors[0].Callbacks!.OnDocumentChanged(WithText(Editors[0], "Erster Text", "Oben getippt")));

        // No focus event (the editor kept the DOM focus): the change itself makes the lower half active, after the upper saved.
        await lower.InvokeAsync(() => Editors[1].Callbacks!.OnDocumentChanged(WithText(Editors[1], "Dritter Text", "Unten getippt")));

        Assert.Same(State.Views[1], State.ActiveView);
        Assert.Contains("Oben getippt", BookFile);
        await lower.InvokeAsync(() => State.Views[1].FlushEditor!());
        Assert.Contains("Unten getippt", BookFile);
    }

    [Fact]
    public async Task Split_LowerEditorRevealsTheActiveSection_WithoutTakingTheFocus()
    {
        var (_, lower) = await RenderEditorsAsync();

        lower.WaitForAssertion(() => Assert.Equal([SectionKey("Eins")], Editors[1].Reveals));
        Assert.Equal([false], Editors[1].RevealFocus);
        Assert.Empty(Editors[0].Reveals);
        Assert.Same(State.Views[0], State.ActiveView);
    }

    [Fact]
    public async Task DirtyPage_IsReportedByTheUpperHalfOnly_AndWarnedOnce()
    {
        var (upper, lower) = await RenderEditorsAsync();
        using (new FileStream(BookPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // Changes in the shared book page that cannot be saved (e.g. a merge whose save failed).
            Book.Page.AllBlocks().Single(b => b.Content == "Erster Text").SetContent("Im Speicher geändert");
            await upper.InvokeAsync(() => State.FlushEditor!());
            Assert.True(Book.Page.IsDirty);

            Assert.True(State.Views[0].HasUnsavedEditorText!());
            Assert.False(State.Views[1].HasUnsavedEditorText!());
            Assert.True(State.HasUnsavedEditorText!());

            Assert.True(await upper.InvokeAsync(() => State.WarnUnsavedText!()));
            upper.WaitForAssertion(() => Assert.Equal(Text("EditorCloseUnsaved"), upper.Find(".ne-editor-error").TextContent));
            Assert.Empty(lower.FindAll(".ne-editor-error"));

            // The lower editor's own text waiting for the next try: it warns, and the upper one no longer does.
            await ClickIntoAsync(lower, Editors[1], CursorIn("Dritter Text"));
            Assert.Same(State.Views[1], State.ActiveView);
            await lower.InvokeAsync(() => Editors[1].Callbacks!.OnDocumentChanged(WithText(Editors[1], "Dritter Text", "Unten gesperrt")));
            await lower.InvokeAsync(() => State.Views[1].FlushEditor!());
            Assert.True(State.Views[1].HasUnsavedEditorText!());
            Assert.False(State.Views[0].HasUnsavedEditorText!());
            Assert.True(State.HasUnsavedEditorText!());
        }
    }

    [Fact]
    public async Task SaveInUpperHalf_ShowsInLowerHalf_KeepingItsCursor()
    {
        var (upper, lower) = await RenderEditorsAsync();
        var lowerDocuments = Editors[1].Documents.Count;

        await upper.InvokeAsync(() => Editors[0].Callbacks!.OnDocumentChanged(WithFirstText(Editors[0], "Oben getippt")));
        AdvanceUntil(lower, () => Assert.True(Editors[1].Documents.Count > lowerDocuments), TimeSpan.FromMilliseconds(500));

        Assert.Contains("Oben getippt", BookFile);
        Assert.True(Editors[1].KeepCursor[^1]);
        Assert.Contains("Oben getippt", Editors[1].Json);
    }

    [Fact]
    public async Task TypeInUpper_ClickIntoLower_SavesFirst()
    {
        var (upper, lower) = await RenderEditorsAsync();
        await upper.InvokeAsync(() => Editors[0].Callbacks!.OnDocumentChanged(WithFirstText(Editors[0], "Noch nicht gespeichert")));
        Assert.DoesNotContain("Noch nicht gespeichert", BookFile);

        // Raised on the lower fake: the text must be in the file by the time the lower half is the active one.
        var fileWhenActive = "";
        var lowerEditor = Editors[1];
        State.Changed += () =>
        {
            if (State.ActiveView == State.Views[1] && fileWhenActive == "")
            {
                fileWhenActive = BookFile;
            }
        };
        await ClickIntoAsync(lower, lowerEditor, CursorIn("Dritter Text"));

        Assert.Same(State.Views[1], State.ActiveView);
        Assert.Contains("Noch nicht gespeichert", fileWhenActive);
        Assert.Contains("Noch nicht gespeichert", BookFile);
        lower.WaitForAssertion(() => Assert.Contains("Noch nicht gespeichert", lowerEditor.Json));
    }

    [Fact]
    public async Task RelevantTab_FollowsTheActiveHalf()
    {
        var relevance = new RecordingRelevance();
        var (upper, lower) = await RenderEditorsAsync(session => session.WrapRelevance = _ => relevance);
        var relevant = Render<NotesPane>();
        relevant.Find(".ne-tab-relevant").Click();
        relevant.WaitForAssertion(() => Assert.Equal(SectionKey("Eins"), relevance.Sections.LastOrDefault()));

        await ClickIntoAsync(lower, Editors[1], CursorIn("Dritter Text"));
        Assert.Equal(SectionKey("Zwei"), State.CurrentSectionKey);
        AdvanceUntil(relevant, () => Assert.Equal(SectionKey("Zwei"), relevance.Sections.Last()), RelevantTab.Debounce);

        await ClickIntoAsync(upper, Editors[0], CursorIn("Erster Text"));
        AdvanceUntil(relevant, () => Assert.Equal(SectionKey("Eins"), relevance.Sections.Last()), RelevantTab.Debounce);
    }
}
