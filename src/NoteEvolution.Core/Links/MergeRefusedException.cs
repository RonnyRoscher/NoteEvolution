namespace NoteEvolution.Core.Links;

/// <summary>
/// <see cref="ILinkService.MergeTextBlocks"/> refused the merge before any change: the blocks have different values for
/// the same property, or the <c>id::</c> of a block that would go is referenced elsewhere in the vault.
/// </summary>
public sealed class MergeRefusedException(string message) : InvalidOperationException(message);
