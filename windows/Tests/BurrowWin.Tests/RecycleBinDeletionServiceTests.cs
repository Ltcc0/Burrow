using BurrowWin.Models;
using BurrowWin.Services;
using System.Text.Json;
using Xunit;

namespace BurrowWin.Tests;

public sealed class RecycleBinDeletionServiceTests : IDisposable
{
    private readonly string _scope = Path.Combine(
        Path.GetTempPath(),
        "BurrowWinRecycleTests",
        Guid.NewGuid().ToString("N"));

    public RecycleBinDeletionServiceTests()
    {
        Directory.CreateDirectory(_scope);
    }

    [Fact]
    public async Task DeleteAsync_RecyclesAuthorizedPathAndRecordsRecoveryReceipt()
    {
        var target = Path.Combine(_scope, "installer.msi");
        await File.WriteAllTextAsync(target, "payload");
        var authorization = DestructiveActionAuthorization.Confirmed("installer", [target]);
        var backend = new RecordingRecycleBinBackend();
        var receipts = new RecordingReceiptStore();
        var service = new RecycleBinDeletionService(
            new WindowsPathSafetyPolicy(),
            backend,
            receipts);

        var result = await service.DeleteFileOrDirectoryAsync(new SafeDeletionRequest(
            target,
            7,
            _scope,
            "installer",
            authorization,
            "File"));

        Assert.Equal(DeletionDisposition.Recycled, result.Disposition);
        Assert.Equal("shell:RecycleBinFolder", result.RecoveryLocator);
        Assert.True(result.RecoveryLocatorAvailable);
        Assert.Equal(Path.GetFullPath(target), Assert.Single(backend.Paths));
        var receipt = Assert.Single(receipts.Receipts);
        Assert.Equal(authorization.OperationId, receipt.OperationId);
        Assert.Equal(DeletionDisposition.Recycled, receipt.Disposition);
        Assert.Equal("shell:RecycleBinFolder", receipt.RecoveryLocator);
    }

    [Fact]
    public async Task DeleteAsync_RejectsExpiredOrNonExactAuthorization()
    {
        var target = Path.Combine(_scope, "artifact");
        Directory.CreateDirectory(target);
        var now = DateTimeOffset.UtcNow;
        var authorization = DestructiveActionAuthorization.Confirmed(
            "purge",
            [Path.Combine(_scope, "different")],
            now.AddMinutes(-10),
            TimeSpan.FromMinutes(1));
        var backend = new RecordingRecycleBinBackend();
        var receipts = new RecordingReceiptStore();
        var service = new RecycleBinDeletionService(
            new WindowsPathSafetyPolicy(),
            backend,
            receipts,
            () => now);

        var result = await service.DeleteFileOrDirectoryAsync(new SafeDeletionRequest(
            target,
            1,
            _scope,
            "purge",
            authorization,
            "Directory"));

        Assert.Equal(DeletionDisposition.Rejected, result.Disposition);
        Assert.Empty(backend.Paths);
        Assert.Equal(DeletionDisposition.Rejected, Assert.Single(receipts.Receipts).Disposition);
    }

    [Fact]
    public async Task DeleteAsync_RevalidatesAtFinalBoundary()
    {
        var target = Path.Combine(_scope, "artifact");
        Directory.CreateDirectory(target);
        var authorization = DestructiveActionAuthorization.Confirmed("purge", [target]);
        var policy = new SequencedPathPolicy(Path.GetFullPath(target));
        var backend = new RecordingRecycleBinBackend();
        var receipts = new RecordingReceiptStore();
        var service = new RecycleBinDeletionService(policy, backend, receipts);

        var result = await service.DeleteFileOrDirectoryAsync(new SafeDeletionRequest(
            target,
            1,
            _scope,
            "purge",
            authorization,
            "Directory"));

        Assert.Equal(DeletionDisposition.Rejected, result.Disposition);
        Assert.Equal(2, policy.CallCount);
        Assert.Empty(backend.Paths);
    }

    [Fact]
    public async Task DeleteAsync_ReportsAlreadyAbsentWithoutClaimingReclaimedBytes()
    {
        var target = Path.Combine(_scope, "missing");
        var authorization = DestructiveActionAuthorization.Confirmed("purge", [target]);
        var backend = new RecordingRecycleBinBackend();
        var receipts = new RecordingReceiptStore();
        var service = new RecycleBinDeletionService(
            new WindowsPathSafetyPolicy(),
            backend,
            receipts);

        var result = await service.DeleteFileOrDirectoryAsync(new SafeDeletionRequest(
            target,
            99,
            _scope,
            "purge",
            authorization));

        Assert.Equal(DeletionDisposition.AlreadyAbsent, result.Disposition);
        Assert.False(result.Changed);
        Assert.Empty(backend.Paths);
    }

    [Fact]
    public async Task JsonReceiptStore_PersistsRecoveryMetadataAsJsonLine()
    {
        var receiptPath = Path.Combine(_scope, "receipts", "deletion-receipts.jsonl");
        var operationId = Guid.NewGuid();
        var receipt = new DeletionReceipt(
            operationId,
            DateTimeOffset.UtcNow,
            "installer",
            Path.Combine(_scope, "setup.msi"),
            Path.Combine(_scope, "setup.msi"),
            "File",
            12,
            DeletionDisposition.Recycled,
            "Moved to Recycle Bin.",
            "shell:RecycleBinFolder",
            true);
        var store = new JsonDeletionReceiptStore(receiptPath);

        await store.RecordAsync(receipt);

        var line = Assert.Single(await File.ReadAllLinesAsync(receiptPath));
        var stored = JsonSerializer.Deserialize<DeletionReceipt>(line);
        Assert.NotNull(stored);
        Assert.Equal(operationId, stored.OperationId);
        Assert.Equal("shell:RecycleBinFolder", stored.RecoveryLocator);
        Assert.True(stored.RecoveryLocatorAvailable);
    }

    public void Dispose()
    {
        if (Directory.Exists(_scope))
        {
            Directory.Delete(_scope, recursive: true);
        }
    }

    private sealed class RecordingRecycleBinBackend : IRecycleBinBackend
    {
        public List<string> Paths { get; } = [];

        public Task<RecycleBinBackendResult> RecycleAsync(
            string canonicalPath,
            CancellationToken cancellationToken = default)
        {
            Paths.Add(canonicalPath);
            return Task.FromResult(new RecycleBinBackendResult(
                true,
                "Moved to Recycle Bin.",
                "shell:RecycleBinFolder"));
        }
    }

    private sealed class RecordingReceiptStore : IDeletionReceiptStore
    {
        public string ReceiptFilePath => "memory://deletion-receipts";

        public List<DeletionReceipt> Receipts { get; } = [];

        public Task RecordAsync(DeletionReceipt receipt, CancellationToken cancellationToken = default)
        {
            Receipts.Add(receipt);
            return Task.CompletedTask;
        }
    }

    private sealed class SequencedPathPolicy(string canonicalPath) : IWindowsPathSafetyPolicy
    {
        public int CallCount { get; private set; }

        public PathSafetyResult Evaluate(string path, string scopeRoot)
        {
            CallCount++;
            return CallCount == 1
                ? PathSafetyResult.Allow(canonicalPath)
                : PathSafetyResult.Reject("Target changed before deletion.", canonicalPath);
        }
    }
}
