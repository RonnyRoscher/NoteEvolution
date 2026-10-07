namespace NoteEvolution.Core.Format;

/// <summary>
/// Where a block's property lines are, as indices into the block's own lines (bullet line = 0).
/// </summary>
/// <param name="Start">Index of the first property line, or where the first one would go.</param>
/// <param name="Count">Number of property lines.</param>
/// <param name="FenceUnclosed">The bullet line opens a code fence that is never closed.</param>
internal readonly record struct PropertySection(int Start, int Count, bool FenceUnclosed)
{
    /// <summary>Index at which a new property line is inserted (after the last existing one).</summary>
    public int InsertIndex => Start + Count;
}
