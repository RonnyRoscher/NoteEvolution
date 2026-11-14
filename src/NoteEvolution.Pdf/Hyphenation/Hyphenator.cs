using System.Reflection;
using System.Text;

namespace NoteEvolution.Pdf.Hyphenation;

/// <summary>Liang pattern hyphenation: inserts soft hyphens (U+00AD) at the allowed breaks inside words.</summary>
internal sealed class Hyphenator
{
    public const char SoftHyphen = '­';

    private const string PatternResource = "Hyphenation/hyph-de-1996.pat.txt";
    private const int MinWordLength = 6;
    private const int MinLeft = 2;
    private const int MinRight = 3;

    private static readonly Lazy<Hyphenator?> GermanInstance = new(LoadGerman);

    // Pattern letters (with dots for word boundaries) to the digits between/around them (length = letters + 1).
    private readonly Dictionary<string, byte[]> _patterns;
    private readonly int _maxPatternLength;

    private Hyphenator(Dictionary<string, byte[]> patterns, int maxPatternLength)
    {
        _patterns = patterns;
        _maxPatternLength = maxPatternLength;
    }

    /// <summary>The German hyphenator from the embedded patterns, loaded once; null if the resource is missing or unreadable.</summary>
    public static Hyphenator? German => GermanInstance.Value;

    /// <summary>Patterns in the tex-hyphen txt form, one per line (".ab3a", "1ba").</summary>
    public static Hyphenator FromPatterns(IEnumerable<string> patterns)
    {
        var table = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var maxLength = 0;
        var letters = new StringBuilder();
        var digits = new List<byte>();
        foreach (var raw in patterns)
        {
            var pattern = raw.Trim();
            if (pattern.Length == 0)
                continue;
            letters.Clear();
            digits.Clear();
            digits.Add(0);
            foreach (var c in pattern)
            {
                if (c is >= '0' and <= '9')
                    digits[^1] = (byte)(c - '0');
                else
                {
                    letters.Append(c);
                    digits.Add(0);
                }
            }
            if (letters.Length == 0)
                continue;
            table[letters.ToString()] = digits.ToArray();
            maxLength = Math.Max(maxLength, letters.Length);
        }
        return new Hyphenator(table, maxLength);
    }

    /// <summary>
    /// The text with U+00AD at every allowed break inside its words; everything else unchanged. Internet addresses
    /// ("https://…", "www.…") are left as they are.
    /// </summary>
    public string Hyphenate(string text)
    {
        if (text.Length < MinWordLength)
            return text;

        var result = new StringBuilder(text.Length + text.Length / 4);
        var i = 0;
        while (i < text.Length)
        {
            if (IsInternetAddressAt(text, i, out var end))
            {
                result.Append(text, i, end - i);
                i = end;
                continue;
            }

            if (!char.IsLetter(text[i]))
            {
                result.Append(text[i++]);
                continue;
            }

            var start = i;
            while (i < text.Length && char.IsLetter(text[i]))
                i++;
            var length = i - start;

            var touchesDigitOrBreak = (start > 0 && IsDigitOrSoftHyphen(text[start - 1]))
                || (i < text.Length && IsDigitOrSoftHyphen(text[i]));
            if (length < MinWordLength || touchesDigitOrBreak)
            {
                result.Append(text, start, length);
                continue;
            }

            AppendHyphenated(result, text.AsSpan(start, length));
        }
        return result.ToString();
    }

    /// <summary>
    /// Whether a whitespace-delimited token starts at <paramref name="start"/> that is an internet or mail address (it
    /// contains "://" or "@", or starts with "www."); such a token is not hyphenated. <paramref name="end"/> is where the token ends.
    /// </summary>
    private static bool IsInternetAddressAt(string text, int start, out int end)
    {
        end = start;
        if (char.IsWhiteSpace(text[start]) || (start > 0 && !char.IsWhiteSpace(text[start - 1])))
            return false;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
            end++;
        var token = text.AsSpan(start, end - start);
        // Punctuation before the address ("(www.…", an opening quote) does not hide it.
        var bare = token;
        while (bare.Length > 0 && !char.IsLetterOrDigit(bare[0]))
            bare = bare[1..];
        return token.Contains("://", StringComparison.Ordinal) || token.Contains('@')
            || bare.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDigitOrSoftHyphen(char c) => char.IsDigit(c) || c == SoftHyphen;

    private void AppendHyphenated(StringBuilder result, ReadOnlySpan<char> word)
    {
        var n = word.Length;
        var dotted = "." + word.ToString().ToLowerInvariant() + ".";
        var values = new byte[dotted.Length + 1];

        for (var s = 0; s < dotted.Length; s++)
        {
            var maxLen = Math.Min(_maxPatternLength, dotted.Length - s);
            for (var len = 1; len <= maxLen; len++)
            {
                if (!_patterns.TryGetValue(dotted.Substring(s, len), out var digits))
                    continue;
                for (var t = 0; t < digits.Length; t++)
                    if (digits[t] > values[s + t])
                        values[s + t] = digits[t];
            }
        }

        for (var k = 0; k < n; k++)
        {
            // A break before word[k] sits at index k + 1 in the dotted word.
            if (k >= MinLeft && n - k >= MinRight && (values[k + 1] & 1) == 1)
                result.Append(SoftHyphen);
            result.Append(word[k]);
        }
    }

    private static Hyphenator? LoadGerman()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(PatternResource);
            if (stream == null)
                return null;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
                lines.Add(line);
            return FromPatterns(lines);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
