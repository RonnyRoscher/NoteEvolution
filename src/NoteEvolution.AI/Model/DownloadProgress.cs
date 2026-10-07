namespace NoteEvolution.AI.Model;

/// <summary>Bytes finished and total bytes over all files of the model (files that were already complete count as done).</summary>
public sealed record DownloadProgress(long BytesDone, long BytesTotal);

public sealed class ModelDownloadException : Exception
{
    public ModelDownloadException(string message) : base(message) { }

    public ModelDownloadException(string message, Exception inner) : base(message, inner) { }
}
