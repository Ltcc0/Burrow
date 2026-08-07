namespace BurrowWin.Models;

public sealed record DeletionReceipt(
    Guid OperationId,
    DateTimeOffset TimestampUtc,
    string Source,
    string OriginalPath,
    string? CanonicalPath,
    string? ItemType,
    long SizeBytes,
    DeletionDisposition Disposition,
    string Message,
    string? RecoveryLocator,
    bool RecoveryLocatorAvailable);
