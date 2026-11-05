using System.Text;

namespace NoteEvolution.TestSupport;

/// <summary>
/// A unique temporary directory that is deleted recursively on dispose. A session still open holds its database (a
/// test's context closes the session after the test's vault is disposed); such a folder is deleted by a later dispose,
/// or at the end of the run.
/// </summary>
public sealed class TempDir : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>The folders that could not be deleted yet.</summary>
    private static readonly List<string> Left = [];

    static TempDir() => AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteLeft();

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
        DeleteLeft();
        if (Directory.Exists(Path) && !TryDelete(Path))
        {
            lock (Left)
            {
                Left.Add(Path);
            }
        }
    }

    private static void DeleteLeft()
    {
        lock (Left)
        {
            Left.RemoveAll(TryDelete);
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
