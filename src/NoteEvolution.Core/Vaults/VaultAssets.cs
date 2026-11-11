using NoteEvolution.Core.Books;

namespace NoteEvolution.Core.Vaults;

/// <summary>An image file was refused on import; <see cref="Exception.Message"/> is <c>"format"</c> or <c>"size"</c>.</summary>
public sealed class AssetRefusedException(string reason) : Exception(reason);

/// <summary>The <c>assets</c> folder of a vault: importing image files into it and resolving the paths books use for them.</summary>
public static class VaultAssets
{
    /// <summary>The largest image file that is imported, in bytes.</summary>
    public const long MaxBytes = 20 * 1024 * 1024;

    private const string AssetsFolder = "assets";

    private static readonly Dictionary<string, string> Mimes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
    };

    /// <summary>Whether the file name has an image extension (.png .jpg .jpeg .gif .webp .bmp, any case).</summary>
    public static bool IsImageName(string fileName) => Mimes.ContainsKey(Path.GetExtension(fileName));

    /// <summary>The MIME type of an image file name, by extension.</summary>
    /// <exception cref="ArgumentException">The name is not an image name.</exception>
    public static string MimeOf(string fileName) =>
        Mimes.TryGetValue(Path.GetExtension(fileName), out var mime)
            ? mime
            : throw new ArgumentException("Not an image file name.", nameof(fileName));

    /// <summary>
    /// Copies <paramref name="bytes"/> into <c>&lt;vaultRoot&gt;/assets</c> (created if missing) and returns the Markdown
    /// path relative to the directory of <paramref name="bookFilePath"/> with <c>/</c> separators (e.g.
    /// <c>../assets/name.png</c>). Every character of the name outside <c>[A-Za-z0-9._-]</c> becomes <c>-</c>, an empty
    /// stem becomes <c>bild</c>. The same name with the same bytes is reused; with other bytes a <c>-2</c>, <c>-3</c>, ...
    /// is put before the extension.
    /// </summary>
    /// <exception cref="AssetRefusedException"><c>"format"</c> for another extension, <c>"size"</c> for no bytes or more than <see cref="MaxBytes"/>.</exception>
    public static string Import(string vaultRoot, string bookFilePath, string fileName, byte[] bytes)
    {
        var name = Path.GetFileName(fileName);
        if (!IsImageName(name))
        {
            throw new AssetRefusedException("format");
        }

        if (bytes.Length == 0 || bytes.LongLength > MaxBytes)
        {
            throw new AssetRefusedException("size");
        }

        var extension = Path.GetExtension(name);
        var stem = new string(Path.GetFileNameWithoutExtension(name).Select(c => IsSafe(c) ? c : '-').ToArray());
        if (stem.Length == 0)
        {
            stem = "bild";
        }

        var assets = Path.Combine(vaultRoot, AssetsFolder);
        Directory.CreateDirectory(assets);

        for (var n = 1; ; n++)
        {
            var target = Path.Combine(assets, n == 1 ? stem + extension : $"{stem}-{n}{extension}");
            try
            {
                using var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(bytes);
            }
            catch (IOException) when (File.Exists(target))
            {
                if (File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes))
                {
                    return RelativeTo(bookFilePath, target);
                }

                continue;
            }

            return RelativeTo(bookFilePath, target);
        }
    }

    /// <summary>
    /// The full path of the image file the Markdown path of a book names (also percent-encoded, as Logseq writes it), or
    /// <c>null</c> if the path is remote, not an image name, not an existing file, or outside <paramref name="vaultRoot"/>.
    /// </summary>
    public static string? Resolve(string vaultRoot, string bookFilePath, string markdownPath)
    {
        if (BlockImages.IsRemote(markdownPath))
        {
            return null;
        }

        var bookDirectory = Path.GetDirectoryName(Path.GetFullPath(bookFilePath)) ?? "";
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(vaultRoot)) + Path.DirectorySeparatorChar;

        foreach (var candidate in Candidates(markdownPath))
        {
            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(bookDirectory, candidate));
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && IsImageName(full) && File.Exists(full))
            {
                return full;
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates(string markdownPath)
    {
        yield return markdownPath;
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(markdownPath);
        }
        catch (Exception e) when (e is UriFormatException or ArgumentException)
        {
            yield break;
        }

        if (decoded != markdownPath)
        {
            yield return decoded;
        }
    }

    private static bool IsSafe(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-';

    private static string RelativeTo(string bookFilePath, string target) =>
        Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(bookFilePath)) ?? "", target).Replace('\\', '/');
}
