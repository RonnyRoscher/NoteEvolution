using System.Text.RegularExpressions;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Text;

namespace NoteEvolution.Core.Books;

/// <summary>How a source was published (<c>art::</c>: buch, video, artikel, webseite, sonstiges).</summary>
public enum SourceKind
{
    Book,
    Video,
    Article,
    Website,
    Other,
}

/// <summary>A glossary entry: the block's first line is the term, the following lines the explanation.</summary>
/// <param name="Key">The <see cref="Block.Key"/> of the entry block.</param>
/// <param name="Id">The <c>id::</c> of the block; <see cref="Guid.Empty"/> if it has none yet.</param>
/// <param name="Forms">The other forms of the term (<c>formen::</c>) that count as a hit.</param>
public sealed record GlossaryEntry(Guid Key, Guid Id, string Term, IReadOnlyList<string> Forms, string Explanation);

/// <summary>A person entry: the block's first line is the name, the following lines the description.</summary>
/// <param name="Abbreviation">The short mark (<c>kuerzel::</c>) used in the book's text.</param>
/// <param name="Topics">The topics (<c>themen::</c>) the person stands for.</param>
public sealed record PersonEntry(
    Guid Key, Guid Id, string Name, string Abbreviation, IReadOnlyList<string> Topics, string Description);

/// <summary>A source entry: the block's first line is the title.</summary>
/// <param name="AuthorIds">The <see cref="PersonEntry.Id"/> of the authors (<c>autoren::</c>, block references).</param>
/// <param name="Publisher">The <c>herausgeber::</c> property.</param>
public sealed record SourceEntry(
    Guid Key, Guid Id, string Title, SourceKind Kind, IReadOnlyList<Guid> AuthorIds, string? Year, string? Url, string? Publisher);

/// <summary>
/// Reads and writes the entries of the appendix sections (glossary, people, sources). Entries are the text blocks
/// directly below a section. Writing changes <c>book.Page</c> in memory and does not save; the caller saves the page
/// and reloads the <see cref="Book"/>, because the view is stale afterwards (these methods look at the page itself).
/// Properties the app does not know and the <c>id::</c> stay as they are.
/// </summary>
public static partial class Appendix
{
    private const string FormsKey = "formen";
    private const string AbbreviationKey = "kuerzel";
    private const string TopicsKey = "themen";
    private const string KindKey = "art";
    private const string AuthorsKey = "autoren";
    private const string YearKey = "jahr";
    private const string UrlKey = "url";
    private const string PublisherKey = "herausgeber";
    private const string ListSeparator = ", ";
    private const int MaxAbbreviationLength = 6;

    [GeneratedRegex(@"^[\p{L}\p{N}.\-]{1,6}$")]
    private static partial Regex AbbreviationRegex();

    /// <summary>The glossary entries in file order.</summary>
    public static IReadOnlyList<GlossaryEntry> Glossary(Book book) =>
        [.. EntryBlocks(book, AppendixKind.Glossary).Select(ReadGlossary)];

    /// <summary>The people in file order.</summary>
    public static IReadOnlyList<PersonEntry> People(Book book) =>
        [.. EntryBlocks(book, AppendixKind.People).Select(ReadPerson)];

    /// <summary>The sources in file order.</summary>
    public static IReadOnlyList<SourceEntry> Sources(Book book) =>
        [.. EntryBlocks(book, AppendixKind.Sources).Select(ReadSource)];

    /// <summary>The person with <c>id::</c> <paramref name="id"/>; <c>null</c> if there is none.</summary>
    public static PersonEntry? PersonById(Book book, Guid id) =>
        id == Guid.Empty ? null : People(book).FirstOrDefault(person => person.Id == id);

    /// <summary>The source with <c>id::</c> <paramref name="id"/>; <c>null</c> if there is none.</summary>
    public static SourceEntry? SourceById(Book book, Guid id) =>
        id == Guid.Empty ? null : Sources(book).FirstOrDefault(source => source.Id == id);

    /// <summary>
    /// Adds an empty entry with an <c>id::</c> (and no dates: it is no text of the book) as the last child of the
    /// section of <paramref name="kind"/>; a missing section is created first, with its default title, after the
    /// book's text and the sections that come before it in the order glossary, people, sources.
    /// </summary>
    /// <returns>The <see cref="Block.Key"/> of the new entry.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is <see cref="AppendixKind.None"/>.</exception>
    public static Guid Add(Book book, AppendixKind kind)
    {
        var section = Current(book).AppendixSection(kind) ?? CreateSection(book, kind);
        var entry = Block.CreateDetached("", [new BlockProperty("id", Guid.CreateVersion7().ToString("D"))]);
        book.Page.InsertBlock(section.Block, section.Block!.Children.Count, entry);
        return entry.Key;
    }

