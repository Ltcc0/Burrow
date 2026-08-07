using BurrowWin.Models;

namespace BurrowWin.Services;

public interface IPurgeArtifactService
{
    Task<IReadOnlyList<PurgeProjectCandidate>> PreviewAsync(
        IReadOnlyList<string>? searchRoots = null,
        CancellationToken cancellationToken = default);

    Task<DeletionBatchResult> RemoveAsync(
        IReadOnlyList<PurgeProjectCandidate> projects,
        DestructiveActionAuthorization authorization,
        IProgress<DeletionProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
