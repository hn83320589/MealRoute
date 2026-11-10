using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MealRoute.App.ViewModels.Services;
using MealRoute.Core.Reports;

namespace MealRoute.App.ViewModels;

/// <summary>Route sheets per driver plus print and CSV/GeoJSON export.</summary>
public sealed partial class RouteSheetViewModel : ObservableObject
{
    private readonly Func<PlanningWorkspace?> _workspace;
    private readonly IFileDialogService _files;
    private readonly IPrintService _print;
    private readonly IMessageService _messages;

    public RouteSheetViewModel(Func<PlanningWorkspace?> workspace, IFileDialogService files, IPrintService print, IMessageService messages)
    {
        _workspace = workspace;
        _files = files;
        _print = print;
        _messages = messages;
    }

    [ObservableProperty] private IReadOnlyList<RouteSheet> _sheets = Array.Empty<RouteSheet>();

    public void Refresh()
    {
        var ws = _workspace();
        Sheets = ws?.Editor is { } e ? RouteSheetBuilder.Build(ws.Instance, e.ToSolution()) : Array.Empty<RouteSheet>();
    }

    [RelayCommand]
    public async Task PrintAsync()
    {
        Refresh();
        if (Sheets.Count == 0)
        {
            await _messages.ShowErrorAsync("列印", "目前沒有路線可列印。").ConfigureAwait(false);
            return;
        }
        await _print.PrintAsync(Sheets).ConfigureAwait(false);
    }

    [RelayCommand]
    public async Task ExportCsvAsync()
    {
        if (_workspace() is not { Editor: { } editor } ws) return;
        var path = await _files.PickSaveFileAsync("匯出路線 CSV", "routes.csv", ".csv").ConfigureAwait(false);
        if (path is null) return;
        await Guard(() => RouteCsv.WriteFile(path, ws.Instance, editor.ToSolution())).ConfigureAwait(false);
    }

    [RelayCommand]
    public async Task ExportGeoJsonAsync()
    {
        if (_workspace() is not { Editor: { } editor } ws) return;
        var path = await _files.PickSaveFileAsync("匯出路線 GeoJSON", "routes.geojson", ".geojson").ConfigureAwait(false);
        if (path is null) return;
        await Guard(() => File.WriteAllText(path, RouteGeoJson.Write(ws.Instance, editor.ToSolution(), ws.RouteGeometry))).ConfigureAwait(false);
    }

    private async Task Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _messages.ShowErrorAsync("匯出失敗", ex.Message).ConfigureAwait(false);
        }
    }
}
