using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BurrowWin.Models;
using BurrowWin.Services;

namespace BurrowWin.ViewModels;

public partial class PurgeViewModel : ViewModelBase
{
    private readonly IMoleEngineService _moleEngineService;
    private readonly IPurgeArtifactService _purgeArtifactService;
    private readonly IOperationHistoryService _operationHistoryService;
    private CancellationTokenSource? _operationCts;

    public PurgeViewModel(
        IMoleEngineService moleEngineService,
        IPurgeArtifactService purgeArtifactService,
        IOperationHistoryService operationHistoryService)
    {
        _moleEngineService = moleEngineService;
        _purgeArtifactService = purgeArtifactService;
        _operationHistoryService = operationHistoryService;
    }

    public ObservableCollection<PurgeProjectCandidate> Projects { get; } = new();

    public ObservableCollection<string> OutputLines { get; } = new();

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool canRemove;

    [ObservableProperty]
    private string summary = "Ready to scan project artifacts";

    [ObservableProperty]
    private string selectedSummary = "0 projects";

    [ObservableProperty]
    private string engineSummary = "Mole Windows purge is interactive; BurrowWin previews project artifacts using the same Windows rules.";

    [ObservableProperty]
    private string progressText = "project artifacts";

    public string OutputText => string.Join(Environment.NewLine, OutputLines);

    public bool CanCancel => IsBusy && _operationCts is { IsCancellationRequested: false };

    [RelayCommand]
    public async Task PreviewAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var startedAt = Stopwatch.GetTimestamp();
        var exitCode = 1;
        var historySummary = "Purge preview did not finish";

        var cancellationToken = BeginOperation("Scanning project artifacts...");
        CanRemove = false;
        ClearProjects();
        OutputLines.Clear();
        OnPropertyChanged(nameof(OutputText));
        Summary = "Scanning project artifacts...";

        try
        {
            var availability = _moleEngineService.GetAvailability();
            EngineSummary = availability.IsAvailable
                ? $"Mole engine available at {availability.Path}; purge preview uses non-interactive Windows rules."
                : $"{availability.Message} Purge preview still uses local Windows artifact rules.";

            var projects = await _purgeArtifactService.PreviewAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            exitCode = 0;
            historySummary = BuildPreviewSummary(projects);

            RunOnUiThread(() =>
            {
                ClearProjects();
                foreach (var project in projects)
                {
                    project.PropertyChanged += Project_PropertyChanged;
                    Projects.Add(project);
                }

                Summary = historySummary;
                UpdateSelectionState();
            });
        }
        catch (OperationCanceledException)
        {
            exitCode = 130;
            historySummary = "Purge preview cancelled";
            RunOnUiThread(() => Summary = historySummary);
        }
        finally
        {
            await RecordHistoryAsync(
                "purge-preview",
                "project artifacts",
                exitCode,
                Stopwatch.GetElapsedTime(startedAt),
                historySummary).ConfigureAwait(false);

            RunOnUiThread(EndOperation);
        }
    }

    public DestructiveActionAuthorization CreateRemovalAuthorization()
    {
        var paths = Projects
            .Where(project => project.IsSelected)
            .SelectMany(project => project.Artifacts)
            .Select(artifact => artifact.Path)
            .ToArray();
        return DestructiveActionAuthorization.Confirmed(PurgeArtifactService.DeletionSource, paths);
    }

    public async Task RemoveAsync(DestructiveActionAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);

        var selectedProjects = Projects.Where(project => project.IsSelected).ToList();
        if (selectedProjects.Count == 0 || IsBusy)
        {
            return;
        }

        var startedAt = Stopwatch.GetTimestamp();
        var exitCode = 1;
        var historySummary = "Purge removal did not finish";

        var cancellationToken = BeginOperation("Removing selected project artifacts...");
        CanRemove = false;
        OutputLines.Clear();
        OnPropertyChanged(nameof(OutputText));
        Summary = "Removing selected project artifacts...";

        try
        {
            var progress = new Progress<DeletionProgress>(UpdateProgress);
            var batch = await _purgeArtifactService
                .RemoveAsync(selectedProjects, authorization, progress, cancellationToken)
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
                "purge-remove",
                $"{selectedProjects.Count} selected projects",
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
        foreach (var project in Projects)
        {
            project.IsSelected = true;
        }

        UpdateSelectionState();
    }

    [RelayCommand]
    public void ClearSelection()
    {
        foreach (var project in Projects)
        {
            project.IsSelected = false;
        }

        UpdateSelectionState();
    }

    [RelayCommand]
    public async Task CheckMoleAsync()
    {
        IsBusy = true;
        try
        {
            var result = await _moleEngineService.ExecuteCommandAsync("purge --help", AppendOutput).ConfigureAwait(false);
            RunOnUiThread(() =>
            {
                EngineSummary = result.Succeeded
                    ? "Mole purge is present; its Windows command is interactive, so BurrowWin uses a safe preview list before deleting artifacts."
                    : $"Mole purge help failed with exit code {result.ExitCode}; local preview remains available.";
            });
        }
        finally
        {
            RunOnUiThread(() => IsBusy = false);
        }
    }

    private void Project_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PurgeProjectCandidate.IsSelected))
        {
            UpdateSelectionState();
        }
    }

    private void UpdateSelectionState()
    {
        var selected = Projects.Where(project => project.IsSelected).ToList();
        var selectedBytes = selected.Sum(project => project.TotalSizeBytes);
        SelectedSummary = $"{selected.Count} projects - {SystemTelemetryFormatter.Bytes(selectedBytes)}";
        CanRemove = selected.Count > 0 && !IsBusy;
    }

    private void ClearProjects()
    {
        foreach (var project in Projects)
        {
            project.PropertyChanged -= Project_PropertyChanged;
        }

        Projects.Clear();
        UpdateSelectionState();
    }

    private static string BuildPreviewSummary(IReadOnlyList<PurgeProjectCandidate> projects)
    {
        if (projects.Count == 0)
        {
            return "No cleanable project artifacts found";
        }

        var totalBytes = projects.Sum(project => project.TotalSizeBytes);
        var totalArtifacts = projects.Sum(project => project.ArtifactCount);
        return $"{projects.Count} projects - {totalArtifacts} artifacts - {SystemTelemetryFormatter.Bytes(totalBytes)}";
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
        ProgressText = "project artifacts";
        OnPropertyChanged(nameof(CanCancel));
        CancelCommand.NotifyCanExecuteChanged();
        UpdateSelectionState();
    }

    private void UpdateProgress(DeletionProgress progress)
    {
        RunOnUiThread(() =>
        {
            ProgressText = progress.TotalCount == 0
                ? "No selected artifacts"
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

    private void AppendOutput(string line)
    {
        RunOnUiThread(() =>
        {
            OutputLines.Add(line);
            OnPropertyChanged(nameof(OutputText));
        });
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
