using NoteEvolution.AI.Relevance;

namespace NoteEvolution.AI.Tests.Relevance;

public class NoteDuplicatesTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    [Fact]
    public void Nearest_GivesTheMostSimilarUsedNote()
    {
        var hit = NoteDuplicates.Nearest([1f, 0f], [(A, [0.6f, 0.8f]), (B, [0.99f, 0.1f])]);

        Assert.NotNull(hit);
        Assert.Equal(B, hit.Value.Key);
        Assert.InRange(hit.Value.Score, 0.99f, 1f);
    }

    [Fact]
    public void Nearest_WithoutUsedNotes_IsNull() => Assert.Null(NoteDuplicates.Nearest([1f, 0f], []));

    [Fact]
    public void Nearest_SkipsVectorsOfAnotherDimensionAndWithoutMeaning()
    {
        var hit = NoteDuplicates.Nearest([1f, 0f], [(A, [1f, 0f, 0f]), (B, [0f, 0f])]);

        Assert.Null(hit);
    }

    [Fact]
    public void Nearest_OfEqualScores_KeepsTheFirst()
    {
        var hit = NoteDuplicates.Nearest([1f, 0f], [(A, [1f, 0f]), (B, [1f, 0f])]);

        Assert.Equal(A, hit!.Value.Key);
    }

    [Theory]
    [InlineData(0.92f, true)]
    [InlineData(0.95f, true)]
    [InlineData(0.9199f, false)]
    public void IsDuplicate_FromTheThreshold(float score, bool expected) =>
        Assert.Equal(expected, NoteDuplicates.IsDuplicate(score));
}
