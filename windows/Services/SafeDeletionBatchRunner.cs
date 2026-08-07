using BurrowWin.Models;

namespace BurrowWin.Services;

public static class SafeDeletionBatchRunner
{
    public static async Task<DeletionBatchResult> RunAsync(
        ISafeDeletionService deletionService,
        IReadOnlyList<SafeDeletionRequest> requests,
        DestructiveActionAuthorization authorization,
        IProgress<DeletionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deletionService);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(authorization);

        if (requests.Count == 0)
        {
            return DeletionBatchResult.Empty(authorization);
        }

        var results = new List<LeftoverRemovalResult>(requests.Count);
        var wasCancelled = false;
        string? lastProcessedPath = null;
        Report(progress, results, requests.Count, lastProcessedPath);

        foreach (var request in requests)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                wasCancelled = true;
                break;
            }

            var result = await deletionService
                .DeleteFileOrDirectoryAsync(request, cancellationToken)
                .ConfigureAwait(false);
            results.Add(result);
            lastProcessedPath = request.Path;
            Report(progress, results, requests.Count, lastProcessedPath);

            if (result.Disposition == DeletionDisposition.Cancelled)
            {
                wasCancelled = true;
                break;
            }
        }

        if (cancellationToken.IsCancellationRequested && results.Count < requests.Count)
        {
            wasCancelled = true;
        }

        return new DeletionBatchResult(
            authorization.OperationId,
            requests.Count,
            results,
            wasCancelled,
            lastProcessedPath);
    }

    private static void Report(
        IProgress<DeletionProgress>? progress,
        IReadOnlyList<LeftoverRemovalResult> results,
        int totalCount,
        string? currentPath)
    {
        progress?.Report(new DeletionProgress(
            results.Count,
            totalCount,
            results.Count(result => result.Disposition == DeletionDisposition.Recycled),
            results.Count(result => result.Disposition == DeletionDisposition.AlreadyAbsent),
            results.Count(result => result.Disposition == DeletionDisposition.Rejected),
            results.Count(result => result.Disposition == DeletionDisposition.Failed),
            results.Where(result => result.Disposition == DeletionDisposition.Recycled).Sum(result => result.SizeBytes),
            currentPath));
    }
}
