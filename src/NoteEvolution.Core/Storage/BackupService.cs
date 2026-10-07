using System.Globalization;
using NoteEvolution.Core.Format;

namespace NoteEvolution.Core.Storage;

/// <summary>Copies vault files to <c>.noteevolution/backups/yyyy-MM-dd/&lt;relative path&gt;</c> before they are overwritten.</summary>
public sealed class BackupService(string vaultRoot, IClock clock)
{
    private const string DayFormat = "yyyy-MM-dd";
    private const int RetentionDays = 30;

    private readonly string _root = Path.GetFullPath(vaultRoot);

    private string BackupsDirectory => Path.Combine(_root, ".noteevolution", "backups");

    /// <summary>Backs the file up unless today's backup already exists or the file does not exist (yet).</summary>
    /// <exception cref="ArgumentException">The file is not inside the vault.</exception>
    public void EnsureDailyBackup(string path)
    {
        var target = Path.Combine(DayDirectory(), RelativePath(path));
        if (File.Exists(target) || !File.Exists(path))
        {
            return;
        }

        CopyAtomically(path, target);
    }

    /// <summary>Always copies the file to <c>&lt;relative path&gt;.HHmmss.bak</c> in today's folder; returns that path.</summary>
    /// <exception cref="ArgumentException">The file is not inside the vault.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public string CreateBackup(string path)
    {
        var baseName = Path.Combine(DayDirectory(), RelativePath(path)) + "." + clock.Now.ToString("HHmmss", CultureInfo.InvariantCulture);
        var target = baseName + ".bak";
        for (var n = 2; File.Exists(target); n++)
        {
            target = $"{baseName}-{n}.bak";
        }

        CopyAtomically(path, target);
        return target;
    }

    /// <summary>Deletes day folders older than 30 days; folders whose name is not a date are left alone.</summary>
    public void Cleanup()
    {
        if (!Directory.Exists(BackupsDirectory))
        {
            return;
        }

        var oldest = Today().AddDays(-RetentionDays);
        foreach (var directory in Directory.EnumerateDirectories(BackupsDirectory))
        {
            if (DateOnly.TryParseExact(Path.GetFileName(directory), DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                && day < oldest)
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Housekeeping only: a folder that is in use now is removed by a later cleanup.
                }
            }
        }
    }

    private DateOnly Today() => DateOnly.FromDateTime(clock.Now.DateTime);

    private string DayDirectory() => Path.Combine(BackupsDirectory, Today().ToString(DayFormat, CultureInfo.InvariantCulture));

    private string RelativePath(string path)
    {
        var relative = Path.GetRelativePath(_root, Path.GetFullPath(path));
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{path}' liegt nicht im Vault '{_root}'.", nameof(path));
        }

        return relative;
    }

    /// <summary>A half-written backup must never look like a complete one, so it goes through a temporary file.</summary>
    private static void CopyAtomically(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        AtomicFile.Write(target, File.ReadAllBytes(source));
    }
}
