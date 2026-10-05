using System.Text;
using NoteEvolution.Core.Vaults;

namespace NoteEvolution.TestSupport;

/// <summary>A temporary vault folder filled with the given files; deleted on dispose.</summary>
public sealed class TestVault : IDisposable
{
    private readonly TempDir _dir = new();

    private TestVault()
    {
    }

    public string Root => _dir.Path;

    /// <summary>Creates a vault folder with <c>(relative path, content)</c> files; <c>/</c> is accepted in paths.</summary>
    public static TestVault Create(params (string Path, string Content)[] files)
    {
        var vault = new TestVault();
        foreach (var (path, content) in files)
        {
            vault._dir.Write(path, content);
        }
        return vault;
    }

    public Vault Open() => Vault.Open(Root);

    /// <summary>The file's content as UTF-8 text (a BOM is not stripped).</summary>
    public string Read(string relPath) =>
        new UTF8Encoding(false).GetString(File.ReadAllBytes(System.IO.Path.Combine(Root, relPath)));

    public void Dispose() => _dir.Dispose();
}
