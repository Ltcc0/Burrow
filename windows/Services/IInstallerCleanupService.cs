using BurrowWin.Models;

namespace BurrowWin.Services;

public interface IInstallerCleanupService
{
    Task<IReadOnlyList<InstallerCleanupCandidate>> PreviewAsync(CancellationToken cancellationToken = default);

    Task<DeletionBatchResult> RemoveAsync(
        IReadOnlyList<InstallerCleanupCandidate> candidates,
        DestructiveActionAuthorization authorization,
        IProgress<DeletionProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
