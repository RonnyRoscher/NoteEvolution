namespace NoteEvolution.AI.Search;

/// <summary>Turns user text into an FTS5 MATCH expression that cannot contain operators or syntax errors.</summary>
public static class FtsQuery
{
    /// <summary>
    /// Control characters separate words like whitespace. Every word becomes a quoted prefix term (<c>"word"*</c>), joined
    /// by spaces (= AND). Double quotes are removed and words without any letter or digit are dropped. Returns <c>null</c> if nothing is left.
    /// </summary>
    public static string? Build(string userText)
    {
        var cleaned = string.Concat(userText.Select(c => char.IsControl(c) ? ' ' : c));
        var terms = cleaned
            .Replace("\"", string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Any(char.IsLetterOrDigit))
            .Select(word => $"\"{word}\"*")
            .ToList();
        return terms.Count == 0 ? null : string.Join(' ', terms);
    }
}
