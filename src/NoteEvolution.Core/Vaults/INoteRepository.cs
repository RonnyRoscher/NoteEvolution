namespace NoteEvolution.Core.Vaults;

/// <summary>Restricts notes: unused only and/or an inclusive date range. A date filter excludes undated notes.</summary>
public record NoteFilter(bool HideUsed, DateOnly? From, DateOnly? To);

public interface INoteRepository
{
    /// <summary>Every block (any depth) of every non-book page, in vault/file order.</summary>
    IEnumerable<NoteBlock> All();

    NoteBlock? Get(Guid key);

    /// <summary>Root blocks of the journal pages that match, newest day first, in file order within a day.</summary>
    IReadOnlyList<NoteBlock> Journal(NoteFilter filter);

    bool Matches(NoteBlock note, NoteFilter filter);
}
