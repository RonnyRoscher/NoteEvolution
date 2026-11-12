using System.Text.RegularExpressions;

namespace NoteEvolution.Core.Books;

/// <summary>One image in a text: <c>![Alt](Path)Suffix</c>; the suffix is Logseq's <c>{...}</c> directly after it, or <c>""</c>.</summary>
public readonly record struct BlockImage(string Alt, string Path, string Suffix, int Start, int Length);

/// <summary>The Markdown image syntax <c>![Alt](Path)</c> inside text: finding, formatting and changing the caption.</summary>
public static partial class BlockImages
{
    [GeneratedRegex(@"!\[([^\]\n]*)\]\(([^)\n]*)\)(\{[^}\n]*\})?")]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"\G!\[[^\]\n]*\]\([^)\n]*\)(?:\{[^}\n]*\})?")]
    private static partial Regex ImageAtRegex();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]*://")]
    private static partial Regex RemoteRegex();

    /// <summary>All images in text order; the path is trimmed.</summary>
    public static IReadOnlyList<BlockImage> Find(string text)
    {
        var images = new List<BlockImage>();
        foreach (Match m in ImageRegex().Matches(text))
        {
            images.Add(new BlockImage(
                m.Groups[1].Value, m.Groups[2].Value.Trim(), m.Groups[3].Success ? m.Groups[3].Value : "", m.Index, m.Length));
        }

        return images;
    }

    /// <summary>The length of the image that starts at <paramref name="index"/> of <paramref name="text"/>; 0 if none starts there.</summary>
    public static int LengthAt(string text, int index) =>
        index >= 0 && index + 1 < text.Length && text[index] == '!' && text[index + 1] == '['
        && ImageAtRegex().Match(text, index) is { Success: true } m
            ? m.Length
            : 0;

    /// <summary>The image syntax for <paramref name="alt"/> and <paramref name="path"/>, followed by <paramref name="suffix"/>.</summary>
    public static string Format(string alt, string path, string suffix = "") => $"![{alt}]({path}){suffix}";

    /// <summary>
    /// A caption that fits into the alt text: <c>]</c> and line breaks removed, every <c>::</c> made a single <c>:</c>
    /// (so the block text is never read as a property), trimmed.
    /// </summary>
    public static string CleanCaption(string caption)
    {
        var clean = caption.Replace("]", "").Replace("\r", "").Replace("\n", "");
        while (clean.Contains("::", StringComparison.Ordinal))
        {
            clean = clean.Replace("::", ":");
        }

        return clean.Trim();
    }

    /// <summary>
    /// The caption an image's <paramref name="alt"/> text shows: the trimmed alt, or <c>""</c> if it is only an image
    /// file name (Logseq writes the file name as alt for pasted pictures). The alt text itself is never changed for this.
    /// </summary>
    public static string ShownCaption(string alt)
    {
        var caption = alt.Trim();
        return Vaults.VaultAssets.IsImageName(caption) ? "" : caption;
    }

    /// <summary>
    /// <paramref name="text"/> with the caption of its <paramref name="imageIndex"/>-th image (0-based) replaced by the
    /// cleaned <paramref name="caption"/>; everything else, also the path as written, stays byte-identical.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The text has no such image.</exception>
    public static string WithCaption(string text, int imageIndex, string caption)
    {
        var images = Find(text);
        if (imageIndex < 0 || imageIndex >= images.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(imageIndex));
        }

        var image = images[imageIndex];
        var from = image.Start + 2; // after "!["
        return string.Concat(text.AsSpan(0, from), CleanCaption(caption), text.AsSpan(from + image.Alt.Length));
    }

    /// <summary>Whether <paramref name="path"/> names a remote resource (has a <c>scheme://</c>); such images are never loaded.</summary>
    public static bool IsRemote(string path) => RemoteRegex().IsMatch(path);
}
