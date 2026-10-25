namespace NoteEvolution.Core.Books;

/// <summary>Which appendix section a top-level heading is (spec 1.1); <see cref="None"/> for every other heading.</summary>
public enum AppendixKind
{
    /// <summary>No appendix section: a heading of the book's text (or the root).</summary>
    None,

    /// <summary><c>anhang:: glossar</c>.</summary>
    Glossary,

    /// <summary><c>anhang:: menschen</c>.</summary>
    People,

    /// <summary><c>anhang:: quellen</c>.</summary>
    Sources,
}

/// <summary>Reads and names the <see cref="AppendixKind"/> values as the <c>anhang::</c> property stores them.</summary>
public static class AppendixKinds
{
    /// <summary>The property that marks a top-level heading as an appendix section.</summary>
    public const string Key = "anhang";

    /// <summary>
    /// The kind an <c>anhang::</c> value names (case-insensitive: glossar, menschen, quellen); <see cref="AppendixKind.None"/>
    /// for no, an empty or an unknown value.
    /// </summary>
    public static AppendixKind Parse(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "glossar" => AppendixKind.Glossary,
            "menschen" => AppendixKind.People,
            "quellen" => AppendixKind.Sources,
            _ => AppendixKind.None,
        };

    /// <summary>The <c>anhang::</c> value written for a kind; <c>null</c> for <see cref="AppendixKind.None"/>.</summary>
    public static string? FileValue(AppendixKind kind) =>
        kind switch
        {
            AppendixKind.Glossary => "glossar",
            AppendixKind.People => "menschen",
            AppendixKind.Sources => "quellen",
            _ => null,
        };
}
