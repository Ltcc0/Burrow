using BurrowWin.Models;
using BurrowWin.Services;

namespace BurrowWin.Tests;

internal sealed class RecordingSafeDeletionService : ISafeDeletionService
{
    public List<string> DeletedPaths { get; } = [];

    public LeftoverRemovalResult DeleteFileOrDirectory(string path, long sizeBytes)
    {
        DeletedPaths.Add(Path.GetFullPath(path));
        return new LeftoverRemovalResult(path, true, "Moved to Recycle Bin.", sizeBytes);
    }
}
