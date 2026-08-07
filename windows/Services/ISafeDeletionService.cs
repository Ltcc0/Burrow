using BurrowWin.Models;

namespace BurrowWin.Services;

public interface ISafeDeletionService
{
    Task<LeftoverRemovalResult> DeleteFileOrDirectoryAsync(
        SafeDeletionRequest request,
        CancellationToken cancellationToken = default);
}
