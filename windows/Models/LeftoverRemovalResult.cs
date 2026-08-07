namespace BurrowWin.Models;

public sealed record LeftoverRemovalResult(
    string Path,
    DeletionDisposition Disposition,
    string Message,
    long SizeBytes,
    Guid OperationId,
    string? CanonicalPath = null,
    DateTimeOffset? RecycledAtUtc = null,
    string? RecoveryLocator = null,
    bool RecoveryLocatorAvailable = false)
{
    public LeftoverRemovalResult(string path, bool succeeded, string message, long sizeBytes)
        : this(
            path,
            succeeded ? DeletionDisposition.Recycled : DeletionDisposition.Failed,
            message,
            sizeBytes,
            Guid.Empty)
    {
    }

    public bool Succeeded => Disposition is DeletionDisposition.Recycled or DeletionDisposition.AlreadyAbsent;

    public bool Changed => Disposition == DeletionDisposition.Recycled;
}
