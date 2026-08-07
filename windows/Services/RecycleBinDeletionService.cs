using System.Security;
using BurrowWin.Models;

namespace BurrowWin.Services;

public sealed class RecycleBinDeletionService : ISafeDeletionService
{
    private readonly IWindowsPathSafetyPolicy _pathSafetyPolicy;
    private readonly IRecycleBinBackend _recycleBinBackend;
    private readonly IDeletionReceiptStore _receiptStore;
    private readonly Func<DateTimeOffset> _utcNow;

    public RecycleBinDeletionService()
        : this(
            new WindowsPathSafetyPolicy(),
            new VisualBasicRecycleBinBackend(),
            new JsonDeletionReceiptStore(),
            () => DateTimeOffset.UtcNow)
    {
    }

    public RecycleBinDeletionService(
        IWindowsPathSafetyPolicy pathSafetyPolicy,
        IRecycleBinBackend recycleBinBackend,
        IDeletionReceiptStore receiptStore,
        Func<DateTimeOffset>? utcNow = null)
    {
        _pathSafetyPolicy = pathSafetyPolicy ?? throw new ArgumentNullException(nameof(pathSafetyPolicy));
        _recycleBinBackend = recycleBinBackend ?? throw new ArgumentNullException(nameof(recycleBinBackend));
        _receiptStore = receiptStore ?? throw new ArgumentNullException(nameof(receiptStore));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<LeftoverRemovalResult> DeleteFileOrDirectoryAsync(
        SafeDeletionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        LeftoverRemovalResult result;
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                result = CreateResult(request, DeletionDisposition.Cancelled, "Deletion was cancelled before this item started.");
                return await RecordReceiptAsync(request, result).ConfigureAwait(false);
            }

            if (!request.BusinessRuleSatisfied)
            {
                result = CreateResult(
                    request,
                    DeletionDisposition.Rejected,
                    request.BusinessRuleFailure ?? "Deletion target no longer satisfies its fallback scope rules.");
                return await RecordReceiptAsync(request, result).ConfigureAwait(false);
            }

            var safety = _pathSafetyPolicy.Evaluate(request.Path, request.ScopeRoot);
            if (!safety.IsSafe || string.IsNullOrWhiteSpace(safety.CanonicalPath))
            {
                result = CreateResult(request, DeletionDisposition.Rejected, safety.Message, safety.CanonicalPath);
                return await RecordReceiptAsync(request, result).ConfigureAwait(false);
            }

            var canonicalPath = safety.CanonicalPath;
            var now = _utcNow();
            if (!request.Authorization.Allows(request.Source, canonicalPath, now))
            {
                result = CreateResult(
                    request,
                    DeletionDisposition.Rejected,
                    "Deletion authorization is missing, expired, for a different action, or does not include this exact path.",
                    canonicalPath);
                return await RecordReceiptAsync(request, result).ConfigureAwait(false);
            }

            var existsAsDirectory = Directory.Exists(canonicalPath);
            var existsAsFile = File.Exists(canonicalPath);
            if (!existsAsDirectory && !existsAsFile)
            {
                result = CreateResult(
                    request,
                    DeletionDisposition.AlreadyAbsent,
                    "Path was already absent; no bytes were reclaimed.",
                    canonicalPath);
                return await RecordReceiptAsync(request, result).ConfigureAwait(false);
            }

            if (!MatchesExpectedItemType(request.ExpectedItemType, existsAsDirectory, existsAsFile))
            {
                result = CreateResult(
                    request,
                    DeletionDisposition.Rejected,
                    "Deletion target type changed after preview.",
                    canonicalPath);
                return await RecordReceiptAsync(request, result).ConfigureAwait(false);
            }

            // Re-run the full policy at the last boundary before entering the Shell backend.
            safety = _pathSafetyPolicy.Evaluate(canonicalPath, request.ScopeRoot);
            if (!safety.IsSafe || string.IsNullOrWhiteSpace(safety.CanonicalPath))
            {
                result = CreateResult(request, DeletionDisposition.Rejected, safety.Message, canonicalPath);
                return await RecordReceiptAsync(request, result).ConfigureAwait(false);
            }

            canonicalPath = safety.CanonicalPath;
            existsAsDirectory = Directory.Exists(canonicalPath);
            existsAsFile = File.Exists(canonicalPath);
            if (!existsAsDirectory && !existsAsFile)
            {
                result = CreateResult(
                    request,
                    DeletionDisposition.AlreadyAbsent,
                    "Path disappeared before the Recycle Bin operation started; no bytes were reclaimed.",
                    canonicalPath);
                return await RecordReceiptAsync(request, result).ConfigureAwait(false);
            }

            if (!MatchesExpectedItemType(request.ExpectedItemType, existsAsDirectory, existsAsFile))
            {
                result = CreateResult(
                    request,
                    DeletionDisposition.Rejected,
                    "Deletion target type changed at the final safety boundary.",
                    canonicalPath);
                return await RecordReceiptAsync(request, result).ConfigureAwait(false);
            }

            if (!request.Authorization.Allows(request.Source, canonicalPath, _utcNow()))
            {
                result = CreateResult(
                    request,
                    DeletionDisposition.Rejected,
                    "Deletion authorization expired or no longer matches at the final safety boundary.",
                    canonicalPath);
                return await RecordReceiptAsync(request, result).ConfigureAwait(false);
            }

            var backendResult = await _recycleBinBackend
                .RecycleAsync(canonicalPath, cancellationToken)
                .ConfigureAwait(false);
            result = backendResult.Succeeded
                ? new LeftoverRemovalResult(
                    request.Path,
                    DeletionDisposition.Recycled,
                    backendResult.Message,
                    request.SizeBytes,
                    request.Authorization.OperationId,
                    canonicalPath,
                    _utcNow(),
                    backendResult.RecoveryLocator,
                    !string.IsNullOrWhiteSpace(backendResult.RecoveryLocator))
                : CreateResult(request, DeletionDisposition.Failed, backendResult.Message, canonicalPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or OperationCanceledException or ArgumentException or NotSupportedException)
        {
            result = CreateResult(
                request,
                ex is OperationCanceledException ? DeletionDisposition.Cancelled : DeletionDisposition.Failed,
                ex.Message);
        }

        return await RecordReceiptAsync(request, result).ConfigureAwait(false);
    }

