using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BurrowWin.Models;
using BurrowWin.Services;

namespace BurrowWin.ViewModels;

public partial class InstallerViewModel : ViewModelBase
{
    private readonly IInstallerCleanupService _installerCleanupService;
    private readonly IMoleEngineService _moleEngineService;
    private readonly IOperationHistoryService _operationHistoryService;
    private CancellationTokenSource? _operationCts;

    public InstallerViewModel(
        IInstallerCleanupService installerCleanupService,
        IMoleEngineService moleEngineService,
        IOperationHistoryService operationHistoryService)
    {
        _installerCleanupService = installerCleanupService;
        _moleEngineService = moleEngineService;
        _operationHistoryService = operationHistoryService;
    }

    public ObservableCollection<InstallerCleanupCandidate> Items { get; } = new();

    public ObservableCollection<string> OutputLines { get; } = new();

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool canRemove;

    [ObservableProperty]
    private string summary = "Ready to scan old installers";

    [ObservableProperty]
    private string selectedSummary = "0 files";

    [ObservableProperty]
    private string engineSummary = "Mole Windows has no dedicated installer command yet; this view mirrors Mole's old Downloads installer/archive rules.";

    [ObservableProperty]
    private string progressText = "old Downloads installers";

    public string OutputText => string.Join(Environment.NewLine, OutputLines);

    public bool CanCancel => IsBusy && _operationCts is { IsCancellationRequested: false };

    [RelayCommand]
    public async Task ScanAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var startedAt = Stopwatch.GetTimestamp();
        var exitCode = 1;
        var historySummary = "Installer preview did not finish";

        var cancellationToken = BeginOperation("Scanning old installers...");
        CanRemove = false;
        ClearItems();
        OutputLines.Clear();
        OnPropertyChanged(nameof(OutputText));
        Summary = "Scanning old installers...";

