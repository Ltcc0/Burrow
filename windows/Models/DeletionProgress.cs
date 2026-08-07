namespace BurrowWin.Models;

public sealed record DeletionProgress(
    int ProcessedCount,
    int TotalCount,
    int RecycledCount,
    int AlreadyAbsentCount,
    int RejectedCount,
    int FailedCount,
    long RecycledBytes,
    string? CurrentPath)
{
    public double Fraction => TotalCount <= 0 ? 0 : Math.Clamp((double)ProcessedCount / TotalCount, 0, 1);
}
