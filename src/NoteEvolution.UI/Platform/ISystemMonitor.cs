namespace NoteEvolution.UI.Platform;

/// <summary>What a part of the app's data on disk is.</summary>
public enum DataPartKind
{
    /// <summary><c>vectors.db</c>: the meaning vectors of the notes.</summary>
    NoteVectors,

    /// <summary><c>book-vectors.db</c>: the meaning vectors of the book's blocks (repetitions).</summary>
    BookVectors,

    /// <summary><c>understanding.db</c>: what the language model made of the notes.</summary>
    Understanding,

    /// <summary><c>history.db</c>: the versions of the text blocks.</summary>
    History,

    /// <summary>The <c>backups</c> folder: the daily copies before a file is first written.</summary>
    Backups,

    /// <summary>The <c>logs</c> folder.</summary>
    Logs,

    /// <summary>Every other file below <c>.noteevolution</c> (settings, pending updates, repetitions, ...).</summary>
    Other,

    /// <summary>The Markdown file of the open book.</summary>
    Book,

    /// <summary>All Markdown pages and journals of the vault (not in the sum).</summary>
    VaultPages,

    /// <summary>The downloaded AI model in the user data folder (not in the sum).</summary>
    Model,
}

/// <param name="Kind">What the part is.</param>
/// <param name="Bytes">Its size; a database with its <c>-wal</c>/<c>-shm</c> files, a folder with everything below it.</param>
/// <param name="Counted">Whether it is in <see cref="DataUsage.Total"/>.</param>
/// <param name="Name">The file name of the open book (<see cref="DataPartKind.Book"/>).</param>
public sealed record DataPart(DataPartKind Kind, long Bytes, bool Counted, string? Name = null);

/// <summary>The app's data on disk, part by part (the tooltip of "App-Daten").</summary>
public sealed record DataUsage(IReadOnlyList<DataPart> Parts)
{
    /// <summary>The size shown as "App-Daten": the parts that are counted.</summary>
    public long Total => Parts.Where(p => p.Counted).Sum(p => p.Bytes);
}

/// <summary>
/// One reading of the computer's load (spec 11.2); every value is <c>null</c> when it cannot be told on this platform.
/// </summary>
/// <param name="CpuPercent">The processor load of the whole computer since the previous reading, 0–100.</param>
/// <param name="RamUsedGb">The memory in use, in GB (2^30 bytes).</param>
/// <param name="RamTotalGb">The memory of the computer, in GB.</param>
/// <param name="DiskFreeGb">The free space on the vault's drive, in GB; <c>null</c> without a vault.</param>
/// <param name="OllamaCpuPercent">The processor share of Ollama's processes since the previous reading, 0–100.</param>
/// <param name="OllamaRamGb">The memory of Ollama's processes, in GB.</param>
/// <param name="AppCpuPercent">The processor share of NoteEvolution since the previous reading, 0–100.</param>
/// <param name="AppRamGb">The memory of NoteEvolution, in GB.</param>
/// <param name="Data">The app's data on disk, part by part (<see cref="SystemReadings.DataUsage"/>); <c>null</c> without a vault.</param>
/// <param name="OnBattery">The computer runs on battery; <c>null</c> when that cannot be told.</param>
public sealed record SystemSnapshot(
    double? CpuPercent, double? RamUsedGb, double? RamTotalGb, double? DiskFreeGb,
    double? OllamaCpuPercent, double? OllamaRamGb, double? AppCpuPercent, double? AppRamGb, bool? OnBattery,
    DataUsage? Data = null);

/// <summary>Reads the computer's load for the header's system meter and the language model's battery pause.</summary>
public interface ISystemMonitor
{
    /// <summary>
    /// Reads the current values; cheap enough to be called every 2 s. Processor loads cover the time since the previous
    /// call (the first call has none). Never throws: what cannot be read is <c>null</c>.
    /// </summary>
    /// <param name="vaultRoot">The open vault's folder, whose drive's free space and data are read; none if <c>null</c>.</param>
    /// <param name="bookFile">The open book's Markdown file, counted with the app's data; none if <c>null</c>.</param>
    SystemSnapshot Read(string? vaultRoot, string? bookFile = null);
}
