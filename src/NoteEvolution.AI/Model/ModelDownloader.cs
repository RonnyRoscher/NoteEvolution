using System.Security.Cryptography;

namespace NoteEvolution.AI.Model;

/// <summary>Downloads a model's files with the given client: streamed to a <c>.part</c> file, SHA-256 verified, then renamed atomically.</summary>
public sealed class ModelDownloader(HttpClient http, ModelStore store)
{
    private const int BufferSize = 81_920;
    private const long ReportInterval = 1_000_000;

    public async Task DownloadAsync(ModelInfo model, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var total = model.TotalSize;
        long done = 0;
        progress?.Report(new DownloadProgress(0, total));

        foreach (var file in model.Files)
        {
            var target = store.PathOf(model, file);
            if (ModelStore.HasSize(target, file.Size))
            {
                done += file.Size;
                progress?.Report(new DownloadProgress(done, total));
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await DownloadFileAsync(file, target, done, total, progress, ct);
            done += file.Size;
            progress?.Report(new DownloadProgress(done, total));
        }
    }

    private async Task DownloadFileAsync(ModelFile file, string target, long doneBefore, long total,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var part = target + ".part";
        var completed = false;
        try
        {
            string actualHash;
            try
            {
                using var response = await http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                    throw new ModelDownloadException($"Download of {file.RelativePath} failed: HTTP {(int)response.StatusCode}.");

                await using var source = await response.Content.ReadAsStreamAsync(ct);
                actualHash = await CopyAndHashAsync(source, part, doneBefore, total, progress, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new ModelDownloadException($"Download of {file.RelativePath} failed: {ex.Message}", ex);
            }
            catch (IOException ex)
            {
                throw new ModelDownloadException($"Download of {file.RelativePath} failed: {ex.Message}", ex);
            }

            if (!string.Equals(actualHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new ModelDownloadException($"Checksum mismatch for {file.RelativePath}: expected {file.Sha256}, got {actualHash}.");

            File.Move(part, target, overwrite: true);
            completed = true;
        }
        finally
        {
            if (!completed)
                TryDelete(part);
        }
    }

    private static async Task<string> CopyAndHashAsync(Stream source, string partPath, long doneBefore, long total,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var output = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
        var buffer = new byte[BufferSize];
        long read = 0, lastReported = 0;
        int n;
        while ((n = await source.ReadAsync(buffer, ct)) > 0)
        {
            hash.AppendData(buffer, 0, n);
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (read - lastReported >= ReportInterval)
            {
                lastReported = read;
                progress?.Report(new DownloadProgress(doneBefore + read, total));
            }
        }

        await output.FlushAsync(ct);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: a leftover .part file is overwritten by the next download anyway.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
