using System.Text;
using Microsoft.Extensions.Time.Testing;
using NoteEvolution.AI.Search;
using NoteEvolution.Core.Storage;
using NoteEvolution.Core.Vaults;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

public class VaultSessionTests
{
    private const string NoteId = "7f3a91c2-4b1d-4e8a-9c3f-1a2b3c4d5e6f";
    private static readonly NoteFilter All = new(false, null, null);

    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.FromHours(2)));
    private readonly FakeTimeProvider _time = new();

    private static TestVault BookWithLinkedNote() => TestVault.Create(
        ("pages/Buch - Alpha.md",
            "title:: Alpha\ntype:: book\n\n- # Eins\n\t- Übernommener Text\n\t  id:: 6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70\n" +
            $"\t  source:: (({NoteId}))\n"),
        ("journals/2026_03_01.md", $"- Vertrauen wächst\n  id:: {NoteId}\n- Zweiter Gedanke\n"));

    private Task<VaultSession> Open(TestVault tv, Func<Action, Task>? dispatch = null) =>
        VaultSession.OpenAsync(tv.Root, _clock, timeProvider: _time, dispatch: dispatch);

    private static string Journal(TestVault tv) => Path.Combine(tv.Root, "journals", "2026_03_01.md");

    private static List<string> Find(VaultSession session, string text) =>
        [.. session.Search.Search(new SearchQuery(text, All)).Select(h => session.Notes.Get(h.NoteBlockKey)!.Block.Content)];

    [Fact]
    public async Task OpenAsync_ComposesServices_AndRunsStartupSteps()
    {
        using var tv = BookWithLinkedNote();
        var oldBackup = Directory.CreateDirectory(Path.Combine(tv.Root, ".noteevolution", "backups", "2026-08-01")).FullName;

        using var session = await Open(tv);

        Assert.Equal(Path.GetFullPath(tv.Root), session.Vault.Root);
        Assert.Equal(["Alpha"], session.Vault.Books.Select(b => b.Title));
        var report = Assert.IsType<Core.Links.LinkCheckReport>(session.StartupReport);
        var missing = Assert.Single(report.Missing);
        Assert.Equal(Guid.Parse(NoteId), missing.NoteBlockId);
        Assert.Equal(["Vertrauen wächst"], Find(session, "vertrauen"));
        Assert.False(Directory.Exists(oldBackup));
    }

    [Fact]
    public async Task OpenAsync_MissingFolder_Throws()
    {
        using var dir = new TempDir();

        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => VaultSession.OpenAsync(Path.Combine(dir.Path, "fehlt"), _clock, timeProvider: _time));
    }

    [Fact]
    public async Task HandleExternalChange_Reload_UpdatesSearchAndRaisesPagesChanged()
    {
        using var tv = BookWithLinkedNote();
        using var session = await Open(tv);
        var changed = new List<string>();
        session.PagesChanged += changed.Add;

        File.WriteAllText(Journal(tv), $"- Vertrauen wächst\n  id:: {NoteId}\n- Neuer Gedanke über Mut\n", new UTF8Encoding(false));
        session.HandleExternalChange(Journal(tv));

        Assert.Equal([Path.GetFullPath(Journal(tv))], changed);
        Assert.Equal(["Neuer Gedanke über Mut"], Find(session, "mut"));
    }

    [Fact]
    public async Task HandleExternalChange_DeletedFile_RemovesPageFromSearch()
    {
        using var tv = BookWithLinkedNote();
        using var session = await Open(tv);
        var changed = new List<string>();
        session.PagesChanged += changed.Add;

        File.Delete(Journal(tv));
        session.HandleExternalChange(Journal(tv));

        Assert.Single(changed);
        Assert.Empty(Find(session, "vertrauen"));
        Assert.Null(session.Vault.FindPageByPath(Journal(tv)));
    }

    [Fact]
    public async Task WatcherEvent_IsDispatched_AndHandled()
    {
        using var tv = BookWithLinkedNote();
        var dispatched = 0;
        using var session = await Open(tv, action =>
        {
            Interlocked.Increment(ref dispatched);
            action();
            return Task.CompletedTask;
        });
        var changed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.PagesChanged += path => changed.TrySetResult(path);

        File.WriteAllText(Journal(tv), $"- Vertrauen wächst\n  id:: {NoteId}\n- Gedanke über Wolken\n", new UTF8Encoding(false));

        // The file system event arrives on its own time; each advance fires a debounce that has started meanwhile.
        for (var i = 0; i < 100 && !changed.Task.IsCompleted; i++)
        {
            await Task.Delay(50);
            _time.Advance(TimeSpan.FromMilliseconds(300));
        }

        Assert.Equal(Path.GetFullPath(Journal(tv)), await changed.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.True(dispatched > 0);
        Assert.Equal(["Gedanke über Wolken"], Find(session, "wolken"));
    }

    [Fact]
    public async Task HandleExternalChange_ConflictingEdit_RaisesConflict_AndBlocksSave()
    {
        using var tv = BookWithLinkedNote();
        using var session = await Open(tv);
        var page = session.Vault.FindPageByPath(Journal(tv))!;
        page.Roots[1].SetContent("Lokal geändert");
        var conflicts = new List<ExternalChangeOutcome.Conflict>();
        session.ConflictDetected += conflicts.Add;
        const string external = $"- Vertrauen wächst\n  id:: {NoteId}\n- Extern geändert\n";

        File.WriteAllText(Journal(tv), external, new UTF8Encoding(false));
        session.HandleExternalChange(Journal(tv));

        Assert.Single(conflicts);
        Assert.True(session.HasOpenConflict(Journal(tv)));
        Assert.False(session.TrySave(page, out var error));
        Assert.NotNull(error);
        Assert.Equal(external, tv.Read("journals/2026_03_01.md"));
    }

    [Fact]
    public async Task TrySave_WhileDeletedFileConflictOpen_DoesNotRecreateFile()
    {
        using var tv = BookWithLinkedNote();
        using var session = await Open(tv);
        var page = session.Vault.FindPageByPath(Journal(tv))!;
        page.Roots[1].SetContent("Lokal geändert");

        File.Delete(Journal(tv));
        session.HandleExternalChange(Journal(tv));

        Assert.True(session.HasOpenConflict(Journal(tv)));
        Assert.False(session.TrySave(page, out _));
        Assert.False(File.Exists(Journal(tv)));
    }

    [Fact]
    public async Task ResolveConflict_ClosesIt_AndSavingWorksAgain()
    {
        using var tv = BookWithLinkedNote();
        using var session = await Open(tv);
        var page = session.Vault.FindPageByPath(Journal(tv))!;
        page.Roots[1].SetContent("Lokal geändert");
        ExternalChangeOutcome.Conflict? conflict = null;
        session.ConflictDetected += c => conflict = c;
        File.Delete(Journal(tv));
        session.HandleExternalChange(Journal(tv));

        session.ResolveConflict(conflict!, conflict!.Conflicts.ToDictionary(c => c.Local.Key, _ => ConflictChoice.Mine));

        Assert.False(session.HasOpenConflict(Journal(tv)));
        Assert.True(session.TrySave(session.Vault.FindPageByPath(Journal(tv))!, out var error), error?.Message);
        Assert.Contains("Lokal geändert", tv.Read("journals/2026_03_01.md"));
    }

    [Fact]
    public async Task TrySave_FileChangedExternally_MergesAndSaves_ReturnsFalse()
    {
        using var tv = BookWithLinkedNote();
        using var session = await Open(tv);
        var page = session.Vault.FindPageByPath(Journal(tv))!;
        page.Roots[1].SetContent("Lokal geändert");
        var changed = new List<string>();
        session.PagesChanged += changed.Add;

        // Another program edits a different block before the watcher reported it.
        File.WriteAllText(Journal(tv), $"- Vertrauen wächst extern\n  id:: {NoteId}\n- Zweiter Gedanke\n", new UTF8Encoding(false));

        Assert.False(session.TrySave(page, out var error));

        Assert.IsType<FileChangedExternallyException>(error);
        Assert.Single(changed);
        Assert.Equal($"- Vertrauen wächst extern\n  id:: {NoteId}\n- Lokal geändert\n", tv.Read("journals/2026_03_01.md"));
        Assert.False(session.Vault.FindPageByPath(Journal(tv))!.IsDirty);
    }

    [Fact]
    public async Task TrySave_Success_WritesPage()
    {
        using var tv = BookWithLinkedNote();
        using var session = await Open(tv);
        var page = session.Vault.FindPageByPath(Journal(tv))!;
        page.Roots[1].SetContent("Neu");

        Assert.True(session.TrySave(page, out var error));

        Assert.Null(error);
        Assert.Equal($"- Vertrauen wächst\n  id:: {NoteId}\n- Neu\n", tv.Read("journals/2026_03_01.md"));
    }

    [Fact]
    public async Task TryUndo_WhileDeletedFileConflictOpen_RefusesAndDoesNotRecreateFile()
    {
        using var tv = BookWithLinkedNote();
        using var session = await Open(tv);
        var page = session.Vault.FindPageByPath(Journal(tv))!;
        page.Roots[1].SetContent("Lokal geändert");
        var undo = new SavingUndo(() => session.Writer.Save(page));
        session.Undo.Push(undo);
        var conflictsChanged = 0;
        session.ConflictsChanged += () => conflictsChanged++;

        File.Delete(Journal(tv));
        session.HandleExternalChange(Journal(tv));

        Assert.True(session.HasAnyOpenConflict);
        Assert.Equal(1, conflictsChanged);
        Assert.False(session.TryUndo(out var error));
        Assert.IsType<InvalidOperationException>(error);
        Assert.False(undo.Ran);
        Assert.True(session.Undo.CanUndo);
        Assert.False(File.Exists(Journal(tv)));
    }

    [Fact]
    public async Task TryUndo_NoConflict_Undoes()
    {
        using var tv = BookWithLinkedNote();
        using var session = await Open(tv);
        var undo = new SavingUndo(() => { });
        session.Undo.Push(undo);

        Assert.True(session.TryUndo(out var error));

        Assert.Null(error);
        Assert.True(undo.Ran);
        Assert.False(session.HasAnyOpenConflict);
    }

    [Fact]
    public async Task HandleExternalChange_AfterDispose_DoesNothing()
    {
        using var tv = BookWithLinkedNote();
        var session = await Open(tv);
        var page = session.Vault.FindPageByPath(Journal(tv))!;
        page.Roots[1].SetContent("Lokal geändert");
        var events = 0;
        session.PagesChanged += _ => events++;
        session.ConflictDetected += _ => events++;
        session.ConflictsChanged += () => events++;
        const string external = $"- Vertrauen wächst extern\n  id:: {NoteId}\n- Zweiter Gedanke\n";
        File.WriteAllText(Journal(tv), external, new UTF8Encoding(false));

        session.Dispose();
        session.HandleExternalChange(Journal(tv));

        Assert.Equal(0, events);
        Assert.Equal(external, tv.Read("journals/2026_03_01.md"));
        Assert.Same(page, session.Vault.FindPageByPath(Journal(tv)));
    }

    [Fact]
    public async Task WriteOperations_AfterDispose_Throw()
    {
        using var tv = BookWithLinkedNote();
        var session = await Open(tv);
        var page = session.Vault.FindPageByPath(Journal(tv))!;
        page.Roots[1].SetContent("Lokal geändert");
        ExternalChangeOutcome.Conflict? conflict = null;
        session.ConflictDetected += c => conflict = c;
        File.Delete(Journal(tv));
        session.HandleExternalChange(Journal(tv));

        session.Dispose();

        Assert.Throws<ObjectDisposedException>(() => session.TrySave(page, out _));
        Assert.Throws<ObjectDisposedException>(() => session.TryUndo(out _));
        Assert.Throws<ObjectDisposedException>(
            () => session.ResolveConflict(conflict!, conflict!.Conflicts.ToDictionary(c => c.Local.Key, _ => ConflictChoice.Mine)));
        Assert.False(File.Exists(Journal(tv)));
    }

    [Fact]
    public async Task TrySave_ReadOnlyPage_ReturnsFalseWithError()
    {
        using var tv = BookWithLinkedNote();
        File.WriteAllBytes(Path.Combine(tv.Root, "pages", "Kaputt.md"), [0x2D, 0x20, 0xFF, 0xFE, 0x0A]);
        using var session = await Open(tv);
        var page = session.Vault.Pages.Single(p => p.IsReadOnly);

        Assert.False(session.TrySave(page, out var error));

        Assert.IsType<ReadOnlyPageException>(error);
    }

    /// <summary>An undo action that records whether it ran (and may write, like the real ones).</summary>
    private sealed class SavingUndo(Action undo) : Core.Links.IUndoAction
    {
        public bool Ran { get; private set; }

        public string Description => "Übernehmen";

        public void Undo()
        {
            Ran = true;
            undo();
        }
    }
}
