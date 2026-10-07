using System.Text.Json;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Vaults;

namespace NoteEvolution.Core.Links;

/// <summary>
/// Finds a note block whose <c>id::</c> may not be in the file yet.
/// </summary>
/// <param name="PagePath">The note file relative to the vault root, with <c>/</c> as separator.</param>
/// <param name="TreePath">Child indices from the page's root blocks down to the block.</param>
/// <param name="ContentSha256">Lowercase hex SHA-256 of the block's <c>Content</c> (UTF-8).</param>
public sealed record NoteLocator(string PagePath, int[] TreePath, string ContentSha256)
{
    public bool Equals(NoteLocator? other) =>
        other is not null
        && PagePath == other.PagePath
        && TreePath.SequenceEqual(other.TreePath)
        && ContentSha256 == other.ContentSha256;

    public override int GetHashCode() => HashCode.Combine(PagePath, TreePath.Length, ContentSha256);
}

/// <summary>A <c>used-in::</c> change of a note that could not be written yet.</summary>
/// <param name="Remove"><c>true</c>: remove the entry; <c>false</c>: add it (and set the note's <c>id::</c>).</param>
public sealed record PendingNoteUpdate(
    Guid NoteBlockId, NoteLocator Locator, string BookLinkName, Guid BookBlockId, bool Remove);

/// <summary>
/// The open note updates in <c>.noteevolution/pending.json</c>. A missing or damaged file counts as empty.
/// The list is read once and then kept in memory; every change is written atomically.
/// </summary>
public sealed class PendingStore(string vaultRoot)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path = Path.Combine(vaultRoot, VaultSettings.FolderName, "pending.json");
    private readonly object _gate = new();
    private List<PendingNoteUpdate>? _entries;

    public IReadOnlyList<PendingNoteUpdate> Load()
    {
        lock (_gate)
        {
            return [.. Entries];
        }
    }

    public void Add(PendingNoteUpdate update)
    {
        lock (_gate)
        {
            Entries.Add(update);
            Write();
        }
    }

    /// <summary>Removes every entry equal to <paramref name="update"/>.</summary>
    public void Remove(PendingNoteUpdate update)
    {
        lock (_gate)
        {
            if (Entries.RemoveAll(e => e == update) > 0)
            {
                Write();
            }
        }
    }

    private List<PendingNoteUpdate> Entries => _entries ??= Read();

    private List<PendingNoteUpdate> Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var entries = JsonSerializer.Deserialize<List<PendingNoteUpdate?>>(File.ReadAllBytes(_path), Json) ?? [];
            return [.. entries.OfType<PendingNoteUpdate>().Where(IsComplete)];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // JSON may omit required members; such entries cannot be retried.
    private static bool IsComplete(PendingNoteUpdate e) =>
        e.Locator is { PagePath: not null, TreePath: not null, ContentSha256: not null } && e.BookLinkName is not null;

    private void Write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        AtomicFile.Write(_path, JsonSerializer.SerializeToUtf8Bytes(_entries, Json));
    }
}
