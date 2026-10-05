namespace NoteEvolution.Core.Model;

/// <summary>A <c>key:: value</c> property line of a page or block; the value is trimmed.</summary>
public sealed record BlockProperty(string Key, string Value);
