namespace NoteEvolution.AI.Understanding;

/// <summary>The fixed German instructions for the language model and the cleaning of its answers. Pure.</summary>
public static class UnderstandingPrompts
{
    public const int SummaryLimit = 600;
    public const int CoreLimit = 300;
    public const int SummaryTokens = 220;
    public const int CoreTokens = 120;
    public const int CheckTokens = 120;

    /// <summary>The instructions for a heading's summary; part of its fingerprint, so a change here recomputes the summaries.</summary>
    internal const string SummarySystem =
        "Du fasst Abschnitte eines deutschsprachigen Buchs zusammen. Antworte nur mit der Zusammenfassung: 1 bis 3 möglichst knappe Sätze auf Deutsch, die den Inhalt des Abschnitts einschließlich seiner Unterabschnitte selbst wiedergeben. Schreibe die Aussagen direkt, ohne über den Text, den Abschnitt, das Buch, den Autor oder die Leser zu sprechen: nicht „Der Abschnitt beschreibt, wie …“, „Der Autor erklärt, dass …“ oder „Das Buch richtet sich an Menschen, die …“, sondern etwa „Feste Schlafzeiten stabilisieren den Tagesrhythmus.“ Keine Einleitung, keine Aufzählung, kein Markdown.";

    private const string CoreSystem =
        "Du bestimmst die Kernaussage einer persönlichen Notiz. Antworte nur mit 1 bis 2 sachlichen Sätzen auf Deutsch, was die Notiz im Kern sagt. Keine Einleitung, kein Markdown.";

    private static readonly char[] SentenceEnds = ['.', '!', '?'];
    private static readonly char[] QuoteChars = ['"', '\'', '„', '“', '”', '‚', '‘', '’', '«', '»'];

    /// <summary>Prompt for a heading's summary: its title, the sub-headings' summaries and its own text (empty parts are left out).</summary>
    public static (string System, string User) Summary(UnderstandingItem heading,
        IReadOnlyList<(string Title, string Summary)> children)
    {
        var parts = new List<string> { "Überschrift: " + heading.Title };
        if (children.Count > 0)
        {
            parts.Add("Unterabschnitte:\n" + string.Join("\n", children.Select(c => $"- {c.Title}: {c.Summary}")));
        }

        if (!string.IsNullOrWhiteSpace(heading.OwnText))
        {
            parts.Add("Text des Abschnitts:\n" + heading.OwnText);
        }

        return (SummarySystem, string.Join("\n\n", parts));
    }

    /// <summary>Prompt for a note's core statement.</summary>
    public static (string System, string User) Core(UnderstandingItem note) => (CoreSystem, "Notiz:\n" + note.OwnText);

    /// <summary>
    /// The plain content of a model answer: without a leading preamble line ending in ":", without Markdown and surrounding
    /// quotes, whitespace collapsed, cut at the last sentence end within <paramref name="limit"/> (else hard cut).
    /// </summary>
    public static string Clean(string answer, int limit)
    {
        var lines = answer.Split('\n').Select(StripMarkdown).Where(l => l.Length > 0).ToList();
        if (lines.Count > 0 && lines[0].EndsWith(':'))
        {
            lines.RemoveAt(0);
        }

        var text = string.Join(" ", lines);
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim(QuoteChars).Trim();
        if (text.Length <= limit)
        {
            return text;
        }

        var cut = text[..limit];
        var end = cut.LastIndexOfAny(SentenceEnds);
        return (end > 0 ? cut[..(end + 1)] : cut).TrimEnd();
    }

    private static string StripMarkdown(string line)
    {
        var s = line.Replace("**", "").Replace("__", "").Trim().TrimStart('#').TrimStart();
        if (s.StartsWith("- ") || s.StartsWith("* "))
        {
            s = s[2..];
        }

        return s.Trim();
    }
}
