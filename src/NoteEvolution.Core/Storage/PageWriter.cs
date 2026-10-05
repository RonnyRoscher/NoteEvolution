using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Vaults;

namespace NoteEvolution.Core.Storage;

public sealed class PageWriter(IVault vault, BackupService backups, SelfWriteRegistry registry) : IPageWriter
{
    private const int Attempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>How the writer waits between attempts; tests replace it so they do not really sleep.</summary>
    internal Action<TimeSpan> Sleep { get; set; } = Thread.Sleep;

    public void Save(Page page)
    {
        if (page.IsReadOnly)
        {
            throw new ReadOnlyPageException($"Die Datei '{page.FilePath}' ist schreibgeschützt: {page.ParseError}");
        }

        var bytes = PageSerializer.Serialize(page);

        // The file is only ever replaced with bytes that parse back safely.
        var check = LogseqParser.Parse(page.FilePath, bytes);
        if (check.IsReadOnly)
        {
            throw new InvalidOperationException(
                $"Die Datei '{page.FilePath}' wird nicht geschrieben, da das Ergebnis nicht sicher lesbar wäre: {check.ParseError}");
        }

        // A file that is briefly locked by another process blocks the backup read as well as the replace.
        WithRetry(page.FilePath, () =>
        {
            backups.EnsureDailyBackup(page.FilePath);
            AtomicFile.Write(page.FilePath, bytes);
        });

        registry.Record(page.FilePath, bytes);
        page.MarkSaved();
        vault.ReplacePage(page);
    }

    private void WithRetry(string path, Action write)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                write();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == Attempts)
                {
                    throw new IOException($"Die Datei '{path}' konnte nicht geschrieben werden: {ex.Message}", ex);
                }
            }

            Sleep(RetryDelay);
        }
    }
}
