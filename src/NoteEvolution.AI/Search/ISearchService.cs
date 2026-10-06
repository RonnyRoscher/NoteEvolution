using NoteEvolution.Core.Vaults;

namespace NoteEvolution.AI.Search;

/// <param name="Text">What the user typed; words are matched as prefixes, operators are not interpreted.</param>
/// <param name="Filter">Same semantics as <see cref="INoteRepository.Matches"/>.</param>
/// <param name="Limit">Maximum number of hits.</param>
public record SearchQuery(string Text, NoteFilter Filter, int Limit = 100);

/// <param name="NoteBlockKey">The <see cref="NoteBlock.Key"/> of the matching note.</param>
/// <param name="Score">Relevance; higher is better.</param>
public record SearchHit(Guid NoteBlockKey, double Score);

/// <summary>Full-text search over the notes of a vault.</summary>
public interface ISearchService
{
    /// <summary>Replaces the whole index with the current notes.</summary>
    void Rebuild(INoteRepository notes);

    /// <summary>Re-indexes one page (full path, case-insensitive); a page that left the vault is only removed from the index.</summary>
    void UpdatePage(INoteRepository notes, string pagePath);

    /// <summary>The matching notes, best first. Empty if the text has no searchable word.</summary>
    IReadOnlyList<SearchHit> Search(SearchQuery query);
}