    /// <summary>Writes the entry: first line term, the explanation below; empty fields remove their property.</summary>
    /// <exception cref="ArgumentException">There is no such entry in the appendix.</exception>
    public static void Save(Book book, GlossaryEntry entry)
    {
        var block = EntryBlock(book, entry.Key);
        SetText(block, entry.Term, entry.Explanation);
        SetList(block, FormsKey, entry.Forms);
        block.EnsureId();
    }

    /// <inheritdoc cref="Save(Book, GlossaryEntry)"/>
    public static void Save(Book book, PersonEntry entry)
    {
        var block = EntryBlock(book, entry.Key);
        SetText(block, entry.Name, entry.Description);
        SetValue(block, AbbreviationKey, entry.Abbreviation);
        SetList(block, TopicsKey, entry.Topics);
        block.EnsureId();
    }

    /// <inheritdoc cref="Save(Book, GlossaryEntry)"/>
    public static void Save(Book book, SourceEntry entry)
    {
        var block = EntryBlock(book, entry.Key);
        // A source has no description of its own; lines below the title that someone wrote in Logseq stay.
        SetText(block, entry.Title, ReadText(block).Remainder);
        SetValue(block, KindKey, KindValue(entry.Kind));
        SetList(block, AuthorsKey, entry.AuthorIds.Select(BlockRefs.Format));
        SetValue(block, YearKey, entry.Year);
        SetValue(block, UrlKey, entry.Url);
        SetValue(block, PublisherKey, entry.Publisher);
        block.EnsureId();
    }

    /// <summary>Removes the entry block with everything below it.</summary>
    /// <exception cref="ArgumentException">There is no such entry in the appendix.</exception>
    public static void Delete(Book book, Guid key) => book.Page.RemoveBlock(EntryBlock(book, key));

    /// <summary>
    /// The initials of the parts of <paramref name="name"/> in capitals (at most six); if another person
    /// (not <paramref name="exceptKey"/>) has them already, a digit from 2 is appended.
    /// </summary>
    public static string SuggestAbbreviation(Book book, string name, Guid? exceptKey = null)
    {
        var initials = name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.FirstOrDefault(char.IsLetterOrDigit))
            .Where(c => c != '\0')
            .Select(char.ToUpperInvariant);
        var baseText = new string([.. initials.Take(MaxAbbreviationLength)]);
        var taken = People(book)
            .Where(person => person.Key != exceptKey)
            .Select(person => person.Abbreviation.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = baseText;
        for (var digit = 2; candidate.Length > 0 && taken.Contains(candidate); digit++)
        {
            var suffix = digit.ToString();
            candidate = baseText[..Math.Min(baseText.Length, MaxAbbreviationLength - suffix.Length)] + suffix;
        }

        return candidate;
    }

    /// <summary>
    /// The resource key of the first problem with the person: <c>AppendixNameEmpty</c>,
    /// <c>AppendixAbbreviationInvalid</c> (1-6 letters, digits, <c>.</c> or <c>-</c>) or <c>AppendixAbbreviationTaken</c>
    /// (by another person, ignoring case); <c>null</c> if there is none.
    /// </summary>
    public static string? ValidationError(Book book, PersonEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            return "AppendixNameEmpty";
        }

        var abbreviation = entry.Abbreviation.Trim();
        if (!AbbreviationRegex().IsMatch(abbreviation))
        {
            return "AppendixAbbreviationInvalid";
        }

