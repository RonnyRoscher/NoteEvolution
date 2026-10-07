namespace NoteEvolution.AI.Embeddings;

/// <summary>Vector helpers for embeddings. A zero vector stays zero and never produces NaN.</summary>
public static class VectorMath
{
    /// <summary>
    /// Averages the token vectors of one sequence (<paramref name="hidden"/> is <c>tokens × dims</c>, row-major) over the
    /// tokens whose <paramref name="mask"/> is non-zero, then L2-normalizes. No unmasked token gives a zero vector.
    /// </summary>
    public static float[] MeanPoolNormalize(ReadOnlySpan<float> hidden, int tokens, int dims, ReadOnlySpan<long> mask)
    {
        if (hidden.Length != tokens * dims || mask.Length != tokens)
            throw new ArgumentException($"Expected {tokens}×{dims} values and {tokens} mask entries.");

        var sum = new float[dims];
        var count = 0;
        for (var t = 0; t < tokens; t++)
        {
            if (mask[t] == 0) continue;
            count++;
            var row = hidden.Slice(t * dims, dims);
            for (var d = 0; d < dims; d++) sum[d] += row[d];
        }

        if (count == 0) return sum;
        for (var d = 0; d < dims; d++) sum[d] /= count;
        return NormalizeInPlace(sum);
    }

    /// <summary>Returns <paramref name="v"/> scaled to unit length as a new array; a zero vector stays a zero vector.</summary>
    public static float[] Normalize(float[] v) => NormalizeInPlace((float[])v.Clone());

    /// <summary>The element-wise average. The list must be non-empty and all vectors equally long.</summary>
    public static float[] Mean(IReadOnlyList<float[]> vs)
    {
        if (vs.Count == 0) throw new ArgumentException("Cannot average an empty list of vectors.", nameof(vs));

        var mean = new float[vs[0].Length];
        foreach (var v in vs)
        {
            if (v.Length != mean.Length) throw new ArgumentException("All vectors must have the same length.", nameof(vs));
            for (var d = 0; d < mean.Length; d++) mean[d] += v[d];
        }

        for (var d = 0; d < mean.Length; d++) mean[d] /= vs.Count;
        return mean;
    }

    /// <summary>Cosine similarity; for normalized vectors this is the dot product. With a zero vector it is 0.</summary>
    public static float Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length) throw new ArgumentException("Vectors must have the same length.");

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        return normA == 0 || normB == 0 ? 0f : (float)(dot / Math.Sqrt(normA * normB));
    }

    private static float[] NormalizeInPlace(float[] v)
    {
        double norm = 0;
        foreach (var x in v) norm += x * x;
        if (norm == 0) return v;

        var scale = (float)(1 / Math.Sqrt(norm));
        for (var i = 0; i < v.Length; i++) v[i] *= scale;
        return v;
    }
}
