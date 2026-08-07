using BurrowWin.Models;
using BurrowWin.Services;

namespace BurrowWin.Tests;

internal sealed class RecordingSafeDeletionService : ISafeDeletionService
{
    public List<string> DeletedPaths { get; } = [];

    public List<SafeDeletionRequest> Requests { get; } = [];

    public Task<LeftoverRemovalResult> DeleteFileOrDirectoryAsync(
        SafeDeletionRequest request,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(new LeftoverRemovalResult(
                request.Path,
                DeletionDisposition.Cancelled,
                "Cancelled.",
                request.SizeBytes,
                request.Authorization.OperationId));
        }

        if (!request.BusinessRuleSatisfied)
        {
            return Task.FromResult(new LeftoverRemovalResult(
                request.Path,
                DeletionDisposition.Rejected,
                request.BusinessRuleFailure ?? "Rejected.",
                request.SizeBytes,
                request.Authorization.OperationId));
        }

        var canonicalPath = Path.GetFullPath(request.Path);
        DeletedPaths.Add(canonicalPath);
        return Task.FromResult(new LeftoverRemovalResult(
            request.Path,
            DeletionDisposition.Recycled,
            "Moved to Recycle Bin.",
            request.SizeBytes,
            request.Authorization.OperationId,
            canonicalPath,
            DateTimeOffset.UtcNow,
            "shell:RecycleBinFolder",
            true));
    }
}
