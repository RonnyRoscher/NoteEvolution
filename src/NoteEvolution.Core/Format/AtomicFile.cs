namespace NoteEvolution.Core.Format;

public static class AtomicFile
{
    /// <summary>
    /// Writes <paramref name="bytes"/> to a temporary file next to <paramref name="path"/> and
    /// then moves it over the target, so the target is never left half-written.
    /// </summary>
    public static void Write(string path, byte[] bytes)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        var tempPath = Path.Combine(directory, "." + Path.GetFileName(fullPath) + ".ne-tmp");

        try
        {
            File.WriteAllBytes(tempPath, bytes);
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