        return People(book).Any(person =>
            person.Key != entry.Key && string.Equals(person.Abbreviation.Trim(), abbreviation, StringComparison.OrdinalIgnoreCase))
            ? "AppendixAbbreviationTaken"
            : null;
    }

    /// <summary>The resource key <c>AppendixTermEmpty</c> if the term is blank; otherwise <c>null</c>.</summary>
    public static string? ValidationError(Book book, GlossaryEntry entry) =>
        string.IsNullOrWhiteSpace(entry.Term) ? "AppendixTermEmpty" : null;

    /// <summary>The resource key <c>AppendixTitleEmpty</c> if the title is blank; otherwise <c>null</c>.</summary>
    public static string? ValidationError(Book book, SourceEntry entry) =>
        string.IsNullOrWhiteSpace(entry.Title) ? "AppendixTitleEmpty" : null;

    /// <summary>
    /// The <c>id::</c> of the person called each name (ignoring case); a person written in Logseq without one gets it
    /// now. A name nobody has is added as a person with a suggested abbreviation. Blank names are skipped.
    /// </summary>
    public static IReadOnlyList<Guid> EnsureAuthors(Book book, IReadOnlyList<string> names)
    {
        var ids = new List<Guid>();
        foreach (var name in names.Select(n => n.Trim()).Where(n => n.Length > 0))
        {
            var known = People(Current(book)).FirstOrDefault(person =>
                string.Equals(person.Name, name, StringComparison.OrdinalIgnoreCase));
            if (known is not null)
            {
                ids.Add(known.Id != Guid.Empty ? known.Id : EntryBlock(book, known.Key).EnsureId());
                continue;
            }

            var key = Add(book, AppendixKind.People);
            var current = Current(book);
            var created = People(current).Single(person => person.Key == key);
            Save(book, created with { Name = name, Abbreviation = SuggestAbbreviation(current, name, key) });
            ids.Add(created.Id);
        }

        return ids;
    }

    /// <summary>A fresh view of the page: the book's view does not show what was written since it was loaded.</summary>
    private static Book Current(Book book) => Book.Load(book.Page);

    private static OutlineNode CreateSection(Book book, AppendixKind kind)
    {
        var title = kind switch
        {
            AppendixKind.Glossary => "Glossar",
            AppendixKind.People => "Menschen, die mich inspiriert haben",
            AppendixKind.Sources => "Quellen",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "An appendix section needs a kind."),
        };
        var next = Current(book).Root.Children.FirstOrDefault(node => node.AppendixKind > kind);
        var roots = book.Page.Roots;
        var index = next is null ? roots.Count : roots.ToList().IndexOf(next.Block!);
        var heading = Block.CreateDetached(
            HeadingText.WithTitle("", 1, title), [new BlockProperty(AppendixKinds.Key, AppendixKinds.FileValue(kind)!)]);
        book.Page.InsertBlock(null, index, heading);
        return Current(book).AppendixSection(kind)!;
    }

    private static IEnumerable<Block> EntryBlocks(Book book, AppendixKind kind) =>
        book.AppendixSection(kind)?.TextBlocks.Select(text => text.Block) ?? [];

    private static Block EntryBlock(Book book, Guid key)
    {
        var current = Current(book);
        return current.FindTextBlock(key) is { } text && current.IsInAppendix(key)
            ? text.Block
            : throw new ArgumentException("There is no such entry in the appendix.", nameof(key));
    }

    private static (string First, string Remainder) ReadText(Block block)
    {
        var parts = BlockTextEscape.Unescape(block.Content).Split('\n', 2);
        return (parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : "");
    }

    private static GlossaryEntry ReadGlossary(Block block)
    {
        var (term, explanation) = ReadText(block);
        return new GlossaryEntry(block.Key, block.Id ?? Guid.Empty, term, GetList(block, FormsKey), explanation);
    }

    private static PersonEntry ReadPerson(Block block)
    {
        var (name, description) = ReadText(block);
        return new PersonEntry(
            block.Key, block.Id ?? Guid.Empty, name, GetValue(block, AbbreviationKey) ?? "", GetList(block, TopicsKey), description);
    }

    private static SourceEntry ReadSource(Block block)
    {
        var (title, _) = ReadText(block);
        var authors = BlockRefs.Find(block.GetProperty(AuthorsKey) ?? "").Select(r => r.Id).ToList();
        return new SourceEntry(
            block.Key, block.Id ?? Guid.Empty, title, ParseKind(block.GetProperty(KindKey)), authors,
            GetValue(block, YearKey), GetValue(block, UrlKey), GetValue(block, PublisherKey));
    }

    private static string? GetValue(Block block, string key) =>
        block.GetProperty(key) is { } value && value.Trim().Length > 0 ? value.Trim() : null;

    private static List<string> GetList(Block block, string key) =>
        [.. (block.GetProperty(key) ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

    private static SourceKind ParseKind(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "buch" => SourceKind.Book,
            "video" => SourceKind.Video,
            "artikel" => SourceKind.Article,
            "webseite" => SourceKind.Website,
            _ => SourceKind.Other,
        };

    private static string KindValue(SourceKind kind) =>
        kind switch
        {
            SourceKind.Book => "buch",
            SourceKind.Video => "video",
            SourceKind.Article => "artikel",
            SourceKind.Website => "webseite",
            _ => "sonstiges",
        };

    /// <summary>First line = <paramref name="first"/> (one line), then the lines of <paramref name="rest"/>.</summary>
    private static void SetText(Block block, string first, string rest)
    {
        first = OneLine(first);
        rest = rest.Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n');
        block.SetContent(BlockTextEscape.Escape(rest.Length > 0 ? first + "\n" + rest : first));
    }

    private static void SetValue(Block block, string key, string? value)
    {
        value = value is null ? "" : OneLine(value);
        if (value.Length == 0)
        {
            block.RemoveProperty(key);
        }
        else
        {
            block.SetProperty(key, value);
        }
    }

    private static void SetList(Block block, string key, IEnumerable<string> values) =>
        SetValue(block, key, string.Join(ListSeparator, values.Select(v => OneLine(v).Replace(',', ' ').Trim()).Where(v => v.Length > 0)));

    private static string OneLine(string text) => text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
}
