namespace BurrowWin.Services;

public sealed record PathSafetyResult(bool IsSafe, string? CanonicalPath, string Message)
{
    public static PathSafetyResult Allow(string canonicalPath)
    {
        return new PathSafetyResult(true, canonicalPath, "Path passed Windows safety validation.");
    }

    public static PathSafetyResult Reject(string message, string? canonicalPath = null)
    {
        return new PathSafetyResult(false, canonicalPath, message);
    }
}

public interface IWindowsPathSafetyPolicy
{
    PathSafetyResult Evaluate(string path, string scopeRoot);
}

public interface IWindowsPathInspector
{
    FileAttributes? GetAttributes(string path);
}
