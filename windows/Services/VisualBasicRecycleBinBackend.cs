using Microsoft.VisualBasic.FileIO;

namespace BurrowWin.Services;

public sealed class VisualBasicRecycleBinBackend : IRecycleBinBackend
{
    public Task<RecycleBinBackendResult> RecycleAsync(
        string canonicalPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Once the Windows Shell operation begins it cannot be cancelled reliably. The caller
        // waits for this item to finish, records its outcome, and then stops before the next item.
        return Task.Run(() =>
        {
            if (Directory.Exists(canonicalPath))
            {
                FileSystem.DeleteDirectory(
                    canonicalPath,
                    UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin,
                    UICancelOption.ThrowException);
                return new RecycleBinBackendResult(
                    true,
                    "Directory moved to Recycle Bin.",
                    "shell:RecycleBinFolder");
            }

            if (File.Exists(canonicalPath))
            {
                FileSystem.DeleteFile(
                    canonicalPath,
                    UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin,
                    UICancelOption.ThrowException);
                return new RecycleBinBackendResult(
                    true,
                    "File moved to Recycle Bin.",
                    "shell:RecycleBinFolder");
            }

            return new RecycleBinBackendResult(false, "Path disappeared before the Recycle Bin operation started.");
        }, CancellationToken.None);
    }
}
