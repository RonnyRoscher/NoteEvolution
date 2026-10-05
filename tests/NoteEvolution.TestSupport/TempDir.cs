using System.Text;

namespace NoteEvolution.TestSupport;

/// <summary>A unique temporary directory that is deleted recursively on dispose.</summary>
public sealed class TempDir : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ne-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Writes <paramref name="content"/> as UTF-8 without BOM, creating parent folders; returns the full path.</summary>
    public string Write(string relPath, string content)
    {
        var full = System.IO.Path.Combine(Path, relPath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, Utf8NoBom);
        return full;
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
