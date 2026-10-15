namespace NoteEvolution.UI.Platform;

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
/// <param name="OnBattery">The computer runs on battery; <c>null</c> when that cannot be told.</param>
public sealed record SystemSnapshot(
    double? CpuPercent, double? RamUsedGb, double? RamTotalGb, double? DiskFreeGb,
    double? OllamaCpuPercent, double? OllamaRamGb, double? AppCpuPercent, double? AppRamGb, bool? OnBattery);

/// <summary>Reads the computer's load for the header's system meter and the language model's battery pause.</summary>
public interface ISystemMonitor
{
    /// <summary>
    /// Reads the current values; cheap enough to be called every 2 s. Processor loads cover the time since the previous
    /// call (the first call has none). Never throws: what cannot be read is <c>null</c>.
    /// </summary>
    /// <param name="vaultRoot">The open vault's folder, whose drive's free space is read; none if <c>null</c>.</param>
    SystemSnapshot Read(string? vaultRoot);
}
