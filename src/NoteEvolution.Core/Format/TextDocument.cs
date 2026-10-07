using System.Text;

namespace NoteEvolution.Core.Format;

/// <summary>
/// A UTF-8 text file split into lines that remembers BOM and line endings, so that
/// <see cref="Parse"/> followed by <see cref="Encode"/> reproduces the input byte for byte.
/// </summary>
public sealed class TextDocument
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    private TextDocument(bool hasBom, IReadOnlyList<RawLine> lines, string dominantEnding)
    {
        HasBom = hasBom;
        Lines = lines;
        DominantEnding = dominantEnding;
    }

    public bool HasBom { get; }

    public IReadOnlyList<RawLine> Lines { get; }

    /// <summary>The most frequent line ending (first found on a tie); <c>"\n"</c> if no line has an ending.</summary>
    public string DominantEnding { get; }

    /// <exception cref="ParseException">The bytes are not valid UTF-8; carries the 1-based line number.</exception>
    public static TextDocument Parse(byte[] bytes)
    {
        var hasBom = bytes.AsSpan().StartsWith(Bom);
        var position = hasBom ? Bom.Length : 0;

        var lines = new List<RawLine>();
        int lfCount = 0, crlfCount = 0;
        string? firstEnding = null;

        while (position < bytes.Length)
        {
            var newline = Array.IndexOf(bytes, (byte)'\n', position);
            var textEnd = newline < 0 ? bytes.Length : newline;
            string ending;

            if (newline < 0)
            {
                ending = "";
            }
            else if (newline > position && bytes[newline - 1] == (byte)'\r')
            {
                textEnd = newline - 1;
                ending = "\r\n";
                crlfCount++;
            }
            else
            {
                ending = "\n";
                lfCount++;
            }

            if (ending.Length > 0)
            {
                firstEnding ??= ending;
            }

            string text;
            try
            {
                text = StrictUtf8.GetString(bytes, position, textEnd - position);
            }
            catch (DecoderFallbackException)
            {
                throw new ParseException($"Invalid UTF-8 in line {lines.Count + 1}.", lines.Count + 1);
            }

            lines.Add(new RawLine(text, ending));
            position = newline < 0 ? bytes.Length : newline + 1;
        }

        var dominant =
            crlfCount > lfCount ? "\r\n" :
            lfCount > crlfCount ? "\n" :
            firstEnding ?? "\n";

        return new TextDocument(hasBom, lines, dominant);
    }

    public static byte[] Encode(bool hasBom, IEnumerable<RawLine> lines)
    {
        using var stream = new MemoryStream();
        if (hasBom)
        {
            stream.Write(Bom);
        }

        foreach (var line in lines)
        {
            stream.Write(StrictUtf8.GetBytes(line.Text));
            stream.Write(StrictUtf8.GetBytes(line.Ending));
        }

        return stream.ToArray();
    }
}
