using BurrowWin.Models;

namespace BurrowWin.Services;

public interface IDeletionReceiptStore
{
    string ReceiptFilePath { get; }

    Task RecordAsync(DeletionReceipt receipt, CancellationToken cancellationToken = default);
}
