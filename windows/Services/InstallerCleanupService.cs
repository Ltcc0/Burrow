using BurrowWin.Models;

namespace BurrowWin.Services;

public sealed class InstallerCleanupService : IInstallerCleanupService
{
    public const string DeletionSource = "installer";

    private const int DefaultDaysOld = 30;

    private static readonly string[] InstallerPatterns =
    [
        "*.exe",
        "*.msi",
        "*.zip",
        "*.7z",
        "*.rar",
        "*.tar.gz",
        "*.iso"
    ];

    private readonly ISafeDeletionService _safeDeletionService;
    private readonly string _downloadsPath;
    private readonly int _daysOld;

    public InstallerCleanupService()
        : this(ResolveDefaultDownloadsPath(), DefaultDaysOld, new RecycleBinDeletionService())
    {
    }

    public InstallerCleanupService(ISafeDeletionService safeDeletionService)
        : this(ResolveDefaultDownloadsPath(), DefaultDaysOld, safeDeletionService)
    {
    }

    public InstallerCleanupService(string downloadsPath, int daysOld = DefaultDaysOld)
        : this(downloadsPath, daysOld, new RecycleBinDeletionService())
    {
    }

    public InstallerCleanupService(
        string downloadsPath,
        int daysOld,
        ISafeDeletionService safeDeletionService)
    {
        _safeDeletionService = safeDeletionService;
        _downloadsPath = Path.GetFullPath(downloadsPath);
        _daysOld = Math.Max(1, daysOld);
    }

    private static string ResolveDefaultDownloadsPath()
    {
        var diagnosticRoot = Environment.GetEnvironmentVariable("BURROWWIN_INSTALLER_ROOT");
        if (!string.IsNullOrWhiteSpace(diagnosticRoot))
        {
            return diagnosticRoot;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    public Task<IReadOnlyList<InstallerCleanupCandidate>> PreviewAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            if (!Directory.Exists(_downloadsPath))
            {
                return (IReadOnlyList<InstallerCleanupCandidate>)Array.Empty<InstallerCleanupCandidate>();
            }

            var cutoffUtc = DateTimeOffset.UtcNow.AddDays(-_daysOld);
            var candidates = new Dictionary<string, InstallerCleanupCandidate>(StringComparer.OrdinalIgnoreCase);

            foreach (var pattern in InstallerPatterns)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(_downloadsPath, pattern, SearchOption.TopDirectoryOnly);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var candidate = BuildCandidate(file, cutoffUtc);
                    if (candidate is not null)
                    {
                        candidates[candidate.Path] = candidate;
                    }
                }
            }

            return (IReadOnlyList<InstallerCleanupCandidate>)candidates.Values
                .OrderByDescending(candidate => candidate.SizeBytes)
                .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, cancellationToken);
    }

    public Task<DeletionBatchResult> RemoveAsync(
        IReadOnlyList<InstallerCleanupCandidate> candidates,
        DestructiveActionAuthorization authorization,
        IProgress<DeletionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(authorization);

        var requests = candidates.Select(candidate =>
        {
            var businessRuleSatisfied = IsCandidateStillAllowed(candidate, out var failure);
            return new SafeDeletionRequest(
                candidate.Path,
                candidate.SizeBytes,
                _downloadsPath,
                DeletionSource,
                authorization,
                "File",
                businessRuleSatisfied,
                failure);
        }).ToArray();

        return SafeDeletionBatchRunner.RunAsync(
            _safeDeletionService,
            requests,
            authorization,
            progress,
            cancellationToken);
    }

    private InstallerCleanupCandidate? BuildCandidate(string file, DateTimeOffset cutoffUtc)
    {
        try
        {
            var fullPath = Path.GetFullPath(file);
            if (!IsPathDirectlyInDownloads(fullPath) || !IsInstallerPattern(fullPath))
            {
                return null;
            }

            var info = new FileInfo(fullPath);
            var lastWriteTime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            if (lastWriteTime >= cutoffUtc)
            {
                return null;
            }

            return new InstallerCleanupCandidate(
                info.Name,
                info.FullName,
                GetKind(info.Name),
                info.Length,
                lastWriteTime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private bool IsCandidateStillAllowed(InstallerCleanupCandidate candidate, out string? failure)
    {
        failure = null;
        try
        {
            var candidatePath = Path.GetFullPath(candidate.Path);
            if (IsPathDirectlyInDownloads(candidatePath) && IsInstallerPattern(candidatePath))
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            failure = $"Installer target could not be revalidated ({ex.GetType().Name}).";
            return false;
        }

        failure = "Path is outside the installer preview scope or no longer matches an allowed installer pattern.";
        return false;
    }

    private bool IsPathDirectlyInDownloads(string fullPath)
    {
        var parent = Path.GetDirectoryName(fullPath);
        return string.Equals(
            Path.GetFullPath(parent ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            _downloadsPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInstallerPattern(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetKind(string name)
    {
        if (name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
        {
            return "MSI installer";
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return "Windows installer";
        }

        if (name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
        {
            return "Disk image";
        }

        return "Archive";
    }
}
