using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MealRoute.App.ViewModels.Services;
using MealRoute.Core.Evaluation;
using MealRoute.Core.Geo;
using MealRoute.Core.Optimization;
using MealRoute.Core.Rescheduling;

namespace MealRoute.App.ViewModels;

public sealed partial class CompletedStopItem(int stop, string label, bool isCompleted) : ObservableObject
{
    public int Stop { get; } = stop;
    public string Label { get; } = label;

    [ObservableProperty]
    private bool _isCompleted = isCompleted;
}

public sealed record VehicleOption(int Index, string Label);

/// <summary>
/// Same-day rescheduling input flow: pick a vehicle, report time (defaults to now), where it is
/// (last completed stop or a map point), which stops are done, delay or breakdown; add reports for
/// several vehicles; run (background, cancellable); review before/after; apply.
/// </summary>
public sealed partial class RescheduleViewModel : ObservableObject
{
    private readonly Func<PlanningWorkspace?> _workspace;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly Action _applied;
    private Action? _cancel;

    public RescheduleViewModel(Func<PlanningWorkspace?> workspace, IUiDispatcher dispatcher, TimeProvider time, Action applied)
    {
        _workspace = workspace;
        _dispatcher = dispatcher;
        _time = time;
        _applied = applied;
    }

    public ObservableCollection<VehicleOption> Vehicles { get; } = new();
    public ObservableCollection<CompletedStopItem> Stops { get; } = new();
    public ObservableCollection<VehicleReport> Reports { get; } = new();

    [ObservableProperty] private int _selectedVehicle = -1;
    [ObservableProperty] private double _reportTime;
    [ObservableProperty] private bool _useMapPosition;
    [ObservableProperty] private GeoPoint? _mapPosition;
    [ObservableProperty] private double _delayMinutes;
    [ObservableProperty] private bool _isBrokenDown;
    [ObservableProperty] private double _seconds = 20;
    /// <summary>Optional iteration limit per run (reproducible results); 0 = only the time limit.</summary>
    [ObservableProperty] private int _maxIterations;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isRunning;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _summary = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private RescheduleResult? _result;

    /// <summary>Current time of day (seconds from midnight) from the TimeProvider.</summary>
    public double Now => _time.GetLocalNow().TimeOfDay.TotalSeconds;

    /// <summary>Fills vehicle choices and defaults (report time = now).</summary>
    public void Prepare()
    {
        Vehicles.Clear();
        Reports.Clear();
        Result = null;
        Summary = "";
        if (_workspace() is not { } ws) return;
        foreach (var (v, i) in ws.Instance.Vehicles.Select((v, i) => (v, i))) Vehicles.Add(new VehicleOption(i, $"{v.DriverName}（{v.Id}）"));
        ReportTime = Now;
        SelectedVehicle = Vehicles.Count > 0 ? 0 : -1;
    }

    partial void OnSelectedVehicleChanged(int value) => LoadStops();
    partial void OnReportTimeChanged(double value) => LoadStops();

    /// <summary>The selected vehicle's planned stops; those planned to be left before the report time are pre-checked.</summary>
    private void LoadStops()
    {
        Stops.Clear();
        if (_workspace()?.Editor is not { } editor || SelectedVehicle < 0) return;
        var e = editor.CurrentEvaluation(SelectedVehicle);
        foreach (var v in e.Visits)
            Stops.Add(new CompletedStopItem(v.Stop, $"{editor.Instance.Stops[v.Stop].Name}（預計 {TimeOfDay.Format(v.Arrival)}）", v.Departure <= ReportTime));
    }

    public void SetPositionFromMap(GeoPoint p)
    {
        MapPosition = p;
        UseMapPosition = true;
    }

    /// <summary>Adds (or replaces) the report of the selected vehicle.</summary>
    [RelayCommand]
    public void AddReport()
    {
        if (_workspace() is not { } ws || SelectedVehicle < 0) return;
        var completed = Stops.Where(s => s.IsCompleted).Select(s => s.Stop).ToArray();
        GeoPoint position;
        if (UseMapPosition && MapPosition is { } p) position = p;
        else if (completed.Length > 0) position = ws.Instance.Stops[completed[^1]].Location;
        else position = ws.Instance.Vehicles[SelectedVehicle].StartDepot;
        var existing = Reports.FirstOrDefault(r => r.Vehicle == SelectedVehicle);
        if (existing is not null) Reports.Remove(existing);
        Reports.Add(new VehicleReport(SelectedVehicle, ReportTime, position, completed, DelayMinutes, IsBrokenDown));
    }

    private bool CanRun() => !IsRunning && _workspace()?.Editor is not null;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunAsync() => RunCoreAsync();

    public async Task RunCoreAsync()
    {
        if (_workspace() is not { } ws || ws.Editor is not { } editor || IsRunning) return;
        if (Reports.Count == 0) AddReport();
        IsRunning = true;
        Progress = 0;
        Summary = "重新排程中…";
        using var cts = new CancellationTokenSource();
        _cancel = cts.Cancel;
        var before = editor.Objective;
        var request = new RescheduleRequest(editor.ToSolution(), Now, Reports.ToArray(), editor.Locks);
        var progress = new OptimizationViewModel.DispatchedProgress(_dispatcher, p => { if (p.Fraction > Progress) Progress = p.Fraction; });
        try
        {
            var options = new OptimizerOptions
            {
                Seed = 1,
                Runs = 2,
                MaxIterations = MaxIterations > 0 ? MaxIterations : 10_000_000,
                TimeLimit = TimeSpan.FromSeconds(Math.Max(1, Seconds)),
                TimeProvider = _time,
            };
            var result = await ws.CreateRescheduler().RunAsync(request, options, progress, cts.Token).ConfigureAwait(false);
            _dispatcher.Post(() =>
            {
                Result = result;
                var unservedBefore = editor.Unserved.Count;
                Summary = $"{(result.Cancelled ? "（已取消）" : "")}重新排程前：{before}\n重新排程後：{result.Objective}\n未服務站 {unservedBefore} → {result.Solution.Unserved.Count}";
            });
        }
        finally
        {
            _dispatcher.Post(() => IsRunning = false);
            _cancel = null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _cancel?.Invoke();

    private bool CanApply() => !IsRunning && Result is not null;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (Result is null || _workspace()?.Editor is not { } editor) return;
        editor.ReplaceSolution(Result.Solution);
        Result = null;
        Reports.Clear();
        Summary = "已套用新排程（可復原）";
        _applied();
    }
}
