using NoteEvolution.Core.Format;
using NoteEvolution.TestSupport;

namespace NoteEvolution.Core.Tests.Format;

public class PageRoundTripTests
{
    [Theory, MemberData(nameof(Samples.RoundTrip), MemberType = typeof(Samples))]
    public void RoundTrip_IsByteIdentical(string name, byte[] bytes) =>
        Assert.Equal(bytes, PageSerializer.Serialize(LogseqParser.Parse(name, bytes)));

    [Theory, MemberData(nameof(Samples.RoundTrip), MemberType = typeof(Samples))]
    public void RoundTrip_SamplesParseWithoutError(string name, byte[] bytes)
    {
        var page = LogseqParser.Parse(name, bytes);

        Assert.False(page.IsReadOnly, page.ParseError);
    }
}
