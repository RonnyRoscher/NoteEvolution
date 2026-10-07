namespace NoteEvolution.Core.Storage;

/// <summary>The current time, injectable so tests control dates and times.</summary>
public interface IClock
{
    DateTimeOffset Now { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}
