using Microsoft.ML.Tokenizers;

namespace NoteEvolution.AI.Embeddings;

/// <summary>
/// Tokenizes for the e5 (XLM-RoBERTa) model with its SentencePiece model and maps to the fairseq ids the model was
/// trained with: <c>&lt;s&gt;</c> 0, <c>&lt;pad&gt;</c> 1, <c>&lt;/s&gt;</c> 2, <c>&lt;unk&gt;</c> 3, SentencePiece id <c>i &gt; 0</c> becomes <c>i + 1</c>.
/// </summary>
public sealed class E5Tokenizer
{
    public const long BeginOfSentence = 0;
    public const long Padding = 1;
    public const long EndOfSentence = 2;
    public const long Unknown = 3;

    private readonly SentencePieceTokenizer _pieces;

    private E5Tokenizer(SentencePieceTokenizer pieces) => _pieces = pieces;

    public static E5Tokenizer Load(string sentencePieceModelPath)
    {
        using var stream = File.OpenRead(sentencePieceModelPath);
        // The model's own normalizer spec applies: precompiled NFKC charsmap, whitespace collapsed, U+2581 for spaces, dummy prefix.
        return new E5Tokenizer(SentencePieceTokenizer.Create(stream, addBeginningOfSentence: false, addEndOfSentence: false));
    }

    /// <summary>Returns <c>&lt;s&gt; … &lt;/s&gt;</c>, at most <paramref name="maxTokens"/> ids long; a longer text is cut before <c>&lt;/s&gt;</c>.</summary>
    public long[] Encode(string text, int maxTokens)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTokens, 2);

        var pieces = _pieces.EncodeToIds(text);
        var count = Math.Min(pieces.Count, maxTokens - 2);
        var ids = new long[count + 2];
        ids[0] = BeginOfSentence;
        for (var i = 0; i < count; i++)
            ids[i + 1] = pieces[i] == 0 ? Unknown : pieces[i] + 1L;
        ids[^1] = EndOfSentence;
        return ids;
    }
}
