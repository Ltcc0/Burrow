namespace BurrowWin.Services;

public sealed record RecycleBinBackendResult(
    bool Succeeded,
    string Message,
    string? RecoveryLocator = null);

public interface IRecycleBinBackend
{
    Task<RecycleBinBackendResult> RecycleAsync(string canonicalPath, CancellationToken cancellationToken = default);
}
