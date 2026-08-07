namespace BurrowWin.Models;

public enum DeletionBatchOutcome
{
    Succeeded,
    PartialSuccess,
    Failed,
    Cancelled
}

public sealed record DeletionBatchResult(
    Guid OperationId,
    int TotalCount,
    IReadOnlyList<LeftoverRemovalResult> Results,
    bool WasCancelled,
    string? LastProcessedPath)
{
    public int ProcessedCount => Results.Count;

    public int RecycledCount => Results.Count(result => result.Disposition == DeletionDisposition.Recycled);

    public int AlreadyAbsentCount => Results.Count(result => result.Disposition == DeletionDisposition.AlreadyAbsent);

    public int RejectedCount => Results.Count(result => result.Disposition == DeletionDisposition.Rejected);

    public int FailedCount => Results.Count(result => result.Disposition == DeletionDisposition.Failed);

    public long RecycledBytes => Results
        .Where(result => result.Disposition == DeletionDisposition.Recycled)
        .Sum(result => result.SizeBytes);

    public bool Succeeded => Outcome == DeletionBatchOutcome.Succeeded;

    public DeletionBatchOutcome Outcome
    {
        get
        {
            if (WasCancelled)
            {
                return DeletionBatchOutcome.Cancelled;
            }

            var failures = RejectedCount + FailedCount;
            if (failures == 0 && ProcessedCount == TotalCount)
            {
                return DeletionBatchOutcome.Succeeded;
            }

            return RecycledCount + AlreadyAbsentCount > 0
                ? DeletionBatchOutcome.PartialSuccess
                : DeletionBatchOutcome.Failed;
        }
    }

    public static DeletionBatchResult Empty(DestructiveActionAuthorization authorization)
    {
        return new DeletionBatchResult(authorization.OperationId, 0, [], false, null);
    }
}
