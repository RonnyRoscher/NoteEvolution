using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NoteEvolution.Core.Storage;
using NoteEvolution.TestSupport;
using NoteEvolution.UI.Platform;
using NoteEvolution.UI.State;

namespace NoteEvolution.UI.Tests;

/// <summary>A bUnit context with the services the components expect, all fake or in memory.</summary>
public abstract class UiTestContext : BunitContext
{
    protected UiTestContext()
    {
        Services.AddLocalization();
        Services.AddLogging();
        Services.AddSingleton<IPlatformServices>(Platform);
        Services.AddSingleton(Settings);
        Services.AddSingleton(State);
        Services.AddSingleton<IClock>(Clock);
        Services.AddSingleton<TimeProvider>(Time);
    }

    protected FakePlatformServices Platform { get; } = new();

    protected UiSettings Settings { get; } = new();

    protected AppState State { get; } = new();

    protected FakeClock Clock { get; } = new(new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.FromHours(2)));

    /// <summary>Never advances on its own, so the vault watcher's debounce timers only fire when a test says so.</summary>
    protected FakeTimeProvider Time { get; } = new();

    /// <summary>Opens <paramref name="vault"/> as the app's session (dispatching inline) and selects its first book.</summary>
    protected async Task<VaultSession> OpenSessionAsync(TestVault vault)
    {
        var session = await VaultSession.OpenAsync(vault.Root, Clock, timeProvider: Time);
        State.Session = session;
        State.CurrentBook = session.Vault.Books.FirstOrDefault();
        State.CurrentSectionKey = State.CurrentBook?.Root.Key ?? Guid.Empty;
        return session;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            State.Session?.Dispose();
            Platform.Dispose();
        }

        base.Dispose(disposing);
    }
}
