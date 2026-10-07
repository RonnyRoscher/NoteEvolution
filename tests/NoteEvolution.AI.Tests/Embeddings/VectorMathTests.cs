using NoteEvolution.AI.Embeddings;

namespace NoteEvolution.AI.Tests.Embeddings;

public class VectorMathTests
{
    [Fact]
    public void MeanPoolNormalize_IgnoresMaskedTokens_UnitLength()
    {
        // three tokens of two dimensions; the third is padding with large values that must not count
        float[] hidden = [1, 0, 0, 1, 100, -50];
        long[] mask = [1, 1, 0];

        var v = VectorMath.MeanPoolNormalize(hidden, tokens: 3, dims: 2, mask);

        Assert.Equal(2, v.Length);
        Assert.Equal(MathF.Sqrt(0.5f), v[0], 5);
        Assert.Equal(MathF.Sqrt(0.5f), v[1], 5);
    }

    [Fact]
    public void MeanPoolNormalize_AllMasked_IsZeroVector()
    {
        var v = VectorMath.MeanPoolNormalize([1, 2, 3, 4], tokens: 2, dims: 2, [0, 0]);

        Assert.Equal([0f, 0f], v);
    }

    [Fact]
    public void Normalize_ScalesToUnitLength_WithoutChangingInput()
    {
        float[] input = [3, 4];

        var v = VectorMath.Normalize(input);

        Assert.Equal(0.6f, v[0], 6);
        Assert.Equal(0.8f, v[1], 6);
        Assert.Equal([3f, 4f], input);
    }

    [Fact]
    public void Normalize_ZeroVector_StaysZero_NoNaN()
    {
        var v = VectorMath.Normalize(new float[384]);

        Assert.Equal(384, v.Length);
        Assert.All(v, x => Assert.Equal(0f, x));
    }

    [Fact]
    public void Mean_IsElementwiseAverage()
    {
        var m = VectorMath.Mean([[1, 2], [3, 6]]);

        Assert.Equal([2f, 4f], m);
    }

    [Fact]
    public void Mean_EmptyListOrDifferentLengths_Throws()
    {
        Assert.Throws<ArgumentException>(() => VectorMath.Mean([]));
        Assert.Throws<ArgumentException>(() => VectorMath.Mean([[1, 2], [3]]));
    }

    [Fact]
    public void Cosine_NormalizedVectors_IsDotProduct()
    {
        var a = VectorMath.Normalize([1, 0]);
        var b = VectorMath.Normalize([1, 1]);

        Assert.Equal(MathF.Sqrt(0.5f), VectorMath.Cosine(a, b), 5);
        Assert.Equal(1f, VectorMath.Cosine(a, a), 5);
        Assert.Equal(-1f, VectorMath.Cosine(a, [-1, 0]), 5);
    }

    [Fact]
    public void Cosine_ZeroVector_IsZero()
    {
        Assert.Equal(0f, VectorMath.Cosine([0, 0], [1, 0]));
        Assert.Equal(0f, VectorMath.Cosine([0, 0], [0, 0]));
    }

    [Fact]
    public void Cosine_DifferentLengths_Throws()
    {
        Assert.Throws<ArgumentException>(() => VectorMath.Cosine([1, 0], [1, 0, 0]));
    }
}
