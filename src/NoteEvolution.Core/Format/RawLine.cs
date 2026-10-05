namespace NoteEvolution.Core.Format;

/// <summary>One physical line of a text file: its text and the line ending that followed it.</summary>
/// <param name="Text">The line content without its line ending.</param>
/// <param name="Ending">One of <c>"\n"</c>, <c>"\r\n"</c> or <c>""</c> (last line without final newline).</param>
public sealed record RawLine(string Text, string Ending);