    private static bool MatchesExpectedItemType(string? expectedItemType, bool isDirectory, bool isFile)
    {
        if (string.IsNullOrWhiteSpace(expectedItemType))
        {
            return true;
        }

        return expectedItemType.Equals("Directory", StringComparison.OrdinalIgnoreCase)
            ? isDirectory
            : expectedItemType.Equals("File", StringComparison.OrdinalIgnoreCase)
                ? isFile
                : true;
    }

    private static LeftoverRemovalResult CreateResult(
        SafeDeletionRequest request,
        DeletionDisposition disposition,
        string message,
        string? canonicalPath = null)
    {
        return new LeftoverRemovalResult(
            request.Path,
            disposition,
            message,
            request.SizeBytes,
            request.Authorization.OperationId,
            canonicalPath);
    }

    private async Task<LeftoverRemovalResult> RecordReceiptAsync(
        SafeDeletionRequest request,
        LeftoverRemovalResult result)
    {
        var receipt = new DeletionReceipt(
            request.Authorization.OperationId,
            _utcNow(),
            request.Source,
            request.Path,
            result.CanonicalPath,
            request.ExpectedItemType,
            request.SizeBytes,
            result.Disposition,
            result.Message,
            result.RecoveryLocator,
            result.RecoveryLocatorAvailable);

        try
        {
            await _receiptStore.RecordAsync(receipt, CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return result with
            {
                Message = $"{result.Message} Recovery receipt could not be recorded ({ex.GetType().Name})."
            };
        }
    }
}
