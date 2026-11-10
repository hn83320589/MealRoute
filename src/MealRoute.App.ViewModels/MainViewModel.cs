using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MealRoute.App.ViewModels.Services;
using MealRoute.Core.Evaluation;
using MealRoute.Core.GeoJson;
using MealRoute.Core.Projects;

namespace MealRoute.App.ViewModels;

/// <summary>Top-level view model: project lifecycle, imports, warnings, undo/redo, locks, and the child view models.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IFileDialogService _files;
    private readonly IMessageService _messages;
    private readonly IUiDispatcher _dispatcher;
    private readonly JsonSettingsStore _settingsStore;
    private PlanningWorkspace? _workspace;

    public MainViewModel(IFileDialogService files, IMessageService messages, IPrintService print, IUiDispatcher dispatcher,
        TimeProvider time, JsonSettingsStore settings)
    {
        _files = files;
        _messages = messages;
        _dispatcher = dispatcher;
        _settingsStore = settings;
        Settings = settings.Load();

        Selection = new SelectionService();
        Map = new MapViewModel(Selection);
        Timeline = new TimelineViewModel(Selection);
        Drag = new DragPreviewViewModel(() => _workspace?.Editor, PlanChanged);
        Optimization = new OptimizationViewModel(() => _workspace, dispatcher, time, PlanChanged)
        {
            Seconds = Settings.OptimizerSeconds,
            Runs = Settings.OptimizerRuns,
        };
        Reschedule = new RescheduleViewModel(() => _workspace, dispatcher, time, PlanChanged);
        RouteSheets = new RouteSheetViewModel(() => _workspace, files, print, messages);
        Selection.PropertyChanged += (_, _) => UpdateSelectionText();
    }

    public SelectionService Selection { get; }
    public MapViewModel Map { get; }
    public TimelineViewModel Timeline { get; }
    public DragPreviewViewModel Drag { get; }
    public OptimizationViewModel Optimization { get; }
    public RescheduleViewModel Reschedule { get; }
    public RouteSheetViewModel RouteSheets { get; }
    public AppSettings Settings { get; private set; }
    public PlanningWorkspace? Workspace => _workspace;
    public ObservableCollection<WorkspaceWarning> Warnings { get; } = new();

    [ObservableProperty] private string _title = "MealRoute 社區送餐排班台";
    [ObservableProperty] private string _status = "請開啟專案，或匯入路網與名單";
    [ObservableProperty] private string _objectiveText = "";
    [ObservableProperty] private string _selectionText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _busyProgress;

    // ------------------------------------------------------------------ project lifecycle

    [RelayCommand]
    public async Task OpenProjectAsync()
    {
        var path = await _files.PickOpenFileAsync("開啟專案", ".json").ConfigureAwait(true);
        if (path is not null) await OpenProjectFromPathAsync(path).ConfigureAwait(true);
    }

    public async Task OpenProjectFromPathAsync(string path)
    {
        await Busy("開啟專案中…", async progress =>
        {
            var ws = await PlanningWorkspace.OpenAsync(path, progress, CancellationToken.None).ConfigureAwait(true);
            Attach(ws);
            Settings = Settings with { LastProjectPath = ws.ProjectPath };
            _settingsStore.Save(Settings);
            Status = $"已開啟 {Path.GetFileName(path)}：{ws.Instance.Stops.Count} 站、{ws.Instance.Vehicles.Count} 車";
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task OpenSolomonAsync()
    {
        var path = await _files.PickOpenFileAsync("開啟 Solomon 實例（靜態模式）", ".txt").ConfigureAwait(true);
        if (path is null) return;
        await Busy("讀取 Solomon 實例…", _ =>
        {
            Attach(PlanningWorkspace.OpenSolomon(path));
            Status = $"靜態模式（歐氏距離、無路網）：{_workspace!.Project.Name}";
            return Task.CompletedTask;
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task SaveProjectAsync()
    {
        if (_workspace is null) return;
        var path = _workspace.ProjectPath
                   ?? await _files.PickSaveFileAsync("儲存專案", "project.mealroute.json", ".json").ConfigureAwait(true);
        if (path is null) return;
        await SaveToAsync(path).ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task SaveProjectAsAsync()
    {
        if (_workspace is null) return;
        var path = await _files.PickSaveFileAsync("另存專案", "project.mealroute.json", ".json").ConfigureAwait(true);
        if (path is not null) await SaveToAsync(path).ConfigureAwait(true);
    }

    private async Task SaveToAsync(string path)
    {
        try
        {
            _workspace!.Save(path);
            Status = $"已儲存 {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProjectFileException)
        {
            await _messages.ShowErrorAsync("儲存失敗", ex.Message).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    public async Task ImportNetworkAsync()
    {
        var path = await _files.PickOpenFileAsync("匯入路網 GeoJSON", ".geojson;.json").ConfigureAwait(true);
        if (path is null) return;
        await Busy("匯入路網…", async progress =>
        {
            var ws = _workspace ?? PlanningWorkspace.Empty();
            await ws.LoadNetworkFileAsync(path, rebuild: true, progress, CancellationToken.None).ConfigureAwait(true);
            Attach(ws);
            Status = $"路網：{ws.Network!.NodeCount} 節點、{ws.Network.EdgeCount} 有向邊，{ws.Analysis!.SmallComponents.Count} 個小分量";
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task ImportStopsAsync()
    {
        var path = await _files.PickOpenFileAsync("匯入當日名單（站點 CSV）", ".csv").ConfigureAwait(true);
        if (path is null) return;
        await Busy("匯入名單…", async progress =>
        {
            var stops = RosterCsv.ReadStops(await File.ReadAllTextAsync(path).ConfigureAwait(true));
            var ws = _workspace ?? PlanningWorkspace.Empty();
            await ws.SetRosterAsync(stops, null, progress, CancellationToken.None).ConfigureAwait(true);
            Attach(ws);
            Status = $"名單：{stops.Count} 站";
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task ImportVehiclesAsync()
    {
        var path = await _files.PickOpenFileAsync("匯入車輛 CSV", ".csv").ConfigureAwait(true);
        if (path is null) return;
        await Busy("匯入車輛…", async progress =>
        {
            var vehicles = RosterCsv.ReadVehicles(await File.ReadAllTextAsync(path).ConfigureAwait(true));
            var ws = _workspace ?? PlanningWorkspace.Empty();
            await ws.SetRosterAsync(null, vehicles, progress, CancellationToken.None).ConfigureAwait(true);
            Attach(ws);
            Status = $"車輛：{vehicles.Count} 台";
        }).ConfigureAwait(true);
    }

    /// <summary>Runs an operation with busy state; user-facing failures become an error message (never a crash).</summary>
    private async Task Busy(string status, Func<IProgress<double>, Task> work)
    {
        IsBusy = true;
        BusyProgress = 0;
        Status = status;
        try
        {
            await work(new Progress(_dispatcher, p => BusyProgress = Math.Max(BusyProgress, p))).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is ProjectFileException or GeoJsonFormatException or FormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status = "操作失敗";
            await _messages.ShowErrorAsync("無法完成操作", ex.Message).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private sealed class Progress(IUiDispatcher dispatcher, Action<double> handler) : IProgress<double>
    {
        public void Report(double value) => dispatcher.Post(() => handler(value));
    }

    /// <summary>Makes a workspace current and refreshes every view.</summary>
    public void Attach(PlanningWorkspace ws)
    {
        _workspace = ws;
        Title = $"MealRoute 社區送餐排班台 — {ws.Project.Name}";
        Warnings.Clear();
        foreach (var w in ws.Warnings) Warnings.Add(w);
        Selection.Clear();
        Map.Load(ws);
        Optimization.StartCommand.NotifyCanExecuteChanged();
        PlanChanged();
    }

    // ------------------------------------------------------------------ plan changes, undo, locks

    /// <summary>Called after any change to the plan.</summary>
    public void PlanChanged()
    {
        Map.RefreshPlan();
        Timeline.Refresh(_workspace?.Editor);
        RouteSheets.Refresh();
        ObjectiveText = _workspace?.Editor is { } e ? e.Objective.ToString() : "";
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        Optimization.StartCommand.NotifyCanExecuteChanged();
        UpdateSelectionText();
    }

    private bool CanUndo() => _workspace?.Editor?.CanUndo ?? false;
    private bool CanRedo() => _workspace?.Editor?.CanRedo ?? false;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    public void Undo()
    {
        _workspace?.Editor?.Undo();
        PlanChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    public void Redo()
    {
        _workspace?.Editor?.Redo();
        PlanChanged();
    }

    /// <summary>Locks/unlocks the selected stop at its current vehicle and position.</summary>
    [RelayCommand]
    public void ToggleStopLock()
    {
        if (_workspace?.Editor is not { } e || Selection.SelectedStop is not { } stop) return;
        if (e.IsStopLocked(stop)) e.UnlockStop(stop);
        else if (Selection.SelectedVehicle is not null) e.LockStop(stop);
        else { Status = "未排入路線的站無法鎖定"; return; }
        PlanChanged();
    }

    /// <summary>Locks/unlocks the whole route of the selected vehicle.</summary>
    [RelayCommand]
    public void ToggleRouteLock()
    {
        if (_workspace?.Editor is not { } e || Selection.SelectedVehicle is not { } v) return;
        if (e.IsRouteLocked(v)) e.UnlockRoute(v); else e.LockRoute(v);
        PlanChanged();
    }

    private void UpdateSelectionText()
    {
        if (_workspace is not { } ws) { SelectionText = ""; return; }
        if (Selection.SelectedStop is { } s)
        {
            var stop = ws.Instance.Stops[s];
            var visit = Selection.SelectedVehicle is { } v && ws.Editor is { } e
                ? e.CurrentEvaluation(v).Visits.FirstOrDefault(x => x.Stop == s) : null;
            SelectionText = $"{stop.Name}（{stop.Id}）{stop.Address}｜時間窗 {TimeOfDay.Format(stop.WindowStart)}–{TimeOfDay.Format(stop.WindowEnd)}｜餐盒 {stop.Meals}、保溫箱 {stop.Boxes}" +
                            (visit is null ? "｜未排入路線" : $"｜{ws.Instance.Vehicles[Selection.SelectedVehicle!.Value].DriverName} 預計 {TimeOfDay.Format(visit.Arrival)} 到")
                            + (ws.Editor?.IsStopLocked(s) == true ? "｜已鎖定" : "");
        }
        else if (Selection.SelectedVehicle is { } veh && ws.Editor is { } ed)
        {
            var e = ed.CurrentEvaluation(veh);
            SelectionText = $"{ws.Instance.Vehicles[veh].DriverName}：{e.Stops.Count} 站｜出發 {TimeOfDay.Format(e.Departure)}｜返回 {TimeOfDay.Format(e.EndArrival)}" +
                            (e.IsFeasible ? "" : $"｜違反：{e.FirstViolation?.Message}") + (ed.IsRouteLocked(veh) ? "｜路線已鎖定" : "");
        }
        else SelectionText = "";
    }
}
