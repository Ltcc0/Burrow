using System.Security;

namespace BurrowWin.Services;

public sealed class WindowsPathSafetyPolicy : IWindowsPathSafetyPolicy
{
    private static readonly char[] DirectorySeparators = ['\\', '/'];
    private readonly IWindowsPathInspector _pathInspector;

    public WindowsPathSafetyPolicy()
        : this(new SystemWindowsPathInspector())
    {
    }

    public WindowsPathSafetyPolicy(IWindowsPathInspector pathInspector)
    {
        _pathInspector = pathInspector ?? throw new ArgumentNullException(nameof(pathInspector));
    }

    public PathSafetyResult Evaluate(string path, string scopeRoot)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(scopeRoot))
        {
            return PathSafetyResult.Reject("Deletion path and scope root are required.");
        }

        var rawPath = path.Trim();
        var rawScope = scopeRoot.Trim();
        if (ContainsDevicePrefix(rawPath) || ContainsDevicePrefix(rawScope))
        {
            return PathSafetyResult.Reject("Windows device and extended-length paths are not accepted for deletion.");
        }

        if (HasTraversalSegment(rawPath) || HasTraversalSegment(rawScope))
        {
            return PathSafetyResult.Reject("Traversal segments are not accepted for deletion.");
        }

        var expandedPath = Environment.ExpandEnvironmentVariables(rawPath);
        var expandedScope = Environment.ExpandEnvironmentVariables(rawScope);
        if (expandedPath.Contains('%', StringComparison.Ordinal) || expandedScope.Contains('%', StringComparison.Ordinal))
        {
            return PathSafetyResult.Reject("Deletion paths contain unresolved environment variables.");
        }

        if (!Path.IsPathFullyQualified(expandedPath) || !Path.IsPathFullyQualified(expandedScope))
        {
            return PathSafetyResult.Reject("Deletion paths must be fully qualified.");
        }

        if (IsUncPath(expandedPath) || IsUncPath(expandedScope))
        {
            return PathSafetyResult.Reject("UNC deletion targets are not supported by the reversible Windows fallback.");
        }

        if (HasAlternateDataStream(expandedPath) || HasAlternateDataStream(expandedScope))
        {
            return PathSafetyResult.Reject("Alternate data stream paths are not accepted for deletion.");
        }

        string canonicalPath;
        string canonicalScope;
        try
        {
            canonicalPath = Normalize(Path.GetFullPath(expandedPath));
            canonicalScope = Normalize(Path.GetFullPath(expandedScope));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            return PathSafetyResult.Reject($"Deletion path could not be canonicalized ({ex.GetType().Name}).");
        }

        if (IsRoot(canonicalPath) || IsRoot(canonicalScope))
        {
            return PathSafetyResult.Reject("Volume roots cannot be deletion targets or deletion scopes.", canonicalPath);
        }

        if (string.Equals(canonicalPath, canonicalScope, StringComparison.OrdinalIgnoreCase))
        {
            return PathSafetyResult.Reject("The configured scope root itself cannot be deleted.", canonicalPath);
        }

        if (!IsStrictlyUnder(canonicalPath, canonicalScope))
        {
            return PathSafetyResult.Reject("Deletion target is outside its approved scope.", canonicalPath);
        }

        if (IsProtectedTarget(canonicalPath))
        {
            return PathSafetyResult.Reject("Deletion target is within a protected Windows location.", canonicalPath);
        }

        var reparseResult = InspectReparsePoints(canonicalPath);
        if (!reparseResult.IsSafe)
        {
            return reparseResult with { CanonicalPath = canonicalPath };
        }

        return PathSafetyResult.Allow(canonicalPath);
    }

    private static bool ContainsDevicePrefix(string path)
    {
        return path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("GLOBALROOT", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasTraversalSegment(string path)
    {
        return path.Split(DirectorySeparators, StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or "..");
    }

    private static bool IsUncPath(string path)
    {
        return path.StartsWith(@"\\", StringComparison.Ordinal);
    }

    private static bool HasAlternateDataStream(string path)
    {
        var colon = path.IndexOf(':');
        if (colon < 0)
        {
            return false;
        }

        return colon != 1 || path.IndexOf(':', colon + 1) >= 0;
    }

    private static bool IsRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        return !string.IsNullOrWhiteSpace(root) &&
               string.Equals(Normalize(root), path, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStrictlyUnder(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) &&
               !string.Equals(relative, ".", StringComparison.Ordinal) &&
               relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static bool IsProtectedTarget(string path)
    {
        var protectedRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.SystemDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        };

        return protectedRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Normalize(Path.GetFullPath(root)))
            .Any(root => string.Equals(path, root, StringComparison.OrdinalIgnoreCase) || IsStrictlyUnder(path, root));
    }

    private PathSafetyResult InspectReparsePoints(string canonicalPath)
    {
        var root = Path.GetPathRoot(canonicalPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return PathSafetyResult.Reject("Deletion target has no filesystem root.");
        }

        var current = Normalize(root);
        var relative = canonicalPath[root.Length..];
        foreach (var segment in relative.Split(DirectorySeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                var attributes = _pathInspector.GetAttributes(current);
                if (attributes is not null && (attributes.Value & FileAttributes.ReparsePoint) != 0)
                {
                    return PathSafetyResult.Reject($"Deletion target crosses a reparse point: {current}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                return PathSafetyResult.Reject($"Deletion target could not be inspected safely ({ex.GetType().Name}).");
            }
        }

        return PathSafetyResult.Allow(canonicalPath);
    }

    private static string Normalize(string path)
    {
        var root = Path.GetPathRoot(path);
        if (!string.IsNullOrWhiteSpace(root) && string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

public sealed class SystemWindowsPathInspector : IWindowsPathInspector
{
    public FileAttributes? GetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}
