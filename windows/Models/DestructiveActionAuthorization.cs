namespace BurrowWin.Models;

public sealed class DestructiveActionAuthorization
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(5);

    private readonly HashSet<string> _approvedPaths;

    private DestructiveActionAuthorization(
        Guid operationId,
        string source,
        DateTimeOffset confirmedAtUtc,
        DateTimeOffset expiresAtUtc,
        IEnumerable<string> approvedPaths)
    {
        OperationId = operationId;
        Source = source;
        ConfirmedAtUtc = confirmedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        _approvedPaths = approvedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public Guid OperationId { get; }

    public string Source { get; }

    public DateTimeOffset ConfirmedAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public int ApprovedPathCount => _approvedPaths.Count;

    public static DestructiveActionAuthorization Confirmed(
        string source,
        IEnumerable<string> approvedPaths,
        DateTimeOffset? confirmedAtUtc = null,
        TimeSpan? lifetime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(approvedPaths);

        var now = confirmedAtUtc ?? DateTimeOffset.UtcNow;
        var effectiveLifetime = lifetime ?? DefaultLifetime;
        if (effectiveLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        var normalizedPaths = approvedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeForComparison)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedPaths.Length == 0)
        {
            throw new ArgumentException("At least one deletion path must be explicitly approved.", nameof(approvedPaths));
        }

        return new DestructiveActionAuthorization(
            Guid.NewGuid(),
            source.Trim(),
            now,
            now + effectiveLifetime,
            normalizedPaths);
    }

    public bool Allows(string source, string canonicalPath, DateTimeOffset nowUtc)
    {
        if (!string.Equals(Source, source, StringComparison.Ordinal) ||
            nowUtc < ConfirmedAtUtc ||
            nowUtc >= ExpiresAtUtc)
        {
            return false;
        }

        try
        {
            return _approvedPaths.Contains(NormalizeForComparison(canonicalPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string NormalizeForComparison(string path)
    {
        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
        var root = Path.GetPathRoot(fullPath);
        return !string.IsNullOrWhiteSpace(root) &&
               string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? root
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
