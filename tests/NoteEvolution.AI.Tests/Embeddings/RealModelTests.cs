using System.Text;
using System.Text.Json;
using Microsoft.ML.Tokenizers;
using NoteEvolution.AI.Embeddings;

namespace NoteEvolution.AI.Tests.Embeddings;

/// <summary>Runs against the installed multilingual-e5-small model (skipped when it is missing, see <see cref="RealModelFactAttribute"/>).</summary>
public class RealModelTests
{
    private const int MaxTokens = 512;

    private static readonly Lazy<Dictionary<string, (int Id, double Score)>> HfVocabulary = new(LoadHfVocabulary);

    [RealModelFact(NeedsTokenizerJson = true)]
    public void Tokenizer_IdsMatchTokenizerJsonVocabulary()
    {
        var tokenizer = E5Tokenizer.Load(RealModelFactAttribute.SentencePiecePath);
        using var spm = File.OpenRead(RealModelFactAttribute.SentencePiecePath);
        var pieces = SentencePieceTokenizer.Create(spm, addBeginningOfSentence: false, addEndOfSentence: false);

        foreach (var text in new[] { "query: Vertrauen baut Schwung auf.", "Ängste überwinden", "Größe  und Maß", "passage: Am 3. März 2024 – „Mut“ statt Angst?" })
        {
            var ids = tokenizer.Encode(text, MaxTokens);
            var tokens = pieces.EncodeToTokens(text, out _).Select(t => t.Value).ToList();

            Assert.Equal(0, ids[0]);
            Assert.Equal(2, ids[^1]);
            Assert.Equal(tokens.Select(p => (long)HfVocabulary.Value[p].Id), ids[1..^1]);
            // Same normalization as the HF tokenizer: NFKC, runs of spaces collapsed, space -> U+2581, one prefix U+2581.
            var normalized = "▁" + CollapseSpaces(text.Normalize(NormalizationForm.FormKC)).Replace(' ', '▁');
            Assert.Equal(normalized, string.Concat(tokens));
            // Same segmentation as HF's Unigram model: the highest-scoring split by the tokenizer.json scores.
            Assert.Equal(BestUnigramSplit(normalized), tokens);
        }
    }

    [RealModelFact]
    public void Tokenizer_KnownXlmRobertaIds()
    {
        var tokenizer = E5Tokenizer.Load(RealModelFactAttribute.SentencePiecePath);

        // The well-known XLM-R encoding of "Hello world!": <s> ▁Hello ▁world ! </s>
        Assert.Equal([0L, 35378, 8999, 38, 2], tokenizer.Encode("Hello world!", MaxTokens));
    }

    [RealModelFact]
    public void Tokenizer_DecomposedUmlauts_GiveSameIdsAsComposed()
    {
        var tokenizer = E5Tokenizer.Load(RealModelFactAttribute.SentencePiecePath);
        const string text = "Ängste überwinden";

        Assert.Equal(tokenizer.Encode(text, MaxTokens), tokenizer.Encode(text.Normalize(NormalizationForm.FormD), MaxTokens));
    }

    [RealModelFact]
    public void Tokenizer_LongText_TruncatedTo512_EndsWithEos()
    {
        var tokenizer = E5Tokenizer.Load(RealModelFactAttribute.SentencePiecePath);
        var text = string.Concat(Enumerable.Repeat("Vertrauen baut Schwung auf, Angst baut Widerstand auf. ", 200));

        var full = tokenizer.Encode(text, int.MaxValue);
        var ids = tokenizer.Encode(text, MaxTokens);

        Assert.True(full.Length > MaxTokens);
        Assert.Equal(MaxTokens, ids.Length);
        Assert.Equal(0, ids[0]);
        Assert.Equal(2, ids[^1]);
        Assert.Equal(full[..(MaxTokens - 1)], ids[..^1]);
        Assert.DoesNotContain(2L, ids[..^1]);
    }

    [RealModelFact]
    public void Tokenizer_ShortText_IsNotPadded()
    {
        var tokenizer = E5Tokenizer.Load(RealModelFactAttribute.SentencePiecePath);

        var ids = tokenizer.Encode("Hallo", MaxTokens);

        Assert.InRange(ids.Length, 3, 4);
        Assert.DoesNotContain(1L, ids);
    }

