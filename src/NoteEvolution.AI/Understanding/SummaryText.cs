namespace NoteEvolution.AI.Understanding;

/// <summary>Short forms of the language model's section summaries.</summary>
public static class SummaryText
{
    /// <summary>The first sentence of a summary, at most <paramref name="maxLength"/> characters (cut with "…").</summary>
    public static string FirstSentence(string summary, int maxLength)
    {
        // A sentence ends at ".", "!" or "?" before a space, unless the word before the "." is an abbreviation like "z.",
        // "B." or "bzw.".
        var text = summary.Trim();
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (text[i] is '.' or '!' or '?' && text[i + 1] == ' ')
            {
                var word = text[(text.LastIndexOf(' ', i) + 1)..i];
                if (text[i] != '.' || (word.Length > 1 && !Abbreviations.Contains(word)))
                {
                    text = text[..(i + 1)];
                    break;
                }
            }
        }

        return text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";
    }

    private static readonly HashSet<string> Abbreviations = new(
        ["bzw", "ca", "d.h", "etc", "evtl", "ggf", "inkl", "insb", "Nr", "S", "u.a", "usw", "vgl", "z.B"], StringComparer.OrdinalIgnoreCase);
}
