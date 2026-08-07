using BurrowWin.Models;
using BurrowWin.Services;
using Xunit;

namespace BurrowWin.Tests;

public sealed class SafeDeletionBatchRunnerTests
{
    [Fact]
    public async Task RunAsync_StopsFutureWorkAfterCancellationAndPreservesCompletedResults()
    {
        using var cancellation = new CancellationTokenSource();
        var paths = TestPaths(3);
        var authorization = DestructiveActionAuthorization.Confirmed("test", paths);
        var service = new SequencedDeletionService((request, index) =>
        {
            if (index == 0)
            {
                cancellation.Cancel();
            }

            return Result(request, DeletionDisposition.Recycled);
        });
        var requests = Requests(paths, authorization);

        var batch = await SafeDeletionBatchRunner.RunAsync(
            service,
            requests,
            authorization,
            cancellationToken: cancellation.Token);

        Assert.Equal(DeletionBatchOutcome.Cancelled, batch.Outcome);
        Assert.True(batch.WasCancelled);
        Assert.Equal(1, batch.ProcessedCount);
        Assert.Equal(1, batch.RecycledCount);
        Assert.Equal(1, service.CallCount);
    }

    [Fact]
    public async Task RunAsync_ReportsMonotonicProgressAndPartialFailureHonestly()
    {
        var paths = TestPaths(3);
        var authorization = DestructiveActionAuthorization.Confirmed("test", paths);
        var dispositions = new[]
        {
            DeletionDisposition.Recycled,
            DeletionDisposition.Failed,
            DeletionDisposition.AlreadyAbsent
        };
        var service = new SequencedDeletionService((request, index) => Result(request, dispositions[index]));
        var snapshots = new List<DeletionProgress>();

        var batch = await SafeDeletionBatchRunner.RunAsync(
            service,
            Requests(paths, authorization),
            authorization,
            new InlineProgress<DeletionProgress>(snapshots.Add));

        Assert.Equal(DeletionBatchOutcome.PartialSuccess, batch.Outcome);
        Assert.False(batch.Succeeded);
        Assert.Equal(1, batch.RecycledCount);
        Assert.Equal(1, batch.FailedCount);
        Assert.Equal(1, batch.AlreadyAbsentCount);
        Assert.Equal(new[] { 0, 1, 2, 3 }, snapshots.Select(snapshot => snapshot.ProcessedCount).ToArray());
        Assert.True(snapshots.Zip(snapshots.Skip(1), (left, right) => right.Fraction >= left.Fraction).All(value => value));
    }

    private static string[] TestPaths(int count)
    {
        var scope = Path.Combine(Path.GetTempPath(), "BurrowWinBatchTests", Guid.NewGuid().ToString("N"));
        return Enumerable.Range(0, count).Select(index => Path.Combine(scope, $"item-{index}")).ToArray();
    }

    private static SafeDeletionRequest[] Requests(
        IReadOnlyList<string> paths,
        DestructiveActionAuthorization authorization)
    {
        var scope = Path.GetDirectoryName(paths[0])!;
        return paths.Select(path => new SafeDeletionRequest(path, 10, scope, "test", authorization)).ToArray();
    }

    private static LeftoverRemovalResult Result(
        SafeDeletionRequest request,
        DeletionDisposition disposition)
    {
        return new LeftoverRemovalResult(
            request.Path,
            disposition,
            disposition.ToString(),
            request.SizeBytes,
            request.Authorization.OperationId,
            Path.GetFullPath(request.Path));
    }

    private sealed class SequencedDeletionService(
        Func<SafeDeletionRequest, int, LeftoverRemovalResult> resultFactory) : ISafeDeletionService
    {
        public int CallCount { get; private set; }

        public Task<LeftoverRemovalResult> DeleteFileOrDirectoryAsync(
            SafeDeletionRequest request,
            CancellationToken cancellationToken = default)
        {
            var result = resultFactory(request, CallCount);
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
