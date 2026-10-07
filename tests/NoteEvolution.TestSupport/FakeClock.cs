using NoteEvolution.Core.Storage;

namespace NoteEvolution.TestSupport;

/// <summary>An <see cref="IClock"/> that only moves when the test says so.</summary>
public sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset Now { get; set; } = now;

    public void Advance(TimeSpan by) => Now += by;
}
