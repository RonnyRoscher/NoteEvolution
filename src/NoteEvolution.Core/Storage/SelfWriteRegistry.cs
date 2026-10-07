using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace NoteEvolution.Core.Storage;

/// <summary>
/// Remembers the SHA-256 of what NoteEvolution last wrote to each file, so the file watcher
/// (another thread) can tell its own writes from external changes.
/// </summary>
public sealed class SelfWriteRegistry
{
    private readonly ConcurrentDictionary<string, byte[]> _hashes = new(StringComparer.OrdinalIgnoreCase);

    public void Record(string path, byte[] bytes) => _hashes[Path.GetFullPath(path)] = SHA256.HashData(bytes);

    /// <summary>The current content of <paramref name="path"/> is exactly what was recorded last for it.</summary>
    public bool IsOwnWrite(string path, byte[] currentBytes) =>
        _hashes.TryGetValue(Path.GetFullPath(path), out var recorded)
        && CryptographicOperations.FixedTimeEquals(recorded, SHA256.HashData(currentBytes));
}