    [RealModelFact]
    public void Embedder_SimilarMeaningScoresHigher()
    {
        using var embedder = LoadEmbedder();

        var v = embedder.Embed([
            "query: Wie gehe ich mit Angst um?",
            "passage: Angst baut Widerstand auf, Vertrauen baut Schwung auf.",
            "passage: Der Zug fährt morgen um acht Uhr.",
        ]);

        Assert.True(VectorMath.Cosine(v[0], v[1]) > VectorMath.Cosine(v[0], v[2]),
            $"related {VectorMath.Cosine(v[0], v[1])} vs unrelated {VectorMath.Cosine(v[0], v[2])}");
    }

    [RealModelFact]
    public void Embedder_BatchEqualsSingle_384Dims_UnitLength()
    {
        using var embedder = LoadEmbedder();
        string[] texts =
        [
            "passage: Kurz.",
            "  ",
            "passage: Ein deutlich längerer Text, damit die anderen Texte im Stapel aufgefüllt werden müssen und die Maske zählt.",
            "",
            "query: Ängste überwinden",
        ];

        var batch = embedder.Embed(texts);

        Assert.Equal(384, embedder.Dimensions);
        Assert.Equal(texts.Length, batch.Count);
        for (var i = 0; i < texts.Length; i++)
        {
            Assert.Equal(384, batch[i].Length);
            if (string.IsNullOrWhiteSpace(texts[i]))
            {
                Assert.All(batch[i], x => Assert.Equal(0f, x));
                continue;
            }

            Assert.Equal(1f, MathF.Sqrt(Norm2(batch[i])), 4);
            var single = embedder.Embed([texts[i]])[0];
            Assert.True(VectorMath.Cosine(batch[i], single) > 0.98f, $"text {i}: cosine {VectorMath.Cosine(batch[i], single)}");
        }
    }

    [RealModelFact]
    public void Embedder_OnlyEmptyTexts_GivesZeroVectors()
    {
        using var embedder = LoadEmbedder();

        var v = embedder.Embed(["", " \n"]);

        Assert.Equal(2, v.Count);
        Assert.All(v, e => Assert.Equal(new float[384], e));
    }

    private static OnnxEmbedder LoadEmbedder() =>
        OnnxEmbedder.Load(RealModelFactAttribute.ModelDirectory, RealModelFactAttribute.Model);

    private static float Norm2(float[] v) => v.Sum(x => x * x);

    private static string CollapseSpaces(string s)
    {
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s;
    }

    /// <summary>An independent Viterbi over the tokenizer.json pieces (no specials): the split with the highest total score.</summary>
    private static List<string> BestUnigramSplit(string normalized)
    {
        var vocabulary = HfVocabulary.Value;
        var best = new double[normalized.Length + 1];
        var from = new int[normalized.Length + 1];
        Array.Fill(best, double.NegativeInfinity);
        best[0] = 0;
        for (var end = 1; end <= normalized.Length; end++)
        {
            for (var start = 0; start < end; start++)
            {
                if (double.IsNegativeInfinity(best[start])
                    || !vocabulary.TryGetValue(normalized[start..end], out var piece) || piece.Id <= 3) continue;
                if (best[start] + piece.Score > best[end])
                {
                    best[end] = best[start] + piece.Score;
                    from[end] = start;
                }
            }
        }

        Assert.False(double.IsNegativeInfinity(best[^1]), "text cannot be split into known pieces");
        var split = new List<string>();
        for (var end = normalized.Length; end > 0; end = from[end]) split.Insert(0, normalized[from[end]..end]);
        return split;
    }

    private static Dictionary<string, (int Id, double Score)> LoadHfVocabulary()
    {
        using var stream = File.OpenRead(RealModelFactAttribute.TokenizerJsonPath);
        using var json = JsonDocument.Parse(stream);
        var vocabulary = new Dictionary<string, (int Id, double Score)>();
        var index = 0;
        foreach (var entry in json.RootElement.GetProperty("model").GetProperty("vocab").EnumerateArray())
            vocabulary[entry[0].GetString()!] = (index++, entry[1].GetDouble());
        return vocabulary;
    }
}
