using NoteEvolution.UI.Platform;

namespace NoteEvolution.UI.Tests;

/// <summary>A system monitor whose reading is set by the test; by default nothing is known.</summary>
public sealed class FakeSystemMonitor : ISystemMonitor
{
    private readonly Lock _gate = new();
    private SystemSnapshot _snapshot = new(null, null, null, null, null, null, null, null, null);
    private int _reads;

    /// <summary>What every <see cref="Read"/> returns.</summary>
    public SystemSnapshot Snapshot
    {
        get { lock (_gate) return _snapshot; }
        set { lock (_gate) _snapshot = value; }
    }

    /// <summary>How often <see cref="Read"/> was called (from any thread).</summary>
    public int Reads => Volatile.Read(ref _reads);

    /// <summary>The vault folder of the last <see cref="Read"/>.</summary>
    public string? LastVaultRoot { get; private set; }

    public SystemSnapshot Read(string? vaultRoot)
    {
        Interlocked.Increment(ref _reads);
        LastVaultRoot = vaultRoot;
        return Snapshot;
    }
}
