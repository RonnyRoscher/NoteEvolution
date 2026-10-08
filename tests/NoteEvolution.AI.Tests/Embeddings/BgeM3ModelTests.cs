using NoteEvolution.AI.Embeddings;
using NoteEvolution.AI.Model;

namespace NoteEvolution.AI.Tests.Embeddings;

public class BgeM3ModelTests
{
    [BgeM3ModelFact]
    public void Embeds1024Normalized_SimilarTextsCloser()
    {
        using var embedder = OnnxEmbedder.Load(BgeM3ModelFactAttribute.ModelDirectory, ModelCatalog.BgeM3);

        var v = embedder.Embed(
        [
            "query: Wie gehe ich mit Angst um?",
            "passage: Angst baut Widerstand auf, Vertrauen baut Schwung auf.",
            "passage: Der Zug fährt morgen um acht Uhr.",
        ]);

        Assert.All(v, x =>
        {
            Assert.Equal(1024, x.Length);
            Assert.Equal(1.0, Math.Sqrt(x.Sum(c => (double)c * c)), 1e-3);
        });
        Assert.True(VectorMath.Cosine(v[0], v[1]) > VectorMath.Cosine(v[0], v[2]));
    }
}
