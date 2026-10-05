namespace NoteEvolution.Core.Format;

/// <summary>A file could not be parsed safely; such files are never written.</summary>
public class ParseException(string message, int lineNumber) : Exception(message)
{
    /// <summary>1-based number of the offending line.</summary>
    public int LineNumber { get; } = lineNumber;
}
