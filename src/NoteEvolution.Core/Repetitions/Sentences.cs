namespace NoteEvolution.Core.Repetitions;

/// <summary>A sentence of a compared block: paragraph index, offset and length in that paragraph's Plain, and its word count.</summary>
public readonly record struct ComparedSentence(int Paragraph, int Start, int Length, int Words);

/// <summary>Splits the paragraphs of a compared block into sentences.</summary>
public static class Sentences
{
    /// <summary>Sentences with fewer words are left out.</summary>
    public const int MinWords = 5;

    /// <summary>
    /// The sentences of the block in order. A sentence ends at ".", "!", "?" or "…" followed by whitespace and then an uppercase
    /// letter, a digit or a quote mark, and at the end of a paragraph. Sentences with fewer than <see cref="MinWords"/> words are left out.
    /// </summary>
    public static IReadOnlyList<ComparedSentence> Of(ComparedBlock block)
    {
        var sentences = new List<ComparedSentence>();
        for (var index = 0; index < block.Paragraphs.Count; index++)
        {
            var plain = block.Paragraphs[index].Plain;
            var start = 0;
            for (var i = 0; i < plain.Length; i++)
            {
                if (IsEnd(plain[i]) && !IsInitial(plain, i) && StartsNextSentence(plain, i + 1))
                {
                    Add(sentences, block, index, plain, start, i + 1);
                    start = i + 1;
                }
            }

            Add(sentences, block, index, plain, start, plain.Length);
        }

        return sentences;
    }

    /// <summary>The text of the sentence.</summary>
    public static string Text(ComparedBlock block, ComparedSentence sentence) =>
        block.Paragraphs[sentence.Paragraph].Plain.Substring(sentence.Start, sentence.Length);

    private static bool IsEnd(char c) => c is '.' or '!' or '?' or '…';

    /// <summary>A period after a single letter ("z. B.", "J. Meier") is an abbreviation, not a sentence end.</summary>
    private static bool IsInitial(string plain, int position) =>
        plain[position] == '.' && position > 0 && char.IsLetter(plain[position - 1]) &&
        (position == 1 || !char.IsLetterOrDigit(plain[position - 2]));

    /// <summary>Whitespace at <paramref name="position"/> followed by an uppercase letter, a digit or a quote mark.</summary>
    private static bool StartsNextSentence(string plain, int position)
    {
        var next = position;
        while (next < plain.Length && char.IsWhiteSpace(plain[next]))
        {
            next++;
        }

        return next > position && next < plain.Length &&
            (char.IsUpper(plain[next]) || char.IsDigit(plain[next]) || plain[next] is '"' or '„' or '»' or '“');
    }

    private static void Add(List<ComparedSentence> sentences, ComparedBlock block, int paragraph, string plain, int from, int to)
    {
        while (from < to && char.IsWhiteSpace(plain[from]))
        {
            from++;
        }

        while (to > from && char.IsWhiteSpace(plain[to - 1]))
        {
            to--;
        }

        var words = block.Words.Count(w => w.Paragraph == paragraph && w.Start >= from && w.Start < to);
        if (words >= MinWords)
        {
            sentences.Add(new ComparedSentence(paragraph, from, to - from, words));
        }
    }
}
