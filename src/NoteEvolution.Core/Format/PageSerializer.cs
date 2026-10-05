using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Format;

/// <summary>Writes a <see cref="Page"/> back to bytes: prefix lines, then every block's own lines depth-first.</summary>
public static class PageSerializer
{
    /// <exception cref="InvalidOperationException">The page is read-only because it could not be parsed safely.</exception>
    public static byte[] Serialize(Page page)
    {
        if (page.IsReadOnly)
        {
            throw new InvalidOperationException($"Die Datei '{page.FilePath}' ist schreibgeschützt: {page.ParseError}");
        }

        return TextDocument.Encode(page.HasBom, page.PrefixLines.Concat(page.AllBlocks().SelectMany(b => b.Lines)));
    }
}