        try
        {
            var availability = _moleEngineService.GetAvailability();
            var items = await _installerCleanupService.PreviewAsync(cancellationToken).ConfigureAwait(false);
            exitCode = 0;
            historySummary = BuildPreviewSummary(items);

            RunOnUiThread(() =>
            {
                EngineSummary = availability.IsAvailable
                    ? $"Mole engine available at {availability.Path}; installer preview uses Mole-compatible Downloads rules."
                    : $"{availability.Message} Installer preview uses local Windows Downloads rules.";

                ClearItems();
                foreach (var item in items)
                {
                    item.PropertyChanged += Item_PropertyChanged;
                    Items.Add(item);
                }

                Summary = historySummary;
                UpdateSelectionState();
            });
        }
        catch (OperationCanceledException)
        {
            exitCode = 130;
            historySummary = "Installer preview cancelled";
            RunOnUiThread(() => Summary = historySummary);
        }
        finally
        {
            await RecordHistoryAsync(
                "installer-preview",
                "old Downloads installers",
                exitCode,
                Stopwatch.GetElapsedTime(startedAt),
                historySummary).ConfigureAwait(false);

            RunOnUiThread(EndOperation);
        }
    }

    public DestructiveActionAuthorization CreateRemovalAuthorization()
    {
        return DestructiveActionAuthorization.Confirmed(
            InstallerCleanupService.DeletionSource,
            Items.Where(item => item.IsSelected).Select(item => item.Path));
    }

    public async Task RemoveAsync(DestructiveActionAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);

        var selected = Items.Where(item => item.IsSelected).ToList();
        if (selected.Count == 0 || IsBusy)
        {
            return;
        }

        var startedAt = Stopwatch.GetTimestamp();
        var exitCode = 1;
        var historySummary = "Installer removal did not finish";

        var cancellationToken = BeginOperation("Removing selected installers...");
        CanRemove = false;
        OutputLines.Clear();
        OnPropertyChanged(nameof(OutputText));
        Summary = "Removing selected installers...";

        try
        {
            var progress = new Progress<DeletionProgress>(UpdateProgress);
            var batch = await _installerCleanupService
                .RemoveAsync(selected, authorization, progress, cancellationToken)
                .ConfigureAwait(false);
            exitCode = ExitCodeFor(batch);
            historySummary = BuildRemovalSummary(batch);

            RunOnUiThread(() =>
            {
                foreach (var result in batch.Results)
                {
                    var prefix = result.Disposition.ToString().ToLowerInvariant();
                    OutputLines.Add($"{prefix}: {result.Path} ({SystemTelemetryFormatter.Bytes(result.SizeBytes)}) {result.Message}");
                }

                Summary = historySummary;
                OnPropertyChanged(nameof(OutputText));
            });
        }
        finally
        {
            await RecordHistoryAsync(
                "installer-remove",
                $"{selected.Count} selected old Downloads installers",
                exitCode,
                Stopwatch.GetElapsedTime(startedAt),
                historySummary).ConfigureAwait(false);

            RunOnUiThread(EndOperation);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    public void Cancel()
    {
        _operationCts?.Cancel();
        ProgressText = "Cancelling after the current item...";
        OnPropertyChanged(nameof(CanCancel));
        CancelCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    public void SelectAll()
    {
        foreach (var item in Items)
        {
            item.IsSelected = true;
        }

        UpdateSelectionState();
    }

    [RelayCommand]
    public void ClearSelection()
    {
        foreach (var item in Items)
        {
            item.IsSelected = false;
        }

        UpdateSelectionState();
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstallerCleanupCandidate.IsSelected))
        {
            UpdateSelectionState();
        }
    }

    private void UpdateSelectionState()
    {
        var selected = Items.Where(item => item.IsSelected).ToList();
        var selectedBytes = selected.Sum(item => item.SizeBytes);
        SelectedSummary = $"{selected.Count} files - {SystemTelemetryFormatter.Bytes(selectedBytes)}";
        CanRemove = selected.Count > 0 && !IsBusy;
    }

    private void ClearItems()
    {
        foreach (var item in Items)
        {
            item.PropertyChanged -= Item_PropertyChanged;
        }

        Items.Clear();
        UpdateSelectionState();
    }

    private static string BuildPreviewSummary(IReadOnlyList<InstallerCleanupCandidate> items)
    {
        if (items.Count == 0)
        {
            return "No old installers found";
        }

        var totalBytes = items.Sum(item => item.SizeBytes);
        return $"{items.Count} files - {SystemTelemetryFormatter.Bytes(totalBytes)}";
    }

    private CancellationToken BeginOperation(string progressText)
    {
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        ProgressText = progressText;
        IsBusy = true;
        OnPropertyChanged(nameof(CanCancel));
        CancelCommand.NotifyCanExecuteChanged();
        return _operationCts.Token;
    }

    private void EndOperation()
    {
        _operationCts?.Dispose();
        _operationCts = null;
        IsBusy = false;
        ProgressText = "old Downloads installers";
        OnPropertyChanged(nameof(CanCancel));
        CancelCommand.NotifyCanExecuteChanged();
        UpdateSelectionState();
    }

    private void UpdateProgress(DeletionProgress progress)
    {
        RunOnUiThread(() =>
        {
            ProgressText = progress.TotalCount == 0
                ? "No selected installers"
                : $"{progress.ProcessedCount}/{progress.TotalCount} processed · {progress.RecycledCount} recycled";
        });
    }

    private static int ExitCodeFor(DeletionBatchResult batch)
    {
        return batch.Outcome switch
        {
            DeletionBatchOutcome.Succeeded => 0,
            DeletionBatchOutcome.Cancelled => 130,
            DeletionBatchOutcome.PartialSuccess => 2,
            _ => 1
        };
    }

    private static string BuildRemovalSummary(DeletionBatchResult batch)
    {
        var prefix = batch.Outcome switch
        {
            DeletionBatchOutcome.Succeeded => "Completed",
            DeletionBatchOutcome.Cancelled => "Cancelled",
            DeletionBatchOutcome.PartialSuccess => "Partially completed",
            _ => "Failed"
        };
        return $"{prefix}: {batch.RecycledCount} recycled, {batch.AlreadyAbsentCount} already absent, " +
               $"{batch.RejectedCount} rejected, {batch.FailedCount} failed · {SystemTelemetryFormatter.Bytes(batch.RecycledBytes)}";
    }

    private async Task RecordHistoryAsync(
        string operation,
        string arguments,
        int exitCode,
        TimeSpan duration,
        string historySummary)
    {
        var entry = new OperationHistoryEntry(
            DateTimeOffset.UtcNow,
            "burrowwin",
            operation,
            arguments,
            exitCode,
            exitCode == 0,
            (long)duration.TotalMilliseconds,
            historySummary);

        try
        {
            await _operationHistoryService.RecordAsync(entry).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
