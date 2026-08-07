using BurrowWin.Services;
using Xunit;

namespace BurrowWin.Tests;

public sealed class WindowsPathSafetyPolicyTests
{
    private readonly string _scope = Path.Combine(
        Path.GetTempPath(),
        "BurrowWinPathSafetyTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Evaluate_AllowsCanonicalChildPath()
    {
        var policy = new WindowsPathSafetyPolicy(new FakePathInspector());
        var target = Path.Combine(_scope, "project", "bin");

        var result = policy.Evaluate(target, _scope);

        Assert.True(result.IsSafe, result.Message);
        Assert.Equal(Path.GetFullPath(target), result.CanonicalPath);
    }

    [Fact]
    public void Evaluate_RejectsTraversal()
    {
        var policy = new WindowsPathSafetyPolicy(new FakePathInspector());

        var result = policy.Evaluate(Path.Combine(_scope, "..", "outside"), _scope);

        Assert.False(result.IsSafe);
        Assert.Contains("Traversal", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"\\?\C:\Users\me\artifact")]
    [InlineData(@"\\.\C:\Users\me\artifact")]
    [InlineData(@"\??\C:\Users\me\artifact")]
    [InlineData(@"\\server\share\artifact")]
    public void Evaluate_RejectsDeviceAndUncPaths(string target)
    {
        var policy = new WindowsPathSafetyPolicy(new FakePathInspector());

        var result = policy.Evaluate(target, _scope);

        Assert.False(result.IsSafe);
    }

    [Fact]
    public void Evaluate_RejectsVolumeRootAndScopeRoot()
    {
        var policy = new WindowsPathSafetyPolicy(new FakePathInspector());
        var volumeRoot = Path.GetPathRoot(_scope)!;

        var volumeResult = policy.Evaluate(volumeRoot, _scope);
        var scopeResult = policy.Evaluate(_scope, _scope);

        Assert.False(volumeResult.IsSafe);
        Assert.False(scopeResult.IsSafe);
    }

    [Fact]
    public void Evaluate_RejectsOutsideScopeAndAlternateDataStream()
    {
        var policy = new WindowsPathSafetyPolicy(new FakePathInspector());
        var outside = Path.Combine(Path.GetDirectoryName(_scope)!, "outside");

        var outsideResult = policy.Evaluate(outside, _scope);
        var adsResult = policy.Evaluate(Path.Combine(_scope, "archive.zip:stream"), _scope);

        Assert.False(outsideResult.IsSafe);
        Assert.False(adsResult.IsSafe);
    }

    [Fact]
    public void Evaluate_RejectsProtectedWindowsLocation()
    {
        var windowsRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.False(string.IsNullOrWhiteSpace(windowsRoot));
        var policy = new WindowsPathSafetyPolicy(new FakePathInspector());

        var result = policy.Evaluate(Path.Combine(windowsRoot, "System32"), windowsRoot);

        Assert.False(result.IsSafe);
        Assert.Contains("protected", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("junction")]
    [InlineData("symlink")]
    public void Evaluate_RejectsEveryReparsePointKind(string reparseName)
    {
        var reparsePath = Path.GetFullPath(Path.Combine(_scope, reparseName));
        var policy = new WindowsPathSafetyPolicy(new FakePathInspector(reparsePath));

        var result = policy.Evaluate(Path.Combine(reparsePath, "child"), _scope);

        Assert.False(result.IsSafe);
        Assert.Contains("reparse point", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakePathInspector : IWindowsPathInspector
    {
        private readonly HashSet<string> _reparsePaths;

        public FakePathInspector(params string[] reparsePaths)
        {
            _reparsePaths = reparsePaths
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public FileAttributes? GetAttributes(string path)
        {
            return _reparsePaths.Contains(Path.GetFullPath(path))
                ? FileAttributes.Directory | FileAttributes.ReparsePoint
                : FileAttributes.Directory;
        }
    }
}
